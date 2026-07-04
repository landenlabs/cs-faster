// Copyright (c) 2026 LanDen Labs - Dennis Lang

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Faster
{
    /// <summary>
    /// Loads/saves the user's named service lists - one JSON file per list under
    /// <see cref="AppPaths.UserListsDir"/> (<c>%LocalAppData%\Faster\user_lists\&lt;name&gt;.json</c>),
    /// rather than a single flat <c>lists.json</c> array. A pre-existing flat <c>lists.json</c>
    /// from before this layout, if present, is deliberately never read - there's no migration,
    /// it's just ignored. Each list's "Modified" timestamp (shown in the Lists tab's table) is
    /// the file's own last-write time, not a hand-stamped field - the filesystem already tracks
    /// this, so there's nothing to keep in sync.
    /// </summary>
    public static class ListStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
            { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        /// <summary>Reads every *.json file in UserListsDir. A single corrupt/unreadable file is
        /// skipped rather than failing the whole load - matches the rest of the app's convention
        /// of isolating one bad item instead of aborting (e.g. ServiceOps.Activate per-service).</summary>
        public static List<ServiceListDefinition> LoadAll()
        {
            var result = new List<ServiceListDefinition>();
            foreach (string path in Directory.EnumerateFiles(AppPaths.UserListsDir, "*.json"))
            {
                var list = TryRead(path);
                if (list != null) result.Add(list);
            }
            return result;
        }

        private static ServiceListDefinition? TryRead(string path)
        {
            try
            {
                string json = File.ReadAllText(path);
                var list = JsonSerializer.Deserialize<ServiceListDefinition>(json, JsonOptions);
                if (list == null) return null;
                // Not persisted in the JSON itself - see the class doc comment above.
                list.ModifiedUtc = File.GetLastWriteTimeUtc(path);
                return list;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Case-insensitive lookup by name, or null if no list has that name.</summary>
        public static ServiceListDefinition? FindByName(List<ServiceListDefinition> lists, string name) =>
            lists.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>Creates or overwrites the one file for this list (matched by Name, not by
        /// filename - see FindFilePath) - the per-file equivalent of the old "remove by name,
        /// add, save the whole array" Upsert.</summary>
        public static void Upsert(ServiceListDefinition list)
        {
            string path = FindFilePath(list.Name) ?? AllocateNewFilePath(list.Name);
            AppPaths.WriteAtomic(path, JsonSerializer.Serialize(list, JsonOptions));
            list.ModifiedUtc = File.GetLastWriteTimeUtc(path);
        }

        /// <summary>Removes a list by name. Returns false if no list had that name.</summary>
        public static bool Delete(string name)
        {
            string? path = FindFilePath(name);
            if (path == null) return false;
            File.Delete(path);
            return true;
        }

        /// <summary>Records that a list was just activated (persists LastActivatedUtc).</summary>
        public static void MarkActivated(string name)
        {
            string? path = FindFilePath(name);
            if (path == null) return;
            var list = TryRead(path);
            if (list == null) return;
            list.LastActivatedUtc = DateTime.UtcNow;
            AppPaths.WriteAtomic(path, JsonSerializer.Serialize(list, JsonOptions));
        }

        /// <summary>Finds the existing file whose stored Name matches (case-insensitive) -
        /// content is the source of truth for "which list is this", not the filename, so this
        /// still finds the right file even if the sanitized name would collide with an unrelated
        /// list's file (see AllocateNewFilePath) or if a file was renamed by hand outside the app.</summary>
        private static string? FindFilePath(string name)
        {
            foreach (string path in Directory.EnumerateFiles(AppPaths.UserListsDir, "*.json"))
            {
                var list = TryRead(path);
                if (list != null && string.Equals(list.Name, name, StringComparison.OrdinalIgnoreCase))
                    return path;
            }
            return null;
        }

        /// <summary>Picks a not-yet-used file path for a brand new list name - the sanitized name
        /// itself, or that name with a numeric suffix if some unrelated list already claimed it
        /// (e.g. two names that only differ in characters invalid in a filename).</summary>
        private static string AllocateNewFilePath(string name)
        {
            string baseName = SanitizeFileName(name);
            string path = Path.Combine(AppPaths.UserListsDir, baseName + ".json");
            for (int suffix = 2; File.Exists(path); suffix++)
                path = Path.Combine(AppPaths.UserListsDir, $"{baseName}_{suffix}.json");
            return path;
        }

        /// <summary>Replaces every character Windows disallows in a filename with '_'. Falls back
        /// to "list" for a name that sanitizes away to nothing (e.g. all-invalid-characters).</summary>
        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(invalid.Contains(c) ? '_' : c);
            string sanitized = sb.ToString().Trim();
            return sanitized.Length == 0 ? "list" : sanitized;
        }
    }
}
