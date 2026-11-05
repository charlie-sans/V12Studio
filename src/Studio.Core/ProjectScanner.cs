using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace V12Studio.Core
{
    /// <summary>A file found by the project scanner.</summary>
    public sealed record AssetRecord(string FullPath, string Folder, string Name, string Type);

    /// <summary>
    /// Bounded project asset scan (depth ≤ 4 by default, bin/obj/.git skipped).
    /// Drives the Project tab's file list; re-scans on demand, never on a timer.
    /// </summary>
    public sealed class ProjectScanner
    {
        public const int DefaultMaxDepth = 4;

        private static readonly (string Ext, string Type)[] ExtensionMap =
        {
            (".worldml", "Scene"),
            (".v12pak", "Pack"),
            (".glb", "Model"), (".gltf", "Model"), (".obj", "Model"), (".fbx", "Model"),
            (".png", "Texture"), (".jpg", "Texture"), (".jpeg", "Texture"),
            (".tga", "Texture"), (".dds", "Texture"), (".bmp", "Texture"),
            (".wav", "Audio"), (".ogg", "Audio"), (".mp3", "Audio"),
            (".ct", "Script"), (".contract", "Script"), (".orbt", "Script"), (".oil", "Script"),
            (".lua", "Script"), (".cs", "Script"),
            (".json", "Data"), (".xml", "Data"),
        };

        private static readonly HashSet<string> SkippedDirs = new(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", ".git", ".godot", ".vs", ".idea", "node_modules",
        };

        /// <summary>Scan <paramref name="rootDir"/> for known asset types.
        /// Results are sorted by folder then name.</summary>
        public IReadOnlyList<AssetRecord> Scan(string rootDir, int maxDepth = DefaultMaxDepth)
        {
            var results = new List<AssetRecord>();
            if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir))
                return results;

            var rootFull = Path.GetFullPath(rootDir);
            ScanDir(rootFull, rootFull, 0, maxDepth, results);
            return results
                .OrderBy(a => a.Folder, StringComparer.OrdinalIgnoreCase)
                .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Distinct first-level folders present in the scan results.</summary>
        public IReadOnlyList<string> Folders(IEnumerable<AssetRecord> assets)
        {
            return assets.Select(a => a.Folder)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static string TypeFor(string path)
        {
            var ext = Path.GetExtension(path);
            foreach (var (e, type) in ExtensionMap)
                if (string.Equals(ext, e, StringComparison.OrdinalIgnoreCase))
                    return type;
            return "Other";
        }

        private static void ScanDir(string rootFull, string dir, int depth, int maxDepth, List<AssetRecord> results)
        {
            if (depth > maxDepth) return;

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir); }
            catch { return; } // unreadable directory: skip, don't fail the scan

            foreach (var file in files)
            {
                var type = TypeFor(file);
                if (type == "Other") continue;
                var rel = Path.GetRelativePath(rootFull, file);
                var folder = Path.GetDirectoryName(rel);
                if (string.IsNullOrEmpty(folder)) folder = "(root)";
                else folder = folder.Replace('\\', '/');
                results.Add(new AssetRecord(file, folder, Path.GetFileName(file), type));
            }

            if (depth == maxDepth) return;

            IEnumerable<string> subdirs;
            try { subdirs = Directory.EnumerateDirectories(dir); }
            catch { return; }

            foreach (var sub in subdirs)
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.') || SkippedDirs.Contains(name))
                    continue;
                ScanDir(rootFull, sub, depth + 1, maxDepth, results);
            }
        }
    }
}
