// Copyright (c) 2026 LanDen Labs - Dennis Lang

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Faster
{
    /// <summary>Loads/saves the user's named service lists (lists.json - a flat array).</summary>
    public static class ListStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
            { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        public static List<ServiceListDefinition> LoadAll()
        {
            if (!File.Exists(AppPaths.ListsPath)) return new List<ServiceListDefinition>();
            try
            {
                string json = File.ReadAllText(AppPaths.ListsPath);
                return JsonSerializer.Deserialize<List<ServiceListDefinition>>(json, JsonOptions)
                       ?? new List<ServiceListDefinition>();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Could not read saved lists at '{AppPaths.ListsPath}': {ex.Message}", ex);
            }
        }

        public static void SaveAll(List<ServiceListDefinition> lists) =>
            AppPaths.WriteAtomic(AppPaths.ListsPath, JsonSerializer.Serialize(lists, JsonOptions));

        /// <summary>Case-insensitive lookup by name, or null if no list has that name.</summary>
        public static ServiceListDefinition? FindByName(List<ServiceListDefinition> lists, string name) =>
            lists.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>Adds or replaces (by name) a list, then persists the whole set.</summary>
        public static void Upsert(ServiceListDefinition list)
        {
            var lists = LoadAll();
            lists.RemoveAll(l => string.Equals(l.Name, list.Name, StringComparison.OrdinalIgnoreCase));
            lists.Add(list);
            SaveAll(lists);
        }

        /// <summary>Removes a list by name. Returns false if no list had that name.</summary>
        public static bool Delete(string name)
        {
            var lists = LoadAll();
            int removed = lists.RemoveAll(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
            if (removed > 0) SaveAll(lists);
            return removed > 0;
        }

        /// <summary>Records that a list was just activated (persists LastActivatedUtc).</summary>
        public static void MarkActivated(string name)
        {
            var lists = LoadAll();
            var list = FindByName(lists, name);
            if (list == null) return;
            list.LastActivatedUtc = DateTime.UtcNow;
            SaveAll(lists);
        }
    }
}
