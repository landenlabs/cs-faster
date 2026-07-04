// Copyright (c) 2026 LanDen Labs - Dennis Lang

using System;
using System.Reflection;

namespace Faster
{
    /// <summary>
    /// Central application metadata - the single runtime source for the version shown in-app.
    /// Mirrors cs-b4browse's AppInfo.cs exactly: Version and BuildDate are read from the
    /// assembly's own metadata, which the build derives from Faster.csproj - the &lt;Version&gt;
    /// property flows into the assembly's informational version, and a build-time-stamped
    /// AssemblyMetadata("BuildDate") carries the date. The repo-root `set-version.ps1` (see
    /// c:\opt\projects\common\set-version.ps1) bumps &lt;Version&gt; in the csproj (and the
    /// VERSION file / README markers) on release, so the in-app version follows automatically -
    /// there is no separate hand-edited constant to keep in sync.
    /// </summary>
    internal static class AppInfo
    {
        private static readonly Assembly Asm = typeof(AppInfo).Assembly;

        /// <summary>Bare semantic version, e.g. "1.0.0" (no leading 'v'); from csproj &lt;Version&gt;.</summary>
        public static string Version { get; } = ReadVersion();

        /// <summary>Build date in dd-MMM-yyyy form, stamped into the assembly at build time.</summary>
        public static string BuildDate { get; } = ReadMetadata("BuildDate");

        public const string Product = "Faster";
        public const string Company = "LanDen Labs";
        public const string Author = "Dennis Lang";

        // Copyright; the 4-digit year is maintained in the csproj <Copyright> alongside this.
        public const string Copyright = "LanDen Labs (2026)";

        private static string ReadVersion()
        {
            // <Version> flows to AssemblyInformationalVersion (a string that preserves "1.0.0");
            // the .NET SDK may append "+<git-sha>", so trim at the '+'.
            string? info = Asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(info))
            {
                int plus = info.IndexOf('+');
                return plus >= 0 ? info[..plus] : info;
            }
            // Fallback: the numeric assembly version (loses any leading zeros, but never blank).
            System.Version? v = Asm.GetName().Version;
            return v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";
        }

        private static string ReadMetadata(string key)
        {
            foreach (var m in Asm.GetCustomAttributes<AssemblyMetadataAttribute>())
                if (m.Key == key && !string.IsNullOrEmpty(m.Value))
                    return m.Value!;
            return "";
        }
    }
}
