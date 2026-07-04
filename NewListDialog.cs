// Copyright (c) 2026 LanDen Labs - Dennis Lang

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.ServiceProcess;
using System.Windows.Forms;

namespace Faster
{
    /// <summary>
    /// The one dialog behind three related flows in MainForm's "Lists" tab, all built around the
    /// same per-row grid (Service / Action / Start Type / Delayed):
    ///   - <b>Create</b> (<c>existingName</c> null, <c>readOnly</c> false) - name a brand new list
    ///     from the currently checked services, each row defaulting to Stop/Disabled.
    ///   - <b>Update</b> (<c>existingName</c> set, <c>readOnly</c> false) - re-save an existing
    ///     list's contents from the currently checked services; the name is fixed (shown but not
    ///     editable) and each row is seeded from <paramref name="existingItems"/> where a service
    ///     name matches, so previously-chosen actions aren't lost. Saving overwrites that same
    ///     list directly - no "replace this list?" prompt, since Update is already an explicit,
    ///     unambiguous target (unlike Create, which can still collide with a typed name).
    ///   - <b>Read-only "Show Details"</b> (<c>readOnly</c> true) - the same grid, disabled for
    ///     editing, with a single Close button instead of OK/Cancel - replaces the old plain-text
    ///     MessageBox dump.
    /// A "Set all rows to" bar above the grid (Create/Update only) applies one action/start-type/
    /// delayed choice to every row in a click, covering the common all-one-action case without
    /// forcing a per-row edit; rows can still be tweaked individually for a mixed list.
    /// </summary>
    public sealed class NewListDialog : Form
    {
        private readonly TextBox _nameBox;
        private readonly DataGridView _grid = new();
        private readonly BindingList<RowItem> _rows;

        public string ListName => _nameBox.Text.Trim();

        /// <summary>The finished per-service list items, one per row, action/start-type/delayed
        /// exactly as left in the grid when OK was pressed. Not meaningful in read-only mode.</summary>
        public List<ServiceListItem> Items => _rows.Select(r => new ServiceListItem
        {
            ServiceName = r.ServiceName,
            DisplayName = r.DisplayName,
            Action = r.Action,
            TargetStartType = r.StartType,
            TargetDelayedAutoStart = r.Delayed,
        }).ToList();

        /// <summary>One row's editable state - a private DTO bound to the grid, distinct from
        /// MainForm's own ServiceRow (this dialog only ever needs name + chosen action, never any
        /// of MainForm's live/baseline/metrics state).</summary>
        private sealed class RowItem
        {
            public string ServiceName { get; init; } = "";
            public string DisplayName { get; init; } = "";
            public ServiceTargetAction Action { get; set; } = ServiceTargetAction.Stop;
            public ServiceStartMode StartType { get; set; } = ServiceStartMode.Disabled;
            public bool Delayed { get; set; }
        }

