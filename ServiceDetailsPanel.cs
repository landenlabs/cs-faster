// Copyright (c) 2026 LanDen Labs - Dennis Lang

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Faster
{
    /// <summary>
    /// Everything known about the currently selected service, rendered as label/value tables
    /// instead of one free-text block. Lives inline in MainForm's "Details" tab (it used to be a
    /// modal popup) and updates automatically as the grid selection changes - see
    /// MainForm.UpdateDetailsPanel / _grid.SelectionChanged.
    /// </summary>
    public sealed class ServiceDetailsPanel : UserControl
    {
        private readonly FlowLayoutPanel _flow;
        private int _contentWidth;
        private string? _currentServiceName;

        // Cached args from the last ShowService call, so a manual splitter/window resize can
        // rebuild the same service's sections at the new width - see OnResize. Sizing the label/
        // value columns and the wrap width of long text is baked into each Label at creation time
        // (AutoSize + MaximumSize), so simply re-anchoring the existing controls wouldn't make
        // wrapped text reflow to use newly available space; rebuilding with the new width does.
        private (string ServiceName, string DisplayName, string CategoryLabel, string Purpose,
            string StartTypeText, string RunningText, string BaselineText)? _lastArgs;

        // WMI/metrics are slow to fetch (a WMI query + a ~300ms CPU sample), so a resize-driven
        // rebuild (same service, just a new width) must reuse whatever was already fetched
        // instead of re-querying - _extrasFetchedFor tracks which service that cache belongs to.
        private string? _extrasFetchedFor;
        private (string, string)[]? _cachedWmiRows;
        private (string, string)[]? _cachedMetricRows;

        private static readonly Font TitleFont = new("Segoe UI", 12f, FontStyle.Bold);
        private static readonly Font SectionTitleFont = new("Segoe UI", 9.5f, FontStyle.Bold);
        private static readonly Font LabelFont = new("Segoe UI", 9f, FontStyle.Bold);
        private static readonly Font ValueFont = new("Segoe UI", 9f, FontStyle.Regular);
        private static readonly Color LabelColor = Color.FromArgb(60, 60, 60);

        public ServiceDetailsPanel(int contentWidth = 300)
        {
            _contentWidth = contentWidth;
            Dock = DockStyle.Fill;
            AutoScroll = true;

            // Deliberately NOT Dock=Fill: AutoScroll on the UserControl needs its content to be
            // free to grow taller than the visible area, which a Docked child can't do (Fill
            // clamps to the current client size). AutoSize + FlowLayoutPanel.TopDown gives a
            // single stack of sections that grows downward as content is added.
            _flow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(8),
                Location = new Point(0, 0),
            };
            Controls.Add(_flow);
            ShowPlaceholder();

            // Dragging the SplitContainer's splitter (or resizing the whole window) changes this
            // control's Width - re-derive the usable content width from it and rebuild so the
            // tables/text stretch (and long values re-wrap) to use the full new width, instead of
            // staying pinned at whatever width was current when they were first built.
            Resize += (_, _) => OnResized();
        }

        private void OnResized()
        {
            // Leave room for the vertical scrollbar (it only appears once content is taller than
            // the visible area, but reserving its width up front avoids a horizontal scrollbar
            // fighting the vertical one) plus the flow panel's own left/right padding.
            int newWidth = Math.Max(Width - SystemInformation.VerticalScrollBarWidth - 24, 160);
            if (Math.Abs(newWidth - _contentWidth) < 4) return;   // ignore sub-pixel/noise resizes
            _contentWidth = newWidth;

            if (_lastArgs is { } a)
            {
                ShowService(a.ServiceName, a.DisplayName, a.CategoryLabel, a.Purpose,
                    a.StartTypeText, a.RunningText, a.BaselineText);
            }
            else
            {
                ShowPlaceholder();
            }
        }

        private void ShowPlaceholder()
        {
            _flow.Controls.Clear();
            _flow.Controls.Add(new Label
            {
                Text = "Select a service in the grid to see its details here.",
                Font = ValueFont,
                ForeColor = SystemColors.GrayText,
                AutoSize = true,
                MaximumSize = new Size(_contentWidth, 0),
            });
        }

        /// <summary>
        /// Rebuilds the panel for one service. The fields already known to the caller (from the
        /// grid row) render immediately; WMI details and a live resource-usage sample are fetched
        /// in the background - both are slow enough that doing them for every row up front would
        /// make selecting rows feel sluggish - and dropped in once ready. If the selection has
        /// since moved to a different service by the time that finishes, the stale response for
        /// the old one is discarded rather than overwriting what's now showing.
        /// </summary>
        public void ShowService(string serviceName, string displayName, string categoryLabel, string purpose,
            string startTypeText, string runningText, string baselineText)
        {
            _currentServiceName = serviceName;
            _lastArgs = (serviceName, displayName, categoryLabel, purpose, startTypeText, runningText, baselineText);

            RebuildLayout();

            // Only re-fetch WMI/metrics for an actually-new service - a resize-driven rebuild of
            // the SAME service (see OnResized) reuses whatever's already cached instead of
            // re-querying WMI and re-sampling CPU on every few pixels of a splitter drag.
            if (_extrasFetchedFor != serviceName)
            {
                _extrasFetchedFor = serviceName;
                _cachedWmiRows = null;
                _cachedMetricRows = null;
                _ = LoadExtrasAsync(serviceName);
            }
        }

        /// <summary>(Re)builds the section stack from `_lastArgs` at the current `_contentWidth` -
        /// shared by ShowService (new selection) and OnResized (same selection, new width).</summary>
        private void RebuildLayout()
        {
            if (_lastArgs is not { } a)
            {
                ShowPlaceholder();
                return;
            }

            _flow.SuspendLayout();
            _flow.Controls.Clear();

            _flow.Controls.Add(new Label
            {
                Text = a.DisplayName,
                Font = TitleFont,
                AutoSize = true,
                MaximumSize = new Size(_contentWidth, 0),
                Margin = new Padding(2, 2, 2, 10),
            });

            _flow.Controls.Add(BuildTableGroup("Overview", new[]
            {
                ("Service name:", a.ServiceName),
                ("Display name:", a.DisplayName),
                ("Category:", a.CategoryLabel),
                ("Start type:", a.StartTypeText),
                ("Current state:", a.RunningText),
                ("Baseline:", a.BaselineText),
            }));

            _flow.Controls.Add(BuildTextGroup("Purpose", a.Purpose));

            _flow.Controls.Add(BuildTableGroup("Windows Details (WMI)", _cachedWmiRows ?? new[] { ("Loading...", "") }, "wmi"));
            _flow.Controls.Add(BuildTableGroup("Resource Usage", _cachedMetricRows ?? new[] { ("Loading...", "") }, "metrics"));

            _flow.ResumeLayout();
        }

        private async Task LoadExtrasAsync(string serviceName)
        {
            var (wmiRows, metrics) = await Task.Run(() =>
                (BuildWmiRows(serviceName), ServiceMetricsCollector.CollectOne(serviceName)));

            if (IsDisposed || serviceName != _currentServiceName) return;   // selection moved on

            _cachedWmiRows = wmiRows;
            _cachedMetricRows = FormatMetricRows(metrics, serviceName);

            ReplaceGroup("wmi", BuildTableGroup("Windows Details (WMI)", _cachedWmiRows, "wmi"));
            ReplaceGroup("metrics", BuildTableGroup("Resource Usage", _cachedMetricRows, "metrics"));
        }

        /// <summary>Swaps a named placeholder section for its finished version, in place, so the
        /// WMI/metrics sections don't jump to the bottom of the stack once they load.</summary>
        private void ReplaceGroup(string name, Control replacement)
        {
            var old = _flow.Controls.Cast<Control>().FirstOrDefault(c => (c.Tag as string) == name);
            if (old == null) return;

            _flow.SuspendLayout();
            int index = _flow.Controls.GetChildIndex(old);
            _flow.Controls.Remove(old);
            old.Dispose();
            _flow.Controls.Add(replacement);
            _flow.Controls.SetChildIndex(replacement, index);
            _flow.ResumeLayout();
        }

        /// <summary>One titled "table": a bold section header above a bordered label/value grid
        /// (label column bold + right-aligned, value column regular + left-aligned, wrapping long
        /// values like a binary path). <paramref name="name"/> tags the section so a placeholder
        /// can be found and swapped out later by <see cref="ReplaceGroup"/>.
        ///
        /// The title and the inner data table are stacked via a 1-column, 2-row TableLayoutPanel
        /// rather than a plain Panel with Dock=Top children: a Panel's AutoSize does not reliably
        /// compute a height from Docked children (that combination can silently collapse to zero
        /// height - the original bug that made the whole "Details" tab render blank), whereas
        /// TableLayoutPanel's AutoSize rows are specifically designed to measure Docked/Filled
        /// cell content correctly.</summary>
        private TableLayoutPanel BuildTableGroup(string title, IEnumerable<(string Label, string Value)> rows, string? name = null)
        {
            var table = new TableLayoutPanel
            {
                ColumnCount = 2,
                RowCount = 0,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                CellBorderStyle = TableLayoutPanelCellBorderStyle.Single,
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            int i = 0;
            foreach (var (label, value) in rows)
            {
                table.RowCount = i + 1;
                table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                table.Controls.Add(new Label
                {
                    Text = label,
                    Font = LabelFont,
                    ForeColor = LabelColor,
                    AutoSize = true,
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleRight,
                    Padding = new Padding(4, 3, 8, 3),
                }, 0, i);
                table.Controls.Add(new Label
                {
                    Text = string.IsNullOrEmpty(value) ? "-" : value,
                    Font = ValueFont,
                    AutoSize = true,
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Padding = new Padding(4, 3, 4, 3),
                    MaximumSize = new Size(Math.Max(_contentWidth - 140, 80), 0),
                }, 1, i);
                i++;
            }

            var titleLabel = new Label
            {
                Text = title,
                Font = SectionTitleFont,
                Dock = DockStyle.Fill,
                AutoSize = true,
                Padding = new Padding(0, 0, 0, 4),
                Margin = new Padding(0),
            };

            var section = new TableLayoutPanel
            {
                ColumnCount = 1,
                RowCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Width = _contentWidth,
                Margin = new Padding(2, 0, 2, 12),
                Tag = name,
            };
            section.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            section.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            section.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            section.Controls.Add(titleLabel, 0, 0);
            section.Controls.Add(table, 0, 1);
            return section;
        }

        /// <summary>A section that's prose rather than a label/value table (just "Purpose") -
        /// same TableLayoutPanel-wrapper reasoning as <see cref="BuildTableGroup"/>.</summary>
        private TableLayoutPanel BuildTextGroup(string title, string text)
        {
            var titleLabel = new Label
            {
                Text = title,
                Font = SectionTitleFont,
                Dock = DockStyle.Fill,
                AutoSize = true,
                Padding = new Padding(0, 0, 0, 4),
                Margin = new Padding(0),
            };
            var body = new Label
            {
                Text = string.IsNullOrWhiteSpace(text) ? "(no description available)" : text,
                Font = ValueFont,
                Dock = DockStyle.Fill,
                AutoSize = true,
                MaximumSize = new Size(_contentWidth, 0),
                Padding = new Padding(4),
                Margin = new Padding(0),
            };

            var section = new TableLayoutPanel
            {
                ColumnCount = 1,
                RowCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Width = _contentWidth,
                Margin = new Padding(2, 0, 2, 12),
            };
            section.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            section.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            section.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            section.Controls.Add(titleLabel, 0, 0);
            section.Controls.Add(body, 0, 1);
            return section;
        }

        /// <summary>
        /// Win32_Service resolves the (possibly MUI-indirect) registry Description string for
        /// us, which is why this goes through WMI rather than a plain registry read.
        /// </summary>
        private static (string, string)[] BuildWmiRows(string serviceName)
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
                    return new[]
                    {
                        ("Description:", description),
                        ("Runs as:", account),
                        ("Service type:", type),
                        ("Binary path:", path),
                    };
                }
                return new[] { ("Status:", "No WMI record found for this service.") };
            }
            catch (Exception ex)
            {
                return new[] { ("Error:", ex.Message) };
            }
        }

        /// <summary>When the host process is shared with other services, every number is the
        /// WHOLE process's total, not this service's share alone - Windows doesn't expose a
        /// finer-grained per-service breakdown for that case, so the sharing is called out
        /// explicitly rather than presenting the numbers as exact.</summary>
        private static (string, string)[] FormatMetricRows(ServiceMetrics? m, string serviceName)
        {
            if (m == null)
                return new[] { ("Status:", "Not running - no process to sample.") };

            var rows = new List<(string, string)>
            {
                ("PID:", m.Pid.ToString()),
                ("Memory (WS):", FormatBytes(m.WorkingSetBytes)),
                ("Handles:", m.HandleCount.ToString("N0")),
                ("Threads:", m.ThreadCount.ToString("N0")),
                ("CPU (approx):", $"{m.CpuPercent:0.0}%"),
            };

            if (m.SharedWithCount > 0)
            {
                var others = m.SharedWithNames.Where(n => !string.Equals(n, serviceName, StringComparison.OrdinalIgnoreCase));
                rows.Add(("Shared process:", $"yes - hosts {m.SharedWithCount + 1} services total"));
                rows.Add(("Also hosts:", string.Join(", ", others)));
            }
            else
            {
                rows.Add(("Shared process:", "no - dedicated process"));
            }
            return rows.ToArray();
        }

        private static string FormatBytes(long bytes) => bytes switch
        {
            <= 0 => "0 MB",
            _ when bytes >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.0} GB",
            _ => $"{bytes / (1024.0 * 1024):0} MB",
        };

        private static string Escape(string s) => s.Replace("'", "''");
    }
}
