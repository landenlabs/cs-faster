// Copyright (c) 2026 LanDen Labs - Dennis Lang

using System;
using System.Drawing;
using System.Management;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Faster
{
    /// <summary>
    /// Read-only "everything we know about this service" popup: the catalog category/purpose
    /// (if any), its live start type/state and baseline comparison (all passed in - already
    /// known to the caller), plus a few extra fields (description, binary path, run-as account)
    /// fetched from WMI on demand, since that query is slow enough to not do for every row in
    /// the grid up front.
    /// </summary>
    public sealed class ServiceDetailsDialog : Form
    {
        private readonly TextBox _box;
        private readonly StringBuilder _baseText;
        private readonly string _serviceName;

        public ServiceDetailsDialog(string serviceName, string displayName, string categoryLabel, string purpose,
            string startTypeText, string runningText, string baselineText)
        {
            _serviceName = serviceName;
            Text = $"{displayName} - Details";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(540, 420);

            _box = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                WordWrap = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 9.5f),
                BackColor = SystemColors.Window,
            };

            _baseText = new StringBuilder();
            _baseText.AppendLine($"Service name:   {serviceName}");
            _baseText.AppendLine($"Display name:   {displayName}");
            _baseText.AppendLine($"Category:       {categoryLabel}");
            _baseText.AppendLine();
            _baseText.AppendLine("Purpose:");
            _baseText.AppendLine(purpose);
            _baseText.AppendLine();
            _baseText.AppendLine($"Start type:     {startTypeText}");
            _baseText.AppendLine($"Current state:  {runningText}");
            _baseText.AppendLine($"Baseline:       {baselineText}");
            _baseText.AppendLine();
            _baseText.AppendLine("Loading additional details and live resource usage ...");
            _box.Text = _baseText.ToString();

            var closeBtn = new Button
                { Text = "Close", DialogResult = DialogResult.OK, Dock = DockStyle.Bottom, Height = 34 };

            Controls.Add(_box);
            Controls.Add(closeBtn);
            AcceptButton = closeBtn;
            CancelButton = closeBtn;

            Shown += async (_, _) =>
            {
                var (wmi, metrics) = await Task.Run(() =>
                    (FetchWmiDetails(_serviceName), ServiceMetricsCollector.CollectOne(_serviceName)));
                if (IsDisposed) return;
                _box.Text = _baseText.ToString() + Environment.NewLine + wmi +
                    Environment.NewLine + Environment.NewLine + FormatMetrics(metrics);
            };
        }

        /// <summary>
        /// Renders a live resource-usage snapshot, or an explanatory line when the service isn't
        /// currently running (no process to sample). When the host process is shared with other
        /// services, every number is the WHOLE process's total, not this service's share alone -
        /// Windows doesn't expose a finer-grained per-service breakdown for that case, so the
        /// sharing is called out explicitly rather than presenting the numbers as exact.
        /// </summary>
        private static string FormatMetrics(ServiceMetrics? m)
        {
            if (m == null)
                return "Resource usage:\r\n(not running - no process to sample)";

            var sb = new StringBuilder();
            sb.AppendLine("Resource usage (live sample):");
            sb.AppendLine($"PID:            {m.Pid}");
            sb.AppendLine($"Memory (WS):    {FormatBytes(m.WorkingSetBytes)}");
            sb.AppendLine($"Handles:        {m.HandleCount:N0}");
            sb.AppendLine($"Threads:        {m.ThreadCount:N0}");
            sb.AppendLine($"CPU (approx):   {m.CpuPercent:0.0}%");
            if (m.SharedWithCount > 0)
            {
                sb.AppendLine();
                sb.Append($"Note: this process hosts {m.SharedWithCount + 1} services total, so the " +
                          "numbers above are for the WHOLE process, not this service alone: ");
                sb.Append(string.Join(", ", m.SharedWithNames));
            }
            return sb.ToString();
        }

        private static string FormatBytes(long bytes) => bytes switch
        {
            <= 0 => "0 MB",
            _ when bytes >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.0} GB",
            _ => $"{bytes / (1024.0 * 1024):0} MB",
        };

        /// <summary>
        /// Win32_Service resolves the (possibly MUI-indirect) registry Description string for
        /// us, which is why this goes through WMI rather than a plain registry read.
        /// </summary>
        private static string FetchWmiDetails(string serviceName)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT Description, PathName, StartName, ServiceType FROM Win32_Service WHERE Name='{Escape(serviceName)}'");
                foreach (ManagementBaseObject mo in searcher.Get())
                {
                    string description = mo["Description"] as string ?? "(none provided)";
                    string path = mo["PathName"] as string ?? "(unknown)";
                    string account = mo["StartName"] as string ?? "(unknown)";
                    string type = mo["ServiceType"] as string ?? "(unknown)";
                    return "From Windows (WMI):\r\n" +
                           $"Description:    {description}\r\n" +
                           $"Runs as:        {account}\r\n" +
                           $"Service type:   {type}\r\n" +
                           $"Binary path:    {path}";
                }
                return "(no WMI record found for this service - it may have been removed since the grid loaded.)";
            }
            catch (Exception ex)
            {
                return $"(could not read additional details from WMI: {ex.Message})";
            }
        }

        private static string Escape(string s) => s.Replace("'", "''");
    }
}
