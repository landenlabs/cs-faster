// Copyright (c) 2026 LanDen Labs - Dennis Lang

using System;
using System.Collections.Generic;
using System.IO;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Faster
{
    /// <summary>
    /// Loads/saves the one machine-wide <see cref="Baseline"/> snapshot and knows how to capture
    /// a fresh one by walking every service the SCM reports.
    /// </summary>
    public static class BaselineStore
    {
        // JsonStringEnumConverter so baseline.json reads "Automatic" rather than the numeric
        // enum value - it's meant to be human-inspectable, not just machine-readable.
        private static readonly JsonSerializerOptions JsonOptions = new()
            { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        public static bool Exists() => File.Exists(AppPaths.BaselinePath);

        /// <summary>Loads the saved baseline, or null if none has been captured yet.</summary>
        public static Baseline? Load()
        {
            if (!Exists()) return null;
            try
            {
                string json = File.ReadAllText(AppPaths.BaselinePath);
                var baseline = JsonSerializer.Deserialize<Baseline>(json, JsonOptions);
                if (baseline == null) return null;

                // Re-wrap with a case-insensitive comparer: System.Text.Json rebuilds the
                // dictionary with the default (ordinal, case-sensitive) comparer on deserialize,
                // discarding the one set at construction time.
                baseline.Services = new Dictionary<string, ServiceSnapshot>(
                    baseline.Services, StringComparer.OrdinalIgnoreCase);
                return baseline;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Could not read baseline at '{AppPaths.BaselinePath}': {ex.Message}", ex);
            }
        }

        public static void Save(Baseline baseline) =>
            AppPaths.WriteAtomic(AppPaths.BaselinePath, JsonSerializer.Serialize(baseline, JsonOptions));

        /// <summary>Loads the saved baseline if one exists; otherwise captures the machine's
        /// current service configuration, saves it, and returns that. This is the "on first run"
        /// behaviour - called once at startup by both the GUI and every headless command.</summary>
        public static Baseline LoadOrCapture()
        {
            var existing = Load();
            if (existing != null) return existing;
            var fresh = Capture();
            Save(fresh);
            return fresh;
        }

        /// <summary>Walks every service currently known to the SCM and records its start type,
        /// delayed-auto flag, trigger presence, and running state. Each service is isolated - one
        /// that can't be queried (rare, access-denied edge cases) is skipped rather than aborting
        /// the whole capture.</summary>
        public static Baseline Capture()
        {
            var baseline = new Baseline { CapturedUtc = DateTime.UtcNow };
            foreach (var sc in ServiceController.GetServices())
            {
                try
                {
                    using (sc)
                    {
                        var snap = new ServiceSnapshot
                        {
                            ServiceName = sc.ServiceName,
                            DisplayName = sc.DisplayName,
                            StartType = sc.StartType,
                            WasRunning = sc.Status == ServiceControllerStatus.Running,
                            DelayedAutoStart = sc.StartType == ServiceStartMode.Automatic
                                && RegistryHelpers.GetDelayedAutoStart(sc.ServiceName),
                            HasTriggers = RegistryHelpers.HasTriggerStart(sc.ServiceName),
                            CapturedUtc = baseline.CapturedUtc,
                        };
                        baseline.Services[snap.ServiceName] = snap;
                    }
                }
                catch
                {
                    // Access denied or the service vanished mid-enumeration - skip it, don't
                    // let one bad entry blank out the whole baseline.
                }
            }
            return baseline;
        }
    }
}
