// Copyright (c) 2026 LanDen Labs - Dennis Lang

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
    ///             Faster.exe --admin                     Force the UAC elevation prompt (see below).
    ///             Faster.exe --help                      Show usage and exit.
    ///
    /// --activate/--restore/--recapture-baseline change a service's configuration and need
    /// Administrator, so Main checks elevation for those and, if not elevated, relaunches with a
    /// UAC prompt (rather than declaring requireAdministrator in app.manifest, which hit a
    /// side-by-side activation error on some SDKs - see app.manifest's comment).
    /// --help/--list/--show/--delete/--baseline are read-only and never elevate on their own, so
    /// they always print straight to the calling console with no UAC prompt in the way.
    /// --admin can be added alongside any command (e.g. from a batch file) to force that UAC
    /// prompt/relaunch regardless of which command follows - a no-op if already elevated. Mainly
    /// useful for making a script's intent to elevate explicit rather than relying on which
    /// specific command happens to auto-elevate, or for forcing elevation on an otherwise
    /// read-only command. See the elevation gate near the top of Main for how it's wired in.
    ///
    /// The GUI itself launches unelevated (asInvoker) regardless of the current process's rights
    /// - it has its own on-demand affordances (a toolbar "Run as Admin" button, a bottom-bar
    /// elevation indicator, and a relaunch prompt if you click Activate/Restore while
    /// unelevated) instead of a UAC prompt blocking every startup. See Elevation.cs and MainForm.
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
            // mutating command - --activate/--restore/--recapture-baseline - would trip it:
            // Elevation.RelaunchAsAdmin spawns a brand new process whose "parent" is the UAC
            // broker, not this terminal, so that new process's own EnsureConsole() can't reattach
            // and its output goes nowhere. Read-only commands must never reach that relaunch at
            // all - see the elevation gate below.
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

            // ---- --admin: forces the UAC elevation prompt/relaunch up front, before ANY command
            // (read-only or mutating) runs, regardless of which command follows it - unlike the
            // isMutatingCommand check further down, which only elevates for the three commands
            // that specifically need it. Exists for batch/script use: --activate/--restore/
            // --recapture-baseline already auto-elevate on their own, so --admin's value there is
            // just making the script's intent explicit, but it also lets an otherwise read-only
            // command (e.g. --show) be forced to run elevated. No-op if already elevated - nothing
            // to relaunch. Deliberately placed after EnsureConsole() (so this instance is still
            // attached to the caller's console for as long as it's actually running) but before
            // the read-only dispatch below, since forceAdmin can accompany a read-only command
            // too. A forced relaunch here hits the same "new process can't reattach to the
            // caller's console" limitation as the mutating commands' own elevation check below -
            // run from an already-elevated prompt if you need to see that command's output inline.
            if (args.Any(a => a.Equals("--admin", StringComparison.OrdinalIgnoreCase)) && !Elevation.IsAdmin)
            {
                if (!Elevation.RelaunchAsAdmin())
                {
                    ShowElevationMessage("Administrator privileges were requested (--admin), " +
                        "and the elevation prompt was cancelled or failed.");
                }
                return;   // either a new elevated process is taking over, or the user cancelled/it
                          // failed and a message was already shown - this process is done either way.
            }

            // ---- Read-only commands: just inspect saved files / live service config, never
            // change anything, so they run immediately with no elevation check at all (unless
            // --admin forced one above). ---- //
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

            // ---- Everything below here can change a service's start type or running state -
            // THIS is the only path that needs Administrator, so it's the only path that checks
            // elevation. The GUI is deliberately NOT included here: it launches unelevated
            // regardless, and elevates on demand from inside the window instead (see Elevation.cs
            // and MainForm's "Run as Admin" button / Activate/Restore relaunch prompt). ---- //
            bool isMutatingCommand = Arg("--activate") != null
                || args.Any(a => a.Equals("--restore", StringComparison.OrdinalIgnoreCase))
                || args.Any(a => a.Equals("--recapture-baseline", StringComparison.OrdinalIgnoreCase));

            if (isMutatingCommand && !Elevation.IsAdmin)
            {
                if (!Elevation.RelaunchAsAdmin())
                {
                    ShowElevationMessage("Administrator privileges are required for this command, " +
                        "and the elevation prompt was cancelled or failed.");
                }
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
            // --admin is excluded from this check: it's a modifier, not a command on its own, and
            // by this point it's already done its job (either this process is already elevated,
            // or the gate above already relaunched/returned) - "Faster.exe --admin" with nothing
            // else is a legitimate way to launch the GUI pre-elevated, not an error.
            var leftover = args.Skip(1)
                .Where(a => !a.Equals("--admin", StringComparison.OrdinalIgnoreCase)).ToList();
            if (leftover.Count > 0)
            {
                Console.Error.WriteLine($"Unrecognized argument(s): {string.Join(" ", leftover)}");
                Console.Error.WriteLine("Run 'Faster.exe --help' for usage.");
                Environment.Exit(2);
                return;
            }

            // No arguments left (aside from a possible --admin, already handled above) - launch
            // the GUI, unelevated unless --admin forced it above (see Elevation.cs/MainForm for
            // how it elevates on demand from inside the window otherwise). Capture the baseline
            // on first run before the window shows, so the "current config" grid has something
            // to compare against.
            ApplicationConfiguration.Initialize();
            Theme.Load();
            Theme.Apply(Theme.Current);   // apply the saved light/dark mode before any window is shown
            try { BaselineStore.LoadOrCapture(); }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not read or capture the baseline:\n{ex.Message}",
                    "Faster", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            Application.Run(new MainForm());
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
        /// --baseline) never trigger a relaunch on their own - they run right here, already
        /// attached. --activate/--restore/--recapture-baseline elevate automatically, and --admin
        /// can force a relaunch for any command; run from an already-elevated prompt if you need
        /// one of those commands' output to appear inline instead of nowhere.
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
  Faster.exe --admin                Force the UAC elevation prompt (see NOTES) - can combine
                                     with any other command, e.g. --admin --activate <name>.
  Faster.exe --help                 Show this help and exit.

NOTES:
  --activate/--restore/--recapture-baseline change a service's configuration and request
  Administrator - relaunching with a UAC prompt if not already elevated.
  --help/--list/--show/--delete/--baseline are read-only and never prompt for elevation on
  their own.
  --admin forces that UAC prompt/relaunch up front regardless of which command follows it (a
  no-op if already elevated) - useful from a batch file to make elevation explicit, or to force
  an otherwise read-only command to run elevated. ""Faster.exe --admin"" with no other command
  launches the GUI pre-elevated.
  The GUI always launches unelevated and elevates on demand from inside the window instead
  (a toolbar ""Run as Admin"" button; Activate/Restore also offer to relaunch as admin first
  if you're not already elevated) - unless started with --admin, above.
  Run from an already-elevated prompt if you want an elevating CLI command's output to appear
  inline in that same terminal (a UAC relaunch spawns a separate process that can't reattach
  to it).

EXAMPLES:
  Faster.exe --list
  Faster.exe --show ""Gaming Mode""
  Faster.exe --activate ""Gaming Mode""
  Faster.exe --restore
  Faster.exe --admin --activate ""Gaming Mode""");
        }
    }
}
