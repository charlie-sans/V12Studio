using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using V12.Core.Core.Interfaces;
using System.Reflection;
using V12.Basic.Components;
using V12.Bindings;
using ComponentEntryAttribute = V12.Components.ComponentEntryAttribute;
using TransformComponent = V12.Components.TransformComponent;
using V12.Components.UI;

namespace V12Studio.Core
{
    /// <summary>One row of the New Component dialog catalogue.</summary>
    public sealed record CatalogEntry(
        string Category,
        string DisplayName,
        Type? ComponentType,
        Func<IComponent>? Factory,
        bool SingleInstance,
        string? ScriptPath = null,
        string? Language = null,
        string Icon = "");

    /// <summary>
    /// The component catalogue: engine built-ins plus script files discovered
    /// in the project ("Your Components"). Script entries attach through a
    /// ScriptComponent pointing at the file on disk.
    /// </summary>
    public static class ComponentCatalog
    {
        /// <summary>Built-in categories, in display order.</summary>
        public static readonly string[] Categories =
        {
            "TRANSFORM", "RENDER", "LIGHTING", "PHYSICS", "AUDIO", "GAMEPLAY", "UI",
        };

        /// <summary>Languages the New Component dialog can create, with the
        /// file extension each writes.</summary>
        public static readonly (string Language, string Extension)[] ScriptLanguages =
        {
            ("Contract", ".ct"),
            ("Lua", ".lua"),
            ("ObjektRT", ".orbt"),
            ("C#", ".cs"),
        };

        public static readonly string YourComponentsCategory = "Your Components";

        // The built-in catalogue is derived by reflection from the
        // [ComponentEntry] attribute stamped on each component class, so adding
        // a component to the Add Component menu is just stamping one attribute.
        // The two anchor types span both engine assemblies that hold components:
        // 19 live in the V12 assembly (TransformComponent); Camera lives in
        // V12.Basic.
        private static readonly Type[] CatalogAnchors =
        {
            typeof(TransformComponent),
            typeof(CanvasComponent),
            typeof(CameraComponent),
        };

        private static readonly IReadOnlyList<CatalogEntry> BuiltInEntriesCache = BuildBuiltIns();

        private static IReadOnlyList<CatalogEntry> BuildBuiltIns()
        {
            var seen = new HashSet<Type>();
            var found = new List<(CatalogEntry Entry, int SortOrder)>();
            foreach (var anchor in CatalogAnchors)
            {
                Type[] types;
                try { types = anchor.Assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                foreach (var type in types)
                {
                    if (!seen.Add(type)) continue;
                    if (type.IsAbstract || type.IsInterface) continue;
                    if (!typeof(IComponent).IsAssignableFrom(type)) continue;
                    var attr = type.GetCustomAttribute<ComponentEntryAttribute>();
                    if (attr == null) continue;
                    found.Add((new CatalogEntry(
                        attr.Category,
                        attr.DisplayName,
                        type,
                        () => (IComponent)Activator.CreateInstance(type)!,
                        attr.SingleInstance,
                        Icon: attr.Icon),
                        attr.SortOrder));
                        
                }
            }

            found.Sort((x, y) =>
            {
                int ci = CompareCategory(x.Entry.Category, y.Entry.Category);
                if (ci != 0) return ci;
                if (x.SortOrder != y.SortOrder) return x.SortOrder - y.SortOrder;
                return string.Compare(x.Entry.DisplayName, y.Entry.DisplayName, StringComparison.OrdinalIgnoreCase);
            });
            return found.Select(f => f.Entry).ToList();
        }

        private static int CompareCategory(string a, string b)
        {
            int ia = Array.IndexOf(Categories, a);
            int ib = Array.IndexOf(Categories, b);
            if (ia < 0 && ib < 0) return 0;
            if (ia < 0) return 1;
            if (ib < 0) return -1;
            return ia - ib;
        }

        public static IReadOnlyList<CatalogEntry> BuiltInEntries => BuiltInEntriesCache;

        /// <summary>Spec §4 group names for a catalogue key (the catalog keys
        /// stay machine-facing; the UI shows these).</summary>
        public static string CategoryTitle(string key) => key switch
        {
            "TRANSFORM" => "Transform",
            "RENDER" => "Rendering",
            "LIGHTING" => "Lighting",
            "PHYSICS" => "Physics",
            "AUDIO" => "Audio",
            "GAMEPLAY" => "Gameplay",
            "UI" => "UI",
            _ => key, // "Your Components", "Custom script…"
        };

        /// <summary>Spec §4 entry format: BossArena (Contract), SpinForever (C#).
        /// Script entries carry their language; built-ins show their name.</summary>
        public static string EntryDisplayName(CatalogEntry entry)
        {
            if (entry.Language != null) return $"{entry.DisplayName} ({entry.Language})";
            if (entry.ScriptPath == null) return entry.DisplayName;
            var lang = Path.GetExtension(entry.ScriptPath) switch
            {
                ".ct" => "Contract",
                ".lua" => "Lua",
                ".orbt" => "ObjektRT",
                ".cs" => "C#",
                var ext => ext,
            };
            return $"{entry.DisplayName} ({lang})";
        }

        private static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".ct", ".orbt", ".oil", ".lua",
        };

