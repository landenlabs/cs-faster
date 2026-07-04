// Copyright (c) 2026 LanDen Labs - Dennis Lang

using Microsoft.Win32;

namespace Faster
{
    /// <summary>
    /// Small reads against HKLM\SYSTEM\CurrentControlSet\Services\&lt;name&gt; for the two bits
    /// of configuration the Service Control Manager API doesn't expose: the "Delayed Start" flag
    /// on Automatic services, and whether a service has any trigger-start events registered.
    /// Read-only - Faster never edits trigger definitions.
    /// </summary>
    public static class RegistryHelpers
    {
        private const string ServicesKeyPath = @"SYSTEM\CurrentControlSet\Services\";

        /// <summary>True if the service's Start value is Automatic (2) AND DelayedAutoStart is 1.</summary>
        public static bool GetDelayedAutoStart(string serviceName)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(ServicesKeyPath + serviceName);
                object? value = key?.GetValue("DelayedAutoStart");
                return value is int i && i != 0;
            }
            catch
            {
                return false;   // access denied / key missing - treat as "not delayed"
            }
        }

        /// <summary>True if the service has a TriggerInfo subkey with at least one trigger entry.</summary>
        public static bool HasTriggerStart(string serviceName)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(ServicesKeyPath + serviceName + @"\TriggerInfo");
                if (key == null) return false;
                return key.GetSubKeyNames().Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
