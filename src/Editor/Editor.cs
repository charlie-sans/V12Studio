using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Components.UI;
using V12.Core.UI;
using V12.Rendering;
using V12.Core.Interfaces.Renderer;
using V12.Basic.Components;
using V12.WorldML;
using System;
using System.Numerics;

// Non-UI components share names with UI components (ButtonComponent), so pull
// the mesh/light types in by alias instead of importing V12.Components wholesale.
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

namespace V12Studio.Editor
{
    public class Editor : IEditor
    {
        // ── Live UI state: the world owns the truth, the inspector is derived ──
        private IWorldElement? _selected;
        private HierarchySyncService? _hierarchy;
        private IViewportInteraction? _interaction;
        private Element? _inspectorName;
        private Element? _posLabel;
        private Element? _rotLabel;
        private Element? _scaleLabel;
        private Element? _nameField;
        private Element? _componentsPanel;
        private Element? _addCompPanel;
        private Element? _inspectorActions;

        // ── Scene editing: the editable scene lives in its own world, so the
        // editor's own UI (canvas, panels, component viewer) never shows up in
        // the scene hierarchy or the viewport. The GameView's
        // ViewportComponent{RenderWorld=true} claims this world for rendering.
        private World? _sceneWorld;
        private TextInputComponent? _scenePathInput; // Save/Load path
        private TextInputComponent? _newElNameInput; // Add Element name
        private int _addCount;

        // ── Add-component catalog: every scene component the editor can attach
        // to the selected element. SingleInstance components refuse a duplicate. ──
        private sealed record ComponentEntry(string Category, string DisplayName,
            Type Type, Func<IComponent> Factory, bool SingleInstance);

        private static readonly ComponentEntry[] ComponentCatalog = new ComponentEntry[]
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

        /// <summary>Select an element: highlights its hierarchy row, moves the
        /// viewport gizmo, and fills the inspector.</summary>
        public void Select(IWorldElement? el)
        {
            Console.WriteLine($"[Editor] Select -> {el?.Name ?? "(none)"}");
            _selected = el;
            if (_hierarchy != null) _hierarchy.SetSelected(el?.Id ?? 0);
            _interaction?.SetGizmoTarget(el);
            if (_inspectorName != null)
            {
                var lbl = _inspectorName.GetComponent<LabelComponent>();
                if (lbl != null) lbl.Text = el?.Name ?? "(none)";
            }
            RebuildComponents(el);
            RebuildAddList();
            RebuildInspectorActions(el);
            if (el == null) return;
            var t = el.LocalTransform;
            SetLabelText(_posLabel, $"Position: {t.Position.X:F2}, {t.Position.Y:F2}, {t.Position.Z:F2}");
            var (yaw, pitch, roll) = ToEulerDegrees(t.Rotation);
            SetLabelText(_rotLabel, $"Rotation: {yaw:F1}°, {pitch:F1}°, {roll:F1}°");
            SetLabelText(_scaleLabel, $"Scale: {t.Scale.X:F2}, {t.Scale.Y:F2}, {t.Scale.Z:F2}");
            if (_nameField != null)
            {
                var ti = _nameField.GetComponent<TextInputComponent>();
                if (ti != null && ti.Value != (el.Name ?? "")) ti.Value = el.Name ?? "";
            }
        }

        /// <summary>
        /// Rebuild the Components section of the inspector: one header + the
        /// component's own generated UI (BuildInspector) per component on the
        /// selected element. The world owns the component data; the inspector
        /// is derived, so editing a field writes straight back into the
        /// component via the getter/setter delegates.
        /// </summary>
        private void RebuildComponents(IWorldElement? el)
        {
            if (_componentsPanel == null) return;
            foreach (var child in _componentsPanel.Children.ToList())
                _componentsPanel.RemoveChild(child);

            if (el == null)
            {
                var none = new Element("NoComponents");
                none.AddComponent(new LabelComponent("No selection"));
                none.AddComponent(new UIStyleComponent { StyleHint = "muted" });
                _componentsPanel.AddChild(none);
                return;
            }

            if (el.Components == null || el.Components.Count == 0)
            {
                var none = new Element("NoComponents");
                none.AddComponent(new LabelComponent("No components"));
                none.AddComponent(new UIStyleComponent { StyleHint = "muted" });
                _componentsPanel.AddChild(none);
                return;
            }

            foreach (var comp in el.Components)
            {
                // Header row: component name + a remove button. The world owns
                // the component; removing writes intent back and rebuilds.
                var header = new Element("comp:" + comp.Id);
                header.AddComponent(new HLayoutComponent { Spacing = 4, Padding = 0 });

                var title = new Element("comptitle:" + comp.Id);
                title.AddComponent(new LabelComponent(comp.Name ?? comp.GetType().Name));
                title.AddComponent(new UIStyleComponent { StyleHint = "compheader" });
                title.AddComponent(new LayoutElementComponent { FlexibleWidth = 1 });
                header.AddChild(title);

                var removeBtn = new Element("remcomp:" + comp.Id);
                // ASCII "x" — the bundled UI font lacks fancier glyphs (e.g. ×).
                removeBtn.AddComponent(new ButtonComponent("x", () => RemoveComponentFromSelection(comp)));
                removeBtn.AddComponent(new UIStyleComponent { StyleHint = "danger" });
                removeBtn.AddComponent(new LayoutElementComponent { MinWidth = 26, MinHeight = 22 });
                header.AddChild(removeBtn);

                _componentsPanel.AddChild(header);

                // Component generates its own inspector UI (fields bound to its
                // properties). No-op for components without BuildInspector.
                comp.BuildInspector(new V12ElementInspector(_componentsPanel));
            }
        }

