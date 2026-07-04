// Copyright (c) 2026 LanDen Labs - Dennis Lang

using System;
using System.IO;

namespace Faster
{
    /// <summary>
    /// Where Faster keeps its data. Machine-wide (%ProgramData%) rather than per-user, because
    /// services are a machine-level concept - a saved list should look the same no matter which
    /// admin account launches the app.
    /// </summary>
    public static class AppPaths
    {
        /// <summary>C:\ProgramData\Faster - created on first access if missing.</summary>
        public static string RootDir
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Faster");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string BaselinePath => Path.Combine(RootDir, "baseline.json");
        public static string ListsPath => Path.Combine(RootDir, "lists.json");

        /// <summary>
        /// Writes <paramref name="json"/> to <paramref name="path"/> via a temp file + move, so a
        /// crash or power loss mid-write can never leave a half-written, corrupt store behind.
        /// </summary>
        public static void WriteAtomic(string path, string json)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
    }
}
