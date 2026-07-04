// Copyright (c) 2026 LanDen Labs - Dennis Lang

using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace Faster
{
    /// <summary>
    /// The right panel's "About" tab - app icon/name/version/description/legal text, plus a
    /// button to open the folder where <see cref="AppPaths"/> keeps <c>baseline.json</c> and the
    /// <c>user_lists\</c> subfolder (one file per saved list). Content mirrors cs-b4browse's
    /// AboutForm (icon beside the title,
    /// version, copyright/license footer), but lives inline as a tab instead of a modal dialog,
    /// and skips the update-check/GitHub-link machinery cs-b4browse's AboutForm has - Faster has
    /// no <c>UpdateCheck</c> equivalent. Version/BuildDate/Copyright come from
    /// <see cref="AppInfo"/> (same single source of truth as the MainForm title bar); the
    /// description still comes straight from the assembly's AssemblyDescription attribute, which
    /// MSBuild generates from Faster.csproj's &lt;Description&gt; - AppInfo has no property for
    /// it since only the title bar needs it. The icon image comes from <see cref="AppIcon"/>.
    /// </summary>
    public sealed class AboutPanel : UserControl
    {
        private readonly Label _openFolderStatus = new();

        public AboutPanel()
        {
            Dock = DockStyle.Fill;

            var asm = Assembly.GetExecutingAssembly();
            string title = asm.GetCustomAttribute<AssemblyTitleAttribute>()?.Title ?? AppInfo.Product;
            string version = AppInfo.Version;
            string buildDate = AppInfo.BuildDate;
            string description = asm.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description ?? "";
            string copyright = AppInfo.Copyright;
            string company = AppInfo.Company;

            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(16, 20, 16, 12),
            };

            Label AddLabel(string text, Font font, Color? color = null, int topGap = 0)
            {
                var lbl = new Label
                {
                    Text = text,
                    Font = font,
                    AutoSize = true,
                    MaximumSize = new Size(260, 0),
                    Margin = new Padding(0, topGap, 0, 0),
                    ForeColor = color ?? SystemColors.WindowText,
                };
                flow.Controls.Add(lbl);
                return lbl;
            }

            // Header row: the icon (if it loaded) on the left, title + version stacked to its
            // right - same shape as cs-b4browse's About dialog. Falls back to just the text,
            // flush left, if AppIcon.LoadImage() couldn't find/read icon.png.
            const int iconSize = 56, textLeft = 64;
            Image? icon = AppIcon.LoadImage();
            int titleLeft = icon != null ? textLeft : 0;

            var headerPanel = new Panel { Width = 260, Height = iconSize, Margin = new Padding(0) };
            if (icon != null)
            {
                headerPanel.Controls.Add(new PictureBox
                {
                    Image = icon,
                    SizeMode = PictureBoxSizeMode.Zoom,
                    Bounds = new Rectangle(0, 0, iconSize, iconSize),
                });
            }
            var titleLabel = new Label
            {
                Text = title, Font = new Font("Segoe UI", 13f, FontStyle.Bold),
                AutoSize = true, Location = new Point(titleLeft, 2),
            };
            headerPanel.Controls.Add(titleLabel);
            headerPanel.Controls.Add(new Label
            {
                Text = $"Version {version}   ({buildDate})", Font = new Font("Segoe UI", 9f),
                ForeColor = SystemColors.GrayText, AutoSize = true,
                Location = new Point(titleLeft, titleLabel.Bottom + 4),
            });
            flow.Controls.Add(headerPanel);

            if (!string.IsNullOrEmpty(description))
                AddLabel(description, new Font("Segoe UI", 9f), topGap: 12);

            AddLabel(
                $"© {copyright}" + (string.IsNullOrEmpty(company) ? "" : $"  ·  {company}") +
                "\nLicensed under the Apache License 2.0.",
                new Font("Segoe UI", 8.5f), SystemColors.GrayText, topGap: 16);

            var openFolderBtn = new Button
            {
                Text = "Open Settings Folder",
                Width = 220,
                Height = 30,
                Margin = new Padding(0, 20, 0, 4),
            };
            openFolderBtn.Click += (_, _) => OpenSettingsFolder();
            flow.Controls.Add(openFolderBtn);

            _openFolderStatus.AutoSize = true;
            _openFolderStatus.MaximumSize = new Size(260, 0);
            _openFolderStatus.ForeColor = SystemColors.GrayText;
            _openFolderStatus.Font = new Font("Segoe UI", 8.5f);
            flow.Controls.Add(_openFolderStatus);

            Controls.Add(flow);
        }

        /// <summary>
        /// Opens Windows Explorer at <see cref="AppPaths.RootDir"/> (%LocalAppData%\Faster,
        /// where baseline.json lives and the user_lists\ subfolder holds one file per saved
        /// list) - same "just show me the folder" affordance as
        /// cs-b4browse's repo-link button, but pointed at local settings instead of GitHub.
        /// AppPaths.RootDir itself creates the directory if it doesn't exist yet, so this always
        /// has somewhere valid to open.
        /// </summary>
        private void OpenSettingsFolder()
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.RootDir}\"") { UseShellExecute = true });
                _openFolderStatus.Text = "";
            }
            catch (Exception ex)
            {
                _openFolderStatus.ForeColor = Color.FromArgb(200, 80, 80);
                _openFolderStatus.Text = "Could not open Explorer: " + ex.Message;
            }
        }
    }
}
