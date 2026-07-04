// Copyright (c) 2026 LanDen Labs - Dennis Lang

using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceProcess;

namespace Faster
{
    /// <summary>The headless (<c>--list</c> / <c>--show</c> / <c>--activate</c> / <c>--restore</c> /
    /// <c>--baseline</c>) command implementations. Called from <see cref="Program"/> after
    /// argument parsing.</summary>
    public static class CliRunner
    {
        public static int List()
        {
            var lists = ListStore.LoadAll();
            if (lists.Count == 0)
            {
                Console.WriteLine("No saved lists yet. Create one from the GUI first.");
                return 0;
            }

            Console.WriteLine($"{"NAME",-24} {"ITEMS",-6} {"CREATED",-20} {"LAST ACTIVATED",-20}");
            foreach (var l in lists.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
            {
                string created = l.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                string lastRun = l.LastActivatedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "(never)";
                Console.WriteLine($"{l.Name,-24} {l.Items.Count,-6} {created,-20} {lastRun,-20}");
            }
            return 0;
        }

        public static int Show(string name)
        {
            var list = ListStore.FindByName(ListStore.LoadAll(), name);
            if (list == null)
            {
                Console.Error.WriteLine($"No list named '{name}'. Run --list to see saved lists.");
                return 1;
            }

            Console.WriteLine($"{list.Name}  ({list.Items.Count} service(s))");
            Console.WriteLine($"Created:        {list.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
            Console.WriteLine($"Last activated: {(list.LastActivatedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "(never)")}");
            Console.WriteLine();
            foreach (var item in list.Items)
            {
                string action = item.Action switch
                {
                    ServiceTargetAction.Stop => $"Stop, start type -> {item.TargetStartType}",
                    ServiceTargetAction.Start => $"Start, start type -> {item.TargetStartType}" +
                        (item.TargetStartType == ServiceStartMode.Automatic && item.TargetDelayedAutoStart
                            ? " (Delayed Start)" : ""),
                    ServiceTargetAction.RestoreToBaseline => "Restore to baseline",
                    _ => "?",
                };
                Console.WriteLine($"  {item.ServiceName,-32} {action}");
            }
            return 0;
        }

        public static int Activate(string name)
        {
            var list = ListStore.FindByName(ListStore.LoadAll(), name);
            if (list == null)
            {
                Console.Error.WriteLine($"No list named '{name}'. Run --list to see saved lists.");
                return 1;
            }
            if (list.Items.Count == 0)
            {
                Console.WriteLine($"'{name}' has no services in it - nothing to do.");
                return 0;
            }

            var baseline = BaselineStore.LoadOrCapture();
            var results = ServiceOps.Activate(list, baseline);
            return PrintResults(results, $"'{name}' activated", $"'{name}' activated");
        }

        /// <summary>
        /// Restores EVERY service in the baseline to its captured configuration - no saved list
        /// needed. Builds a one-off, in-memory list (never written to disk via ListStore) of
        /// RestoreToBaseline items and runs it through the same engine <c>--activate</c> uses.
        /// </summary>
        public static int RestoreBaseline()
        {
            var baseline = BaselineStore.LoadOrCapture();
            if (baseline.Services.Count == 0)
            {
                Console.WriteLine("Baseline is empty - nothing to restore.");
                return 0;
            }

            var restoreList = new ServiceListDefinition
            {
                Name = "(restore-to-baseline)",
                CreatedUtc = DateTime.UtcNow,
                Items = baseline.Services.Values
                    .OrderBy(s => s.ServiceName, StringComparer.OrdinalIgnoreCase)
                    .Select(s => new ServiceListItem
                    {
                        ServiceName = s.ServiceName,
                        DisplayName = s.DisplayName,
                        Action = ServiceTargetAction.RestoreToBaseline,
                    }).ToList(),
            };

            Console.WriteLine($"Restoring {restoreList.Items.Count} service(s) to the baseline captured " +
                $"{baseline.CapturedUtc.ToLocalTime():yyyy-MM-dd HH:mm} ...");
            Console.WriteLine();

            var results = ServiceOps.Activate(restoreList, baseline);
            return PrintResults(results, "Restore to baseline", "Restore to baseline");
        }

        /// <summary>Prints one line per service (OK/FAIL + message) then a summary line, shared
        /// by --activate and --restore. Returns 0 if every item succeeded, else 1.</summary>
        private static int PrintResults(List<ServiceActionResult> results, string doneLabel, string failLabel)
        {
            int failed = 0;
            foreach (var r in results)
            {
                string tag = r.Success ? "[ OK ]  " : "[ FAIL ]";
                Console.WriteLine($"{tag} {r.DisplayName} ({r.ServiceName}) - {r.Message}");
                if (!r.Success) failed++;
            }

            Console.WriteLine();
            Console.WriteLine(failed == 0
                ? $"{doneLabel}: {results.Count} service(s) updated."
                : $"{failLabel}: {failed} of {results.Count} service(s) failed - see [ FAIL ] lines above.");
            return failed == 0 ? 0 : 1;
        }

        public static int Baseline(bool recapture)
        {
            var baseline = recapture ? BaselineStore.Capture() : BaselineStore.LoadOrCapture();
            if (recapture) BaselineStore.Save(baseline);

            Console.WriteLine(recapture ? "Baseline re-captured." : "Baseline:");
            Console.WriteLine($"Captured: {baseline.CapturedUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
            Console.WriteLine($"Services: {baseline.Services.Count}");
            return 0;
        }

        public static int Delete(string name)
        {
            if (!ListStore.Delete(name))
            {
                Console.Error.WriteLine($"No list named '{name}'.");
                return 1;
            }
            Console.WriteLine($"Deleted '{name}'.");
            return 0;
        }
    }
}
