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
        private readonly ListBox _listsBox = new();
        private readonly Label _status = new();
        private readonly Label _baselineLabel = new();
        private readonly ContextMenuStrip _rowMenu = new();
        private readonly Button _metricsBtn = new();
        private readonly ServiceDetailsPanel _detailsPanel = new(300);

        // ---- Elevation affordances (mirrors cs-b4browse's Elevation.cs + MainForm pattern) --- //
        private readonly Label _adminStatusLabel = new();
        private readonly ToolTip _adminTip = new();
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

        // ---- Sorting (header click) ---------------------------------------------------- //
        private string _sortProperty = "DisplayName";
        private bool _sortAscending = true;

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

        public MainForm()
        {
            Text = "Faster - Windows Service Switcher";
            Width = 1040;
            Height = 660;
            StartPosition = FormStartPosition.CenterScreen;

            BuildLayout();
            LoadData();
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
                var adminBtn = new Button { Text = "Run as Admin", Left = x, Top = 8, Width = 110 };
                adminBtn.ForeColor = AdminAccent;
                adminBtn.Click += (_, _) => RelaunchAsAdmin();
                _adminTip.SetToolTip(adminBtn,
                    "Relaunch Faster as Administrator - required to activate or restore a list.");
                top.Controls.Add(adminBtn);
                x = adminBtn.Right + 8;
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

            // SplitterDistance is set once the form has its real size (Load, below) rather than
            // here: its setter validates against the control's CURRENT width, which at this point
            // (unparented, default-sized) is far smaller than 680 and would throw.
            var split = new SplitContainer { Dock = DockStyle.Fill, Width = 1040, Height = 600 };
            split.SplitterDistance = 680;   // Width is fixed above, so this is now in-range
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
            _grid.Columns.Add(new DataGridViewCheckBoxColumn
                { DataPropertyName = "Selected", HeaderText = "Use", Width = 40 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
                { DataPropertyName = "ServiceName", HeaderText = "Service", Width = 190, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
                { DataPropertyName = "DisplayName", HeaderText = "Display Name", Width = 200, ReadOnly = true });
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
            split.Panel1.Controls.Add(_grid);
            split.Panel1.Controls.Add(BuildFilterBar());

            var right = new Panel { Dock = DockStyle.Fill };
            var listsLabel = new Label { Text = "Saved lists", Dock = DockStyle.Top, Height = 20, Padding = new Padding(4, 4, 0, 0) };
            _listsBox.Dock = DockStyle.Fill;
            // Selecting a saved list checks exactly its services in the grid (and unchecks
            // everything else), so "Activate Selected List" and "what's in this list" are
            // visually the same thing - also fires (harmlessly) with SelectedIndex=-1 whenever
            // LoadData() repopulates _listsBox.Items, which ApplyListSelectionToChecks ignores.
            _listsBox.SelectedIndexChanged += (_, _) => ApplyListSelectionToChecks();
            var btnPanel = new FlowLayoutPanel
                { Dock = DockStyle.Bottom, Height = 190, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(4) };
            var newBtn = new Button { Text = "New List From Checked...", Width = 220 };
            newBtn.Click += (_, _) => CreateListFromChecked();
            var activateBtn = new Button { Text = "Activate Selected List", Width = 220 };
            activateBtn.Click += (_, _) => ActivateSelectedList();
            var restoreAllBtn = new Button { Text = "Restore All to Baseline", Width = 220 };
            restoreAllBtn.Click += (_, _) => RestoreAllToBaseline();
            var showBtn = new Button { Text = "Show Details", Width = 220 };
            showBtn.Click += (_, _) => ShowSelectedListDetails();
            var deleteBtn = new Button { Text = "Delete Selected List", Width = 220 };
            deleteBtn.Click += (_, _) => DeleteSelectedList();
            btnPanel.Controls.Add(newBtn);
            btnPanel.Controls.Add(activateBtn);
            btnPanel.Controls.Add(restoreAllBtn);
            btnPanel.Controls.Add(showBtn);
            btnPanel.Controls.Add(deleteBtn);

            // Purple dot call-out on the two buttons that touch live service state - both need
            // Administrator, so both prompt to relaunch elevated (see TryElevateForAction) the
            // moment you click them while running as a standard user. No dot once elevated: at
            // that point clicking them just works, nothing to warn about. Drawn via each
            // button's own Paint event (not a separate overlay control) so it doesn't depend on
            // knowing the button's absolute position inside the FlowLayoutPanel, which isn't
            // settled until the parent actually lays out.
            if (!Elevation.IsAdmin)
            {
                AddAdminDot(activateBtn);
                AddAdminDot(restoreAllBtn);
            }

            right.Controls.Add(_listsBox);
            right.Controls.Add(listsLabel);
            right.Controls.Add(btnPanel);

            // Right side is now a tab strip: "Lists" is exactly what used to be the whole right
            // panel; "Details" is what used to be the right-click "Details..." popup, now living
            // inline and updating as the grid selection changes (see _grid.SelectionChanged above).
            var rightTabs = new TabControl { Dock = DockStyle.Fill };
            var listsTab = new TabPage("Lists");
            listsTab.Controls.Add(right);
            var detailsTab = new TabPage("Details");
            detailsTab.Controls.Add(_detailsPanel);
            rightTabs.TabPages.Add(listsTab);
            rightTabs.TabPages.Add(detailsTab);
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
            _adminTip.SetToolTip(_adminStatusLabel, admin
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

        /// <summary>Draws a small purple dot over a button's top-right corner via its own Paint
        /// event - avoids needing to know the button's absolute position inside a
        /// FlowLayoutPanel, which isn't settled until the parent actually lays out.</summary>
        private static void AddAdminDot(Button btn)
        {
            btn.Paint += (_, e) =>
            {
                const int d = 8;
                using var brush = new SolidBrush(AdminAccent);
                e.Graphics.FillEllipse(brush, btn.Width - d - 5, 4, d, d);
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
        }

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

        // Excel's own "Light Green Fill with Dark Green Text" conditional-formatting colors -
        // familiar at a glance, and readable on a white grid background.
        private static readonly Color RunningBack = Color.FromArgb(198, 239, 206);
        private static readonly Color RunningFore = Color.FromArgb(0, 97, 0);

        /// <summary>Colors the Running column's cell light green when the service is running;
        /// stopped services keep the grid's normal colors. Runs on every cell paint rather than
        /// being set once on the row, so it survives every sort/filter rebind (see the comment
        /// where this is wired up in BuildLayout).</summary>
        private void Grid_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count) return;
            if (_grid.Columns[e.ColumnIndex].DataPropertyName != "RunningText") return;
            if (_grid.Rows[e.RowIndex].DataBoundItem is not ServiceRow row) return;
            if (!row.Running) return;   // leave Stopped cells at the grid's default styling

            e.CellStyle.BackColor = RunningBack;
            e.CellStyle.ForeColor = RunningFore;
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
                _listsBox.Items.Clear();
                foreach (var l in _lists)
                {
                    string lastRun = l.LastActivatedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "never run";
                    _listsBox.Items.Add($"{l.Name}  -  {l.Items.Count} service(s), {lastRun}");
                }

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
                Items = selectedRows.Select(r => new ServiceListItem
                {
                    ServiceName = r.ServiceName,
                    DisplayName = r.DisplayName,
                    Action = dlg.Action,
                    TargetStartType = dlg.TargetStartType,
                    TargetDelayedAutoStart = dlg.TargetDelayedAutoStart,
                }).ToList(),
            };

            ListStore.Upsert(list);
            LoadData();
        }

        /// <summary>Checks exactly the services belonging to the newly-selected saved list in the
        /// grid (unchecking everything else) - silent no-op when nothing is selected (e.g. the
        /// -1 SelectedIndex that fires while LoadData() is repopulating _listsBox.Items), unlike
        /// SelectedList() below which is used by explicit button clicks and prompts instead.</summary>
        private void ApplyListSelectionToChecks()
        {
            int i = _listsBox.SelectedIndex;
            if (i < 0 || i >= _lists.Count) return;

            var list = _lists[i];
            var namesInList = new HashSet<string>(
                list.Items.Select(item => item.ServiceName), StringComparer.OrdinalIgnoreCase);

            foreach (var row in _allRows)
                row.Selected = namesInList.Contains(row.ServiceName);

            // ServiceRow doesn't implement INotifyPropertyChanged, so the grid's checkbox cells
            // won't notice the change on their own - ResetBindings forces every bound row to be
            // re-read, same idea as ApplyFilterAndSort's full rebind but without reshuffling sort/
            // filter state.
            _rows.ResetBindings();
            _status.Text = $"Checked {namesInList.Count} service(s) from '{list.Name}'.";
        }

        private ServiceListDefinition? SelectedList()
        {
            int i = _listsBox.SelectedIndex;
            if (i < 0 || i >= _lists.Count)
            {
                MessageBox.Show(this, "Select a saved list first.", "Faster",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }
            return _lists[i];
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
        /// list (never saved to lists.json) rather than requiring the user to first create a
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

        private void ShowSelectedListDetails()
        {
            var list = SelectedList();
            if (list == null) return;

            var sb = new StringBuilder();
            sb.AppendLine($"Created:        {list.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
            sb.AppendLine($"Last activated: {(list.LastActivatedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "(never)")}");
            sb.AppendLine();
            foreach (var item in list.Items)
            {
                string detail = item.Action == ServiceTargetAction.RestoreToBaseline
                    ? "restore to baseline"
                    : $"{item.Action} -> {item.TargetStartType}" +
                      (item.TargetStartType == ServiceStartMode.Automatic && item.TargetDelayedAutoStart ? " (Delayed)" : "");
                sb.AppendLine($"{item.DisplayName} ({item.ServiceName}): {detail}");
            }

            MessageBox.Show(this, sb.ToString(), list.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
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
