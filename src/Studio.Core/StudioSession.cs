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
using EditorOnlyComponent = V12.Components.EditorOnlyComponent;

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
        private World? _editorSceneWorld;
        private World? _playWorld;
        public Action<Action>? Dispatch { get; set; }
        public void SelectFromWorker(IWorldElement? el)
        {
            if (Dispatch != null) Dispatch(() => Select(el)); else Select(el);
        }
        public GameRoot Root { get; }
        /// <summary>Canonical editable scene world. Non-ticking while editing; cloned into PlayWorld for Play.</summary>
        public World? EditorSceneWorld
        {
            get => _editorSceneWorld;
            private set
            {
                _editorSceneWorld = value;
                if (value != null) Root.EditorSceneWorld = value;
            }
        }
        /// <summary>Backwards compat alias: returns <see cref="EditorSceneWorld"/>.</summary>
        public World? SceneWorld => EditorSceneWorld;
        /// <summary>Transient ticking clone created on EnterPlayMode; null when not playing.</summary>
        public World? PlayWorld => _playWorld;
        public bool IsPlaying => _playWorld != null;
        /// <summary>Lock editing while playing. TODO: disable this after tested to allow live tweaks.</summary>
        public bool IsSceneLocked => IsPlaying;
        public event Action? PlayModeChanged;
        public IWorldElement? Selected { get; private set; }
        public string? ScenePath { get; private set; }

        /// <summary>Project root: relative scene paths, default save location,
        /// and new script files all resolve against it. Set by the shell from
        /// the host's project directory.</summary>
        public string? ProjectDir { get; set; }

        /// <summary>Root element carrying a RenderWorld ViewportComponent —
        /// routes the whole scene world into the host's registered
        /// SubViewport. Hosts register the viewport under its Id. Lives in PersistentWorld.</summary>
        public IWorldElement? ViewportElement { get; private set; }

        /// <summary>Raised when <see cref="ViewportElement"/> is replaced by a
        /// different element. Scene loads wipe the scene world's roots —
        /// including the old viewport element — so the id changes and the host
        /// must re-register its SubViewport under the new one; otherwise the
        /// renderer never reparents scene nodes into the SubViewport (grey game
        /// view, no camera, picking fails).</summary>
        public event Action? ViewportElementChanged;

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
            CSharp = new CSharpComponentService(this);
        }

        /// <summary>Runtime C# components compiled from the project's <c>scripts</c> folder.</summary>
        public CSharpComponentService CSharp { get; }

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
            if (EditorSceneWorld == null)
            {
                var w = new World("Scene") { TickingEnabled = false };
                EditorSceneWorld = w;
                Root.Worlds.Add(w);
                Log.Info("EditorSceneWorld created (non-ticking).");
            }
            // The host may have created and selected its own placeholder world
            // (V12Runtime does this when no gamepak owns a world). Rendering and
            // the default environment follow SelectedWorld, so point it at the
            // editable scene; it stays non-ticking, so nothing runs.
            if (!ReferenceEquals(Root.SelectedWorld, EditorSceneWorld))
                Root.SelectWorld(EditorSceneWorld);
            EnsurePersistentViewportElement();
            EnsurePersistentEditorCamera();
            if (EditorSceneWorld!.Root.Count == 0)
                CreateDemoScene();

            Interaction?.SetSelectHandler(SelectFromWorker);
            // Re-bind to ensure interaction has the persistent camera/viewport
            EnsurePersistentEditorCamera();
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
            if (EditorSceneWorld == null) return;
            Select(null);
            foreach (var child in EditorSceneWorld.Root.ToList())
                EditorSceneWorld.RemoveElement(child);
            EnsurePersistentViewportElement();
            EnsurePersistentEditorCamera();
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
            if (EditorSceneWorld == null) { error = "No scene world."; return false; }
            var full = ResolvePath(path);
            try
            {
                var xml = WorldMLSerializer.Serialize(EditorSceneWorld.Root, EditorSceneWorld.WorldName ?? "Scene");
                File.WriteAllText(full, xml);
                SetScenePath(full);
                Log.Info($"Scene saved to {full} ({EditorSceneWorld.Root.Count} root elements).");
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
            if (EditorSceneWorld == null) { error = "No scene world."; return false; }
            var full = ResolvePath(path);
            try
            {
                var parser = new WorldMLParser();
                var parsed = (Element)parser.ParseFile(full);

                Select(null);
                foreach (var child in EditorSceneWorld.Root.ToList())
                    EditorSceneWorld.RemoveElement(child);
                foreach (var child in parsed.Children.ToList())
                {
                    parsed.RemoveChild(child);
                    // Strip any viewport/camera that may have been saved from older scenes
                    // (they now live in PersistentWorld).
                    if (child.GetComponent<ViewportComponent>() is { RenderWorld: true })
                        continue;
                    if (string.Equals(child.Name, "ViewportCamera", StringComparison.OrdinalIgnoreCase))
                        continue;
                    EditorSceneWorld.AddElement(child);
                }
                EnsurePersistentViewportElement();
                EnsurePersistentEditorCamera();
                SetScenePath(full);
                NotifyHierarchyChanged();
                Log.Info($"Scene loaded from {full} ({EditorSceneWorld.Root.Count} root elements).");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Error($"Load failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Seed the demo scene: sun, ground, three coloured primitives.</summary>
        public void CreateDemoScene()
        {
            if (EditorSceneWorld == null) return;

            var sun = new Element("Sun");
            sun.AddComponent(new GenericLightComponent
            {
                Type = LightType.Directional,
                ColorR = 1f,
                ColorG = 0.97f,
                ColorB = 0.92f,
                Energy = 1.2f
            });
            sun.LocalTransform = new TRS
            {
                Position = new Vector3(0, 8f, 4f),
                Rotation = Quaternion.CreateFromYawPitchRoll(-0.6f, -0.9f, 0f),
                Scale = Vector3.One
            };
            EditorSceneWorld.AddElement(sun);

            var groundMesh = new MeshComponent { Shape = MeshShape.Box, Width = 12f, Height = 0.2f, Depth = 12f };
            var ground = new Element("Ground");
            ground.AddComponent(groundMesh);
            ground.AddComponent(new MeshRenderer { Mesh = groundMesh });
            ground.AddComponent(new MaterialComponent { R = 0.24f, G = 0.26f, B = 0.3f, A = 1f, Roughness = 0.9f });
            ground.LocalTransform = new TRS { Position = new Vector3(0, -0.1f, 0), Rotation = Quaternion.Identity, Scale = Vector3.One };
            EditorSceneWorld.AddElement(ground);

            var redBox = MakeEditorMesh("RedBox", MeshShape.Box, 0.8f, 0.8f, 0.8f, 0.85f, 0.2f, 0.2f);
            redBox.LocalTransform = new TRS { Position = new Vector3(-1.6f, 0.5f, 0f), Rotation = Quaternion.Identity, Scale = Vector3.One };
            EditorSceneWorld.AddElement(redBox);

            var blueSphere = MakeEditorMesh("BlueSphere", MeshShape.Sphere, 0.6f, 0.6f, 0.6f, 0.2f, 0.35f, 0.9f);
            blueSphere.LocalTransform = new TRS { Position = new Vector3(0f, 0.6f, 0f), Rotation = Quaternion.Identity, Scale = Vector3.One };
            EditorSceneWorld.AddElement(blueSphere);

            var greenCapsule = MakeEditorMesh("GreenCapsule", MeshShape.Capsule, 0.4f, 1.2f, 0.4f, 0.25f, 0.8f, 0.35f);
            greenCapsule.LocalTransform = new TRS { Position = new Vector3(1.6f, 0.7f, 0f), Rotation = Quaternion.Identity, Scale = Vector3.One };
            EditorSceneWorld.AddElement(greenCapsule);

            EnsurePersistentEditorCamera();

            NotifyHierarchyChanged();
            Log.Info($"Demo scene created ({EditorSceneWorld.Root.Count} root elements).");
        }

        /// <summary>
        /// Ensure the persistent world carries the editor's orbit camera.
        /// Lives in PersistentWorld so it stays alive across Play clones.
        /// </summary>
        public void EnsurePersistentEditorCamera()
        {
            var cam = FindPersistentElement("ViewportCamera");
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
                Root.PersistentWorld.AddElement(cam);
            }
            if (cam.GetComponent<NonPersistentComponent>() == null)
                cam.AddComponent(new NonPersistentComponent());
            if (cam.GetComponent<EditorOnlyComponent>() == null)
                cam.AddComponent(new EditorOnlyComponent());
            Interaction?.SetEditorCamera(cam);
        }

        /// <summary>Legacy alias forwards to <see cref="EnsurePersistentEditorCamera"/>.</summary>
        public void EnsureEditorCamera() => EnsurePersistentEditorCamera();

        /// <summary>Find an element by name anywhere in the persistent world (used for camera/viewport).</summary>
        private IWorldElement? FindPersistentElement(string name)
        {
            foreach (var el in Root.PersistentWorld.Root)
            {
                if (string.Equals(el.Name, name, StringComparison.OrdinalIgnoreCase)) return el;
                var found = el.FindChildByNameRecursive(name);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>Find an element by name anywhere in the editor scene world.</summary>
        public IWorldElement? FindSceneElement(string name)
        {
            if (EditorSceneWorld == null) return null;
            foreach (var el in EditorSceneWorld.Root)
            {
                if (string.Equals(el.Name, name, StringComparison.OrdinalIgnoreCase)) return el;
                var found = el.FindChildByNameRecursive(name);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>True when <paramref name="el"/> belongs to the editor scene world.
        /// Only scene elements are editable/deletable.</summary>
        public bool IsSceneElement(IWorldElement? el)
        {
            if (el == null || EditorSceneWorld == null) return false;
            var cur = el;
            while (cur.Parent != null)
                cur = cur.Parent;
            return EditorSceneWorld.Root.Contains(cur);
        }

        /// <summary>True when editing is allowed (not locked in Play mode).</summary>
        public bool CanMutateScene => !IsSceneLocked;

        /// <summary>Ensure the persistent world has exactly one RenderWorld
        /// ViewportComponent root element (the editor game-view router).</summary>
        public void EnsurePersistentViewportElement()
        {
            var previous = ViewportElement;
            var existing = Root.PersistentWorld.Root
                .FirstOrDefault(e => e.GetComponent<ViewportComponent>() is { RenderWorld: true });
            if (existing != null)
            {
                if (existing.GetComponent<EditorOnlyComponent>() == null)
                    existing.AddComponent(new EditorOnlyComponent());
                ViewportElement = existing;
            }
            else
            {
                var vp = new Element("EditorViewport");
                vp.AddComponent(new ViewportComponent { RenderWorld = true });
                vp.AddComponent(new EditorOnlyComponent());
                Root.PersistentWorld.AddElement(vp);
                ViewportElement = vp;
                Log.Info($"Editor viewport element created in PersistentWorld (id {vp.Id}).");
            }

            if (!ReferenceEquals(previous, ViewportElement))
                ViewportElementChanged?.Invoke();
        }

        /// <summary>Legacy alias forwards to <see cref="EnsurePersistentViewportElement"/>.</summary>
        public void EnsureViewportElement() => EnsurePersistentViewportElement();

        // ── Play mode (clone via WorldML round-trip) ─────────────────────
        public bool EnterPlayMode(out string error)
        {
            error = "";
            if (IsPlaying) { error = "Already in Play mode."; return false; }
            if (EditorSceneWorld == null) { error = "No scene world."; return false; }
            try
            {
                var xml = WorldMLSerializer.Serialize(EditorSceneWorld.Root, EditorSceneWorld.WorldName ?? "Scene");
                var parser = new WorldMLParser();
                var parsed = (Element)parser.Parse(xml);
                var playWorld = new World((EditorSceneWorld.WorldName ?? "Scene") + "_Play") { TickingEnabled = true };
                foreach (var child in parsed.Children.ToList())
                {
                    parsed.RemoveChild(child);
                    playWorld.AddElement(child);
                }
                Root.Worlds.Add(playWorld);
                Root.SelectWorld(playWorld);
                _playWorld = playWorld;
                Select(null);
                PlayModeChanged?.Invoke();
                Log.Info($"Entered Play mode (clone {_playWorld.Root.Count} elements, id {playWorld.WorldName}).");
                Root.MarkRenderDirty();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Error($"EnterPlayMode failed: {ex.Message}");
                return false;
            }
        }

        public bool ExitPlayMode(out string error)
        {
            error = "";
            if (!IsPlaying || _playWorld == null) { error = "Not in Play mode."; return false; }
            try
            {
                var pw = _playWorld;
                _playWorld = null;
                Root.Worlds.Remove(pw);
                // Return focus + rendering to the (non-ticking) editor scene.
                if (EditorSceneWorld != null)
                    Root.SelectWorld(EditorSceneWorld);
                else if (Root.SelectedWorld == pw)
                    Root.DeselectWorld();
                // Ensure editor viewport/camera still bound after the swap
                EnsurePersistentViewportElement();
                EnsurePersistentEditorCamera();
                Select(null);
                PlayModeChanged?.Invoke();
                Log.Info("Exited Play mode.");
                Root.MarkRenderDirty();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Error($"ExitPlayMode failed: {ex.Message}");
                return false;
            }
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
