// Copyright (c) 2026 LanDen Labs - Dennis Lang

using System;
using System.Collections.Generic;

namespace Faster
{
    /// <summary>
    /// A named, user-created group of service changes (e.g. "Gaming Mode", "Recording Rig") -
    /// the unit both the GUI and the headless <c>--activate &lt;name&gt;</c> command work with.
    /// </summary>
    public sealed class ServiceListDefinition
    {
        public string Name { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
        public DateTime? LastActivatedUtc { get; set; }
        public List<ServiceListItem> Items { get; set; } = new();
    }
}
