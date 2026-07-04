// Copyright (c) 2026 LanDen Labs - Dennis Lang

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Threading;

namespace Faster
{
    /// <summary>
    /// Live resource snapshot of one service's HOST PROCESS at the moment it was sampled. When
    /// <see cref="SharedWithCount"/> is greater than zero, every number here is the whole host
    /// process's total, not this service's individual share - Windows groups several services
    /// into one shared process (most commonly several packed into one svchost.exe) and doesn't
    /// expose a finer-grained per-service breakdown of memory/CPU/handles for that case.
    /// </summary>
    public sealed class ServiceMetrics
    {
        public int Pid { get; init; }
        public long WorkingSetBytes { get; init; }
        public int HandleCount { get; init; }
        public int ThreadCount { get; init; }
        public double CpuPercent { get; init; }

        /// <summary>How many OTHER services share this same host process - 0 means this service
        /// has its own dedicated process, so every number above is exact for it alone.</summary>
        public int SharedWithCount { get; init; }
        public List<string> SharedWithNames { get; init; } = new();
    }

    /// <summary>
    /// Resolves each service's PID via WMI (<c>Win32_Service.ProcessId</c>) and samples its host
    /// process's memory/handles/threads/CPU. CPU needs two <c>TotalProcessorTime</c> readings a
    /// short interval apart; <see cref="CollectAll"/> takes ONE shared delay for every distinct
    /// host process rather than one per service, so a machine with ~150 services (which usually
    /// reduces to a few dozen distinct host processes) costs one short pause, not dozens. Intended
    /// to be called from a background thread (it blocks for <see cref="SampleWindow"/>) - callers
    /// should wrap it in <c>Task.Run</c>.
    /// </summary>
    public static class ServiceMetricsCollector
    {
        private static readonly TimeSpan SampleWindow = TimeSpan.FromMilliseconds(300);

        /// <summary>Metrics for every currently-running service, keyed by service name (case
        /// insensitive). Stopped services (no process) are simply absent from the result.</summary>
        public static Dictionary<string, ServiceMetrics> CollectAll()
        {
            var pidByService = QueryAllPids();
            var servicesByPid = pidByService
                .Where(kv => kv.Value != 0)
                .GroupBy(kv => (int)kv.Value)
                .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToList());

            var distinctPids = servicesByPid.Keys.ToList();
            var start = SampleCpuTimes(distinctPids);
            Thread.Sleep(SampleWindow);
            var end = SampleCpuTimes(distinctPids);

            var result = new Dictionary<string, ServiceMetrics>(StringComparer.OrdinalIgnoreCase);
            foreach (var (pid, names) in servicesByPid)
            {
                var metrics = BuildMetrics(pid, names, start, end);
                if (metrics == null) continue;
                foreach (var name in names) result[name] = metrics;
            }
            return result;
        }

        /// <summary>Metrics for one named service only - used by the Details popup, so it does
        /// its own short (one-PID) sample rather than paying for every other service's.</summary>
        public static ServiceMetrics? CollectOne(string serviceName)
        {
            int pid = (int)QueryPid(serviceName);
            if (pid == 0) return null;

            var names = QueryServiceNamesForPid(pid);
            var pids = new List<int> { pid };
            var start = SampleCpuTimes(pids);
            Thread.Sleep(SampleWindow);
            var end = SampleCpuTimes(pids);

            return BuildMetrics(pid, names, start, end);
        }

        private static ServiceMetrics? BuildMetrics(
            int pid, List<string> names,
            Dictionary<int, CpuSample> start, Dictionary<int, CpuSample> end)
        {
            using var proc = TryGetProcess(pid);
            if (proc == null) return null;

            double cpuPercent = 0;
            if (start.TryGetValue(pid, out var s0) && end.TryGetValue(pid, out var s1))
            {
                var cpuDelta = s1.Cpu - s0.Cpu;
                var wallDelta = s1.Time - s0.Time;
                if (wallDelta.TotalMilliseconds > 0)
                    cpuPercent = Math.Max(0,
                        cpuDelta.TotalMilliseconds / wallDelta.TotalMilliseconds / Environment.ProcessorCount * 100.0);
            }

            long workingSet = 0;
            int handles = 0, threads = 0;
            try
            {
                proc.Refresh();
                workingSet = proc.WorkingSet64;
                handles = proc.HandleCount;
                threads = proc.Threads.Count;
            }
            catch { /* access denied on a protected process - leave zeros */ }

            return new ServiceMetrics
            {
                Pid = pid,
                WorkingSetBytes = workingSet,
                HandleCount = handles,
                ThreadCount = threads,
                CpuPercent = cpuPercent,
                SharedWithCount = Math.Max(0, names.Count - 1),
                SharedWithNames = names,
            };
        }

        private readonly record struct CpuSample(TimeSpan Cpu, DateTime Time);

        private static Dictionary<int, CpuSample> SampleCpuTimes(List<int> pids)
        {
            var result = new Dictionary<int, CpuSample>();
            var now = DateTime.UtcNow;
            foreach (var pid in pids)
            {
                using var proc = TryGetProcess(pid);
                if (proc == null) continue;
                try { result[pid] = new CpuSample(proc.TotalProcessorTime, now); }
                catch { /* access denied, or the process exited mid-sample - skip it */ }
            }
            return result;
        }

        private static Process? TryGetProcess(int pid)
        {
            try { return Process.GetProcessById(pid); }
            catch { return null; }   // already exited, or an inaccessible protected process
        }

        private static Dictionary<string, uint> QueryAllPids()
        {
            var result = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name, ProcessId FROM Win32_Service");
                foreach (ManagementBaseObject mo in searcher.Get())
                {
                    if (mo["Name"] as string is not { } name) continue;
                    result[name] = mo["ProcessId"] is uint pid ? pid : 0;
                }
            }
            catch { /* WMI unavailable - return whatever was collected (likely nothing) */ }
            return result;
        }

        private static uint QueryPid(string serviceName)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT ProcessId FROM Win32_Service WHERE Name='{Escape(serviceName)}'");
                foreach (ManagementBaseObject mo in searcher.Get())
                    return mo["ProcessId"] is uint pid ? pid : 0;
            }
            catch { /* ignore */ }
            return 0;
        }

        private static List<string> QueryServiceNamesForPid(int pid)
        {
            var names = new List<string>();
            try
            {
                using var searcher = new ManagementObjectSearcher($"SELECT Name FROM Win32_Service WHERE ProcessId={pid}");
                foreach (ManagementBaseObject mo in searcher.Get())
                    if (mo["Name"] as string is { } name) names.Add(name);
            }
            catch { /* ignore */ }
            return names;
        }

        private static string Escape(string s) => s.Replace("'", "''");
    }
}
