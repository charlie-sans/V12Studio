using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using V12.Core.Core.Interfaces;
using V12.Basic.Components;
using MeshShape = V12.Components.MeshShape;
using MeshComponent = V12.Components.MeshComponent;
using MeshRenderer = V12.Components.Renderables.MeshRenderer;
using MaterialComponent = V12.Components.MaterialComponent;
using GenericLightComponent = V12.Components.GenericLightComponent;
using TransformComponent = V12.Components.TransformComponent;
using ScaleComponent = V12.Components.ScaleComponent;
using ColliderComponent = V12.Components.ColliderComponent;
using TagComponent = V12.Components.TagComponent;
using VelocityComponent = V12.Components.VelocityComponent;
using AudioSourceComponent = V12.Components.AudioSourceComponent;
using AudioListenerComponent = V12.Components.AudioListenerComponent;
using PointLightComponent = V12.Components.PointLightComponent;
using SpotLightComponent = V12.Components.SpotLightComponent;
using EnvironmentComponent = V12.Components.EnvironmentComponent;
using FogComponent = V12.Components.FogComponent;
using SpawnPointComponent = V12.Components.SpawnPointComponent;
using ScriptComponent = V12.Components.ScriptComponent;
using InteractionComponent = V12.Components.InteractionComponent;
using ParticleEmitterComponent = V12.Components.ParticleEmitterComponent;

namespace V12Studio.Core
{
    /// <summary>One row of the New Component dialog catalogue.</summary>
    public sealed record CatalogEntry(
        string Category,
        string DisplayName,
        Type? ComponentType,
        Func<IComponent>? Factory,
        bool SingleInstance,
        string? ScriptPath = null);

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
            "TRANSFORM", "RENDER", "LIGHTING", "PHYSICS", "AUDIO", "GAMEPLAY",
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

        private static readonly CatalogEntry[] BuiltIns =
        {
            new("TRANSFORM", "Transform", typeof(TransformComponent), () => new TransformComponent(), true),
            new("TRANSFORM", "Scale", typeof(ScaleComponent), () => new ScaleComponent(), true),
            new("RENDER", "Mesh", typeof(MeshComponent), () => new MeshComponent(), true),
            new("RENDER", "Mesh Renderer", typeof(MeshRenderer), () => new MeshRenderer(), true),
            new("RENDER", "Material", typeof(MaterialComponent), () => new MaterialComponent(), true),
            new("RENDER", "Camera", typeof(CameraComponent), () => new CameraComponent(), true),
            new("LIGHTING", "Directional Light", typeof(GenericLightComponent), () => new GenericLightComponent(), true),
            new("LIGHTING", "Point Light", typeof(PointLightComponent), () => new PointLightComponent(), true),
            new("LIGHTING", "Spot Light", typeof(SpotLightComponent), () => new SpotLightComponent(), true),
            new("LIGHTING", "Environment", typeof(EnvironmentComponent), () => new EnvironmentComponent(), true),
            new("LIGHTING", "Fog", typeof(FogComponent), () => new FogComponent(), true),
            new("PHYSICS", "Collider", typeof(ColliderComponent), () => new ColliderComponent(), true),
            new("PHYSICS", "Velocity", typeof(VelocityComponent), () => new VelocityComponent(), true),
            new("AUDIO", "Audio Source", typeof(AudioSourceComponent), () => new AudioSourceComponent(), false),
            new("AUDIO", "Audio Listener", typeof(AudioListenerComponent), () => new AudioListenerComponent(), true),
            new("GAMEPLAY", "Tag", typeof(TagComponent), () => new TagComponent(), true),
            new("GAMEPLAY", "Script", typeof(ScriptComponent), () => new ScriptComponent(), true),
            new("GAMEPLAY", "Spawn Point", typeof(SpawnPointComponent), () => new SpawnPointComponent(), true),
            new("GAMEPLAY", "Interaction", typeof(InteractionComponent), () => new InteractionComponent(), true),
            new("GAMEPLAY", "Particle Emitter", typeof(ParticleEmitterComponent), () => new ParticleEmitterComponent(), false),
        };

        public static IReadOnlyList<CatalogEntry> BuiltInEntries => BuiltIns;

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
            _ => key, // "Your Components", "Custom script…"
        };

        /// <summary>Spec §4 entry format: BossArena (Contract), SpinForever (C#).
        /// Script entries carry their language; built-ins show their name.</summary>
        public static string EntryDisplayName(CatalogEntry entry)
        {
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

        /// <summary>Discover script files under <paramref name="projectDir"/>
        /// (depth ≤ 4) as "Your Components" catalogue entries.</summary>
        public static IReadOnlyList<CatalogEntry> DiscoverScripts(string? projectDir)
        {
            if (string.IsNullOrWhiteSpace(projectDir) || !Directory.Exists(projectDir))
                return Array.Empty<CatalogEntry>();

            var results = new List<CatalogEntry>();
            foreach (var file in Directory.EnumerateFiles(projectDir, "*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file);
                if (!ScriptExtensions.Contains(ext)) continue;
                var relDepth = Path.GetRelativePath(projectDir, file).Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar);
                if (relDepth > 4) continue;
                results.Add(new CatalogEntry(
                    YourComponentsCategory,
                    Path.GetFileNameWithoutExtension(file),
                    null, null, false,
                    file));
            }
            return results
                .OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
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
