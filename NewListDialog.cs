// Copyright (c) 2026 LanDen Labs - Dennis Lang

using System;
using System.Collections.Generic;
using System.Drawing;
using System.ServiceProcess;
using System.Windows.Forms;

namespace Faster
{
    /// <summary>
    /// Names a new list and picks the single action applied to every checked service when the
    /// list is later activated: stop-and-set-start-type, start-and-set-start-type, or restore
    /// each one to its baseline configuration.
    /// </summary>
    public sealed class NewListDialog : Form
    {
        private readonly TextBox _nameBox;
        private readonly RadioButton _stopRadio;
        private readonly RadioButton _startRadio;
        private readonly RadioButton _restoreRadio;
        private readonly ComboBox _startTypeCombo;
        private readonly CheckBox _delayedCheck;
        private readonly Button _okBtn;

        public string ListName => _nameBox.Text.Trim();

        public ServiceTargetAction Action =>
            _stopRadio.Checked ? ServiceTargetAction.Stop :
            _startRadio.Checked ? ServiceTargetAction.Start :
            ServiceTargetAction.RestoreToBaseline;

        public ServiceStartMode TargetStartType =>
            _startTypeCombo.SelectedItem is ServiceStartMode m ? m : ServiceStartMode.Disabled;

        public bool TargetDelayedAutoStart => _delayedCheck.Checked;

        public NewListDialog(List<(string ServiceName, string DisplayName)> services)
        {
            Text = "New Service List";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(420, 430);

            var nameLabel = new Label { Text = "List name:", Left = 14, Top = 12, AutoSize = true };
            _nameBox = new TextBox { Left = 14, Top = 32, Width = 392 };

            var servicesLabel = new Label
                { Text = $"{services.Count} service(s) selected:", Left = 14, Top = 62, AutoSize = true };
            var servicesList = new ListBox { Left = 14, Top = 82, Width = 392, Height = 110 };
            foreach (var (name, display) in services) servicesList.Items.Add($"{display}  ({name})");

            var actionLabel = new Label
                { Text = "Action when this list is activated:", Left = 14, Top = 200, AutoSize = true };
            _stopRadio = new RadioButton
                { Text = "Stop and set start type below", Left = 14, Top = 222, Width = 390, Checked = true };
            _startRadio = new RadioButton
                { Text = "Start and set start type below", Left = 14, Top = 246, Width = 390 };
            _restoreRadio = new RadioButton
                { Text = "Restore each service to its baseline configuration", Left = 14, Top = 270, Width = 390 };

            var startTypeLabel = new Label { Text = "Start type:", Left = 34, Top = 300, AutoSize = true };
            _startTypeCombo = new ComboBox
                { Left = 120, Top = 296, Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (ServiceStartMode mode in Enum.GetValues<ServiceStartMode>())
                _startTypeCombo.Items.Add(mode);
            _startTypeCombo.SelectedItem = ServiceStartMode.Disabled;

            _delayedCheck = new CheckBox { Text = "Delayed start (Automatic only)", Left = 34, Top = 328, Width = 320 };

            void UpdateEnabled()
            {
                bool restoring = _restoreRadio.Checked;
                _startTypeCombo.Enabled = !restoring;
                startTypeLabel.Enabled = !restoring;
                _delayedCheck.Enabled = !restoring && _startTypeCombo.SelectedItem is ServiceStartMode.Automatic;
            }

            _stopRadio.CheckedChanged += (_, _) =>
            {
                if (_stopRadio.Checked) _startTypeCombo.SelectedItem = ServiceStartMode.Disabled;
                UpdateEnabled();
            };
            _startRadio.CheckedChanged += (_, _) =>
            {
                if (_startRadio.Checked) _startTypeCombo.SelectedItem = ServiceStartMode.Automatic;
                UpdateEnabled();
            };
            _restoreRadio.CheckedChanged += (_, _) => UpdateEnabled();
            _startTypeCombo.SelectedIndexChanged += (_, _) => UpdateEnabled();

            _okBtn = new Button { Text = "OK", Left = 236, Top = 386, Width = 80, DialogResult = DialogResult.OK };
            var cancelBtn = new Button { Text = "Cancel", Left = 326, Top = 386, Width = 80, DialogResult = DialogResult.Cancel };
            _okBtn.Click += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(_nameBox.Text))
                {
                    MessageBox.Show(this, "Enter a name for this list.", "Faster",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    DialogResult = DialogResult.None;   // cancel the auto-close
                }
            };

            Controls.AddRange(new Control[]
            {
                nameLabel, _nameBox, servicesLabel, servicesList, actionLabel,
                _stopRadio, _startRadio, _restoreRadio, startTypeLabel, _startTypeCombo, _delayedCheck,
                _okBtn, cancelBtn,
            });

            AcceptButton = _okBtn;
            CancelButton = cancelBtn;
            UpdateEnabled();
        }
    }
}
