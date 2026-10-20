using System;
using System.IO;
using System.Linq;
using System.Numerics;
using V12.Basic.Components;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Interfaces.Renderer;
using V12.Core.UI;
using V12.Components.UI;
using V12.WorldML;
using MeshShape = V12.Components.MeshShape;
using MeshComponent = V12.Components.MeshComponent;
using MeshRenderer = V12.Components.Renderables.MeshRenderer;
using MaterialComponent = V12.Components.MaterialComponent;
using GenericLightComponent = V12.Components.GenericLightComponent;
using NonPersistentComponent = V12.Components.NonPersistentComponent;

namespace V12Studio.Core
{
    /// <summary>
    /// Host-agnostic editor session. Owns the editable scene world, selection,
    /// scene persistence, and the demo-content factory. The UI layer observes
    /// the events below and never mutates scene state except through the
    /// operation services (<see cref="HierarchyOps"/>, <see cref="TransformEdit"/>,
    /// <see cref="ComponentCreator"/>).
    /// </summary>
    public sealed class StudioSession : IEditor
    {
        private int _addSerial;

        public GameRoot Root { get; }
        public World? SceneWorld { get; private set; }
        public IWorldElement? Selected { get; private set; }
        public string? ScenePath { get; private set; }

        /// <summary>Project root: relative scene paths, default save location,
        /// and new script files all resolve against it. Set by the shell from
        /// the host's project directory.</summary>
        public string? ProjectDir { get; set; }

        /// <summary>Root element carrying a RenderWorld ViewportComponent —
        /// routes the whole scene world into the host's registered
        /// SubViewport. Hosts register the viewport under its Id.</summary>
        public IWorldElement? ViewportElement { get; private set; }

        public ConsoleLog Log { get; } = new();

        /// <summary>Viewport interaction service, registered by the host after
        /// the runtime starts. Session wires selection + gizmo + orbit camera
        /// to it when present; everything still works without it.</summary>
        public IViewportInteraction? Interaction { get; set; }

        public event Action? SelectionChanged;
        public event Action? HierarchyChanged;
        public event Action? ScenePathChanged;

        public StudioSession(GameRoot root)
        {
            Root = root;
        }

        /// <summary>IEditor</summary>
        public StudioSession Session => this;

        /// <summary>
        /// Create the scene world (once), seed demo content, and bind the
        /// viewport interaction service. The host calls this after registering
        /// <see cref="Interaction"/> so the orbit camera and gizmo are live
        /// from the first frame the UI shows.
        /// </summary>
        public void Start()
        {
            if (SceneWorld == null)
            {
                SceneWorld = new World("Scene");
                Root.Worlds.Add(SceneWorld);
                Root.SelectWorld(SceneWorld);
                Log.Info("Scene world created.");
            }
            EnsureViewportElement();
            if (SceneWorld.Root.Count == 1) // only the viewport element: nothing to edit yet
                CreateDemoScene();

            Interaction?.SetSelectHandler(Select);
            EnsureEditorCamera();
            Interaction?.SetGizmoTarget(Selected);
        }

        /// <summary>Current selection. Null clears the gizmo target.</summary>
        public void Select(IWorldElement? el)
        {
            if (ReferenceEquals(Selected, el)) return;
            Selected = el;
            Interaction?.SetGizmoTarget(el);
            SelectionChanged?.Invoke();
        }

        public void NotifyHierarchyChanged() => HierarchyChanged?.Invoke();

        public void SetScenePath(string? path)
        {
            ScenePath = path;
            ScenePathChanged?.Invoke();
        }

        /// <summary>Empty the scene world (keeps the world itself, camera binding reset).</summary>
        public void NewScene()
        {
            if (SceneWorld == null) return;
            Select(null);
            foreach (var child in SceneWorld.Root.ToList())
                SceneWorld.RemoveElement(child);
            EnsureViewportElement();
            EnsureEditorCamera(); // camera is editor-owned: recreate for the fresh scene
            SetScenePath(null);
            NotifyHierarchyChanged();
            Log.Info("New scene.");
        }

        /// <summary>Default save location: the project's scene.worldml.</summary>
        public string DefaultScenePath()
            => Path.Combine(ProjectDir ?? AppDomain.CurrentDomain.BaseDirectory, "scene.worldml");

        /// <summary>Save the scene world as WorldML XML at its current path
        /// (or the project default when the scene was never saved).</summary>
        public bool SaveScene(out string error)
            => SaveScene(ScenePath ?? DefaultScenePath(), out error);

