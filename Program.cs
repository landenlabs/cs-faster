// Copyright (c) 2026 LanDen Labs - Dennis Lang

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows.Forms;

namespace Faster
{
    /// <summary>
    /// Faster - save the machine's current service configuration as a baseline, then create
    /// named lists that stop-and-disable or start-and-restore groups of services, to quickly
    /// switch parts of Windows on or off for performance.
    ///
    /// GUI:        Faster.exe
    /// Headless:   Faster.exe --list                    List saved service lists.
    ///             Faster.exe --show &lt;name&gt;              Show one list's services + actions.
    ///             Faster.exe --activate &lt;name&gt;          Apply a saved list.
    ///             Faster.exe --restore                   Restore EVERY service to the baseline.
    ///             Faster.exe --delete &lt;name&gt;            Delete a saved list.
    ///             Faster.exe --baseline                 Show (capturing if missing) the baseline.
    ///             Faster.exe --recapture-baseline        Force a fresh baseline capture.
    ///             Faster.exe --help                      Show usage and exit.
    ///
    /// --activate/--restore/--recapture-baseline (and the GUI) change a service's configuration
    /// and need Administrator, so Main checks elevation only for those and, if not elevated,
    /// relaunches itself with a UAC prompt (rather than declaring requireAdministrator in
    /// app.manifest, which hit a side-by-side activation error on some SDKs - see app.manifest's
    /// comment). --help/--list/--show/--delete/--baseline are read-only and never elevate, so
    /// they always print straight to the calling console with no UAC prompt in the way.
    ///
    /// Author: Dennis Lang - LanDen Labs - 2026
    /// </summary>
    static class Program
    {
        [STAThread]
        static void Main()
        {
            // Attach to the calling console FIRST, unconditionally, before anything else has a
            // chance to relaunch this process. If the elevation check below ran first, every
            // command - including --help - would trip it: TryRelaunchElevated spawns a brand
            // new process whose "parent" is the UAC broker, not this terminal, so that new
            // process's own EnsureConsole() can't reattach and its output goes nowhere. Read-only
            // commands must never reach that relaunch at all - see the elevation gate below.
            EnsureConsole();

            var args = Environment.GetCommandLineArgs();

            if (args.Any(a => a is "--help" or "-h" or "/?" or "-?" or "/help"))
            {
                PrintHelp();
                return;
            }

            string? Arg(string flag)
            {
                int i = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
                return (i >= 0 && i + 1 < args.Length) ? args[i + 1] : null;
            }

            // ---- Read-only commands: just inspect saved files / live service config, never
            // change anything, so they run immediately with no elevation check at all. ---- //
            try
            {
                string? name;
                if ((name = Arg("--show")) != null) { Environment.Exit(CliRunner.Show(name)); return; }
                if ((name = Arg("--delete")) != null) { Environment.Exit(CliRunner.Delete(name)); return; }
                if (args.Any(a => a.Equals("--list", StringComparison.OrdinalIgnoreCase)))
                { Environment.Exit(CliRunner.List()); return; }
                if (args.Any(a => a.Equals("--baseline", StringComparison.OrdinalIgnoreCase)))
                { Environment.Exit(CliRunner.Baseline(recapture: false)); return; }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Command failed: " + ex.Message);
                Environment.Exit(1);
                return;
            }

            // ---- Everything below here can change a service's start type or running state (or
            // is the GUI, where the user might click Activate at any time) - THIS is the only
            // path that needs Administrator, so it's the only path that checks elevation. ---- //
            bool isMutatingCommand = Arg("--activate") != null
                || args.Any(a => a.Equals("--restore", StringComparison.OrdinalIgnoreCase))
                || args.Any(a => a.Equals("--recapture-baseline", StringComparison.OrdinalIgnoreCase));
            bool isGuiLaunch = !isMutatingCommand && args.Length <= 1;

            if ((isMutatingCommand || isGuiLaunch) && !IsElevated())
            {
                TryRelaunchElevated();
                return;   // either a new elevated process is taking over, or the user cancelled/it
                          // failed and a message was already shown - this process is done either way.
            }

            try
            {
                string? name;
                if ((name = Arg("--activate")) != null) { Environment.Exit(CliRunner.Activate(name)); return; }
                if (args.Any(a => a.Equals("--restore", StringComparison.OrdinalIgnoreCase)))
                { Environment.Exit(CliRunner.RestoreBaseline()); return; }
                if (args.Any(a => a.Equals("--recapture-baseline", StringComparison.OrdinalIgnoreCase)))
                { Environment.Exit(CliRunner.Baseline(recapture: true)); return; }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Command failed: " + ex.Message);
                Environment.Exit(1);
                return;
            }

            // Anything left over here means the user passed something we didn't recognize (a
            // mistyped flag, e.g.) - fail loudly with a non-zero exit code instead of silently
            // launching the GUI, which would otherwise sit there unattended in a batch/CI run.
            if (args.Length > 1)
            {
                Console.Error.WriteLine($"Unrecognized argument(s): {string.Join(" ", args.Skip(1))}");
                Console.Error.WriteLine("Run 'Faster.exe --help' for usage.");
                Environment.Exit(2);
                return;
            }

            // No arguments at all, and elevation is already confirmed above - launch the GUI.
            // Capture the baseline on first run before the window shows, so the "current config"
            // grid has something to compare against.
            ApplicationConfiguration.Initialize();
            try { BaselineStore.LoadOrCapture(); }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not read or capture the baseline:\n{ex.Message}",
                    "Faster", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            Application.Run(new MainForm());
        }

