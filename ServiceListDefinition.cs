// Copyright (c) 2026 LanDen Labs - Dennis Lang

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

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

        // NOT persisted - ListStore.TryRead fills this in from the backing file's own last-write
        // time right after deserializing it, since the filesystem already tracks "when was this
        // list last saved" and a separate hand-stamped field would just be a second copy of the
        // same fact to keep in sync. Feeds the "Saved lists" table's Modified column. Defaults to
        // DateTime.MinValue for a ServiceListDefinition that was never loaded via ListStore (e.g.
        // RestoreAllToBaseline's one-off, never-persisted list) - fine, since that one is never
        // shown in the table either.
        [JsonIgnore]
        public DateTime ModifiedUtc { get; set; }
    }
}