        /// <summary>Save the scene world as WorldML XML to an explicit path
        /// and adopt it as the scene's path on success (Save As).</summary>
        public bool SaveScene(string path, out string error)
        {
            error = "";
            if (SceneWorld == null) { error = "No scene world."; return false; }
            var full = ResolvePath(path);
            try
            {
                var xml = WorldMLSerializer.Serialize(SceneWorld.Root, SceneWorld.WorldName ?? "Scene");
                File.WriteAllText(full, xml);
                SetScenePath(full);
                Log.Info($"Scene saved to {full} ({SceneWorld.Root.Count} root elements).");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Error($"Save failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Replace the scene world's contents with a WorldML file.
        /// Rebinds the orbit camera to a loaded "ViewportCamera" element when present.</summary>
        public bool LoadScene(string path, out string error)
        {
            error = "";
            if (SceneWorld == null) { error = "No scene world."; return false; }
            var full = ResolvePath(path);
            try
            {
                var parser = new WorldMLParser();
                var parsed = (Element)parser.ParseFile(full);

                Select(null);
                foreach (var child in SceneWorld.Root.ToList())
                    SceneWorld.RemoveElement(child);
                foreach (var child in parsed.Children.ToList())
                {
                    parsed.RemoveChild(child);
                    SceneWorld.AddElement(child);
                }
                EnsureViewportElement(); // loaded scenes may not carry one

                EnsureEditorCamera(); // likewise: the orbit camera is editor-owned
                SetScenePath(full);
                NotifyHierarchyChanged();
                Log.Info($"Scene loaded from {full} ({SceneWorld.Root.Count} root elements).");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Error($"Load failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Seed the demo scene: sun, ground, three coloured primitives,
        /// and the "ViewportCamera" the orbit controller drives.</summary>
        public void CreateDemoScene()
        {
            if (SceneWorld == null) return;

            var sun = new Element("Sun");
            sun.AddComponent(new GenericLightComponent
            {
                Type = LightType.Directional,
                ColorR = 1f, ColorG = 0.97f, ColorB = 0.92f,
                Energy = 1.2f
            });
            sun.LocalTransform = new TRS
            {
                Position = new Vector3(0, 8f, 4f),
                Rotation = Quaternion.CreateFromYawPitchRoll(-0.6f, -0.9f, 0f),
                Scale = Vector3.One
            };
            SceneWorld.AddElement(sun);

            var groundMesh = new MeshComponent { Shape = MeshShape.Box, Width = 12f, Height = 0.2f, Depth = 12f };
            var ground = new Element("Ground");
            ground.AddComponent(groundMesh);
            ground.AddComponent(new MeshRenderer { Mesh = groundMesh });
            ground.AddComponent(new MaterialComponent { R = 0.24f, G = 0.26f, B = 0.3f, A = 1f, Roughness = 0.9f });
            ground.LocalTransform = new TRS { Position = new Vector3(0, -0.1f, 0), Rotation = Quaternion.Identity, Scale = Vector3.One };
            SceneWorld.AddElement(ground);

            var redBox = MakeEditorMesh("RedBox", MeshShape.Box, 0.8f, 0.8f, 0.8f, 0.85f, 0.2f, 0.2f);
            redBox.LocalTransform = new TRS { Position = new Vector3(-1.6f, 0.5f, 0f), Rotation = Quaternion.Identity, Scale = Vector3.One };
            SceneWorld.AddElement(redBox);

            var blueSphere = MakeEditorMesh("BlueSphere", MeshShape.Sphere, 0.6f, 0.6f, 0.6f, 0.2f, 0.35f, 0.9f);
            blueSphere.LocalTransform = new TRS { Position = new Vector3(0f, 0.6f, 0f), Rotation = Quaternion.Identity, Scale = Vector3.One };
            SceneWorld.AddElement(blueSphere);

            var greenCapsule = MakeEditorMesh("GreenCapsule", MeshShape.Capsule, 0.4f, 1.2f, 0.4f, 0.25f, 0.8f, 0.35f);
            greenCapsule.LocalTransform = new TRS { Position = new Vector3(1.6f, 0.7f, 0f), Rotation = Quaternion.Identity, Scale = Vector3.One };
            SceneWorld.AddElement(greenCapsule);

            EnsureEditorCamera();

            NotifyHierarchyChanged();
            Log.Info($"Demo scene created ({SceneWorld.Root.Count} root elements).");
        }

        /// <summary>
        /// Ensure the scene world carries the editor's orbit camera: an element
        /// named "ViewportCamera" with an IsCurrent CameraComponent, marked
        /// <see cref="NonPersistentComponent"/> so it renders and picks in the
        /// editor but is never written to the saved world. Re-adopted from the
        /// scene when one was authored there; created fresh otherwise. Binds it
        /// to the viewport interaction service when present.
        /// </summary>
        public void EnsureEditorCamera()
        {
            if (SceneWorld == null) return;

            var cam = FindSceneElement("ViewportCamera");
            if (cam == null)
            {
                cam = new Element("ViewportCamera");
                cam.AddComponent(new CameraComponent { Active = true, IsCurrent = true, Fov = 60f, FarClip = 1000f });
                cam.LocalTransform = new TRS
                {
                    Position = new Vector3(0f, 2.6f, 7f),
                    Rotation = Quaternion.CreateFromYawPitchRoll(0f, -0.35f, 0f),
                    Scale = Vector3.One
                };
                SceneWorld.AddElement(cam);
            }
            if (cam.GetComponent<NonPersistentComponent>() == null)
                cam.AddComponent(new NonPersistentComponent()); // editor-owned: kept out of the saved world
            if (cam.GetComponent<EditorOnlyComponent>() == null)
                cam.AddComponent(new EditorOnlyComponent()); // editor plumbing: hidden from the hierarchy

            Interaction?.SetEditorCamera(cam);
        }

        /// <summary>Find an element by name anywhere in the scene world.</summary>
        public IWorldElement? FindSceneElement(string name)
        {
            if (SceneWorld == null) return null;
            foreach (var el in SceneWorld.Root)
            {
                if (string.Equals(el.Name, name, StringComparison.OrdinalIgnoreCase)) return el;
                var found = el.FindChildByNameRecursive(name);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>True when <paramref name="el"/> belongs to the scene world.
        /// Only scene elements are editable/deletable.</summary>
        public bool IsSceneElement(IWorldElement? el)
        {
            if (el == null || SceneWorld == null) return false;
            var cur = el;
            while (cur.Parent != null)
                cur = cur.Parent;
            return SceneWorld.Root.Contains(cur);
        }

        /// <summary>Ensure the scene world has exactly one RenderWorld
        /// ViewportComponent root element (the editor game-view router).
        /// Creates "EditorViewport" if none exists; adopts the first one found
        /// otherwise (a loaded scene may carry its own).</summary>
        public void EnsureViewportElement()
        {
            if (SceneWorld == null) return;

            var existing = SceneWorld.Root
                .FirstOrDefault(e => e.GetComponent<ViewportComponent>() is { RenderWorld: true });
            if (existing != null)
            {
                if (existing.GetComponent<EditorOnlyComponent>() == null)
                    existing.AddComponent(new EditorOnlyComponent());
                ViewportElement = existing;
                return;
            }

            var vp = new Element("EditorViewport");
            vp.AddComponent(new ViewportComponent { RenderWorld = true });
            vp.AddComponent(new EditorOnlyComponent()); // editor plumbing: hidden from the hierarchy
            SceneWorld.AddElement(vp);
            ViewportElement = vp;
            Log.Info($"Editor viewport element created (id {vp.Id}).");
        }

        /// <summary>Resolve a possibly-relative path against the project
        /// directory (falling back to the executable directory when no
        /// project is open).</summary>
        public string ResolvePath(string path)
        {
            if (Path.IsPathRooted(path))
                return Path.GetFullPath(path);
            var root = ProjectDir ?? AppDomain.CurrentDomain.BaseDirectory;
            return Path.GetFullPath(Path.Combine(root, path));
        }

        /// <summary>Build an element with a coloured primitive mesh.</summary>
        public static Element MakeEditorMesh(string name, MeshShape shape, float w, float h, float d,
            float r, float g, float b)
        {
            var mesh = new MeshComponent { Shape = shape, Width = w, Height = h, Depth = d };
            var el = new Element(name);
            el.AddComponent(mesh);
            el.AddComponent(new MeshRenderer { Mesh = mesh });
            el.AddComponent(new MaterialComponent { R = r, G = g, B = b, A = 1f, Roughness = 0.5f, Metallic = 0.1f });
            return el;
        }

        /// <summary>Next name serial for newly added elements.</summary>
        internal int NextAddSerial() => _addSerial++;
    }
}
