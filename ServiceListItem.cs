// Copyright (c) 2026 LanDen Labs - Dennis Lang

using System.ServiceProcess;

namespace Faster
{
    /// <summary>What to do with one service when its list is activated.</summary>
    public enum ServiceTargetAction
    {
        /// <summary>Stop the service (if running) and set its start type - typically Disabled,
        /// so it can't restart itself on the next boot or trigger event.</summary>
        Stop,

        /// <summary>Set the service's start type - typically Automatic or Manual - and start it
        /// (if not already running).</summary>
        Start,

        /// <summary>Ignore <see cref="ServiceListItem.TargetStartType"/> and instead put the
        /// service back exactly how the baseline snapshot found it: same start type, same
        /// delayed-auto flag, same running/stopped state. This is "the inverse" of a Stop item -
        /// most Start lists are built this way rather than picking a fresh start type by hand.</summary>
        RestoreToBaseline,
    }

    /// <summary>One service's entry inside a named <see cref="ServiceListDefinition"/>.</summary>
    public sealed class ServiceListItem
    {
        public string ServiceName { get; set; } = "";

        /// <summary>Cached for display only (grids/reports) - not authoritative; the live
        /// <see cref="ServiceController"/> is always re-queried before any change is applied.</summary>
        public string DisplayName { get; set; } = "";

        public ServiceTargetAction Action { get; set; } = ServiceTargetAction.Stop;

        /// <summary>Start type to apply for <see cref="ServiceTargetAction.Stop"/> or
        /// <see cref="ServiceTargetAction.Start"/> items. Ignored for RestoreToBaseline, which
        /// pulls the start type from the baseline snapshot instead.</summary>
        public ServiceStartMode TargetStartType { get; set; } = ServiceStartMode.Disabled;

        /// <summary>Only meaningful when <see cref="TargetStartType"/> is Automatic.</summary>
        public bool TargetDelayedAutoStart { get; set; }
    }
}
