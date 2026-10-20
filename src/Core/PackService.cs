using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using V12.Core;
using V12.Pak;

namespace V12Studio.Core
{
    /// <summary>Manifest summary of a discovered .v12pak (no worlds loaded).</summary>
    public sealed record PackInfo(string Path, string Name, int WorldCount, int AssetCount, int GamepakCount, int ScriptCount);

    /// <summary>Outcome of a full pak load into the running runtime.</summary>
    public sealed record PackLoadOutcome(bool Success, string Message);

    /// <summary>
    /// Discover and load .v12pak archives. Discovery reads only the manifest
    /// (cheap); Load performs a real V12PakLoader load with worlds, assets,
    /// and scripts enabled (this is the dev tool, so DLL gamepaks are trusted).
    /// Loaded results stay alive until the session ends.
    /// </summary>
    public sealed class PackService
    {
        private readonly GameRoot _root;
        private readonly StudioSession _session;
        private readonly List<V12PakLoadResult> _loaded = new();

        public PackService(GameRoot root, StudioSession session)
        {
            _root = root;
            _session = session;
        }

        public IReadOnlyList<string> LoadedPaks =>
            _loaded.Select(r => r.PakPath).ToList();

        /// <summary>List *.v12pak in <paramref name="dir"/> with manifest summaries.
        /// Unreadable/corrupt paks are logged and skipped.</summary>
        public IReadOnlyList<PackInfo> Discover(string dir)
        {
            var results = new List<PackInfo>();
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                return results;

            foreach (var pak in Directory.EnumerateFiles(dir, "*.v12pak"))
            {
                try
                {
                    using var reader = new V12PakReader(pak);
                    var m = reader.Manifest;
                    results.Add(new PackInfo(
                        pak,
                        Path.GetFileNameWithoutExtension(pak),
                        m.Worlds?.Count ?? 0,
                        m.Assets?.Count ?? 0,
                        m.Paks?.Count ?? 0,
                        m.Scripts?.Count ?? 0));
                }
                catch (Exception ex)
                {
                    _session.Log.Warn($"Skipping unreadable pak '{Path.GetFileName(pak)}': {ex.Message}");
                }
            }
            return results;
        }

        /// <summary>Load a pak into the running runtime. Real load — worlds,
        /// assets, scripts, and DLL gamepaks all come in.</summary>
        public PackLoadOutcome Load(string pakPath)
        {
            if (string.IsNullOrWhiteSpace(pakPath) || !File.Exists(pakPath))
                return new PackLoadOutcome(false, $"Pak not found: {pakPath}");
            try
            {
                var options = new V12PakOptions
                {
                    LoadDlls = true,
                    LoadContractScripts = true,
                    AllowedAssetExtensions = null,
                    BlockedAssetKeys = null,
                    AllowWorldLoading = true,
                };
                var result = V12PakLoader.Load(pakPath, options, _root);
                _loaded.Add(result);
                var msg = $"Loaded '{Path.GetFileName(pakPath)}': " +
                          $"{result.Manifest.Worlds.Count} world(s), " +
                          $"{result.Manifest.Assets.Count} asset(s), " +
                          $"{result.SkippedGamepaks} gamepak(s) skipped, " +
                          $"{result.SkippedAssets} asset(s) skipped.";
                _session.Log.Info(msg);
                return new PackLoadOutcome(true, msg);
            }
            catch (Exception ex)
            {
                var msg = $"Load failed for '{Path.GetFileName(pakPath)}': {ex.Message}";
                _session.Log.Error(msg);
                return new PackLoadOutcome(false, msg);
            }
        }
    }
}