        /// <summary>
        /// Rebuild the Add Component sidebar: the catalog of components that can
        /// be attached to the selected element, grouped by category. Rows whose
        /// single-instance component is already present show no "+" button and a
        /// dimmed name.
        /// </summary>
        private void RebuildAddList()
        {
            if (_addCompPanel == null) return;
            foreach (var child in _addCompPanel.Children.ToList())
                _addCompPanel.RemoveChild(child);

            string? lastCategory = null;
            foreach (var entry in ComponentCatalog)
            {
                if (entry.Category != lastCategory)
                {
                    lastCategory = entry.Category;
                    var cat = new Element("cat:" + entry.Category);
                    cat.AddComponent(new LabelComponent(entry.Category));
                    cat.AddComponent(new UIStyleComponent { StyleHint = "compheader" });
                    _addCompPanel.AddChild(cat);
                }

                bool present = _selected?.Components.Any(c => c.GetType() == entry.Type) ?? false;
                bool canAdd = _selected != null && (!entry.SingleInstance || !present);

                var row = new Element("add:" + entry.DisplayName);
                row.AddComponent(new HLayoutComponent { Spacing = 4, Padding = 0 });

                var name = new Element("addname:" + entry.DisplayName);
                name.AddComponent(new LabelComponent(entry.DisplayName));
                name.AddComponent(new UIStyleComponent { StyleHint = present ? "muted" : "" });
                name.AddComponent(new LayoutElementComponent { FlexibleWidth = 1 });
                row.AddChild(name);

                if (canAdd)
                {
                    var btn = new Element("addbtn:" + entry.DisplayName);
                    btn.AddComponent(new ButtonComponent("+", () => AddComponentToSelection(entry)));
                    btn.AddComponent(new UIStyleComponent { StyleHint = "accent" });
                    btn.AddComponent(new LayoutElementComponent { MinWidth = 30, MinHeight = 22 });
                    row.AddChild(btn);
                }
                _addCompPanel.AddChild(row);
            }
        }

        private void AddComponentToSelection(ComponentEntry entry)
        {
            if (_selected == null) return;
            var el = _selected;
            // Some components reposition their owner on attach (e.g.
            // CameraComponent drops a default view height onto an element at the
            // origin). The adder must never move the object: restore the
            // element's transform after attaching.
            var saved = el.LocalTransform;
            el.AddComponent(entry.Factory());
            el.LocalTransform = saved;
            RebuildComponents(el);
            RebuildAddList();
        }

        private void RemoveComponentFromSelection(IComponent comp)
        {
            if (_selected == null) return;
            _selected.RemoveComponent(comp);
            RebuildComponents(_selected);
            RebuildAddList();
        }

        // ── Scene editing: add / delete elements, save / load WorldML ─────

        /// <summary>
        /// True when <paramref name="el"/> belongs to the scene world. Only
        /// scene elements are deletable — editor chrome (toolbar, panels, the
        /// viewport) lives in PersistentWorld and is off-limits.
        /// </summary>
        private bool IsSceneElement(IWorldElement? el)
        {
            if (el == null || _sceneWorld == null) return false;
            var cur = el;
            while (cur.Parent != null)
                cur = cur.Parent;
            return _sceneWorld.Root.Contains(cur);
        }

