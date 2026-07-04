// Copyright (c) 2026 LanDen Labs - Dennis Lang

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Faster
{
    /// <summary>
    /// The main window: a grid of every service on the machine (checkable, with its live and
    /// baseline configuration side by side) on the left, and the user's saved named lists with
    /// New/Activate/Show/Delete actions on the right.
    /// </summary>
    public sealed class MainForm : Form
    {
        private readonly DataGridView _grid = new();
        // Overlaid on the "Use" column's header cell (DataGridView has no built-in header
        // checkbox) - tri-state so it can show Indeterminate when only some of the currently
        // VISIBLE rows are checked, not just a plain on/off toggle.
        private readonly CheckBox _selectAllCheck = new() { ThreeState = true, CheckAlign = ContentAlignment.MiddleCenter };
        // The "Saved lists" table: Modified date / # services / Name, sortable by header click
        // like the main grid (see _listSortProperty/_listSortAscending below). Single-row-select
        // stands in for the old ListBox's SelectedIndex.
        private readonly DataGridView _listsGrid = new();
        private readonly Label _status = new();
        private readonly Label _baselineLabel = new();
        private readonly ContextMenuStrip _rowMenu = new();
        private readonly Button _metricsBtn = new();
        private readonly PictureBox _themeToggle = new();
        private readonly Button _helpBtn = new();
        private readonly Button _saveCheckedBtn = new();
        private readonly Button _restoreAllBtn = new();
        private readonly Button _activateBtn = new();
        private readonly Button _updateListBtn = new();
        private readonly Button _showDetailsBtn = new();
        private readonly Button _deleteListBtn = new();
        private readonly ServiceDetailsPanel _detailsPanel = new(300);
        private readonly AboutPanel _aboutPanel = new();

        // Shared by every tooltip on the form (admin affordances, the select-all header
        // checkbox, ...) - one ToolTip component can serve any number of controls.
        private readonly ToolTip _tips = new();

        // ---- Elevation affordances (mirrors cs-b4browse's Elevation.cs + MainForm pattern) --- //
        private readonly Label _adminStatusLabel = new();
        // Null once already elevated - "Run as Admin" doesn't exist at all then (see BuildLayout).
        // Kept as a field (not a BuildLayout-local var) purely so ApplyThemeColors can re-assert
        // its purple ForeColor after Theme.StyleButtons would otherwise reset every button's
        // ForeColor to the theme's neutral text colour.
        private Button? _runAsAdminBtn;
        private static readonly Color AdminAccent = Color.MediumPurple;

        // Metric columns are built once but only added to the grid on the first "Metrics" click -
        // the whole point of the button is that the grid starts up fast without per-process
        // sampling, so these must not exist in the grid until the user actually asks for them.
        private bool _metricsColumnsAdded;
        private bool _metricsLoading;

        private Baseline _baseline = new();
        // _allRows is every service loaded, regardless of the current filter - the source of
        // truth for "which services are checked" (CreateListFromChecked reads from THIS, not
        // _rows, so a checked service that's hidden by a filter still ends up in a new list).
        // _rows is the filtered + sorted view actually bound to the grid; ServiceRow objects are
        // shared by reference between the two, so ticking a checkbox in the (filtered) grid is
        // visible in _allRows too.
        private List<ServiceRow> _allRows = new();
        private BindingList<ServiceRow> _rows = new();
        private List<ServiceListDefinition> _lists = new();
        private BindingList<ListRow> _listRows = new();

        // ---- Sorting (header click) ---------------------------------------------------- //
        private string _sortProperty = "DisplayName";
        private bool _sortAscending = true;
        // Same header-click-to-sort convention as the main grid above, but a separate pair of
        // fields since the two grids sort independently.
        private string _listSortProperty = "Name";
        private bool _listSortAscending = true;

        // ---- Filter bar (row above the grid) -------------------------------------------- //
        private const string AllChoice = "(All)";
        private bool _suspendFilterEvents;
        private TextBox? _searchBox;
        private ComboBox? _categoryFilter;
        private ComboBox? _startTypeFilter;
        private ComboBox? _runningFilter;
        private Label? _filterCountLabel;

        /// <summary>One grid row: a service's live state plus its baseline snapshot, if any.</summary>
        private sealed class ServiceRow
        {
            public bool Selected { get; set; }
            public string ServiceName { get; init; } = "";
            public string DisplayName { get; init; } = "";
            public ServiceStartMode StartType { get; init; }
            public bool DelayedAutoStart { get; init; }
            public bool Running { get; init; }
            public ServiceSnapshot? BaselineSnapshot { get; init; }
            public string CategoryLabel { get; init; } = "";
            public string Purpose { get; init; } = "";

            // ---- Resource metrics (Pid/memory/handles/threads/CPU) - left at defaults until the
            // "Metrics" button is pressed; deliberately NOT collected on every LoadData because
            // sampling CPU needs a real wall-clock delay, which would slow down every refresh. ---- //
            public bool HasMetrics { get; set; }
            public int MetricPid { get; set; }
            public long MetricWorkingSetBytes { get; set; }
            public int MetricHandleCount { get; set; }
            public int MetricThreadCount { get; set; }
            public double MetricCpuPercent { get; set; }
            public int MetricSharedWithCount { get; set; }

            public string StartTypeText => Describe(StartType, DelayedAutoStart);
            public string RunningText => Running ? "Running" : "Stopped";
            public string BaselineText => BaselineSnapshot == null ? "(no baseline)" :
                $"{Describe(BaselineSnapshot.StartType, BaselineSnapshot.DelayedAutoStart)}, " +
                (BaselineSnapshot.WasRunning ? "Running" : "Stopped");

            public string PidText => !Running ? "-" : HasMetrics ? MetricPid.ToString() : "";
            public string MemoryText => !Running ? "-" : HasMetrics ? FormatBytes(MetricWorkingSetBytes) : "";
            public string HandlesText => !Running ? "-" : HasMetrics ? MetricHandleCount.ToString("N0") : "";
            public string ThreadsText => !Running ? "-" : HasMetrics ? MetricThreadCount.ToString("N0") : "";
            public string CpuText => !Running ? "-" : HasMetrics ? $"{MetricCpuPercent:0.0}%" : "";
            public string ProcessText => !Running ? "-" :
                !HasMetrics ? "" :
                MetricSharedWithCount == 0 ? "(dedicated)" : $"shared x{MetricSharedWithCount + 1}";

            private static string Describe(ServiceStartMode mode, bool delayed) =>
                mode == ServiceStartMode.Automatic && delayed ? "Automatic (Delayed)" : mode.ToString();

            private static string FormatBytes(long bytes) => bytes switch
            {
                <= 0 => "0 MB",
                _ when bytes >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.0} GB",
                _ => $"{bytes / (1024.0 * 1024):0} MB",
            };
        }

        /// <summary>One row of the "Saved lists" table - a thin, sortable-column view over a
        /// ServiceListDefinition. Definition is the source of truth for everything that isn't one
        /// of the three displayed columns (Items, CreatedUtc, LastActivatedUtc, ...).</summary>
        private sealed class ListRow
        {
            public required ServiceListDefinition Definition { get; init; }
            public string Name => Definition.Name;
            public int ServiceCount => Definition.Items.Count;
            public DateTime ModifiedUtc => Definition.ModifiedUtc;
            public string ModifiedText => ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }

        public MainForm()
        {
            // Version and build date come from AppInfo, which set-version.ps1 keeps in sync -
            // same title-bar shape as cs-b4browse's MainForm.
            Text = $"Faster - Windows Service Switcher - {AppInfo.Version} - LanDen Labs  {AppInfo.BuildDate}";
            // Guarded rather than a direct `Icon = AppIcon.LoadIcon();` - Form.Icon's setter isn't
            // guaranteed null-safe across WinForms versions, and a missing icon.ico shouldn't be
            // fatal anyway; leaving Icon untouched just keeps the WinForms default.
            if (AppIcon.LoadIcon() is Icon appIcon) Icon = appIcon;
            // 1248 = 1040 * 1.2 - about 20% wider than the original default, so the grid's
            // resource-metrics columns (added on demand by the "Metrics" button) have more room
            // without immediately needing a manual resize.
            Width = 1248;
            Height = 660;
            StartPosition = FormStartPosition.CenterScreen;

            BuildLayout();
            LoadData();
            // Belt-and-suspenders: _grid.Resize (wired in BuildLayout) already repositions
            // _selectAllCheck as the form lays out, but re-measuring once more after the whole
            // form has actually shown catches any DPI/layout pass that settles after that.
            Load += (_, _) => PositionSelectAllHeaderCheck();

            // Paint for whichever theme Program.Main already applied (Theme.Load()/Apply() run
            // before this form is constructed), then keep repainting live on every later toggle -
            // MainForm is the one long-lived window in the app, unlike the modal dialogs (which
            // just read Theme.Current once, in their own constructors, since the toolbar's toggle
            // button is unreachable while one of them is open anyway).
            ApplyThemeColors();
            Theme.Changed += ApplyThemeColors;
        }

        /// <summary>Repaints every themeable control under this form for whatever Theme.Current
        /// now is - see Theme.ApplyToTree for exactly what that covers (backgrounds, grid/text
        /// colours, buttons, native scrollbars). Grid_CellFormatting's running/stopped tinting is
        /// untouched - CellFormatting fires fresh on every paint regardless, so it doesn't need
        /// re-asserting here. The admin purple accent DOES need re-asserting: ApplyToTree's
        /// Theme.StyleButtons/generic Label pass resets every Button/Label's ForeColor to the
        /// theme's neutral text colour, which would otherwise wipe out "Run as Admin"'s and the
        /// status bar's semantic purple/blue - those, like cs-b4browse's own severity colours,
        /// stay the same fixed shades in either theme rather than following Theme.Text.</summary>
        private void ApplyThemeColors()
        {
            Theme.ApplyToTree(this);
            if (_runAsAdminBtn != null) _runAsAdminBtn.ForeColor = AdminAccent;
            UpdateAdminStatusLabel();
            Invalidate(true);
        }

        /// <summary>Native (OS-drawn) scrollbars - the grids' in particular - only pick up
        /// Theme.ApplyScrollbarTheme's SetWindowTheme call once their Win32 handles actually
        /// exist, which isn't yet true back in the constructor's ApplyThemeColors() call (nothing
        /// has been shown yet). OnShown fires once those handles are real, mirroring
        /// cs-b4browse's own MainForm.OnShown for the same reason.</summary>
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Theme.ApplyScrollbarTheme(this);
        }

        private void BuildLayout()
        {
            var top = new Panel { Dock = DockStyle.Top, Height = 44 };

            // "Run as Admin" is the first (leftmost) toolbar button, but only exists at all when
            // not already elevated - there's nothing for it to do otherwise. Everything else is
            // positioned relative to it via a running x cursor instead of hardcoded Lefts, so the
            // rest of the toolbar shifts over cleanly whichever way this comes out.
            int x = 8;
            if (!Elevation.IsAdmin)
            {
                _runAsAdminBtn = new Button { Text = "Run as Admin", Left = x, Top = 8, Width = 110 };
                _runAsAdminBtn.ForeColor = AdminAccent;
                _runAsAdminBtn.Click += (_, _) => RelaunchAsAdmin();
                _tips.SetToolTip(_runAsAdminBtn,
                    "Relaunch Faster as Administrator - required to activate or restore a list.");
                top.Controls.Add(_runAsAdminBtn);
                x = _runAsAdminBtn.Right + 8;
            }

            var refreshBtn = new Button { Text = "Refresh", Left = x, Top = 8, Width = 90 };
            refreshBtn.Click += (_, _) => LoadData();
            x = refreshBtn.Right + 8;

            _metricsBtn.Text = "Metrics";
            _metricsBtn.Left = x;
            _metricsBtn.Top = 8;
            _metricsBtn.Width = 110;
            _metricsBtn.Click += async (_, _) => await ToggleMetricsAsync();
            x = _metricsBtn.Right + 8;

            var recaptureBtn = new Button { Text = "Re-capture Baseline", Left = x, Top = 8, Width = 150 };
            recaptureBtn.Click += (_, _) => RecaptureBaseline();
            x = recaptureBtn.Right + 10;

            _baselineLabel.AutoSize = true;
            _baselineLabel.Left = x;
            _baselineLabel.Top = 15;

            top.Controls.Add(refreshBtn);
            top.Controls.Add(_metricsBtn);
            top.Controls.Add(recaptureBtn);
            top.Controls.Add(_baselineLabel);

            // "Help" sits at the toolbar's right edge rather than joining the left-to-right x
            // cursor above - Anchor alone can't be trusted here because `top` isn't docked (and
            // so isn't at its real width) yet at this point in the constructor, so its position
            // is instead explicitly recomputed on every resize, mirroring the same idiom already
            // used for _selectAllCheck/PositionSelectAllHeaderCheck.
            _helpBtn.Text = "Help";
            _helpBtn.Top = 8;
            _helpBtn.Width = 70;
            _helpBtn.Click += (_, _) => new HelpDialog().ShowDialog(this);
            _tips.SetToolTip(_helpBtn, "Feature tour and links (similar to README.md).");
            top.Controls.Add(_helpBtn);
            top.Resize += (_, _) => PositionHelpButton(top);
            PositionHelpButton(top);

            // Theme toggle sits immediately left of Help, at the same right-anchored edge -
            // PositionThemeToggle is wired up (and called once) AFTER PositionHelpButton above,
            // so on every resize _helpBtn's own position is already correct by the time this one
            // reads _helpBtn.Left off of it.
            _themeToggle.Width = 28;
            _themeToggle.Height = 28;
            _themeToggle.Top = 8;
            _themeToggle.SizeMode = PictureBoxSizeMode.Zoom;
            _themeToggle.Cursor = Cursors.Hand;
            _themeToggle.BackColor = Color.Transparent;
            // Shown as-is (not tinted to match the theme) - same choice cs-b4browse makes for
            // this same image, since a half-black/half-white circle already reads as "light/dark"
            // without needing to match either palette.
            if (AppIcon.LoadThemeToggleImage() is Image themeImg) _themeToggle.Image = themeImg;
            _themeToggle.Click += (_, _) => Theme.Toggle();
            _tips.SetToolTip(_themeToggle, "Toggle dark / light theme");
            top.Controls.Add(_themeToggle);
            top.Resize += (_, _) => PositionThemeToggle();
            PositionThemeToggle();

            // SplitterDistance is set once the form has its real size (Load, below) rather than
            // here: its setter validates against the control's CURRENT width, which at this point
            // (unparented, default-sized) is far smaller than 680 and would throw.
            var split = new SplitContainer { Dock = DockStyle.Fill, Width = 1248, Height = 600 };
            split.SplitterDistance = 749;   // 60% of 1248 - grid (left) is the more important panel
            // FixedPanel defaults to None, which keeps SplitterDistance as a PERCENTAGE of the
            // container on resize (both panels grow/shrink proportionally). Panel2 (the saved-
            // lists side) should instead stay a constant pixel width and let Panel1 (the grid)
            // absorb all the resize, so pin it here.
            split.FixedPanel = FixedPanel.Panel2;

            _grid.Dock = DockStyle.Fill;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AutoGenerateColumns = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.RowHeadersVisible = false;
            // Blank HeaderText: the overlaid _selectAllCheck (added below, once the grid has a
            // Handle to measure against) makes the column's purpose self-explanatory without a
            // label competing for space in a 40px-wide header cell.
            _grid.Columns.Add(new DataGridViewCheckBoxColumn
                { DataPropertyName = "Selected", HeaderText = "", Width = 40 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
                { DataPropertyName = "ServiceName", HeaderText = "Service", Width = 190, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = "DisplayName", HeaderText = "Display Name", Width = 200, ReadOnly = true,
                // Fill mode is independent of the grid-level AutoSizeColumnsMode (which stays at
                // its default None, so every other column keeps its explicit Width and remains
                // manually resizable) - this is the one column that soaks up any extra horizontal
                // room once the left (grid) panel grows wider than the columns' combined default
                // widths, e.g. when the user widens the main window or drags the splitter right,
                // rather than leaving blank grey space past the last column.
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                MinimumWidth = 150,
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
                { DataPropertyName = "CategoryLabel", HeaderText = "Category", Width = 90, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
                { DataPropertyName = "StartTypeText", HeaderText = "Start Type", Width = 130, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
                { DataPropertyName = "RunningText", HeaderText = "Running", Width = 70, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
                { DataPropertyName = "BaselineText", HeaderText = "Baseline (start type, state)", Width = 190, ReadOnly = true });
            // Sorting is handled entirely by hand (Grid_ColumnHeaderMouseClick + ApplyFilterAndSort)
            // rather than DataGridView's built-in Automatic sort, which needs IBindingListView
            // support that a plain BindingList<T> doesn't have.
            foreach (DataGridViewColumn col in _grid.Columns)
                col.SortMode = DataGridViewColumnSortMode.Programmatic;
            _grid.ColumnHeaderMouseClick += Grid_ColumnHeaderMouseClick;
            // CellFormatting (not a stored per-row style) so the coloring survives every rebind:
            // ApplyFilterAndSort recreates _rows and reassigns _grid.DataSource on every sort/
            // filter change, which would silently drop any color set directly on a
            // DataGridViewRow/Cell object instead.
            _grid.CellFormatting += Grid_CellFormatting;
            // A checkbox cell only commits to the bound object when it loses focus by default;
            // force an immediate commit on every toggle so a checkbox click immediately followed
            // by a toolbar button click (no intervening focus change) isn't lost.
            _grid.CurrentCellDirtyStateChanged += (_, _) =>
            {
                if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            BuildRowContextMenu();
            _grid.MouseDown += Grid_MouseDown;
            // Keeps the "Details" tab live: whatever row is selected (left-click, right-click,
            // arrow keys) is reflected there immediately, replacing the old right-click "Details"
            // popup.
            _grid.SelectionChanged += (_, _) => UpdateDetailsPanel();
            // Keeps the "Save Checked (n)..." button's label and the select-all header checkbox
            // (below) live as checkboxes are toggled - CellValueChanged fires once CommitEdit
            // (above) has pushed the new value into the bound ServiceRow.Selected.
            _grid.CellValueChanged += (_, _) =>
            {
                UpdateSaveCheckedButtonText();
                UpdateSelectAllHeaderCheckState();
            };

            // Select-all header checkbox: overlaid directly on the "Use" column's header cell
            // (DataGridView has no built-in one) rather than drawn into the header itself, so it
            // can be a real, clickable, tri-state CheckBox with no owner-draw. Operates on _rows
            // (the currently VISIBLE/filtered rows), not _allRows - a service hidden by a filter
            // is left exactly as it was, matching "only for the visible items."
            _selectAllCheck.Click += (_, _) => ToggleSelectAllVisible();
            _tips.SetToolTip(_selectAllCheck, "Check/uncheck all visible services");
            _grid.Controls.Add(_selectAllCheck);
            // The header cell's position/size (and whether it's even scrolled into view) can only
            // be measured once the grid has columns and a Handle - both already true here - and
            // changes whenever the grid resizes or a column is resized, so re-measure on those too.
            _grid.Resize += (_, _) => PositionSelectAllHeaderCheck();
            _grid.ColumnWidthChanged += (_, _) => PositionSelectAllHeaderCheck();
            PositionSelectAllHeaderCheck();

            split.Panel1.Controls.Add(_grid);
            split.Panel1.Controls.Add(BuildFilterBar());

            var right = new Panel { Dock = DockStyle.Fill };
            var listsLabel = new Label { Text = "Saved lists", Dock = DockStyle.Top, Height = 20, Padding = new Padding(4, 4, 0, 0) };

            _listsGrid.Dock = DockStyle.Fill;
            _listsGrid.AllowUserToAddRows = false;
            _listsGrid.AllowUserToDeleteRows = false;
            _listsGrid.AllowUserToResizeRows = false;
            _listsGrid.AutoGenerateColumns = false;
            _listsGrid.ReadOnly = true;
            _listsGrid.MultiSelect = false;
            _listsGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _listsGrid.RowHeadersVisible = false;
            _listsGrid.Columns.Add(new DataGridViewTextBoxColumn
                { DataPropertyName = "ModifiedText", HeaderText = "Modified", Width = 120 });
            _listsGrid.Columns.Add(new DataGridViewTextBoxColumn
                { DataPropertyName = "ServiceCount", HeaderText = "# Services", Width = 80 });
            _listsGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = "Name", HeaderText = "Name",
                // Same "one column soaks up extra room" idiom as the main grid's Display Name
                // column - Modified/# Services keep their fixed Width and stay manually resizable.
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                MinimumWidth = 120,
            });
            foreach (DataGridViewColumn col in _listsGrid.Columns)
                col.SortMode = DataGridViewColumnSortMode.Programmatic;
            _listsGrid.ColumnHeaderMouseClick += ListsGrid_ColumnHeaderMouseClick;
            // Selecting a row still checks exactly that list's services in the grid (and unchecks
            // everything else), so "Activate '<name>'" and "what's in this list" are visually the
            // same thing - same behavior the old ListBox had.
            _listsGrid.SelectionChanged += (_, _) => OnListsGridSelectionChanged();
            // Esc clears the selection entirely (standard "back out of this" convention, same as
            // most Windows list/grid views) - deliberately does NOT touch the grid's checkboxes;
            // those stay exactly as they were, since selecting/deselecting a list is orthogonal to
            // what's currently checked (see ApplyListSelectionToChecks' no-op guard for a null list).
            _listsGrid.KeyDown += (_, e) =>
            {
                if (e.KeyCode != Keys.Escape || _listsGrid.CurrentRow == null) return;
                _listsGrid.ClearSelection();
                _listsGrid.CurrentCell = null;
                e.Handled = true;
            };

            var btnPanel = new FlowLayoutPanel
                { Dock = DockStyle.Bottom, Height = 226, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(4) };
            _saveCheckedBtn.Text = "Save 0 checked service(s) as...";
            _saveCheckedBtn.Width = 220;
            _saveCheckedBtn.Click += (_, _) => CreateListFromChecked();
            _restoreAllBtn.Text = "Restore All to Baseline";
            _restoreAllBtn.Width = 220;
            _restoreAllBtn.Click += (_, _) => RestoreAllToBaseline();
            _activateBtn.Width = 220;
            _activateBtn.Click += (_, _) => ActivateSelectedList();
            _updateListBtn.Width = 220;
            _updateListBtn.Click += (_, _) => UpdateSelectedList();
            _showDetailsBtn.Width = 220;
            _showDetailsBtn.Click += (_, _) => ShowSelectedListDetails();
            _deleteListBtn.Width = 220;
            _deleteListBtn.Click += (_, _) => DeleteSelectedList();
            // "Save Checked..." and "Restore All" are both global - neither needs a list selected,
            // unlike the four list-scoped actions below them, each of which names its target
            // directly in its own label (see UpdateListActionButtons) rather than a generic
            // "Selected List" so it's never ambiguous which list a click affects. Save Checked is
            // first since it's the workflow's starting point (check services, then save them).
            btnPanel.Controls.Add(_saveCheckedBtn);
            btnPanel.Controls.Add(_restoreAllBtn);
            btnPanel.Controls.Add(_activateBtn);
            btnPanel.Controls.Add(_updateListBtn);
            btnPanel.Controls.Add(_showDetailsBtn);
            btnPanel.Controls.Add(_deleteListBtn);
            // Activate/Update/Show/Delete all act on "the selected list" - start disabled and stay
            // that way until a real list is selected; see UpdateListActionButtons, called from
            // OnListsGridSelectionChanged and LoadData.
            UpdateListActionButtons();

            // Purple dot call-out on the two buttons that touch live service state - both need
            // Administrator, so both prompt to relaunch elevated (see TryElevateForAction) the
            // moment you click them while running as a standard user. No dot once elevated: at
            // that point clicking them just works, nothing to warn about. Drawn via each
            // button's own Paint event (not a separate overlay control) so it doesn't depend on
            // knowing the button's absolute position inside the FlowLayoutPanel, which isn't
            // settled until the parent actually lays out.
            if (!Elevation.IsAdmin)
            {
                AddAdminDot(_activateBtn);
                AddAdminDot(_restoreAllBtn);
            }

            right.Controls.Add(_listsGrid);
            right.Controls.Add(listsLabel);
            right.Controls.Add(btnPanel);

            // Right side is now a tab strip: "Lists" is exactly what used to be the whole right
            // panel; "Details" is what used to be the right-click "Details..." popup, now living
            // inline and updating as the grid selection changes (see _grid.SelectionChanged above);
            // "About" is app name/version/legal text plus a shortcut to the settings folder.
            var rightTabs = new TabControl { Dock = DockStyle.Fill };
            var listsTab = new TabPage("Lists");
            listsTab.Controls.Add(right);
            var detailsTab = new TabPage("Details");
            detailsTab.Controls.Add(_detailsPanel);
            var aboutTab = new TabPage("About");
            aboutTab.Controls.Add(_aboutPanel);
            rightTabs.TabPages.Add(listsTab);
            rightTabs.TabPages.Add(detailsTab);
            rightTabs.TabPages.Add(aboutTab);
            split.Panel2.Controls.Add(rightTabs);

            // Bottom bar: an elevation indicator (left, fixed width, click-to-elevate) + the
            // existing free-text status message filling the rest - same left-cluster-then-fill
            // shape as cs-b4browse's status bar, just with one indicator instead of several.
            var statusBar = new Panel { Dock = DockStyle.Bottom, Height = 24 };
            _adminStatusLabel.Dock = DockStyle.Left;
            _adminStatusLabel.Width = 150;
            _adminStatusLabel.TextAlign = ContentAlignment.MiddleLeft;
            _adminStatusLabel.Padding = new Padding(6, 0, 0, 0);
            _adminStatusLabel.Click += (_, _) => RelaunchAsAdmin();
            UpdateAdminStatusLabel();

            _status.Dock = DockStyle.Fill;
            _status.TextAlign = ContentAlignment.MiddleLeft;
            _status.Padding = new Padding(6, 0, 0, 0);

            statusBar.Controls.Add(_status);
            statusBar.Controls.Add(_adminStatusLabel);

            Controls.Add(split);
            Controls.Add(statusBar);
            Controls.Add(top);
        }

        /// <summary>Sets the bottom-left elevation label's text/color/cursor/tooltip for the
        /// current process - "Administrator" in blue (nothing to click), or "Standard user" in
        /// purple with a click-to-elevate cursor, matching cs-b4browse's status bar.</summary>
        private void UpdateAdminStatusLabel()
        {
            bool admin = Elevation.IsAdmin;
            _adminStatusLabel.Text = admin ? "Administrator" : "Standard user";
            _adminStatusLabel.ForeColor = admin ? Color.FromArgb(0, 90, 200) : AdminAccent;
            _adminStatusLabel.Cursor = admin ? Cursors.Default : Cursors.Hand;
            _tips.SetToolTip(_adminStatusLabel, admin
                ? "Running as Administrator."
                : "Running as a standard user. Click to relaunch as Administrator.");
        }

        /// <summary>Relaunches Faster elevated via UAC and exits this instance. No-op if already
        /// elevated; shows a message if the user declines the prompt or it otherwise fails.</summary>
        private void RelaunchAsAdmin()
        {
            if (Elevation.IsAdmin) return;
            if (Elevation.RelaunchAsAdmin())
            {
                Application.Exit();
            }
            else
            {
                MessageBox.Show(this, "Could not relaunch Faster as Administrator - the prompt " +
                    "may have been cancelled.", "Faster", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>If already elevated, always allowed - returns true immediately. Otherwise
        /// asks whether to relaunch as Administrator before doing <paramref name="action"/> (e.g.
        /// activating a list would otherwise just fail service-by-service with access-denied
        /// errors). Answering yes relaunches (and exits this instance); either way this specific
        /// attempt doesn't proceed in the current, unelevated process - Windows has no way to
        /// elevate a process already running, only to start a new elevated one.</summary>
        private bool ConfirmElevateForAction(string action)
        {
            if (Elevation.IsAdmin) return true;

            var result = MessageBox.Show(this,
                $"{action} requires Administrator rights.\n\nRelaunch Faster as Administrator now?",
                "Faster", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (result == DialogResult.Yes) RelaunchAsAdmin();
            return false;
        }

        /// <summary>Draws a small purple dot over a button's left edge, vertically centered, via
        /// its own Paint event - avoids needing to know the button's absolute position inside a
        /// FlowLayoutPanel, which isn't settled until the parent actually lays out.</summary>
        private static void AddAdminDot(Button btn)
        {
            btn.Paint += (_, e) =>
            {
                const int d = 8;
                using var brush = new SolidBrush(AdminAccent);
                e.Graphics.FillEllipse(brush, 5, (btn.Height - d) / 2, d, d);
            };
        }

        /// <summary>
        /// One filter per interesting column (free-text search across Service/Display Name,
        /// dropdowns for Category/Start Type/Running) docked directly above the grid, plus a
        /// Clear button and a "showing N of M" count - same shape as cs-b4browse's SortableGrid
        /// filter bar, sized to this grid's actual columns rather than a generic per-column bar.
        /// </summary>
        private Panel BuildFilterBar()
        {
            var bar = new Panel { Dock = DockStyle.Top, Height = 40 };
            var ff = new Font("Segoe UI", 9f);
            const int inputTop = 8, inputH = 24;
            int x = 8;

            Label AddLabel(string text)
            {
                int w = TextRenderer.MeasureText(text, ff).Width;
                var lbl = new Label
                {
                    Text = text, Font = ff, AutoSize = false, Left = x, Top = 0,
                    Width = w + 4, Height = bar.Height, TextAlign = ContentAlignment.MiddleLeft,
                };
                bar.Controls.Add(lbl);
                x = lbl.Right + 2;
                return lbl;
            }

            AddLabel("Search:");
            _searchBox = new TextBox
            {
                Left = x, Top = inputTop, Width = 200, Height = inputH, Font = ff,
                PlaceholderText = "service or display name...",
            };
            _searchBox.TextChanged += (_, _) => ApplyFilterAndSort();
            bar.Controls.Add(_searchBox);
            x = _searchBox.Right + 14;

            AddLabel("Category:");
            _categoryFilter = new ComboBox
                { Left = x, Top = inputTop, Width = 110, Font = ff, DropDownStyle = ComboBoxStyle.DropDownList };
            _categoryFilter.SelectedIndexChanged += (_, _) => { if (!_suspendFilterEvents) ApplyFilterAndSort(); };
            bar.Controls.Add(_categoryFilter);
            x = _categoryFilter.Right + 14;

            AddLabel("Start Type:");
            _startTypeFilter = new ComboBox
                { Left = x, Top = inputTop, Width = 150, Font = ff, DropDownStyle = ComboBoxStyle.DropDownList };
            _startTypeFilter.SelectedIndexChanged += (_, _) => { if (!_suspendFilterEvents) ApplyFilterAndSort(); };
            bar.Controls.Add(_startTypeFilter);
            x = _startTypeFilter.Right + 14;

            AddLabel("Running:");
            _runningFilter = new ComboBox
                { Left = x, Top = inputTop, Width = 90, Font = ff, DropDownStyle = ComboBoxStyle.DropDownList };
            _runningFilter.Items.AddRange(new object[] { AllChoice, "Running", "Stopped" });
            _runningFilter.SelectedIndex = 0;
            _runningFilter.SelectedIndexChanged += (_, _) => { if (!_suspendFilterEvents) ApplyFilterAndSort(); };
            bar.Controls.Add(_runningFilter);
            x = _runningFilter.Right + 14;

            var clearBtn = new Button { Text = "Clear Filters", Left = x, Top = inputTop, Width = 100, Height = inputH, Font = ff };
            clearBtn.Click += (_, _) => ClearFilters();
            bar.Controls.Add(clearBtn);
            x = clearBtn.Right + 14;

            _filterCountLabel = new Label
            {
                Left = x, Top = 0, AutoSize = true, Height = bar.Height,
                TextAlign = ContentAlignment.MiddleLeft, Font = ff, ForeColor = SystemColors.GrayText,
            };
            bar.Controls.Add(_filterCountLabel);

            return bar;
        }

        /// <summary>Refills the Category/Start Type dropdown choices from the currently loaded
        /// services, preserving each selection when it still exists (called after every LoadData -
        /// the values on offer can change as services start/stop or a new baseline is captured).</summary>
        private void RebuildFilterChoices()
        {
            void Fill(ComboBox combo, IEnumerable<string> values)
            {
                string? prev = combo.SelectedItem as string;
                var distinct = values.Where(v => !string.IsNullOrEmpty(v))
                    .Distinct().OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
                combo.BeginUpdate();
                combo.Items.Clear();
                combo.Items.Add(AllChoice);
                foreach (var v in distinct) combo.Items.Add(v);
                int idx = prev != null ? combo.Items.IndexOf(prev) : 0;
                combo.SelectedIndex = idx >= 0 ? idx : 0;
                combo.EndUpdate();
            }

            _suspendFilterEvents = true;
            if (_categoryFilter != null) Fill(_categoryFilter, _allRows.Select(r => r.CategoryLabel));
            if (_startTypeFilter != null) Fill(_startTypeFilter, _allRows.Select(r => r.StartTypeText));
            _suspendFilterEvents = false;
        }

        private void ClearFilters()
        {
            _suspendFilterEvents = true;
            if (_searchBox != null) _searchBox.Text = "";
            if (_categoryFilter is { Items.Count: > 0 }) _categoryFilter.SelectedIndex = 0;
            if (_startTypeFilter is { Items.Count: > 0 }) _startTypeFilter.SelectedIndex = 0;
            if (_runningFilter is { Items.Count: > 0 }) _runningFilter.SelectedIndex = 0;
            _suspendFilterEvents = false;
            ApplyFilterAndSort();
        }

        /// <summary>Recomputes the grid's bound rows from _allRows: apply every active filter
        /// (ANDed), sort by the current header-click sort column, rebind, and update the
        /// "showing N of M" count + sort glyph. Called after LoadData and on every filter/sort
        /// change - cheap enough at service-list scale (a few hundred rows) to just rebuild.</summary>
        private void ApplyFilterAndSort()
        {
            if (_suspendFilterEvents) return;

            IEnumerable<ServiceRow> query = _allRows;

            string search = _searchBox?.Text.Trim() ?? "";
            if (search.Length > 0)
                query = query.Where(r =>
                    r.ServiceName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    r.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase));

            if (_categoryFilter?.SelectedItem is string cat && cat != AllChoice)
                query = query.Where(r => r.CategoryLabel == cat);

            if (_startTypeFilter?.SelectedItem is string st && st != AllChoice)
                query = query.Where(r => r.StartTypeText == st);

            if (_runningFilter?.SelectedItem is string run && run != AllChoice)
                query = query.Where(r => (run == "Running") == r.Running);

            var filtered = query.ToList();
            SortRows(filtered);

            _rows = new BindingList<ServiceRow>(filtered);
            _grid.DataSource = _rows;
            UpdateSortGlyphs();

            if (_filterCountLabel != null)
                _filterCountLabel.Text = filtered.Count == _allRows.Count
                    ? $"{_allRows.Count} shown"
                    : $"showing {filtered.Count} of {_allRows.Count}";

            UpdateSelectAllHeaderCheckState();   // the visible set just changed - re-derive Checked/Indeterminate
        }

        /// <summary>Checks or unchecks every currently VISIBLE row (_rows, not _allRows) in one
        /// click - a service hidden by an active filter is left exactly as it was. If the visible
        /// rows aren't all checked already (none, or a mixed Indeterminate state), this checks all
        /// of them; if they're all already checked, it unchecks all of them - the same "click
        /// selects all, click again clears all" rule most select-all header checkboxes use.</summary>
        private void ToggleSelectAllVisible()
        {
            bool allChecked = _rows.Count > 0 && _rows.All(r => r.Selected);
            bool newState = !allChecked;
            foreach (var row in _rows) row.Selected = newState;

            // ServiceRow doesn't implement INotifyPropertyChanged (see ApplyListSelectionToChecks'
            // comment) - ResetBindings forces the grid's checkbox cells to notice.
            _rows.ResetBindings();
            UpdateSaveCheckedButtonText();
            UpdateSelectAllHeaderCheckState();
        }

        /// <summary>Sets _selectAllCheck to Checked/Unchecked/Indeterminate based on how many of
        /// the currently visible rows (_rows) are checked - called after anything that can change
        /// either which rows are visible or which are checked.</summary>
        private void UpdateSelectAllHeaderCheckState()
        {
            if (_rows.Count == 0) { _selectAllCheck.CheckState = CheckState.Unchecked; return; }
            int n = _rows.Count(r => r.Selected);
            _selectAllCheck.CheckState = n == 0 ? CheckState.Unchecked
                : n == _rows.Count ? CheckState.Checked
                : CheckState.Indeterminate;
        }

        /// <summary>Re-measures and repositions _selectAllCheck over the "Use" column's header
        /// cell - called after the grid resizes or any column is resized, since the header cell's
        /// on-screen rectangle (and whether it's scrolled out of view at all) can shift either
        /// way. GetCellDisplayRectangle(0, -1, ...) is the header row (-1) of column 0 (the
        /// checkbox column), already accounting for horizontal scroll.</summary>
        private void PositionSelectAllHeaderCheck()
        {
            if (_grid.Columns.Count == 0) return;
            Rectangle headerRect = _grid.GetCellDisplayRectangle(0, -1, true);
            if (headerRect.IsEmpty) { _selectAllCheck.Visible = false; return; }

            _selectAllCheck.Visible = true;
            const int size = 16;
            _selectAllCheck.SetBounds(
                headerRect.Left + (headerRect.Width - size) / 2,
                headerRect.Top + (headerRect.Height - size) / 2,
                size, size);
        }

        /// <summary>Keeps _helpBtn pinned to the toolbar's right edge - called once at layout time
        /// and again on every resize of the toolbar Panel, since (unlike the rest of the toolbar's
        /// left-to-right buttons) its position depends on the panel's current width rather than
        /// the previous button's right edge.</summary>
        private void PositionHelpButton(Panel top) =>
            _helpBtn.Left = Math.Max(top.ClientSize.Width - _helpBtn.Width - 8, 0);

        /// <summary>Keeps the theme-toggle icon pinned immediately left of _helpBtn - reads
        /// _helpBtn.Left rather than re-deriving its own position from the toolbar's width, so it
        /// always tracks wherever Help itself actually ended up.</summary>
        private void PositionThemeToggle() =>
            _themeToggle.Left = Math.Max(_helpBtn.Left - _themeToggle.Width - 8, 0);

        /// <summary>Sorts in place by the current header-click column, falling back to a string
        /// comparison if the two keys' runtime types can't be compared directly (defensive only -
        /// every SortKey case below returns one consistent type per property).</summary>
        private void SortRows(List<ServiceRow> rows)
        {
            rows.Sort((a, b) =>
            {
                IComparable ka = SortKey(a, _sortProperty);
                IComparable kb = SortKey(b, _sortProperty);
                int cmp;
                try { cmp = Comparer<IComparable>.Default.Compare(ka, kb); }
                catch { cmp = string.Compare(ka?.ToString(), kb?.ToString(), StringComparison.OrdinalIgnoreCase); }
                return _sortAscending ? cmp : -cmp;
            });
        }

        private static IComparable SortKey(ServiceRow r, string property) => property switch
        {
            "Selected" => r.Selected,
            "ServiceName" => r.ServiceName,
            "DisplayName" => r.DisplayName,
            "CategoryLabel" => r.CategoryLabel,
            "StartTypeText" => r.StartTypeText,
            "RunningText" => r.RunningText,
            "BaselineText" => r.BaselineText,
            "PidText" => r.MetricPid,
            "MemoryText" => r.MetricWorkingSetBytes,
            "HandlesText" => r.MetricHandleCount,
            "ThreadsText" => r.MetricThreadCount,
            "CpuText" => r.MetricCpuPercent,
            "ProcessText" => r.ProcessText,
            _ => r.DisplayName,
        };

        private void Grid_ColumnHeaderMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.ColumnIndex < 0 || e.ColumnIndex >= _grid.Columns.Count) return;
            string? prop = _grid.Columns[e.ColumnIndex].DataPropertyName;
            if (string.IsNullOrEmpty(prop)) return;

            if (_sortProperty == prop) _sortAscending = !_sortAscending;
            else { _sortProperty = prop; _sortAscending = true; }
            ApplyFilterAndSort();
        }

        private void UpdateSortGlyphs()
        {
            foreach (DataGridViewColumn col in _grid.Columns)
                col.HeaderCell.SortGlyphDirection = SortOrder.None;
            foreach (DataGridViewColumn col in _grid.Columns)
            {
                if (col.DataPropertyName != _sortProperty) continue;
                col.HeaderCell.SortGlyphDirection = _sortAscending ? SortOrder.Ascending : SortOrder.Descending;
                break;
            }
        }

        // ---- "Saved lists" table sorting - same click-to-sort convention as the main grid above,
        // just against ListRow/_lists instead of ServiceRow/_allRows. -------------------------- //

        private void ListsGrid_ColumnHeaderMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.ColumnIndex < 0 || e.ColumnIndex >= _listsGrid.Columns.Count) return;
            string? prop = _listsGrid.Columns[e.ColumnIndex].DataPropertyName;
            if (string.IsNullOrEmpty(prop)) return;

            if (_listSortProperty == prop) _listSortAscending = !_listSortAscending;
            else { _listSortProperty = prop; _listSortAscending = true; }
            RebuildListRows();
        }

        private void UpdateListSortGlyphs()
        {
            foreach (DataGridViewColumn col in _listsGrid.Columns)
                col.HeaderCell.SortGlyphDirection = SortOrder.None;
            foreach (DataGridViewColumn col in _listsGrid.Columns)
            {
                if (col.DataPropertyName != _listSortProperty) continue;
                col.HeaderCell.SortGlyphDirection = _listSortAscending ? SortOrder.Ascending : SortOrder.Descending;
                break;
            }
        }

        private static IComparable ListSortKey(ListRow r, string property) => property switch
        {
            "ModifiedText" => r.ModifiedUtc,
            "ServiceCount" => r.ServiceCount,
            "Name" => r.Name,
            _ => r.Name,
        };

        /// <summary>Rebuilds _listRows from _lists (sorted per _listSortProperty/_listSortAscending)
        /// and rebinds _listsGrid - the "Saved lists" table's equivalent of ApplyFilterAndSort,
        /// minus any filtering (there's no filter bar on this side). Called after LoadData and
        /// after every header-click sort change.</summary>
        private void RebuildListRows()
        {
            var sorted = _lists.Select(l => new ListRow { Definition = l }).ToList();
            sorted.Sort((a, b) =>
            {
                IComparable ka = ListSortKey(a, _listSortProperty);
                IComparable kb = ListSortKey(b, _listSortProperty);
                int cmp;
                try { cmp = Comparer<IComparable>.Default.Compare(ka, kb); }
                catch { cmp = string.Compare(ka?.ToString(), kb?.ToString(), StringComparison.OrdinalIgnoreCase); }
                return _listSortAscending ? cmp : -cmp;
            });

            // Preserve the current selection across the rebind (by list name) rather than always
            // resetting to none - a header-click sort shouldn't feel like deselecting.
            string? selectedName = SelectedListOrNull()?.Name;

            _listRows = new BindingList<ListRow>(sorted);
            _listsGrid.DataSource = _listRows;
            UpdateListSortGlyphs();

            if (selectedName != null)
            {
                int idx = sorted.FindIndex(r => r.Name == selectedName);
                if (idx >= 0) _listsGrid.Rows[idx].Selected = true;
            }
        }

        // Excel's own "Light Green Fill with Dark Green Text" conditional-formatting colors -
        // familiar at a glance, and readable on a white grid background.
        private static readonly Color RunningBack = Color.FromArgb(198, 239, 206);
        private static readonly Color RunningFore = Color.FromArgb(0, 97, 0);

        // Same idiom for the Start Type column: plain "Automatic" gets the same light green as
        // a Running cell, "Automatic (Delayed)" gets a noticeably deeper shade of the same green
        // so the two read as distinct at a glance without having to read the text. Other start
        // types (Manual/Disabled/Boot/System) are left at the grid's normal colors.
        private static readonly Color AutomaticBack = Color.FromArgb(198, 239, 206);
        private static readonly Color AutomaticDelayedBack = Color.FromArgb(121, 201, 132);

        /// <summary>Colors the Running column's cell light green when the service is running, and
        /// the Start Type column light/deeper green for Automatic/Automatic (Delayed) respectively;
        /// everything else keeps the grid's normal colors. Runs on every cell paint rather than
        /// being set once on the row, so it survives every sort/filter rebind (see the comment
        /// where this is wired up in BuildLayout).</summary>
        private void Grid_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count) return;
            if (_grid.Rows[e.RowIndex].DataBoundItem is not ServiceRow row) return;

            switch (_grid.Columns[e.ColumnIndex].DataPropertyName)
            {
                case "RunningText":
                    if (!row.Running) return;   // leave Stopped cells at the grid's default styling
                    e.CellStyle.BackColor = RunningBack;
                    e.CellStyle.ForeColor = RunningFore;
                    break;

                case "StartTypeText":
                    if (row.StartTypeText == "Automatic (Delayed)")
                        e.CellStyle.BackColor = AutomaticDelayedBack;
                    else if (row.StartTypeText == "Automatic")
                        e.CellStyle.BackColor = AutomaticBack;
                    else
                        return;   // Manual/Disabled/Boot/System - default styling
                    e.CellStyle.ForeColor = RunningFore;
                    break;
            }
        }

        private void BuildRowContextMenu()
        {
            var copyItem = new ToolStripMenuItem("Copy Service Name");
            copyItem.Click += (_, _) => CopySelectedServiceName();

            var searchItem = new ToolStripMenuItem("Search: \"what is windows service <name>\"");
            searchItem.Click += (_, _) => SearchSelectedService();

            var servicesMscItem = new ToolStripMenuItem("Open services.msc");
            servicesMscItem.Click += (_, _) => OpenServicesMsc();

            _rowMenu.Items.Add(copyItem);
            _rowMenu.Items.Add(searchItem);
            _rowMenu.Items.Add(servicesMscItem);
        }

        /// <summary>
        /// DataGridView doesn't select the row under the cursor on a right-click by itself, and
        /// assigning ContextMenuStrip directly to the grid would pop it up over the header/empty
        /// space too - so this does the hit-test, selection, and (only for an actual data row)
        /// the menu Show() by hand.
        /// </summary>
        private void Grid_MouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            var hit = _grid.HitTest(e.X, e.Y);
            if (hit.RowIndex < 0) return;

            _grid.ClearSelection();
            _grid.Rows[hit.RowIndex].Selected = true;
            _grid.CurrentCell = _grid.Rows[hit.RowIndex].Cells[Math.Max(hit.ColumnIndex, 0)];
            _rowMenu.Show(_grid, e.Location);
        }

        private ServiceRow? SelectedRow() => _grid.CurrentRow?.DataBoundItem as ServiceRow;

        private void CopySelectedServiceName()
        {
            var row = SelectedRow();
            if (row == null) return;
            try
            {
                Clipboard.SetText(row.ServiceName);
                _status.Text = $"Copied '{row.ServiceName}' to clipboard.";
            }
            catch (Exception ex)
            {
                _status.Text = "Could not copy to clipboard: " + ex.Message;
            }
        }

        private void SearchSelectedService()
        {
            var row = SelectedRow();
            if (row == null) return;
            string query = $"what is windows service {row.DisplayName}";
            string url = "https://www.google.com/search?q=" + Uri.EscapeDataString(query);
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not open the browser: {ex.Message}", "Faster",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OpenServicesMsc()
        {
            // services.msc has no supported command-line switch to preselect a specific service
            // (unlike, say, devmgmt.msc's device paths). Best-effort workaround: once the console
            // window appears, type the display name at it - the Services snap-in's list view has
            // built-in type-ahead search on the "Name" column, so this lands on (and selects) the
            // matching row, the same as if the user had typed it by hand. See JumpToServiceAsync.
            string? displayName = SelectedRow()?.DisplayName;
            try
            {
                var proc = Process.Start(new ProcessStartInfo("services.msc") { UseShellExecute = true });
                if (proc != null && !string.IsNullOrEmpty(displayName))
                    _ = JumpToServiceAsync(proc, displayName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not open services.msc: {ex.Message}", "Faster",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        /// <summary>
        /// Waits for the newly-launched MMC window to appear, brings it to the foreground, and
        /// types the service's display name at it so the Services snap-in's own type-ahead
        /// search jumps to (and selects) that row - there's no supported API to preselect a
        /// service directly. This is UI automation, not a documented feature, so it's timing-
        /// sensitive: on a slow machine the window or its service list may not be fully ready
        /// when the keys are sent, in which case services.msc still opens fine, just without the
        /// jump. Runs the wait off the UI thread (it polls MainWindowHandle in a loop) and
        /// resumes on the UI thread to call SetForegroundWindow/SendKeys, both of which need to
        /// run on a thread with a message loop.
        /// </summary>
        private static async Task JumpToServiceAsync(Process mmcProcess, string displayName)
        {
            IntPtr handle = await Task.Run(() => WaitForMainWindow(mmcProcess, TimeSpan.FromSeconds(8)));
            if (handle == IntPtr.Zero) return;   // gave up - console is still open, just not auto-selected

            SetForegroundWindow(handle);
            // Give the snap-in time to finish loading the service list - type-ahead can only
            // match rows that have actually been populated by then.
            await Task.Delay(800);

            SendKeys.SendWait(EscapeForSendKeys(displayName));
        }

        private static IntPtr WaitForMainWindow(Process process, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    process.Refresh();
                    if (process.MainWindowHandle != IntPtr.Zero) return process.MainWindowHandle;
                }
                catch { /* process may have exited, or not be queryable yet */ }
                Task.Delay(150).Wait();
            }
            return IntPtr.Zero;
        }

        /// <summary>SendKeys treats + ^ % ~ ( ) {{ }} [ ] as special - escape them so a display
        /// name containing any of those characters is typed literally.</summary>
        private static string EscapeForSendKeys(string text)
        {
            var sb = new StringBuilder();
            foreach (char c in text)
            {
                if ("+^%~(){}[]".IndexOf(c) >= 0) sb.Append('{').Append(c).Append('}');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>Pushes the currently selected row's fields into the "Details" tab. Wired to
        /// _grid.SelectionChanged - replaces the old right-click "Details..." popup.</summary>
        private void UpdateDetailsPanel()
        {
            var row = SelectedRow();
            if (row == null) return;
            _detailsPanel.ShowService(row.ServiceName, row.DisplayName, row.CategoryLabel, row.Purpose,
                row.StartTypeText, row.RunningText, row.BaselineText);
        }

        /// <summary>Refreshes the "Save n checked service(s) as..." shortcut row's count from
        /// _allRows - the source of truth for which services are checked (see the field comment
        /// above). Called after anything that can change a row's Selected flag: a grid checkbox
        /// toggle (_grid.CellValueChanged), selecting a saved list (ApplyListSelectionToChecks),
        /// and every LoadData (which rebuilds _allRows from scratch, clearing all checks).</summary>
        private void UpdateSaveCheckedButtonText()
        {
            int n = _allRows.Count(r => r.Selected);
            _saveCheckedBtn.Text = $"Save {n} checked service(s) as...";
        }

        /// <summary>Fires on every _listsGrid selection change. Checks exactly the selected list's
        /// services (ApplyListSelectionToChecks) and refreshes the Update/Activate/Show/Delete
        /// buttons; a cleared selection (Esc, or the rebind that runs during LoadData) is a no-op
        /// for the checks, but still greys the four list-scoped buttons back out.</summary>
        private void OnListsGridSelectionChanged()
        {
            ApplyListSelectionToChecks();
            UpdateListActionButtons();
        }

        /// <summary>Enables Update/Activate/Show Details/Delete only once a real saved list row is
        /// selected - clicking any of them with nothing selected is no longer possible, replacing
        /// the old "select a list first" MessageBox fallback in SelectedList(). Also sets each
        /// button's label to name the list it would act on.</summary>
        private void UpdateListActionButtons()
        {
            var list = SelectedListOrNull();
            bool hasList = list != null;
            string name = list?.Name ?? "";

            _activateBtn.Enabled = hasList;
            _activateBtn.Text = hasList ? $"Activate '{name}'" : "Activate...";
            _updateListBtn.Enabled = hasList;
            _updateListBtn.Text = hasList ? $"Update '{name}'" : "Update...";
            _showDetailsBtn.Enabled = hasList;
            _showDetailsBtn.Text = hasList ? $"Details of '{name}'" : "Details...";
            _deleteListBtn.Enabled = hasList;
            _deleteListBtn.Text = hasList ? $"Delete '{name}'" : "Delete...";
        }

        private void LoadData()
        {
            _status.Text = "Loading services ...";
            Cursor = Cursors.WaitCursor;
            try
            {
                _baseline = BaselineStore.LoadOrCapture();
                _baselineLabel.Text = $"Baseline: {_baseline.CapturedUtc.ToLocalTime():yyyy-MM-dd HH:mm} " +
                    $"({_baseline.Services.Count} services)";

                var live = BaselineStore.Capture();   // fresh live read - not persisted
                _allRows = new List<ServiceRow>();
                foreach (var snap in live.Services.Values.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase))
                {
                    _baseline.Services.TryGetValue(snap.ServiceName, out var baselineSnap);
                    var catalogEntry = ServiceCatalog.GetOrUnknown(snap.ServiceName);
                    _allRows.Add(new ServiceRow
                    {
                        ServiceName = snap.ServiceName,
                        DisplayName = snap.DisplayName,
                        StartType = snap.StartType,
                        DelayedAutoStart = snap.DelayedAutoStart,
                        Running = snap.WasRunning,
                        BaselineSnapshot = baselineSnap,
                        CategoryLabel = ServiceCatalog.CategoryLabel(catalogEntry.Category),
                        Purpose = catalogEntry.Purpose,
                    });
                }
                RebuildFilterChoices();
                ApplyFilterAndSort();   // builds _rows/_grid.DataSource from _allRows

                _lists = ListStore.LoadAll().OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
                RebuildListRows();          // builds _listRows/_listsGrid.DataSource from _lists
                UpdateSaveCheckedButtonText();   // fresh _allRows means every check was just cleared
                UpdateListActionButtons();       // selection reset to none by the rebind above

                _status.Text = $"{_allRows.Count} service(s), {_lists.Count} saved list(s).";
            }
            catch (Exception ex)
            {
                _status.Text = "Error: " + ex.Message;
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        /// <summary>
        /// Adds the six resource-metric columns to the grid the first time it's called (a no-op
        /// on later calls), then samples every running service's PID/memory/handles/threads/CPU
        /// via <see cref="ServiceMetricsCollector.CollectAll"/> - off the UI thread, since CPU%
        /// needs a real ~300ms wall-clock sampling window - and writes the results onto the
        /// existing <see cref="ServiceRow"/> objects in _allRows before rebinding. Safe to call
        /// again later (button becomes "Refresh Metrics") to re-sample without re-adding columns.
        /// </summary>
        private async Task ToggleMetricsAsync()
        {
            if (_metricsLoading) return;
            _metricsLoading = true;
            _metricsBtn.Enabled = false;
            _status.Text = "Sampling service resource usage ...";
            Cursor = Cursors.WaitCursor;
            try
            {
                if (!_metricsColumnsAdded)
                {
                    AddMetricColumns();
                    _metricsColumnsAdded = true;
                    _metricsBtn.Text = "Refresh Metrics";
                }

                var metrics = await Task.Run(() => ServiceMetricsCollector.CollectAll());

                foreach (var row in _allRows)
                {
                    if (metrics.TryGetValue(row.ServiceName, out var m))
                    {
                        row.HasMetrics = true;
                        row.MetricPid = m.Pid;
                        row.MetricWorkingSetBytes = m.WorkingSetBytes;
                        row.MetricHandleCount = m.HandleCount;
                        row.MetricThreadCount = m.ThreadCount;
                        row.MetricCpuPercent = m.CpuPercent;
                        row.MetricSharedWithCount = m.SharedWithCount;
                    }
                    else
                    {
                        row.HasMetrics = false;
                    }
                }

                ApplyFilterAndSort();
                _status.Text = $"Metrics sampled for {metrics.Count} running service process(es).";
            }
            catch (Exception ex)
            {
                _status.Text = "Could not sample service metrics: " + ex.Message;
            }
            finally
            {
                Cursor = Cursors.Default;
                _metricsBtn.Enabled = true;
                _metricsLoading = false;
            }
        }

        /// <summary>Builds and appends the six metric columns - called exactly once, on the first
        /// "Metrics" click (see <see cref="ToggleMetricsAsync"/>).</summary>
        private void AddMetricColumns()
        {
            _grid.Columns.Add(new DataGridViewTextBoxColumn
                { DataPropertyName = "PidText", HeaderText = "PID", Width = 55, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
                { DataPropertyName = "MemoryText", HeaderText = "Memory", Width = 70, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
                { DataPropertyName = "HandlesText", HeaderText = "Handles", Width = 70, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
                { DataPropertyName = "ThreadsText", HeaderText = "Threads", Width = 65, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
                { DataPropertyName = "CpuText", HeaderText = "CPU %", Width = 60, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
                { DataPropertyName = "ProcessText", HeaderText = "Process", Width = 90, ReadOnly = true });
            foreach (DataGridViewColumn col in _grid.Columns)
                col.SortMode = DataGridViewColumnSortMode.Programmatic;
        }

        private void RecaptureBaseline()
        {
            var confirm = MessageBox.Show(this,
                "Re-capture the baseline from the machine's current service configuration?\n\n" +
                "Any list item set to \"restore to baseline\" will restore to this NEW snapshot " +
                "afterward, not the old one.",
                "Faster", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;

            try
            {
                var fresh = BaselineStore.Capture();
                BaselineStore.Save(fresh);
                LoadData();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Re-capture failed: " + ex.Message, "Faster",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void CreateListFromChecked()
        {
            // Read from _allRows, not _rows: a service checked before a filter was applied (or
            // that a filter now hides) must still be picked up here.
            var selectedRows = _allRows.Where(r => r.Selected).ToList();
            if (selectedRows.Count == 0)
            {
                MessageBox.Show(this, "Check one or more services in the grid first.", "Faster",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var dlg = new NewListDialog(
                selectedRows.Select(r => (r.ServiceName, r.DisplayName)).ToList());
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            if (ListStore.FindByName(ListStore.LoadAll(), dlg.ListName) != null)
            {
                var replace = MessageBox.Show(this,
                    $"A list named '{dlg.ListName}' already exists. Replace it?",
                    "Faster", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (replace != DialogResult.Yes) return;
            }

            var list = new ServiceListDefinition
            {
                Name = dlg.ListName,
                CreatedUtc = DateTime.UtcNow,
                Items = dlg.Items,   // per-row actions, set (individually or via "Apply to All") in the dialog
                // ModifiedUtc isn't set here - ListStore.Upsert/LoadAll derive it from the backing
                // file's own last-write time instead (see ServiceListDefinition.ModifiedUtc).
            };

            ListStore.Upsert(list);
            LoadData();
        }

        /// <summary>Re-saves the selected list's contents from the currently checked services,
        /// overwriting it directly - no name to type, no "replace this list?" prompt, since
        /// Update already names an unambiguous target (unlike CreateListFromChecked, which can
        /// still collide with a typed name). This is the fix for lists being awkward to edit:
        /// select the list (which already checks exactly its services - see
        /// ApplyListSelectionToChecks), tick/untick services as needed, then Update.</summary>
        private void UpdateSelectedList()
        {
            var list = SelectedList();
            if (list == null) return;

            var checkedRows = _allRows.Where(r => r.Selected).ToList();
            if (checkedRows.Count == 0)
            {
                MessageBox.Show(this, "Check one or more services in the grid first.", "Faster",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var dlg = new NewListDialog(
                checkedRows.Select(r => (r.ServiceName, r.DisplayName)).ToList(),
                existingName: list.Name,
                existingItems: list.Items);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            ListStore.Upsert(new ServiceListDefinition
            {
                Name = list.Name,
                CreatedUtc = list.CreatedUtc,
                LastActivatedUtc = list.LastActivatedUtc,
                Items = dlg.Items,
                // ModifiedUtc isn't set here either - see CreateListFromChecked's comment above.
            });
            LoadData();
        }

        /// <summary>Checks exactly the services belonging to the newly-selected saved list in the
        /// grid (unchecking everything else) - silent no-op when nothing is selected (e.g. the
        /// selection-cleared event that fires while LoadData()/RebuildListRows() is rebinding
        /// _listsGrid), unlike SelectedList() below which is used by explicit button clicks and
        /// prompts instead.</summary>
        private void ApplyListSelectionToChecks()
        {
            var list = SelectedListOrNull();
            if (list == null) return;

            var namesInList = new HashSet<string>(
                list.Items.Select(item => item.ServiceName), StringComparer.OrdinalIgnoreCase);

            foreach (var row in _allRows)
                row.Selected = namesInList.Contains(row.ServiceName);

            // ServiceRow doesn't implement INotifyPropertyChanged, so the grid's checkbox cells
            // won't notice the change on their own - ResetBindings forces every bound row to be
            // re-read, same idea as ApplyFilterAndSort's full rebind but without reshuffling sort/
            // filter state.
            _rows.ResetBindings();
            UpdateSaveCheckedButtonText();
            UpdateSelectAllHeaderCheckState();
            _status.Text = $"Checked {namesInList.Count} service(s) from '{list.Name}'.";
        }

        /// <summary>The real saved list backing the current _listsGrid selection, or null if
        /// nothing is selected - a silent, non-prompting lookup used internally (sort/selection
        /// bookkeeping, button enable-state) where "nothing selected" is an expected, normal
        /// state rather than a user mistake. See SelectedList() below for the prompting variant
        /// explicit button clicks use.</summary>
        private ServiceListDefinition? SelectedListOrNull() =>
            (_listsGrid.CurrentRow?.DataBoundItem as ListRow)?.Definition;

        /// <summary>The real saved list backing the current _listsGrid selection, or null if
        /// nothing is selected. Callers no longer need to guard against this in practice -
        /// Update/Activate/Show Details/Delete are all disabled by UpdateListActionButtons
        /// whenever this would return null - but the MessageBox fallback stays as a defensive
        /// no-op for any other caller.</summary>
        private ServiceListDefinition? SelectedList()
        {
            var list = SelectedListOrNull();
            if (list == null)
            {
                MessageBox.Show(this, "Select a saved list first.", "Faster",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return list;
        }

        private void ActivateSelectedList()
        {
            var list = SelectedList();
            if (list == null) return;
            if (!ConfirmElevateForAction($"Activating '{list.Name}'")) return;

            var confirm = MessageBox.Show(this,
                $"Activate '{list.Name}'? This changes {list.Items.Count} service(s) now.",
                "Faster", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            _status.Text = $"Activating '{list.Name}' ...";
            Cursor = Cursors.WaitCursor;
            try
            {
                var results = ServiceOps.Activate(list, _baseline);
                int failed = results.Count(r => !r.Success);

                var sb = new StringBuilder();
                foreach (var r in results)
                    sb.AppendLine($"{(r.Success ? "OK  " : "FAIL")}  {r.DisplayName} ({r.ServiceName})");
                foreach (var r in results.Where(r => !string.IsNullOrEmpty(r.Message)))
                    sb.AppendLine($"      {r.ServiceName}: {r.Message}");

                MessageBox.Show(this, sb.ToString(),
                    failed == 0 ? $"'{list.Name}' activated" : $"'{list.Name}' activated - {failed} failure(s)",
                    MessageBoxButtons.OK, failed == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Activation failed: " + ex.Message, "Faster",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
                LoadData();
            }
        }

        /// <summary>Restores EVERY service in the baseline to its captured configuration - the
        /// GUI equivalent of the headless <c>--restore</c> command. Builds a one-off, in-memory
        /// list (never saved via ListStore) rather than requiring the user to first create a
        /// list with a RestoreToBaseline item for every single service.</summary>
        private void RestoreAllToBaseline()
        {
            if (_baseline.Services.Count == 0)
            {
                MessageBox.Show(this, "No baseline captured yet.", "Faster",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!ConfirmElevateForAction("Restoring all services to baseline")) return;

            var confirm = MessageBox.Show(this,
                $"Restore ALL {_baseline.Services.Count} service(s) to the baseline captured " +
                $"{_baseline.CapturedUtc.ToLocalTime():yyyy-MM-dd HH:mm}? This changes every service on the machine now.",
                "Faster", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;

            var restoreList = new ServiceListDefinition
            {
                Name = "(restore-to-baseline)",
                CreatedUtc = DateTime.UtcNow,
                Items = _baseline.Services.Values.Select(s => new ServiceListItem
                {
                    ServiceName = s.ServiceName,
                    DisplayName = s.DisplayName,
                    Action = ServiceTargetAction.RestoreToBaseline,
                }).ToList(),
            };

            _status.Text = "Restoring all services to baseline ...";
            Cursor = Cursors.WaitCursor;
            try
            {
                var results = ServiceOps.Activate(restoreList, _baseline);
                int failed = results.Count(r => !r.Success);

                var sb = new StringBuilder();
                foreach (var r in results)
                    sb.AppendLine($"{(r.Success ? "OK  " : "FAIL")}  {r.DisplayName} ({r.ServiceName})");
                foreach (var r in results.Where(r => !string.IsNullOrEmpty(r.Message)))
                    sb.AppendLine($"      {r.ServiceName}: {r.Message}");

                MessageBox.Show(this, sb.ToString(),
                    failed == 0 ? "Restore to baseline complete" : $"Restore to baseline - {failed} failure(s)",
                    MessageBoxButtons.OK, failed == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Restore failed: " + ex.Message, "Faster",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
                LoadData();
            }
        }

        /// <summary>Shows the selected list's per-service actions - reuses NewListDialog's grid in
        /// read-only mode instead of the old plain-text MessageBox dump, so a mixed list (some
        /// services Stop, others Start or Restore) reads as a table instead of a wall of text.</summary>
        private void ShowSelectedListDetails()
        {
            var list = SelectedList();
            if (list == null) return;

            using var dlg = new NewListDialog(
                list.Items.Select(item => (item.ServiceName, item.DisplayName)).ToList(),
                existingName: list.Name,
                existingItems: list.Items,
                readOnly: true,
                createdUtc: list.CreatedUtc,
                lastActivatedUtc: list.LastActivatedUtc,
                modifiedUtc: list.ModifiedUtc);
            dlg.ShowDialog(this);
        }

        private void DeleteSelectedList()
        {
            var list = SelectedList();
            if (list == null) return;

            var confirm = MessageBox.Show(this, $"Delete '{list.Name}'? This cannot be undone.",
                "Faster", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;

            ListStore.Delete(list.Name);
            LoadData();
        }
    }
}
