// Copyright (c) 2026 LanDen Labs - Dennis Lang

using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Faster
{
    /// <summary>
    /// App light/dark theme via WinForms <see cref="Application.SetColorMode"/> plus an explicit
    /// colour palette (the app sets many custom colours - grid cell shading, the admin purple
    /// dot/label, severity-tinted rows - that the system dark mode alone won't touch). Choice
    /// persisted to <c>%LocalAppData%\Faster\theme.txt</c>. Mirrors cs-b4browse's <c>Theme.cs</c>,
    /// minus that app's separate content-font-scaling feature, which Faster doesn't have.
    /// </summary>
    public static class Theme
    {
        public enum Mode { Light, Dark }

        public static Mode Current { get; private set; } = Mode.Light;

        /// <summary>Raised after the mode changes so MainForm can re-apply its colours live -
        /// modal dialogs (NewListDialog, HelpDialog) don't need this: they're built fresh each
        /// time they're opened and just read the current colours once, and MainForm's toolbar
        /// (where the toggle button lives) is unreachable while one of them is showing anyway.</summary>
        public static event Action? Changed;

        public static bool IsDark => Current == Mode.Dark;

        // ---- Palette - same shades cs-b4browse uses, for a consistent look across both apps. -- //
        public static Color Window => IsDark ? Color.FromArgb(32, 32, 34) : Color.White;
        public static Color Surface => IsDark ? Color.FromArgb(43, 43, 46) : Color.White;           // grids
        public static Color Panel => IsDark ? Color.FromArgb(50, 50, 54) : Color.FromArgb(238, 240, 243);
        public static Color Toolbar => IsDark ? Color.FromArgb(50, 50, 54) : Color.FromArgb(245, 245, 245);
        public static Color Text => IsDark ? Color.FromArgb(232, 232, 232) : Color.Black;
        public static Color Subtle => IsDark ? Color.FromArgb(165, 165, 165) : Color.FromArgb(70, 70, 70);
        public static Color GridLine => IsDark ? Color.FromArgb(64, 64, 68) : Color.FromArgb(230, 230, 230);
        public static Color ButtonBack => IsDark ? Color.FromArgb(62, 62, 66) : Color.FromArgb(240, 240, 240);
        public static Color ButtonBorder => IsDark ? Color.FromArgb(92, 92, 98) : Color.FromArgb(176, 176, 180);
        public static Color Link => IsDark ? Color.FromArgb(96, 162, 250) : Color.FromArgb(0, 102, 204);

        /// <summary>Paints a button explicitly (FlatStyle.System buttons don't revert from dark
        /// to light on their own).</summary>
        public static void StyleButton(Button b)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.UseVisualStyleBackColor = false;
            b.BackColor = ButtonBack;
            b.ForeColor = Text;
            b.FlatAppearance.BorderColor = ButtonBorder;
            b.FlatAppearance.BorderSize = 1;
        }

        /// <summary>Recursively applies <see cref="StyleButton"/> to every Button under a control.</summary>
        public static void StyleButtons(Control root)
        {
            foreach (Control c in root.Controls)
            {
                if (c is Button b) StyleButton(b);
                if (c.HasChildren) StyleButtons(c);
            }
        }

        /// <summary>
        /// One-call theming for a whole form: sets <paramref name="root"/>'s own BackColor, walks
        /// every descendant applying a sensible default by control TYPE (not by name), then styles
        /// buttons and native scrollbars. Faster's control tree is simple enough (no per-row
        /// severity tinting like cs-b4browse's nav tree) that a generic, type-driven pass covers
        /// every current control without needing each Panel/Label/etc. promoted to a named field
        /// just to be reachable here - callers are still free to override anything more specific
        /// afterward (e.g. a status-bar label that should stay <see cref="Subtle"/> instead of the
        /// default <see cref="Text"/>). Safe to call repeatedly (e.g. once at construction for a
        /// modal dialog, or every time <see cref="Changed"/> fires for a long-lived window).
        /// </summary>
        public static void ApplyToTree(Control root)
        {
            root.BackColor = Window;
            WalkForColors(root);
            StyleButtons(root);
            ApplyScrollbarTheme(root);
        }

        // Named distinctly from ApplyScrollbarTheme's own local "Walk" function below - a local
        // function CAN shadow a class-level method of the same name without conflict, but two
        // "Walk"s in one file is needless confusion for a reader either way.
        private static void WalkForColors(Control root)
        {
            foreach (Control c in root.Controls)
            {
                switch (c)
                {
                    case Button:
                        break;   // handled by StyleButtons, which needs FlatStyle set too
                    case DataGridView grid:
                        // EnableHeadersVisualStyles must be off, or Windows' own visual-style
                        // renderer overrides ColumnHeadersDefaultCellStyle below entirely.
                        grid.EnableHeadersVisualStyles = false;
                        grid.BackgroundColor = Surface;
                        grid.GridColor = GridLine;
                        grid.DefaultCellStyle.BackColor = Surface;
                        grid.DefaultCellStyle.ForeColor = Text;
                        grid.ColumnHeadersDefaultCellStyle.BackColor = Panel;
                        grid.ColumnHeadersDefaultCellStyle.ForeColor = Text;
                        break;
                    case RichTextBox rtb:
                        rtb.BackColor = Surface;
                        rtb.ForeColor = Text;
                        break;
                    case TextBox tb:
                        tb.BackColor = Surface;
                        tb.ForeColor = Text;
                        break;
                    case ComboBox cb:
                        cb.BackColor = Surface;
                        cb.ForeColor = Text;
                        break;
                    case TabControl tabs:
                        tabs.BackColor = Panel;
                        break;
                    case Label or CheckBox:
                        c.ForeColor = Text;
                        break;
                    // TableLayoutPanel/FlowLayoutPanel/SplitterPanel/TabPage all derive from
                    // Panel, so this one case covers every plain-container type in the app. Fully
                    // qualified (unlike every other case above) because a bare "Panel" here binds
                    // to Theme's OWN "Panel" *property* first (ordinary member lookup wins over
                    // type lookup for an unqualified pattern name) and fails to compile as a
                    // Color-vs-Control mismatch - the System.Windows.Forms prefix forces it to
                    // resolve as the type instead.
                    case System.Windows.Forms.Panel:
                        c.BackColor = Panel;
                        break;
                }
                if (c.HasChildren) WalkForColors(c);
            }
        }

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string? pszSubAppName, string? pszSubIdList);

        /// <summary>True for controls with native, OS-drawn scrollbars that ignore managed
        /// BackColor/ForeColor and instead follow the window theme.</summary>
        private static bool WantsScrollbarTheme(Control c) =>
            c is DataGridView or RichTextBox or ScrollBar
            || (c is ScrollableControl sc && sc.AutoScroll);

        /// <summary>
        /// Applies the matching dark/light <em>window</em> theme to every scrollable control under
        /// <paramref name="root"/> so their native scrollbars follow the theme (grids otherwise
        /// keep light scrollbars in dark mode - managed colours can't reach these non-client
        /// scrollbars, but <c>SetWindowTheme</c> can). Idempotent; skips not-yet-created handles.
        /// </summary>
        public static void ApplyScrollbarTheme(Control? root)
        {
            if (root == null) return;
            string sub = IsDark ? "DarkMode_Explorer" : "Explorer";
            void Walk(Control c)
            {
                if (c.IsHandleCreated && WantsScrollbarTheme(c))
                    try { SetWindowTheme(c.Handle, sub, null); } catch { /* best-effort */ }
                foreach (Control child in c.Controls) Walk(child);
            }
            Walk(root);
        }

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Faster", "theme.txt");

        public static void Load()
        {
            try
            {
                if (File.Exists(FilePath) &&
                    File.ReadAllText(FilePath).Trim().Equals("Dark", StringComparison.OrdinalIgnoreCase))
                    Current = Mode.Dark;
            }
            catch { /* default light */ }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, Current.ToString());
            }
            catch { /* non-fatal */ }
        }

        /// <summary>Applies a mode to the running app (best-effort live) without persisting.</summary>
        public static void Apply(Mode mode)
        {
            Current = mode;
            Application.SetColorMode(mode == Mode.Dark ? SystemColorMode.Dark : SystemColorMode.Classic);
            Changed?.Invoke();
        }

        /// <summary>Toggles light/dark, applies it, and persists the choice.</summary>
        public static Mode Toggle()
        {
            Apply(Current == Mode.Dark ? Mode.Light : Mode.Dark);
            Save();
            return Current;
        }
    }
}