        /// <summary>Rebuild the context actions under the inspector's name field:
        /// a Delete button when the selected element is a scene element.</summary>
        private void RebuildInspectorActions(IWorldElement? el)
        {
            if (_inspectorActions == null) return;
            foreach (var child in _inspectorActions.Children.ToList())
                _inspectorActions.RemoveChild(child);
            if (!IsSceneElement(el)) return;

            var row = new Element("DeleteElementRow");
            row.AddComponent(new HLayoutComponent { Spacing = 4, Padding = 0 });
            var btn = new Element("DeleteElement");
            btn.AddComponent(new ButtonComponent("Delete Element", DeleteSelected));
            btn.AddComponent(new UIStyleComponent { StyleHint = "danger" });
            btn.AddComponent(new LayoutElementComponent { FlexibleWidth = 1, MinHeight = 26 });
            row.AddChild(btn);
            _inspectorActions.AddChild(row);
        }

        /// <summary>Remove the selected element (and its subtree) from the scene.</summary>
        private void DeleteSelected()
        {
            if (_selected == null) return;
            var name = _selected.Name;
            var parent = _selected.Parent;
            if (parent != null)
                parent.RemoveChild(_selected);
            else
                _sceneWorld?.RemoveElement(_selected);
            Select(null);
            Console.WriteLine($"[Editor] Deleted element '{name}'");
        }

        /// <summary>Create a new mesh element in the scene with the given shape,
        /// then select it so it can be renamed/moved immediately.</summary>
        private void AddElement(MeshShape shape)
        {
            if (_sceneWorld == null) return;
            var name = _newElNameInput?.Value;
            if (string.IsNullOrWhiteSpace(name)) name = "Object";

            var el = MakeEditorMesh(name, shape, 1f, 1f, 1f, 0.7f, 0.7f, 0.75f);
            float y = 1f + 0.5f * (_addCount++ % 8);
            el.LocalTransform = new TRS
            {
                Position = new Vector3(0f, y, 0f),
                Rotation = Quaternion.Identity,
                Scale = Vector3.One
            };
            _sceneWorld.AddElement(el);
            Select(el);
        }

        /// <summary>
        /// Resolve a possibly-relative scene path against the executable's
        /// directory. The 2dog host sets the process working directory to the
        /// Godot project folder, which would otherwise capture relative paths —
        /// relative paths should land next to the editor exe instead.
        /// </summary>
        private static string ResolveScenePath(string path)
        {
            if (System.IO.Path.IsPathRooted(path))
                return System.IO.Path.GetFullPath(path);
            return System.IO.Path.GetFullPath(
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path));
        }

        /// <summary>Open the host file dialog (open mode) and fill the scene
        /// path field with the picked file. Save/Load then act on that path.</summary>
        private void ShowBrowseDialog()
        {
            _interaction?.ShowFileDialog(false, "Browse Scene",
                ResolveScenePath(_scenePathInput?.Value ?? "scene.worldml"), path =>
                {
                    if (string.IsNullOrEmpty(path)) return;
                    if (_scenePathInput != null) _scenePathInput.Value = path;
                });
        }