        private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", ".git", ".v12", ".godot", "node_modules",
        };

        /// <summary>Discover script files under <paramref name="projectDir"/>
        /// (depth ≤ 4) as "Your Components" catalogue entries. C# files only
        /// count when they live under a <c>scripts</c> folder — that is the
        /// folder compiled into the project's component assembly.</summary>
        public static IReadOnlyList<CatalogEntry> DiscoverScripts(string? projectDir)
        {
            if (string.IsNullOrWhiteSpace(projectDir) || !Directory.Exists(projectDir))
                return Array.Empty<CatalogEntry>();

            var results = new List<CatalogEntry>();
            foreach (var file in Directory.EnumerateFiles(projectDir, "*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file);
                bool isCSharp = ext.Equals(".cs", StringComparison.OrdinalIgnoreCase);
                if (!isCSharp && !ScriptExtensions.Contains(ext)) continue;

                var parts = Path.GetRelativePath(projectDir, file)
                    .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (parts.Length > 5) continue;
                if (parts.Any(p => IgnoredDirectories.Contains(p))) continue;
                if (isCSharp && !parts.Any(p => string.Equals(p, "scripts", StringComparison.OrdinalIgnoreCase)))
                    continue;

                results.Add(new CatalogEntry(
                    YourComponentsCategory,
                    Path.GetFileNameWithoutExtension(file),
                    null, null, false,
                    file,
                    Icon: "scroll"));
            }
            return results
                .OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Compiled runtime C# component types (project scripts and loaded paks)
        /// as catalogue entries tagged "(C#)". They attach like built-ins
        /// through <see cref="CSharpComponentRegistry"/>.
        /// </summary>
        public static IReadOnlyList<CatalogEntry> DiscoverCSharpComponents()
        {
            var registry = CSharpComponentRegistry.FromGameRoot();
            if (registry == null) return Array.Empty<CatalogEntry>();

            var results = new List<CatalogEntry>();
            foreach (var entry in registry.Entries
                .OrderBy(e => e.SortOrder)
                .ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                string typeName = entry.TypeName;
                results.Add(new CatalogEntry(
                    entry.Category,
                    entry.DisplayName,
                    entry.Type,
                    () => registry.Create(typeName),
                    entry.SingleInstance,
                    null,
                    "C#",
                    entry.Type.GetCustomAttribute<ComponentEntryAttribute>()?.Icon ?? ""));
            }
            return results;
        }

        /// <summary>C# identifier check for custom component names.</summary>
        public static bool IsValidIdentifier(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            var n = name.Trim();
            if (!char.IsLetter(n[0]) && n[0] != '_') return false;
            return n.All(c => char.IsLetterOrDigit(c) || c == '_');
        }
    }
}