        /// <summary>True if the current process token has the Administrator role.</summary>
        private static bool IsElevated()
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        /// <summary>
        /// Relaunches this same exe (with the same command-line arguments) via ShellExecute's
        /// "runas" verb, which makes Windows show the UAC consent prompt - the runtime
        /// equivalent of a manifest's requestedExecutionLevel, used here instead of a manifest
        /// declaration because that hit a side-by-side activation error on some SDKs (see
        /// app.manifest). Returns true if the elevated process was started (the caller should
        /// exit either way - this original, unelevated process has nothing further to do).
        /// </summary>
        private static bool TryRelaunchElevated()
        {
            try
            {
                string exe = Environment.ProcessPath
                    ?? Process.GetCurrentProcess().MainModule?.FileName
                    ?? throw new InvalidOperationException("Could not determine this executable's path.");

                var psi = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
                foreach (var a in Environment.GetCommandLineArgs().Skip(1)) psi.ArgumentList.Add(a);

                Process.Start(psi);
                return true;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)   // ERROR_CANCELLED - user clicked "No"
            {
                ShowElevationMessage("Administrator privileges are required to run Faster, and the prompt was cancelled.");
                return false;
            }
            catch (Exception ex)
            {
                ShowElevationMessage($"Could not restart Faster as Administrator: {ex.Message}");
                return false;
            }
        }

        /// <summary>Reports an elevation failure both ways, since at this point in Main we don't
        /// yet know whether the user is at a console (headless use) or double-clicked from
        /// Explorer (GUI use) - one of the two will actually be seen.</summary>
        private static void ShowElevationMessage(string message)
        {
            try { Console.Error.WriteLine(message); } catch { /* no console */ }
            try { MessageBox.Show(message, "Faster", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            catch { /* no GUI available (rare) */ }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);
        private const int ATTACH_PARENT_PROCESS = -1;

        /// <summary>
        /// Faster is a GUI-subsystem (WinExe) app, so headless output is otherwise discarded.
        /// Attach to the parent console (if any) and rebind stdout/stderr so --list/--show/
        /// --activate/--help text appears in the terminal. A no-op if there's no console to
        /// attach to. Called FIRST in Main, before any elevation check, which matters: a UAC
        /// relaunch spawns a brand-new process whose "parent" is the elevation broker, not your
        /// terminal, so THAT process's own call to this method can't reattach and its output
        /// goes nowhere. That's exactly why read-only commands (--help, --list, --show, --delete,
        /// --baseline) never trigger a relaunch - they run right here, already attached. Only
        /// --activate/--restore/--recapture-baseline/GUI elevate, so only those can hit this
        /// limitation; run from an already-elevated prompt for their output to appear inline.
        /// </summary>
        private static void EnsureConsole()
        {
            try
            {
                if (!AttachConsole(ATTACH_PARENT_PROCESS)) return;
                var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                Console.SetOut(stdout);
                var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
                Console.SetError(stderr);
            }
            catch { /* no parent console to attach to */ }
        }

        static void PrintHelp()
        {
            Console.WriteLine(@"Faster - switch groups of Windows services on/off for performance

USAGE:
  Faster.exe                        Launch the GUI (default; no arguments).
  Faster.exe --list                 List saved service lists.
  Faster.exe --show <name>          Show one list's services and planned actions.
  Faster.exe --activate <name>      Apply a saved list now.
  Faster.exe --restore              Restore EVERY service to the baseline - no saved list needed.
  Faster.exe --delete <name>        Delete a saved list.
  Faster.exe --baseline             Show the baseline (captures one first if missing).
  Faster.exe --recapture-baseline   Force a fresh baseline capture, overwriting the old one.
  Faster.exe --help                 Show this help and exit.

NOTES:
  --activate/--restore/--recapture-baseline (and the GUI) change a service's configuration
  and request Administrator - relaunching itself with a UAC prompt if not already elevated.
  --help/--list/--show/--delete/--baseline are read-only and never prompt for elevation.
  Run from an already-elevated prompt if you want the elevating commands' output to appear
  inline in that same terminal (a UAC relaunch spawns a separate process that can't reattach
  to it).

EXAMPLES:
  Faster.exe --list
  Faster.exe --show ""Gaming Mode""
  Faster.exe --activate ""Gaming Mode""
  Faster.exe --restore");
        }
    }
}
