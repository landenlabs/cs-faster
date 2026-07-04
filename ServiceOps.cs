// Copyright (c) 2026 LanDen Labs - Dennis Lang

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.ServiceProcess;

namespace Faster
{
    /// <summary>Outcome of applying one <see cref="ServiceListItem"/>.</summary>
    public sealed class ServiceActionResult
    {
        public string ServiceName { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public bool Success { get; init; }
        public string Message { get; init; } = "";
    }

    /// <summary>
    /// The engine that actually touches services: changing a start type (via <c>sc.exe config</c>
    /// - there is no managed API for this) and stopping/starting with basic dependency handling.
    /// Every public entry point isolates its own exceptions and reports a per-service result
    /// rather than throwing across a whole list activation - one stubborn service should not
    /// stop the other nine from being applied.
    /// </summary>
    public static class ServiceOps
    {
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

        /// <summary>Applies every item in <paramref name="list"/> against the current machine,
        /// then records the activation time. Never throws - failures are reported per item.</summary>
        public static List<ServiceActionResult> Activate(ServiceListDefinition list, Baseline baseline)
        {
            var results = new List<ServiceActionResult>();
            foreach (var item in list.Items)
                results.Add(ApplyItem(item, baseline));
            ListStore.MarkActivated(list.Name);
            return results;
        }

        public static ServiceActionResult ApplyItem(ServiceListItem item, Baseline baseline)
        {
            string name = item.ServiceName;
            string display = item.DisplayName;
            try
            {
                using var sc = new ServiceController(name);
                try { display = sc.DisplayName; } catch { /* keep the cached display name */ }

                switch (item.Action)
                {
                    case ServiceTargetAction.Stop:
                        SetStartType(name, item.TargetStartType, item.TargetDelayedAutoStart);
                        sc.Refresh();
                        StopIfRunning(sc);
                        return Ok(name, display,
                            $"Start type set to {Describe(item.TargetStartType, item.TargetDelayedAutoStart)}; stopped.");

                    case ServiceTargetAction.Start:
                        SetStartType(name, item.TargetStartType, item.TargetDelayedAutoStart);
                        sc.Refresh();
                        StartIfNotRunning(sc);
                        return Ok(name, display,
                            $"Start type set to {Describe(item.TargetStartType, item.TargetDelayedAutoStart)}; started.");

                    case ServiceTargetAction.RestoreToBaseline:
                        if (!baseline.Services.TryGetValue(name, out var snap))
                            return Fail(name, display, "No baseline snapshot recorded for this service - can't restore.");
                        SetStartType(name, snap.StartType, snap.DelayedAutoStart);
                        sc.Refresh();
                        if (snap.WasRunning) StartIfNotRunning(sc); else StopIfRunning(sc);
                        return Ok(name, display,
                            $"Restored to baseline: {Describe(snap.StartType, snap.DelayedAutoStart)}, " +
                            (snap.WasRunning ? "running." : "stopped."));

                    default:
                        return Fail(name, display, "Unrecognized action.");
                }
            }
            catch (Exception ex)
            {
                return Fail(name, display, ex.Message);
            }
        }

        // ---- start type -------------------------------------------------- //

        /// <summary>There is no managed API to change a service's start type - only
        /// <c>sc.exe config</c> (or a direct registry write, which sc.exe does more safely).
        /// The space after "start=" is not a typo: sc.exe's option parser requires it.</summary>
        private static void SetStartType(string serviceName, ServiceStartMode mode, bool delayedAuto)
        {
            string arg = mode switch
            {
                ServiceStartMode.Boot => "boot",
                ServiceStartMode.System => "system",
                ServiceStartMode.Automatic => delayedAuto ? "delayed-auto" : "auto",
                ServiceStartMode.Manual => "demand",
                ServiceStartMode.Disabled => "disabled",
                _ => "demand",
            };
            RunSc($"config \"{serviceName}\" start= {arg}");
        }

        private static void RunSc(string arguments)
        {
            var psi = new ProcessStartInfo("sc.exe", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start sc.exe.");
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(15000);
            if (proc.ExitCode != 0)
                throw new InvalidOperationException(
                    $"sc.exe {arguments} failed (exit {proc.ExitCode}): {(stdout + stderr).Trim()}");
        }

        private static string Describe(ServiceStartMode mode, bool delayedAuto) =>
            mode == ServiceStartMode.Automatic && delayedAuto ? "Automatic (Delayed Start)" : mode.ToString();

        // ---- stop / start with basic dependency handling ------------------ //

        /// <summary>Stops <paramref name="sc"/> if it isn't already stopped, stopping any
        /// currently-running dependent services first (the SCM refuses to stop a service other
        /// running services depend on). Dependent-stop failures are swallowed - the main Stop()
        /// call below surfaces a clear error if one of them actually blocked it.</summary>
        private static void StopIfRunning(ServiceController sc)
        {
            sc.Refresh();
            if (sc.Status == ServiceControllerStatus.Stopped) return;

            foreach (var dep in sc.DependentServices)
            {
                using (dep)
                {
                    try
                    {
                        dep.Refresh();
                        if (dep.Status != ServiceControllerStatus.Stopped)
                        {
                            dep.Stop();
                            dep.WaitForStatus(ServiceControllerStatus.Stopped, DefaultTimeout);
                        }
                    }
                    catch { /* best-effort */ }
                }
            }

            if (sc.Status == ServiceControllerStatus.StartPending)
                sc.WaitForStatus(ServiceControllerStatus.Running, DefaultTimeout);

            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, DefaultTimeout);
        }

        /// <summary>Starts <paramref name="sc"/> if it isn't already running. Starting its
        /// dependencies is left to the SCM (which does this automatically for services that are
        /// themselves startable) - Faster does not reach into unrelated services' configuration
        /// to force a dependency chain open.</summary>
        private static void StartIfNotRunning(ServiceController sc)
        {
            sc.Refresh();
            if (sc.Status == ServiceControllerStatus.Running) return;

            if (sc.Status == ServiceControllerStatus.StopPending)
                sc.WaitForStatus(ServiceControllerStatus.Stopped, DefaultTimeout);

            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, DefaultTimeout);
        }

        private static ServiceActionResult Ok(string name, string display, string message) =>
            new() { ServiceName = name, DisplayName = display, Success = true, Message = message };

        private static ServiceActionResult Fail(string name, string display, string message) =>
            new() { ServiceName = name, DisplayName = display, Success = false, Message = message };
    }
}