        /// <param name="services">Rows to show - the currently checked services for Create/Update,
        /// or a saved list's own items for the read-only "Show Details" view.</param>
        /// <param name="existingName">Null for Create; the fixed, non-editable target list name
        /// for Update and for the read-only view.</param>
        /// <param name="existingItems">Previously-saved per-service actions to seed matching rows
        /// with (Update / read-only); ignored for Create, where every row defaults to Stop/Disabled.</param>
        /// <param name="readOnly">True for the "Show Details" view: grid and name are locked, the
        /// "Set all rows to" bar is hidden, and there's a single Close button instead of OK/Cancel.</param>
        public NewListDialog(
            List<(string ServiceName, string DisplayName)> services,
            string? existingName = null,
            List<ServiceListItem>? existingItems = null,
            bool readOnly = false,
            DateTime? createdUtc = null,
            DateTime? lastActivatedUtc = null,
            DateTime? modifiedUtc = null)
        {
            bool isUpdate = existingName != null && !readOnly;

            Text = readOnly ? $"List Details - {existingName}"
                 : isUpdate ? $"Update '{existingName}'"
                 : "New Service List";
            if (AppIcon.LoadIcon() is Icon appIcon) Icon = appIcon;   // see MainForm's constructor for why this is guarded
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(560, readOnly ? 440 : 480);

            var nameLabel = new Label { Text = "List name:", Left = 14, Top = 12, AutoSize = true };
            _nameBox = new TextBox { Left = 14, Top = 32, Width = 532 };
            if (existingName != null)
            {
                _nameBox.Text = existingName;
                _nameBox.ReadOnly = true;
                _nameBox.TabStop = false;
                _nameBox.BackColor = SystemColors.Control;
            }

            var servicesLabel = new Label
            {
                Text = readOnly ? $"{services.Count} service(s) in this list:"
                                : $"{services.Count} service(s) - set each one's action below:",
                Left = 14, Top = 62, AutoSize = true,
            };

            var controls = new List<Control> { nameLabel, _nameBox, servicesLabel };

            // Read-only view only: created/last-activated dates, carried over from the old plain-
            // text MessageBox dump this dialog replaces - the grid below covers the per-service
            // detail, but these two dates aren't part of any row.
            int gridTop = 84;
            if (readOnly)
            {
                string created = createdUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "unknown";
                string modified = modifiedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? created;
                string lastRun = lastActivatedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "never run";
                var datesLabel = new Label
                {
                    Text = $"Created: {created}      Modified: {modified}      Last activated: {lastRun}",
                    Left = 14, Top = 84, AutoSize = true, ForeColor = SystemColors.GrayText,
                };
                controls.Add(datesLabel);
                gridTop = 110;
            }

            // -- "Set all rows to" bar (Create/Update only): applies one action/start-type/delayed
            // choice to every row in a single click, covering the common all-one-action case
            // without forcing a manual per-row edit; rows can still be tweaked individually
            // afterward for a mixed list. Not shown in the read-only "Show Details" view. --
            if (!readOnly)
            {
                var bulkPanel = new Panel { Left = 14, Top = 84, Width = 532, Height = 28 };
                var bulkLabel = new Label { Text = "Set all rows to:", Left = 0, Top = 6, AutoSize = true };
                var bulkActionCombo = new ComboBox
                    { Left = 104, Top = 2, Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
                foreach (ServiceTargetAction a in Enum.GetValues<ServiceTargetAction>()) bulkActionCombo.Items.Add(a);
                bulkActionCombo.SelectedItem = ServiceTargetAction.Stop;
                var bulkStartTypeCombo = new ComboBox
                    { Left = 262, Top = 2, Width = 130, DropDownStyle = ComboBoxStyle.DropDownList };
                foreach (ServiceStartMode m in Enum.GetValues<ServiceStartMode>()) bulkStartTypeCombo.Items.Add(m);
                bulkStartTypeCombo.SelectedItem = ServiceStartMode.Disabled;
                var bulkDelayedCheck = new CheckBox { Text = "Delayed", Left = 400, Top = 4, Width = 68 };
                var applyBtn = new Button { Text = "Apply to All", Left = 468, Top = 0, Width = 64, Height = 24 };
                applyBtn.Click += (_, _) =>
                {
                    var action = bulkActionCombo.SelectedItem is ServiceTargetAction a2 ? a2 : ServiceTargetAction.Stop;
                    var startType = bulkStartTypeCombo.SelectedItem is ServiceStartMode m2 ? m2 : ServiceStartMode.Disabled;
                    bool delayed = bulkDelayedCheck.Checked;
                    foreach (var row in _rows)
                    {
                        if (row is null) continue;
                        row.Action = action;
                        row.StartType = startType;
                        row.Delayed = delayed;
                    }
                    _rows.ResetBindings();
                };
                bulkPanel.Controls.AddRange(new Control[]
                    { bulkLabel, bulkActionCombo, bulkStartTypeCombo, bulkDelayedCheck, applyBtn });
                controls.Add(bulkPanel);
                gridTop = 120;
            }

            _grid.Left = 14;
            _grid.Top = gridTop;
            _grid.Width = 532;
            _grid.Height = (readOnly ? 440 : 480) - gridTop - 64;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AutoGenerateColumns = false;
            _grid.RowHeadersVisible = false;
            _grid.EditMode = DataGridViewEditMode.EditOnEnter;   // one click focuses + shows the combo's dropdown arrow
            _grid.ReadOnly = readOnly;

            _grid.Columns.Add(new DataGridViewTextBoxColumn
                { DataPropertyName = "DisplayName", HeaderText = "Service", Width = 190, ReadOnly = true });

            var actionCol = new DataGridViewComboBoxColumn
            {
                DataPropertyName = "Action", HeaderText = "Action", Width = 130,
                DataSource = Enum.GetValues<ServiceTargetAction>(), ValueType = typeof(ServiceTargetAction),
            };
            _grid.Columns.Add(actionCol);

            var startTypeCol = new DataGridViewComboBoxColumn
            {
                DataPropertyName = "StartType", HeaderText = "Start Type", Width = 120,
                DataSource = Enum.GetValues<ServiceStartMode>(), ValueType = typeof(ServiceStartMode),
            };
            _grid.Columns.Add(startTypeCol);

            _grid.Columns.Add(new DataGridViewCheckBoxColumn
                { DataPropertyName = "Delayed", HeaderText = "Delayed", Width = 60 });

            if (!readOnly)
            {
                // A checkbox/combo cell only commits to the bound object when it loses focus by
                // default; force an immediate commit on every change (same pattern as MainForm's
                // own grid) so Start Type/Delayed's greyed-out state (see Grid_CellFormatting)
                // reacts to an Action edit right away, and so OK immediately after an edit never
                // loses it.
                _grid.CurrentCellDirtyStateChanged += (_, _) =>
                {
                    if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
                };
                _grid.CellValueChanged += (_, _) => _grid.Invalidate();
            }
            // Start Type/Delayed are meaningless for a Restore row (ServiceOps.Activate ignores
            // them for that action) - grey them out rather than disabling cell-by-cell, which
            // DataGridView doesn't support directly. Applies in every mode, including read-only.
            _grid.CellFormatting += Grid_CellFormatting;

            // Seed each row from existingItems (Update/read-only) where the service name matches,
            // so previously-chosen actions carry forward; anything not found (e.g. a service newly
            // checked since the list was last saved) falls back to the RowItem defaults (Stop/
            // Disabled). Ignored for Create, where existingItems is null and every row is a fresh
            // default.
            var existingByName = (existingItems ?? new List<ServiceListItem>())
                .ToDictionary(it => it.ServiceName, it => it, StringComparer.OrdinalIgnoreCase);
            _rows = new BindingList<RowItem>(services.Select(s =>
            {
                var row = new RowItem { ServiceName = s.ServiceName, DisplayName = s.DisplayName };
                if (existingByName.TryGetValue(s.ServiceName, out var existing))
                {
                    row.Action = existing.Action;
                    row.StartType = existing.TargetStartType;
                    row.Delayed = existing.TargetDelayedAutoStart;
                }
                return row;
            }).ToList());
            _grid.DataSource = _rows;
            controls.Add(_grid);

            int buttonTop = ClientSize.Height - 44;
            if (readOnly)
            {
                var closeBtn = new Button
                    { Text = "Close", Left = 466, Top = buttonTop, Width = 80, DialogResult = DialogResult.OK };
                controls.Add(closeBtn);
                AcceptButton = closeBtn;
                CancelButton = closeBtn;
            }
            else
            {
                var okBtn = new Button { Text = "OK", Left = 376, Top = buttonTop, Width = 80, DialogResult = DialogResult.OK };
                var cancelBtn = new Button { Text = "Cancel", Left = 466, Top = buttonTop, Width = 80, DialogResult = DialogResult.Cancel };
                okBtn.Click += (_, _) =>
                {
                    if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
                    // Update's name is fixed (read-only textbox, always non-empty) - only Create
                    // needs the empty-name guard.
                    if (existingName == null && string.IsNullOrWhiteSpace(_nameBox.Text))
                    {
                        MessageBox.Show(this, "Enter a name for this list.", "Faster",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        DialogResult = DialogResult.None;   // cancel the auto-close
                    }
                };
                controls.Add(okBtn);
                controls.Add(cancelBtn);
                AcceptButton = okBtn;
                CancelButton = cancelBtn;
            }

            Controls.AddRange(controls.ToArray());

            // Read Theme.Current once, here at construction - this dialog is modal (ShowDialog),
            // so the toolbar's theme-toggle button is unreachable for as long as it's open; no
            // need for a live Theme.Changed subscription like MainForm's.
            Theme.ApplyToTree(this);
        }

        /// <summary>_grid's native scrollbar only picks up ApplyToTree's SetWindowTheme call once
        /// its handle actually exists, which isn't yet true at construction time - see MainForm's
        /// own OnShown override for the same reason.</summary>
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Theme.ApplyScrollbarTheme(this);
        }

        private void Grid_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count) return;
            string? prop = _grid.Columns[e.ColumnIndex].DataPropertyName;
            if (prop != "StartType" && prop != "Delayed") return;
            if (_grid.Rows[e.RowIndex].DataBoundItem is not RowItem row) return;
            if (row.Action != ServiceTargetAction.RestoreToBaseline) return;

            e.CellStyle.BackColor = SystemColors.Control;
            e.CellStyle.ForeColor = SystemColors.GrayText;
        }
    }
}
