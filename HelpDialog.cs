// Copyright (c) 2026 LanDen Labs - Dennis Lang

using System;
using System.Diagnostics;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Faster
{
    /// <summary>
    /// A read-only feature-tour dialog opened from the toolbar's "Help" button. Content mirrors
    /// README.md's intro and "How it works" section, deliberately skipping the CLI ("Build &amp;
    /// run") and "Architecture" sections - those are for developers reading the repo, not end
    /// users clicking Help in the running app. Uses a plain RichTextBox with DetectUrls so the
    /// GitHub/README/resource links below are clickable without pulling in a WebBrowser control
    /// for what's otherwise static text.
    /// </summary>
    internal sealed class HelpDialog : Form
    {
        private const string RepoUrl = "https://github.com/landenlabs/cs-faster";
        private const string ReadmeUrl = "https://github.com/landenlabs/cs-faster/blob/main/README.md";

        public HelpDialog()
        {
            Text = "Faster - Help";
            if (AppIcon.LoadIcon() is Icon appIcon) Icon = appIcon;
            Width = 640;
            Height = 620;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = true;

            var text = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                DetectUrls = true,
                Font = new Font("Segoe UI", 9.5f),
                Text = BuildHelpText(),
            };
            // Opens the clicked URL in the user's default browser rather than navigating inside
            // the RichTextBox (which has no browser engine anyway) - same pattern already used
            // for the "Search web for this service" row menu item (MainForm.SearchSelectedService).
            text.LinkClicked += (_, e) =>
            {
                if (e.LinkText == null) return;   // LinkText is nullable; nothing to open if so
                try
                {
                    Process.Start(new ProcessStartInfo(e.LinkText) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"Could not open the browser: {ex.Message}", "Faster",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };

            var closeBtn = new Button { Text = "Close", DialogResult = DialogResult.OK };
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44 };
            closeBtn.Left = bottom.Width - closeBtn.Width - 12;
            closeBtn.Top = 8;
            closeBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            bottom.Controls.Add(closeBtn);

            Controls.Add(text);
            Controls.Add(bottom);
            AcceptButton = closeBtn;
            CancelButton = closeBtn;

            // Read Theme.Current once, here at construction - same reasoning as NewListDialog:
            // this dialog is modal, so the toolbar's theme-toggle button can't be reached (or
            // change anything) while it's open.
            Theme.ApplyToTree(this);

            // ApplyToTree just set the whole box's default ForeColor - style the detected URLs
            // explicitly afterward (blue + underline) rather than relying on DetectUrls' own
            // rendering, which varies by Windows version and would otherwise just inherit that
            // same default text color.
            StyleLinks(text);
        }

        /// <summary>The RichTextBox's native scrollbar only picks up ApplyToTree's
        /// SetWindowTheme call once its handle actually exists - see MainForm's own OnShown
        /// override for the same reason.</summary>
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Theme.ApplyScrollbarTheme(this);
        }

        /// <summary>Colors every detected http(s) URL in <paramref name="rtb"/> blue + underlined
        /// (<see cref="Theme.Link"/>) via per-run SelectionColor/SelectionFont, rather than relying
        /// on DetectUrls' own link rendering - that only guarantees the text is clickable, not that
        /// it looks like a link, and its actual appearance varies by Windows version/theme.</summary>
        private static void StyleLinks(RichTextBox rtb)
        {
            foreach (Match m in Regex.Matches(rtb.Text, @"https?://\S+"))
            {
                rtb.Select(m.Index, m.Length);
                rtb.SelectionColor = Theme.Link;
                rtb.SelectionFont = new Font(rtb.Font, FontStyle.Underline);
            }
            rtb.Select(0, 0);   // clear the selection highlight left over from the loop above
        }

        private static string BuildHelpText() => string.Join(Environment.NewLine, new[]
        {
            "Faster - Windows Service Switcher",
            "",
            "Save your Windows machine's current service configuration as a baseline, then build",
            "named lists that stop-and-disable or start-and-restore groups of services - a quick way",
            "to switch parts of Windows on or off to tune performance (e.g. a \"Gaming Mode\" list",
            "disables indexing/telemetry services; activating it again later, or a \"Restore\" list,",
            "puts them back).",
            "",
            "How it works",
            "",
            "- First run captures a baseline: every service's start type, its \"Delayed Start\" flag,",
            "  whether it has any trigger-start events (informational only - triggers are never",
            "  modified), and whether it was running.",
            "",
            "- Named lists give each service its own action: Stop, Start, or Restore to baseline.",
            "  A \"Set all rows to\" bar bulk-fills the common case where every service in the list",
            "  gets the same action; individual rows can still be changed by hand for a mixed list.",
            "",
            "- Check the services you want in the grid (the checkbox above the checkbox column",
            "  selects/clears every currently visible row at once), then pick the always-present",
            "  \"Save n checked service(s) as...\" row to name and save them as a new list.",
            "  Selecting an existing list checks exactly its services and enables Activate, Update,",
            "  Details, and Delete for that list. Update re-saves the list from whatever's currently",
            "  checked, with no extra confirmation. Press Esc while the list has focus to clear the",
            "  selection - checked services are left as they are.",
            "",
            "- Activating a list applies every item and reports success/failure per service - one",
            "  stubborn service doesn't block the rest of the list.",
            "",
            "- \"Restore All to Baseline\" restores every service on the machine to the baseline in",
            "  one click - no saved list required.",
            "",
            "- The Metrics button adds PID/Memory/Handles/Threads/CPU %/Process columns and samples",
            "  every running service's host process; press it again to re-sample. Because Windows",
            "  often groups several services into one host process, a \"shared xN\" note means the",
            "  numbers are for the whole process, not that one service alone.",
            "",
            "- Some actions (Activate, Restore All, Re-capture Baseline) need Administrator. The",
            "  \"Run as Admin\" button, the purple dot on those buttons, and the status bar all flag",
            "  this - clicking one while unelevated offers to relaunch as Administrator first.",
            "",
            "- The half-black/half-white circle just left of this Help button toggles light/dark",
            "  theme; the choice is remembered for next launch.",
            "",
            "Links",
            "",
            $"GitHub repository: {RepoUrl}",
            $"Full README (for more information): {ReadmeUrl}",
            "",
            "Resources",
            "",
            "A few stable Microsoft Learn pages on how Windows services work:",
            "Services overview: https://learn.microsoft.com/en-us/windows/win32/services/services",
            "About services: https://learn.microsoft.com/en-us/windows/win32/services/about-services",
            "sc config reference: https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/sc-config",
        });
    }
}
