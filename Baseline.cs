// Copyright (c) 2026 LanDen Labs - Dennis Lang

using System;
using System.Collections.Generic;

namespace Faster
{
    /// <summary>The full-machine snapshot captured on first run (or by an explicit re-capture),
    /// keyed by service name. Every "restore to baseline" list item is undone against this.</summary>
    public sealed class Baseline
    {
        public DateTime CapturedUtc { get; set; }
        public Dictionary<string, ServiceSnapshot> Services { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
