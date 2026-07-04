// Copyright (c) 2026 LanDen Labs - Dennis Lang

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace Faster
{
    /// <summary>
    /// Administrator-elevation helpers - mirrors cs-b4browse's Elevation.cs. The app ships with
    /// an <c>asInvoker</c> manifest, so the GUI runs unelevated by default and elevates only on
    /// demand (the toolbar's "Run as Admin" button, or being prompted the first time an admin
    /// action - Activate/Restore - is attempted while unelevated). The CLI's mutating commands
    /// (<c>--activate</c>/<c>--restore</c>/<c>--recapture-baseline</c>) still elevate
    /// automatically, since there's no interactive button to offer there, and the CLI's
    /// <c>--admin</c> flag can force the same relaunch for any command - see Program.cs.
    /// </summary>
    public static class Elevation
    {
        /// <summary>True when the current process is running with administrator rights.</summary>
        public static bool IsAdmin { get; } = ComputeIsAdmin();

        private static bool ComputeIsAdmin()
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        /// <summary>
        /// Relaunches this executable elevated via the UAC "runas" verb, passing through the
        /// same command-line arguments the current process was started with. Returns true if the
        /// new (elevated) process started - the caller should then exit this instance. Returns
        /// false when the user declines the UAC prompt or the launch otherwise fails.
        /// </summary>
        public static bool RelaunchAsAdmin()
        {
            string? exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe)) return false;

            var psi = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
            foreach (var a in Environment.GetCommandLineArgs()[1..]) psi.ArgumentList.Add(a);

            try { Process.Start(psi); return true; }
            catch (Win32Exception) { return false; }   // user declined the UAC prompt
            catch { return false; }
        }
    }
}
