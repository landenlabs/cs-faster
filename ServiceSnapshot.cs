// Copyright (c) 2026 LanDen Labs - Dennis Lang

using System;
using System.ServiceProcess;

namespace Faster
{
    /// <summary>
    /// One service's captured configuration: what it looked like at the moment the baseline
    /// (or a later re-capture) was taken. This is the "restore point" every named list's
    /// "restore to baseline" items are undone against.
    /// </summary>
    public sealed class ServiceSnapshot
    {
        public string ServiceName { get; set; } = "";
        public string DisplayName { get; set; } = "";

        /// <summary>Boot / System / Automatic / Manual / Disabled (as reported by the SCM).</summary>
        public ServiceStartMode StartType { get; set; } = ServiceStartMode.Manual;

        /// <summary>True when an Automatic service is configured "Automatic (Delayed Start)".
        /// Meaningless (and always false) for non-Automatic start types.</summary>
        public bool DelayedAutoStart { get; set; }

        /// <summary>True if the service has one or more trigger-start events registered
        /// (HKLM\SYSTEM\CurrentControlSet\Services\&lt;name&gt;\TriggerInfo). Informational only -
        /// Faster does not add, remove, or restore individual triggers, since a service's trigger
        /// set is Windows/driver-defined configuration, not something a start-type toggle should
        /// touch. Shown so the user understands why a "Manual" service might still start itself.</summary>
        public bool HasTriggers { get; set; }

        /// <summary>Whether the service was running when this snapshot was captured.</summary>
        public bool WasRunning { get; set; }

        public DateTime CapturedUtc { get; set; }
    }
}
