using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Components.UI;
using V12.Rendering;
using V12.Core.Interfaces.Renderer;
using V12.Basic.Components;
using System;
using System.Numerics;

// Non-UI components share names with UI components (ButtonComponent), so pull
// the mesh/light types in by alias instead of importing V12.Components wholesale.
using MeshShape = V12.Components.MeshShape;
using MeshComponent = V12.Components.MeshComponent;
using MeshRenderer = V12.Components.Renderables.MeshRenderer;
using MaterialComponent = V12.Components.MaterialComponent;
using GenericLightComponent = V12.Components.GenericLightComponent;

namespace V12Studio.Editor
{
    public class Editor : IEditor
    {
        // ── Live UI state: the world owns the truth, the inspector is derived ──
        private IWorldElement? _selected;
        private HierarchySyncService? _hierarchy;
        private Element? _inspectorName;
        private Element? _posLabel;
        private Element? _rotLabel;
        private Element? _scaleLabel;
        private Element? _nameField;
        private Element? _componentsPanel;

        /// <summary>Select an element: highlights its hierarchy row and fills the
        /// inspector with its transform/name/components.</summary>
        public void Select(IWorldElement? el)
        {
            _selected = el;
            if (_hierarchy != null) _hierarchy.SetSelected(el?.Id ?? 0);
            if (_inspectorName != null)
            {
                var lbl = _inspectorName.GetComponent<LabelComponent>();
                if (lbl != null) lbl.Text = el?.Name ?? "(none)";
            }
            RebuildComponents(el);
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
                var header = new Element("comp:" + comp.Id);
                header.AddComponent(new LabelComponent(comp.Name ?? comp.GetType().Name));
                header.AddComponent(new UIStyleComponent { StyleHint = "compheader" });
                _componentsPanel.AddChild(header);

                // Component generates its own inspector UI (fields bound to its
                // properties). No-op for components without BuildInspector.
                comp.BuildInspector(new V12.Core.UI.V12ElementInspector(_componentsPanel));
            }
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
            playBtn.AddComponent(new ButtonComponent("▶ Play", () => Console.WriteLine("playing game")));
            playBtn.AddComponent(new UIStyleComponent { StyleHint = "accent" });
            toolbar.AddChild(playBtn);

            var stopBtn = new Element("Stop");
            stopBtn.AddComponent(new ButtonComponent("■ Stop", () => { }));
            stopBtn.AddComponent(new UIStyleComponent { StyleHint = "danger" });
            toolbar.AddChild(stopBtn);

            var pauseBtn = new Element("Pause");
            pauseBtn.AddComponent(new ButtonComponent("❚❚ Pause", () => { }));
            toolbar.AddChild(pauseBtn);

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

            // Center — game viewport: a 3D scene panel. Elements under it render
            // inside a SubViewport (ViewportComponent), and RenderWorld=true also
            // claims the selected world so a gamepak scene shows up here.
            var viewportPanel = new Element("GameView");
            viewportPanel.AddComponent(new VLayoutComponent { Spacing = 0, Padding = 0, Expand = true });
            viewportPanel.AddComponent(new ViewportComponent { RenderWorld = true });
            mainRow.AddChild(viewportPanel);

            // ── Demo 3D scene content for the game viewport ──
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
            viewportPanel.AddChild(sun);

            // Ground plane (thin box so lighting/shadow reads clearly)
            var groundMesh = new MeshComponent { Shape = MeshShape.Box, Width = 12f, Height = 0.2f, Depth = 12f };
            var ground = new Element("Ground");
            ground.AddComponent(groundMesh);
            ground.AddComponent(new MeshRenderer { Mesh = groundMesh });
            ground.AddComponent(new MaterialComponent { R = 0.24f, G = 0.26f, B = 0.3f, A = 1f, Roughness = 0.9f });
            ground.LocalTransform = new TRS { Position = new Vector3(0, -0.1f, 0), Rotation = Quaternion.Identity, Scale = Vector3.One };
            viewportPanel.AddChild(ground);

            // A few coloured primitives so the viewport clearly shows a scene
            var redBox = MakeEditorMesh("RedBox", MeshShape.Box, 0.8f, 0.8f, 0.8f, 0.85f, 0.2f, 0.2f);
            redBox.LocalTransform = new TRS { Position = new Vector3(-1.6f, 0.5f, 0f), Rotation = Quaternion.Identity, Scale = Vector3.One };
            viewportPanel.AddChild(redBox);

            var blueSphere = MakeEditorMesh("BlueSphere", MeshShape.Sphere, 0.6f, 0.6f, 0.6f, 0.2f, 0.35f, 0.9f);
            blueSphere.LocalTransform = new TRS { Position = new Vector3(0f, 0.6f, 0f), Rotation = Quaternion.Identity, Scale = Vector3.One };
            viewportPanel.AddChild(blueSphere);

            var greenCapsule = MakeEditorMesh("GreenCapsule", MeshShape.Capsule, 0.4f, 1.2f, 0.4f, 0.25f, 0.8f, 0.35f);
            greenCapsule.LocalTransform = new TRS { Position = new Vector3(1.6f, 0.7f, 0f), Rotation = Quaternion.Identity, Scale = Vector3.One };
            viewportPanel.AddChild(greenCapsule);

            // Viewport camera: current until a world supplies its own camera.
            var viewCam = new Element("ViewportCamera");
            viewCam.AddComponent(new CameraComponent { Active = true, IsCurrent = true, Fov = 60f, FarClip = 1000f });
            viewCam.LocalTransform = new TRS
            {
                Position = new Vector3(0f, 2.6f, 7f),
                Rotation = Quaternion.CreateFromYawPitchRoll(0f, -0.35f, 0f),
                Scale = Vector3.One
            };
            viewportPanel.AddChild(viewCam);

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

            var addComp = new Element("AddComponent");
            addComp.AddComponent(new ButtonComponent("+ Add Component", () => { }));
            inspectorPanel.AddChild(addComp);

            // Components section: rebuilt per selection, each component renders
            // its own generated UI (BuildInspector) below its header.
            _componentsPanel = new Element("ComponentsPanel");
            _componentsPanel.AddComponent(new VLayoutComponent { Spacing = 2, Padding = 2 });
            _componentsPanel.AddComponent(new LayoutElementComponent { FlexibleHeight = 1 });
            inspectorPanel.AddChild(_componentsPanel);

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

            // ── Live hierarchy sync: walk the V12 world each tick and mirror it
            // into the hierarchy tree. Runs as an IGameService on the worker.
            _hierarchy = new HierarchySyncService(hierarchyTree, fps, Select);
            GameRoot.Instance?.Registry.Register("HierarchySync", _hierarchy);
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
