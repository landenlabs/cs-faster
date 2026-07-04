// Copyright (c) 2026 LanDen Labs - Dennis Lang

using System;
using System.IO;

namespace Faster
{
    /// <summary>
    /// Where Faster keeps its data. Per-user (%LocalAppData%) rather than machine-wide
    /// (%ProgramData%): the app can run either elevated or as a standard user from one run to
    /// the next, and a %ProgramData% file first written while elevated ends up with an ACL that
    /// denies a later standard-user process Modify/Delete rights on it - "sc.exe config" itself
    /// still needs admin for the parts of Activate that actually touch services, but the JSON
    /// stores no longer need to.
    /// </summary>
    public static class AppPaths
    {
        /// <summary>%LocalAppData%\Faster - created on first access if missing.</summary>
        public static string RootDir
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Faster");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string BaselinePath => Path.Combine(RootDir, "baseline.json");

        /// <summary>%LocalAppData%\Faster\user_lists - one JSON file per saved list (see
        /// ListStore), named after the list with any characters invalid in a Windows filename
        /// swapped for '_'. Kept in its own subdirectory, separate from RootDir's other
        /// top-level files (baseline.json, and any pre-existing flat lists.json from before this
        /// per-file layout - no longer read at all, see ListStore), so a directory listing of
        /// this one folder is exactly "the user's saved lists" with nothing else to filter out.</summary>
        public static string UserListsDir
        {
            get
            {
                string dir = Path.Combine(RootDir, "user_lists");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

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
