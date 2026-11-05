using System;
using System.IO;
using System.Text.Json;
using V12.Pak;

namespace V12Studio.Core
{
    /// <summary>Outcome of a build-project-into-gamepak attempt.</summary>
    public sealed record GamepackBuildResult(bool Success, string Message, string OutputPath = "", long SizeBytes = 0);

    /// <summary>
    /// Builds a project into a .v12pak through the engine's V12.Pak writer
    /// (<see cref="V12PakBuilder"/>) — the same library, and the same format,
    /// that <see cref="PackService"/> loads paks back with. Packing and loading
    /// therefore stay symmetric, so anything built here loads in the studio and
    /// in a V12 host.
    ///
    /// The packed directory is the one holding <c>contract.ctproj</c> when there
    /// is one (its <c>worlds/</c>, <c>assets/</c>, <c>paks/</c>, and
    /// <c>scripts/</c> layout is what V12.Pak expects), otherwise the project
    /// directory itself. The pak is named after contract.ctproj's Name, falling
    /// back to the folder name.
    /// </summary>
    public sealed class GamepackBuilder
    {
        private readonly StudioSession _session;

        public GamepackBuilder(StudioSession session)
        {
            _session = session;
        }

        /// <summary>Pack <paramref name="projectDir"/> into a .v12pak.
        /// <paramref name="outputPath"/> defaults to
        /// <c>&lt;project&gt;/bin/&lt;name&gt;.v12pak</c>.</summary>
        public GamepackBuildResult Build(string projectDir, string? outputPath = null)
        {
            if (string.IsNullOrWhiteSpace(projectDir) || !Directory.Exists(projectDir))
                return Fail($"Project directory not found: '{projectDir}'.");

            var full = Path.GetFullPath(projectDir);
            var ctProj = FindCtproj(full);
            var packRoot = ctProj != null ? Path.GetDirectoryName(ctProj)! : full;
            var name = ProjectName(ctProj, packRoot);
            var outPath = ResolveOutput(outputPath, packRoot, name);

            if (ctProj == null)
                _session.Log.Warn($"No contract.ctproj found in '{full}'; packing the directory as-is.");

            try
            {
                _session.Log.Info($"Packing gamepak from '{packRoot}' → '{outPath}'...");
                V12PakBuilder.Build(packRoot, outPath);
                var size = new FileInfo(outPath).Length;
                var msg = $"Built '{Path.GetFileName(outPath)}' ({FormatBytes(size)}).";
                _session.Log.Info(msg);
                return new GamepackBuildResult(true, msg, outPath, size);
            }
            catch (Exception ex)
            {
                return Fail($"Pack failed: {ex.Message}");
            }
        }

        private GamepackBuildResult Fail(string message)
        {
            _session.Log.Error(message);
            return new GamepackBuildResult(false, message);
        }

        /// <summary>Resolve a possibly-relative output path against the pack
        /// root; default is bin/&lt;name&gt;.v12pak.</summary>
        private static string ResolveOutput(string? outputPath, string packRoot, string name)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
                return Path.Combine(packRoot, "bin", name + ".v12pak");
            return Path.IsPathRooted(outputPath)
                ? Path.GetFullPath(outputPath)
                : Path.GetFullPath(Path.Combine(packRoot, outputPath));
        }

        /// <summary>Project name from contract.ctproj's Name, else the folder name.</summary>
        private static string ProjectName(string? ctProj, string packRoot)
        {
            if (ctProj != null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(ctProj));
                    if (doc.RootElement.TryGetProperty("Name", out var name)
                        && name.GetString() is { Length: > 0 } value)
                        return value;
                }
                catch
                {
                    // Unreadable ctproj: fall back to the folder name.
                }
            }
            var trimmed = packRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.IsNullOrEmpty(trimmed) ? packRoot : Path.GetFileName(trimmed);
        }

        /// <summary>Walk up from <paramref name="dir"/> looking for contract.ctproj.</summary>
        private static string? FindCtproj(string dir)
        {
            while (true)
            {
                var candidate = Path.Combine(dir, "contract.ctproj");
                if (File.Exists(candidate)) return candidate;
                var parent = Directory.GetParent(dir);
                if (parent == null || !parent.Exists) return null;
                dir = parent.FullName;
            }
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double value = bytes;
            var i = 0;
            while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; }
            return $"{value:F1}{units[i]}";
        }
    }
}