        /// <summary>Write the scene world as a WorldML XML file.</summary>
        private void SaveScene()
        {
            if (_sceneWorld == null) return;
            var path = _scenePathInput?.Value;
            if (string.IsNullOrEmpty(path)) path = "scene.worldml";
            try
            {
                var full = ResolveScenePath(path);
                var xml = WorldMLSerializer.Serialize(_sceneWorld.Root, _sceneWorld.WorldName ?? "Scene");
                System.IO.File.WriteAllText(full, xml);
                // Show the resolved location in the field so it's never a mystery.
                if (_scenePathInput != null) _scenePathInput.Value = full;
                Console.WriteLine($"[Editor] Scene saved to {full} ({_sceneWorld.Root.Count} elements)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Editor] Save failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Replace the current scene with the contents of a WorldML file.
        /// After a load the orbit camera is re-registered when the loaded scene
        /// carries a camera named "ViewportCamera" (the editor convention).
        /// </summary>
        private void LoadScene()
        {
            if (_sceneWorld == null) return;
            var path = _scenePathInput?.Value;
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                var full = ResolveScenePath(path);
                var parser = new WorldMLParser();
                var parsed = (Element)parser.ParseFile(full);

                foreach (var child in _sceneWorld.Root.ToList())
                    _sceneWorld.RemoveElement(child);
                foreach (var child in parsed.Children.ToList())
                {
                    parsed.RemoveChild(child);
                    _sceneWorld.AddElement(child);
                }

                // Re-register the orbit camera if the loaded scene has one.
                if (_interaction != null)
                    _interaction.SetEditorCamera(FindSceneElement("ViewportCamera"));
                Select(null);
                Console.WriteLine($"[Editor] Scene loaded from {full}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Editor] Load failed: {ex.Message}");
            }
        }

        /// <summary>Find an element by name anywhere in the scene world.</summary>
        private IWorldElement? FindSceneElement(string name)
        {
            if (_sceneWorld == null) return null;
            foreach (var el in _sceneWorld.Root)
            {
                if (string.Equals(el.Name, name, StringComparison.OrdinalIgnoreCase)) return el;
                var found = el.FindChildByNameRecursive(name);
                if (found != null) return found;
            }
            return null;
        }

        private static void SetLabelText(Element? el, string text)
        {
            if (el == null) return;
            var lbl = el.GetComponent<LabelComponent>();
            if (lbl != null) lbl.Text = text;
        }

        private static (float yaw, float pitch, float roll) ToEulerDegrees(Quaternion q)
        {
            float siny_cosp = 2 * (q.W * q.Y + q.Z * q.X);
            float cosy_cosp = 1 - 2 * (q.Y * q.Y + q.Z * q.Z);
            float yaw = MathF.Atan2(siny_cosp, cosy_cosp);
            float sinp = 2 * (q.W * q.X - q.Y * q.Z);
            float pitch = MathF.Abs(sinp) >= 1 ? MathF.CopySign(MathF.PI / 2, sinp) : MathF.Asin(sinp);
            float sinr_cosp = 2 * (q.W * q.Z + q.X * q.Y);
            float cosr_cosp = 1 - 2 * (q.X * q.X + q.Z * q.Z);
            float roll = MathF.Atan2(sinr_cosp, cosr_cosp);
            return (yaw * 180f / MathF.PI, pitch * 180f / MathF.PI, roll * 180f / MathF.PI);
        }

        public void CreateMainWindow()
        {
            // ── Scene world: the editable 3D scene lives here, separate from
            // the editor's own UI (which stays in PersistentWorld). Selecting
            // it makes the GameView's RenderWorld=true claim render it, and the
            // hierarchy sidebar mirrors ONLY this world.
            var g = GameRoot.Instance;
            if (g != null)
            {
                _sceneWorld = new World("Scene");
                g.Worlds.Add(_sceneWorld);
                g.SelectWorld(_sceneWorld);
                Console.WriteLine("[Editor] Scene world created and selected (UI stays in PersistentWorld).");
            }

            // ── Canvas root: screen-space UI surface ──
            // WorldCanvasSystem scans worlds for elements with CanvasComponent
            // and renders their children via Paper.
            var canvas = new Element("EditorCanvas");

            // Width/Height 0 = auto-size from the viewport each frame, so the
            // editor UI (and the embedded 3D viewport) fills the window and
            // scales when it's resized/maximized. Fixed pixels here would pin
            // the canvas to that size forever.
            var cc = new CanvasComponent
            {
                ScreenSpace = true
                // Width = 1280, Height = 720,
                // ScaleToScreen = true
            };
            
            canvas.AddComponent(cc);

            // ── Root vertical layout ──
            var root = new Element("Root");
            root.AddComponent(new VLayoutComponent { Spacing = 4, Padding = 4, Expand = true });
            canvas.AddChild(root);

            // ── Toolbar (horizontal row) ──
            var toolbar = new Element("Toolbar");
            toolbar.AddComponent(new HLayoutComponent { Spacing = 4, Padding = 6 });
            toolbar.AddComponent(new LayoutElementComponent { PreferredHeight = 42 });
            root.AddChild(toolbar);

            // Play is the primary action (accent), Stop the danger one.
            var playBtn = new Element("Play");
            playBtn.AddComponent(new ButtonComponent("Play", () => Console.WriteLine("playing game")));
            playBtn.AddComponent(new UIStyleComponent { StyleHint = "accent" });
            playBtn.AddComponent(new LayoutElementComponent { MinWidth = 72 });
            toolbar.AddChild(playBtn);

            var stopBtn = new Element("Stop");
            stopBtn.AddComponent(new ButtonComponent("Stop", () => { }));
            stopBtn.AddComponent(new UIStyleComponent { StyleHint = "danger" });
            stopBtn.AddComponent(new LayoutElementComponent { MinWidth = 72 });
            toolbar.AddChild(stopBtn);

            var pauseBtn = new Element("Pause");
            pauseBtn.AddComponent(new ButtonComponent("Pause", () => { }));
            pauseBtn.AddComponent(new LayoutElementComponent { MinWidth = 72 });
            toolbar.AddChild(pauseBtn);

            // ── Transform gizmo mode switcher (T / R / S) ──
            // Rebuilds the gizmo handles around the selected element; active
            // mode gets an accent hint so the row reads like a toggle group.
            void AddTool(Element bar, string label, GizmoMode m, string hint)
            {
                var btn = new Element("Tool_" + m);
                btn.AddComponent(new ButtonComponent(label, () => _interaction?.SetGizmoMode(m)));
                btn.AddComponent(new UIStyleComponent { StyleHint = hint });
                btn.AddComponent(new LayoutElementComponent { MinWidth = 76 });
                bar.AddChild(btn);
            }
            AddTool(toolbar, "Move", GizmoMode.Translate, "accent");
            AddTool(toolbar, "Rotate", GizmoMode.Rotate, "");
            AddTool(toolbar, "Scale", GizmoMode.Scale, "");

            // ── Scene save/load: WorldML path + buttons ──
            // Save/Load act directly on the typed path (always works and logs
            // the resolved absolute path); Browse opens a host file dialog that
            // fills the field with the picked path.
            var scenePath = new Element("ScenePath");
            _scenePathInput = new TextInputComponent { Placeholder = "scene.worldml" };
            _scenePathInput.Value = "scene.worldml";
            scenePath.AddComponent(_scenePathInput);
            scenePath.AddComponent(new LayoutElementComponent { MinWidth = 190 });
            toolbar.AddChild(scenePath);

            var browseBtn = new Element("BrowseScene");
            browseBtn.AddComponent(new ButtonComponent("Browse", ShowBrowseDialog));
            browseBtn.AddComponent(new LayoutElementComponent { MinWidth = 56 });
            toolbar.AddChild(browseBtn);

            var saveBtn = new Element("SaveScene");
            saveBtn.AddComponent(new ButtonComponent("Save", SaveScene));
            saveBtn.AddComponent(new LayoutElementComponent { MinWidth = 56 });
            toolbar.AddChild(saveBtn);

            var loadBtn = new Element("LoadScene");
            loadBtn.AddComponent(new ButtonComponent("Load", LoadScene));
            loadBtn.AddComponent(new LayoutElementComponent { MinWidth = 56 });
            toolbar.AddChild(loadBtn);

            // A spacer pushes the mode indicator to the right edge of the bar.
            var spacer = new Element("ToolbarSpacer");
            spacer.AddComponent(new LabelComponent(""));
            spacer.AddComponent(new LayoutElementComponent { FlexibleWidth = 1 });
            toolbar.AddChild(spacer);

            var mode = new Element("Mode");
            mode.AddComponent(new LabelComponent("EDIT"));
            mode.AddComponent(new UIStyleComponent { StyleHint = "muted" });
            toolbar.AddChild(mode);

            // ── Main content row (hierarchy | viewport | inspector) ──
            // SplitterComponent makes the three panels resizable by dragging the
            // dividers between them.
            var mainRow = new Element("MainContent");
            mainRow.AddComponent(new SplitterComponent
            {
                Orientation = SplitterOrientation.Horizontal,
                Spacing = 4,
                Expand = true
            });
            root.AddChild(mainRow);

            // Left panel — scene hierarchy. MinWidth keeps it a proper sidebar
            // instead of collapsing to its text; it resizes via the splitter.
            // Rows are managed live by HierarchySyncService from the V12 world.
            var hierarchyPanel = new Element("Hierarchy");
            hierarchyPanel.AddComponent(new VLayoutComponent { Spacing = 2, Padding = 6 });
            hierarchyPanel.AddComponent(new LayoutElementComponent { MinWidth = 230, PreferredWidth = 250 });
            mainRow.AddChild(hierarchyPanel);

            var hierarchyTitle = new Element("HierarchyTitle");
            hierarchyTitle.AddComponent(new LabelComponent("Scene Hierarchy"));
            hierarchyTitle.AddComponent(new UIStyleComponent { StyleHint = "title" });
            hierarchyPanel.AddChild(hierarchyTitle);

            // The tree container: HierarchySyncService populates its children
            // from the V12 world; WorldCanvasSystem mirrors that nesting onto a
            // Godot Tree control (native collapse arrows + indentation).
            var hierarchyTree = new Element("HierarchyTree");
            hierarchyTree.AddComponent(new TreeComponent());
            hierarchyTree.AddComponent(new LayoutElementComponent { FlexibleHeight = 1 });
            hierarchyPanel.AddChild(hierarchyTree);

            // ── Add Element: name + primitive shape buttons. Creates a new
            // mesh element in the scene (GameView subtree) and selects it. ──
            var addElTitle = new Element("AddElementTitle");
            addElTitle.AddComponent(new LabelComponent("Add Element"));
            addElTitle.AddComponent(new UIStyleComponent { StyleHint = "compheader" });
            hierarchyPanel.AddChild(addElTitle);

            var addElNameRow = new Element("AddElementNameRow");
            addElNameRow.AddComponent(new HLayoutComponent { Spacing = 4, Padding = 0 });
            var addElName = new Element("NewElementName");
            _newElNameInput = new TextInputComponent { Placeholder = "Object" };
            _newElNameInput.Value = "Object";
            addElName.AddComponent(_newElNameInput);
            addElName.AddComponent(new LayoutElementComponent { FlexibleWidth = 1 });
            addElNameRow.AddChild(addElName);
            hierarchyPanel.AddChild(addElNameRow);

            // Five buttons don't fit one row of a 230px sidebar without squeezing
            // the labels, so lay them out 3 + 2.
            var shapeRow = new Element("AddElementShapes");
            shapeRow.AddComponent(new HLayoutComponent { Spacing = 4, Padding = 0 });
            var shapeRow2 = new Element("AddElementShapes2");
            shapeRow2.AddComponent(new HLayoutComponent { Spacing = 4, Padding = 0 });
            int shapeIndex = 0;
            foreach (var (label, shape) in new[]
            {
                ("Box", MeshShape.Box), ("Sphere", MeshShape.Sphere),
                ("Capsule", MeshShape.Capsule), ("Cylinder", MeshShape.Cylinder),
                ("Plane", MeshShape.Plane)
            })
            {
                var btn = new Element("AddEl_" + label);
                btn.AddComponent(new ButtonComponent(label, () => AddElement(shape)));
                btn.AddComponent(new LayoutElementComponent { FlexibleWidth = 1, MinHeight = 24 });
                (shapeIndex++ < 3 ? shapeRow : shapeRow2).AddChild(btn);
            }
            hierarchyPanel.AddChild(shapeRow);
            hierarchyPanel.AddChild(shapeRow2);

            // Center — game viewport: a 3D scene panel. Elements under it render
            // inside a SubViewport (ViewportComponent), and RenderWorld=true
            // claims the scene world so its elements show up here.
            var viewportPanel = new Element("GameView");
            viewportPanel.AddComponent(new VLayoutComponent { Spacing = 0, Padding = 0, Expand = true });
            viewportPanel.AddComponent(new ViewportComponent { RenderWorld = true });
            mainRow.AddChild(viewportPanel);

            // ── Demo 3D scene content: created as roots of the scene world
            // (NOT children of the GameView), so the editor UI stays invisible
            // to the scene hierarchy. Rendering lands in the GameView viewport
            // via RenderWorld=true. ──
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
            _sceneWorld?.AddElement(sun);

            // Ground plane (thin box so lighting/shadow reads clearly)
            var groundMesh = new MeshComponent { Shape = MeshShape.Box, Width = 12f, Height = 0.2f, Depth = 12f };
            var ground = new Element("Ground");
            ground.AddComponent(groundMesh);
            ground.AddComponent(new MeshRenderer { Mesh = groundMesh });
            ground.AddComponent(new MaterialComponent { R = 0.24f, G = 0.26f, B = 0.3f, A = 1f, Roughness = 0.9f });
            ground.LocalTransform = new TRS { Position = new Vector3(0, -0.1f, 0), Rotation = Quaternion.Identity, Scale = Vector3.One };
            _sceneWorld?.AddElement(ground);

            // A few coloured primitives so the viewport clearly shows a scene
            var redBox = MakeEditorMesh("RedBox", MeshShape.Box, 0.8f, 0.8f, 0.8f, 0.85f, 0.2f, 0.2f);
            redBox.LocalTransform = new TRS { Position = new Vector3(-1.6f, 0.5f, 0f), Rotation = Quaternion.Identity, Scale = Vector3.One };
            _sceneWorld?.AddElement(redBox);

            var blueSphere = MakeEditorMesh("BlueSphere", MeshShape.Sphere, 0.6f, 0.6f, 0.6f, 0.2f, 0.35f, 0.9f);
            blueSphere.LocalTransform = new TRS { Position = new Vector3(0f, 0.6f, 0f), Rotation = Quaternion.Identity, Scale = Vector3.One };
            _sceneWorld?.AddElement(blueSphere);

            var greenCapsule = MakeEditorMesh("GreenCapsule", MeshShape.Capsule, 0.4f, 1.2f, 0.4f, 0.25f, 0.8f, 0.35f);
            greenCapsule.LocalTransform = new TRS { Position = new Vector3(1.6f, 0.7f, 0f), Rotation = Quaternion.Identity, Scale = Vector3.One };
            _sceneWorld?.AddElement(greenCapsule);

            // Viewport camera: driven by the editor orbit controller (right-drag
            // orbits, wheel dollies) via IViewportInteraction.SetEditorCamera.
            var viewCam = new Element("ViewportCamera");
            viewCam.AddComponent(new CameraComponent { Active = true, IsCurrent = true, Fov = 60f, FarClip = 1000f });
            viewCam.LocalTransform = new TRS
            {
                Position = new Vector3(0f, 2.6f, 7f),
                Rotation = Quaternion.CreateFromYawPitchRoll(0f, -0.35f, 0f),
                Scale = Vector3.One
            };
            _sceneWorld?.AddElement(viewCam);

            // Right panel — inspector. MinWidth keeps it a proper sidebar.
            var inspectorPanel = new Element("Inspector");
            inspectorPanel.AddComponent(new VLayoutComponent { Spacing = 4, Padding = 6 });
            inspectorPanel.AddComponent(new LayoutElementComponent { MinWidth = 260, PreferredWidth = 280 });
            mainRow.AddChild(inspectorPanel);

            var inspectorTitle = new Element("InspectorTitle");
            inspectorTitle.AddComponent(new LabelComponent("Properties"));
            inspectorTitle.AddComponent(new UIStyleComponent { StyleHint = "title" });
            inspectorPanel.AddChild(inspectorTitle);

            _inspectorName = new Element("SelectedName");
            _inspectorName.AddComponent(new LabelComponent("(none)"));
            _inspectorName.AddComponent(new UIStyleComponent { StyleHint = "compheader" });
            inspectorPanel.AddChild(_inspectorName);

            var transformHeader = new Element("TransformHeader");
            transformHeader.AddComponent(new LabelComponent("Transform"));
            transformHeader.AddComponent(new UIStyleComponent { StyleHint = "compheader" });
            inspectorPanel.AddChild(transformHeader);

            _posLabel = new Element("Position");
            _posLabel.AddComponent(new LabelComponent("Position: —"));
            _posLabel.AddComponent(new UIStyleComponent { StyleHint = "muted" });
            inspectorPanel.AddChild(_posLabel);

            _rotLabel = new Element("Rotation");
            _rotLabel.AddComponent(new LabelComponent("Rotation: —"));
            _rotLabel.AddComponent(new UIStyleComponent { StyleHint = "muted" });
            inspectorPanel.AddChild(_rotLabel);

            _scaleLabel = new Element("Scale");
            _scaleLabel.AddComponent(new LabelComponent("Scale: —"));
            _scaleLabel.AddComponent(new UIStyleComponent { StyleHint = "muted" });
            inspectorPanel.AddChild(_scaleLabel);

            _nameField = new Element("EntityName");
            var nameFieldComp = new TextInputComponent { Placeholder = "Entity name" };
            // Renaming writes intent back into the world — the world owns truth.
            nameFieldComp.OnChanged += newName =>
            {
                if (_selected != null && !string.IsNullOrEmpty(newName))
                {
                    _selected.Name = newName;
                    if (_inspectorName != null)
                    {
                        var lbl = _inspectorName.GetComponent<LabelComponent>();
                        if (lbl != null) lbl.Text = newName;
                    }
                }
            };
            _nameField.AddComponent(nameFieldComp);
            inspectorPanel.AddChild(_nameField);

            // Context actions for the selected element (e.g. delete): rebuilt
            // per selection like the components panel.
            _inspectorActions = new Element("InspectorActions");
            _inspectorActions.AddComponent(new VLayoutComponent { Spacing = 4, Padding = 0 });
            inspectorPanel.AddChild(_inspectorActions);

            // Components section: rebuilt per selection, each component renders
            // its own generated UI (BuildInspector) below its header. Scrolls
            // when the selected element's components overflow the panel.
            _componentsPanel = new Element("ComponentsPanel");
            _componentsPanel.AddComponent(new ScrollComponent());
            _componentsPanel.AddComponent(new VLayoutComponent { Spacing = 2, Padding = 2 });
            _componentsPanel.AddComponent(new LayoutElementComponent { FlexibleHeight = 1 });
            inspectorPanel.AddChild(_componentsPanel);

            // ── Add Component sidebar ──
            // Pinned at the bottom of the inspector: the catalog of components
            // that can be attached to the selected element (see RebuildAddList).
            // Fixed height + scroll so a long catalog never shoves the rest.
            var addCompTitle = new Element("AddComponentTitle");
            addCompTitle.AddComponent(new LabelComponent("Add Component"));
            addCompTitle.AddComponent(new UIStyleComponent { StyleHint = "title" });
            inspectorPanel.AddChild(addCompTitle);

            _addCompPanel = new Element("AddComponentPanel");
            _addCompPanel.AddComponent(new ScrollComponent());
            _addCompPanel.AddComponent(new VLayoutComponent { Spacing = 1, Padding = 2 });
            _addCompPanel.AddComponent(new LayoutElementComponent { PreferredHeight = 260 });
            inspectorPanel.AddChild(_addCompPanel);

            // ── Status bar ──
            var statusBar = new Element("StatusBar");
            statusBar.AddComponent(new HLayoutComponent { Spacing = 12, Padding = 6 });
            root.AddChild(statusBar);

            var appName = new Element("AppName");
            appName.AddComponent(new LabelComponent("V12 Studio"));
            appName.AddComponent(new UIStyleComponent { StyleHint = "muted" });
            statusBar.AddChild(appName);

            var worldInfo = new Element("WorldInfo");
            worldInfo.AddComponent(new LabelComponent("No world selected"));
            worldInfo.AddComponent(new UIStyleComponent { StyleHint = "muted" });
            statusBar.AddChild(worldInfo);

            var statusSpacer = new Element("StatusSpacer");
            statusSpacer.AddComponent(new LabelComponent(""));
            statusSpacer.AddComponent(new LayoutElementComponent { FlexibleWidth = 1 });
            statusBar.AddChild(statusSpacer);

            var fps = new Element("FPS");
            fps.AddComponent(new LabelComponent("FPS: --"));
            fps.AddComponent(new UIStyleComponent { StyleHint = "accent" });
            statusBar.AddChild(fps);

            // ── Add the canvas to PersistentWorld so WorldCanvasSystem picks it up ──
            // SelectedWorld is null until the host creates a world, but PersistentWorld
            // always exists and SyncFromV12 scans it for CanvasComponents.
            GameRoot.Instance?.PersistentWorld.AddElement(canvas);
            Console.WriteLine($"[Editor] Added '{canvas.Name}' to PersistentWorld (ScreenSpace={cc.ScreenSpace})");

            // ── Live hierarchy sync: walk the scene world each tick and mirror it
            // into the hierarchy tree. Runs as an IGameService on the worker.
            // Only the scene world is mirrored — the editor's own UI lives in
            // PersistentWorld and never shows up in the sidebar.
            _hierarchy = new HierarchySyncService(hierarchyTree, fps, Select, _sceneWorld);
            GameRoot.Instance?.Registry.Register("HierarchySync", _hierarchy);

            // ── Viewport picking: clicking a mesh in the GameView selects it
            // (and shows the translation gizmo), with the same Select path.
            // Resolved through the shared V12 interface (IViewportInteraction)
            // rather than the host type — the host assembly (Nova.Shared) is
            // loaded twice (Godot context + app context), so a direct cast on
            // the concrete type would fail type identity.
            _interaction = GameRoot.Instance?.Registry.Get<IViewportInteraction>();
            if (_interaction != null)
            {
                _interaction.SetSelectHandler(Select);
                _interaction.SetEditorCamera(viewCam);
                Console.WriteLine("[Editor] Viewport picking + gizmo + orbit camera wired.");
            }
            else
            {
                Console.WriteLine("[Editor] WARNING: IViewportInteraction not registered — viewport picking disabled.");
            }
            Select(null); // show the empty inspector state
        }

        /// <summary>Build an element with a coloured primitive mesh for the demo scene.</summary>
        private static Element MakeEditorMesh(string name, MeshShape shape, float w, float h, float d,
            float r, float g, float b)
        {
            var mesh = new MeshComponent { Shape = shape, Width = w, Height = h, Depth = d };
            var el = new Element(name);
            el.AddComponent(mesh);
            el.AddComponent(new MeshRenderer { Mesh = mesh });
            el.AddComponent(new MaterialComponent { R = r, G = g, B = b, A = 1f, Roughness = 0.5f, Metallic = 0.1f });
            return el;
        }
    }
}
