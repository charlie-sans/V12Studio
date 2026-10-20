using System;
using System.Linq;
using Godot;
using V12;
using V12.Core.Core.Interfaces;
using V12.Core.UI;
using V12Studio.Core;
using V12Studio.Editor;
using V12TwoDog;
using V12TwoDog.Godot;
using V12TwoDog.Godot.UI;

namespace V12Studio.Godot
{
    /// <summary>
    /// V12 Studio Godot host. Creates the V12 runtime with the Studio pak,
    /// wires the viewport interaction adapter, hosts the editor UI in its own
    /// Window (the GodotUIProvider's root window), and embeds a SubViewport
    /// game view registered against the session's EditorViewport element so
    /// scene renderables route into it.
    /// </summary>
    public partial class StudioHost : Node3D
    {
        private V12Runtime? _runtime;
        private GodotUIProvider? _ui;
        private GodotViewportAdapter? _viewportAdapter;
        private V12TwoDog.Renderer? _renderer;
        private long _viewportElementId;
        private Window? _editorWindow;
        private StudioSession? _session;

        /// <summary>Debug aid: --screenshot=path.png captures the editor window
        /// after two seconds of frames and quits (used to verify layout).
        /// --select=Name selects a scene element, --new-component-form=Name
        /// opens the New Component form for it, so a layout dump can capture
        /// those states without a pointer. --probe-pick logs the picking
        /// diagnosis (ray vs Godot's own unproject, geometry, mouse space).</summary>
        private string? _screenshotPath;
        private string? _layoutDumpPath;
        private string? _selectName;
        private string? _formName;
        private string? _menuName;
        private string? _saveScenePath;
        private string? _loadScenePath;
        private bool _probePick;
        private int _frameCount;

        public override void _Ready()
        {
            foreach (var arg in OS.GetCmdlineUserArgs())
            {
                if (arg.StartsWith("--screenshot=", StringComparison.OrdinalIgnoreCase))
                    _screenshotPath = arg.Substring("--screenshot=".Length);
                else if (arg.StartsWith("--layout-dump=", StringComparison.OrdinalIgnoreCase))
                    _layoutDumpPath = arg.Substring("--layout-dump=".Length);
                else if (arg.StartsWith("--select=", StringComparison.OrdinalIgnoreCase))
                    _selectName = arg.Substring("--select=".Length);
                else if (arg.StartsWith("--new-component-form=", StringComparison.OrdinalIgnoreCase))
                    _formName = arg.Substring("--new-component-form=".Length);
                else if (arg.StartsWith("--add-component-menu=", StringComparison.OrdinalIgnoreCase))
                    _menuName = arg.Substring("--add-component-menu=".Length);
                else if (arg.StartsWith("--save-scene=", StringComparison.OrdinalIgnoreCase))
                    _saveScenePath = arg.Substring("--save-scene=".Length);
                else if (arg.StartsWith("--load-scene=", StringComparison.OrdinalIgnoreCase))
                    _loadScenePath = arg.Substring("--load-scene=".Length);
                else if (arg.Equals("--probe-pick", StringComparison.OrdinalIgnoreCase))
                    _probePick = true;
            }

            var options = new V12RuntimeOptions
            {
                GamepakAssemblies = new[] { typeof(V12StudioGamepack).Assembly },
                ShowLauncher = false,
                CreateEmptyWorldIfNoPak = true,
                // Desktop editor: no XR, no networking, no first-person input,
                // and the mouse stays visible for the UI.
                EnableXR = false,
                EnableNetworking = false,
                EnableInput = false,
                LockMouseOnStart = false,
            };
            _runtime = V12Runtime.Create(this, options);
            _renderer = _runtime.RendererService as V12TwoDog.Renderer
                ?? throw new InvalidOperationException("RendererService is not the Godot renderer.");
            _renderer.LockMouse = false;

            // Viewport interaction: a thin Godot adapter (viewport registry +
            // input + direct transforms) feeding the Studio.Core service that
            // owns fly/orbit/picking/gizmo logic.
            _viewportAdapter = new GodotViewportAdapter(_renderer);
            var interaction = new ViewportInteractionService(_runtime.Root, _viewportAdapter, _viewportAdapter, _viewportAdapter);
            _runtime.Root.Registry.Register("IViewportInteraction", interaction);

            var editor = Registry.Resolve<IEditor>()
                ?? throw new InvalidOperationException("Studio pak did not register IEditor.");
            var session = editor.Session;
            _session = session;
            session.Interaction = interaction;
            session.Start(); // scene world + EditorViewport element + demo content

            var vpElement = session.ViewportElement
                ?? throw new InvalidOperationException("Session did not create a viewport element.");
            _viewportElementId = vpElement.Id;

            // SubViewport the engine renders the scene world into.
            var sub = new SubViewport
            {
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                OwnWorld3D = false,
                HandleInputLocally = false,
            };
            var subContainer = new SubViewportContainer { Stretch = true };
            subContainer.AddChild(sub);
            _renderer.RegisterSceneViewport(vpElement.Id, sub);

            // Editor window: the Godot MAIN window. Keeping the UI in the main
            // window means input events, focus and mouse coordinates are the
            // same space the rest of the host works in (and avoids a second,
            // empty OS window behind the editor).
            _ui = new GodotUIProvider(GetWindow());
            var window = (Window)_ui.RootWindow.NativeControl;
            window.Title = "V12 Studio";
            window.Theme = GodotStudioTheme.Create();
            _editorWindow = window;

            // Game-view panel: hosts the SubViewport inside the panel's own
            // VBox; shell embeds it as the top of the center column.
            var gameViewPanel = _ui.CreatePanel("GameView");
            var gameViewBox = ((PanelContainer)gameViewPanel.NativeControl).GetChild(0) as VBoxContainer;
            if (gameViewBox != null)
                gameViewBox.AddChild(subContainer);
            else
                ((Control)gameViewPanel.NativeControl).AddChild(subContainer);

            var container = _ui.CreatePanel("Root");
            var containerControl = (Control)container.NativeControl;
            containerControl.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            // The main window is still setting up its children while _Ready
            // runs, so the UI container must enter the tree deferred.
            window.CallDeferred(Node.MethodName.AddChild, containerControl);

            var shell = new StudioShell(_ui, session, ProjectSettings.GlobalizePath("res://"));
            var root = shell.Build(container, gameViewPanel);
            if (root.NativeControl is Control rootControl)
                GrowAll(rootControl);

            // Debug aids in the same spirit as --screenshot/--layout-dump:
            // drive an inspector state so a dump can capture it unattended.
            if (_selectName != null)
            {
                var el = session.FindSceneElement(_selectName);
                if (el != null) session.Select(el);
                else GD.PrintErr($"[StudioHost] --select: no scene element '{_selectName}'.");
            }
            if (_formName != null)
            {
                var el = session.FindSceneElement(_formName);
                if (el != null) shell.Inspector.ShowNewComponentForm(el);
                else GD.PrintErr($"[StudioHost] --new-component-form: no scene element '{_formName}'.");
            }
            if (_menuName != null)
            {
                var el = session.FindSceneElement(_menuName);
                if (el != null) shell.Inspector.ShowAddComponentMenu(el);
                else GD.PrintErr($"[StudioHost] --add-component-menu: no scene element '{_menuName}'.");
            }
            if (_loadScenePath != null && !session.LoadScene(_loadScenePath, out var loadErr))
                GD.PrintErr($"[StudioHost] --load-scene: {loadErr}");
            if (_saveScenePath != null && !session.SaveScene(_saveScenePath, out var saveErr))
                GD.PrintErr($"[StudioHost] --save-scene: {saveErr}");
        }

        public override void _PhysicsProcess(double delta)
        {
            // Main-thread pump: applies the worker's latest frame snapshot
            // (creates/moves the scene nodes and reparents viewport subtrees).
            _runtime?.ProcessPhysics((float)delta);
            _viewportAdapter?.ApplyEditorDrivenTransforms();
        }

        public override void _Process(double delta)
        {
            _viewportAdapter?.PollInput();
            _viewportAdapter?.ApplyEditorDrivenTransforms();
            _ui?.ProcessFrame(delta);

            if (_screenshotPath != null || _probePick)
            {
                if (++_frameCount >= 120 && _editorWindow != null
                    && GodotObject.IsInstanceValid(_editorWindow))
                {
                    DumpLayout(_editorWindow, 0);
                    DumpViewports();
                    if (_layoutDumpPath != null)
                    {
                        using var writer = System.IO.File.CreateText(_layoutDumpPath);
                        WriteLayout(_editorWindow, writer, 0);
                    }
                    if (_probePick && _runtime != null && _viewportAdapter != null
                        && _renderer != null && _session != null)
                    {
                        PickProbe.Run(_runtime, _viewportAdapter, _renderer, _session, _viewportElementId);
                    }
                    if (_screenshotPath != null)
                    {
                        try
                        {
                            var img = _editorWindow.GetTexture().GetImage();
                            img.SavePng(_screenshotPath);
                            GD.Print($"[StudioHost] Screenshot saved: {_screenshotPath}");
                        }
                        catch (Exception ex)
                        {
                            GD.PrintErr($"[StudioHost] Screenshot failed: {ex.Message}");
                        }
                    }
                    GetTree().Quit();
                }
            }
        }

        public override void _Input(InputEvent @event)
        {
            _runtime?.HandleInputEvent(@event);
            if (@event is InputEventMouseButton { Pressed: true } mb)
            {
                if (mb.ButtonIndex == MouseButton.WheelUp)
                    _viewportAdapter?.AccumulateWheel(120);
                else if (mb.ButtonIndex == MouseButton.WheelDown)
                    _viewportAdapter?.AccumulateWheel(-120);
            }
        }

        public override void _Notification(int what)
        {
            if (what == NotificationPredelete)
            {
                _renderer?.UnregisterSceneViewport(_viewportElementId);
                _runtime?.Shutdown();
                _runtime = null;
            }
        }

        /// <summary>Debug aid: print where scene nodes ended up relative to the
        /// registered scene viewports (SubViewport size, world sharing, camera,
        /// mesh/child counts).</summary>
        private void DumpViewports()
        {
            if (_renderer == null) return;
            foreach (var (id, vp) in _renderer.SceneViewports)
            {
                if (!GodotObject.IsInstanceValid(vp))
                {
                    GD.Print($"[VP] id={id} INVALID");
                    continue;
                }
                var win = vp.GetWindow();
                var world = vp.World3D;
                var cam = vp.GetCamera3D();
                GD.Print($"[VP] id={id} size={vp.Size} ownWorld={vp.OwnWorld3D} " +
                    $"world={world?.GetInstanceId()} cam={(cam != null ? cam.Name.ToString() : "null")} " +
                    $"camCurrent={(cam != null ? cam.Current.ToString() : "-")} " +
                    $"window={win?.GetInstanceId()} hasCurrentCam={_renderer.HasCurrentCamera(id)}");
                DumpTree(vp, 1, 6);
            }

            var sceneRootChildren = GetTree().Root.GetChildren();
            GD.Print($"[VP] treeRoot children={sceneRootChildren.Count}");
            foreach (var child in sceneRootChildren)
                GD.Print($"[VP]   treeChild: {child.GetType().Name} '{child.Name}'");

            var sceneWorld2 = _session?.SceneWorld;
            if (sceneWorld2 != null)
            {
                void DumpEl(IWorldElement el, int depth)
                {
                    var comps = string.Join(",", el.Components.Select(c => c.GetType().Name));
                    var node = _renderer.GetNode(el.Id);
                    GD.Print($"[VP] {new string(' ', depth * 2)}el id={el.Id} '{el.Name}' " +
                        $"parent={(el.Parent?.Id ?? 0)} vpId={_renderer.GetElementViewportId(el.Id)} " +
                        $"node={(node == null ? "null" : $"'{node.Name}'@{node.GetParent()?.Name}")} comps=[{comps}]");
                    foreach (var child in el.Children)
                        DumpEl(child, depth + 1);
                }
                foreach (var rootEl in sceneWorld2.Root)
                    DumpEl(rootEl, 1);
            }
        }

        private static void DumpTree(Node node, int depth, int maxDepth)
        {
            var pad = new string(' ', depth * 2);
            foreach (var child in node.GetChildren())
            {
                string extra = child switch
                {
                    Node3D n3 => $" pos={n3.GlobalPosition}",
                    Control c => $" size={c.Size}",
                    _ => ""
                };
                GD.Print($"[VP]{pad}{child.GetType().Name} '{child.Name}'{extra}");
                if (depth < maxDepth)
                    DumpTree(child, depth + 1, maxDepth);
            }
        }

        /// <summary>Debug aid: print the editor control tree geometry so layout
        /// can be verified from console output (zero-size collapses, split
        /// ratios, panel widths).</summary>
        private static void DumpLayout(Node node, int depth)
        {
            if (node is Control c)
            {
                var r = c.GetGlobalRect();
                var min = c.GetCombinedMinimumSize();
                var pad = new string(' ', depth * 2);
                var expand = (c.SizeFlagsHorizontal & Control.SizeFlags.Expand) != 0 ? "H+" : "";
                expand += (c.SizeFlagsVertical & Control.SizeFlags.Expand) != 0 ? "V+" : "";
                var split = c is SplitContainer sc
                    ? $" split(offsets=[{string.Join(",", sc.SplitOffsets)}],vert={sc.Vertical})"
                    : "";
                GD.Print($"[Layout]{pad}{node.GetType().Name} '{node.Name}' " +
                    $"pos=({r.Position.X:F0},{r.Position.Y:F0}) size=({r.Size.X:F0}x{r.Size.Y:F0}) " +
                    $"min=({min.X:F0}x{min.Y:F0}) vis={c.Visible} {expand}{split}{DumpText(c)}");

                bool hasControlChild = false;
                foreach (var child in node.GetChildren())
                {
                    if (child is Control) hasControlChild = true;
                    DumpLayout(child, depth + 1);
                }
                if (!hasControlChild && c.Visible && (r.Size.X < 4 || r.Size.Y < 4))
                    GD.Print($"[Layout] !! ZERO-SIZE LEAF: {node.GetType().Name} '{node.Name}'");
            }
            else
            {
                foreach (var child in node.GetChildren())
                    DumpLayout(child, depth);
            }
        }

        /// <summary>Debug aid: label/button text so dumps show what is on
        /// screen, not only geometry.</summary>
        private static string DumpText(Control c) => c switch
        {
            Label l => $" text='{SanitizeDumpText(l.Text)}'",
            Button b => $" text='{SanitizeDumpText(b.Text)}'", // includes OptionButton
            _ => "",
        };

        private static string SanitizeDumpText(string s)
            => s.Replace("'", "\"").Replace("\n", " ");

        private static void WriteLayout(Node node, System.IO.TextWriter writer, int depth)
        {
            if (node is Control control)
            {
                var rect = control.GetGlobalRect();
                var min = control.GetCombinedMinimumSize();
                var indent = new string(' ', depth * 2);
                var split = control is SplitContainer container
                    ? $" split={string.Join(",", container.SplitOffsets)} vertical={container.Vertical}"
                    : "";
                writer.WriteLine($"{indent}{control.GetType().Name} name='{control.Name}' pos={rect.Position} size={rect.Size} min={min} flags=({control.SizeFlagsHorizontal},{control.SizeFlagsVertical}){split}{DumpText(control)}");
            }
            foreach (var child in node.GetChildren())
                WriteLayout(child, writer, depth + 1);
        }

        private static void GrowAll(Control node)
        {
            if (node is VBoxContainer shellRoot
                && shellRoot.GetParent() is VBoxContainer panelLayout
                && panelLayout.GetParent() is PanelContainer)
            {
                node.SizeFlagsHorizontal |= Control.SizeFlags.ExpandFill;
                node.SizeFlagsVertical |= Control.SizeFlags.ExpandFill;
            }
            else if (node is PanelContainer)
            {
                node.SizeFlagsHorizontal |= Control.SizeFlags.ExpandFill;
                node.SizeFlagsVertical |= Control.SizeFlags.ExpandFill;
            }
            else if (node is VBoxContainer vbox && vbox.GetParent() is PanelContainer)
            {
                node.SizeFlagsHorizontal |= Control.SizeFlags.Expand;
                node.SizeFlagsVertical |= Control.SizeFlags.Expand;
            }
            else if (node is SplitContainer or SubViewportContainer or ScrollContainer or GridContainer or Tree)
            {
                node.SizeFlagsHorizontal |= Control.SizeFlags.ExpandFill;
                node.SizeFlagsVertical |= Control.SizeFlags.ExpandFill;
            }
            else if (node is HBoxContainer or VBoxContainer)
            {
                node.SizeFlagsHorizontal |= Control.SizeFlags.Expand;
                node.SizeFlagsVertical &= ~Control.SizeFlags.Expand;
                node.SizeFlagsVertical |= Control.SizeFlags.ShrinkBegin;
            }
            else if (node is LineEdit && node.GetParent() is HBoxContainer)
            {
                node.SizeFlagsHorizontal |= Control.SizeFlags.Expand;
                node.SizeFlagsVertical &= ~Control.SizeFlags.Expand;
            }

            // A widget can opt out of the blanket expansion (Expand = false,
            // stored as node metadata by the box wrappers): strip the
            // horizontal Expand back off so it hugs its content instead of
            // splitting spare width with its siblings.
            if (node.HasMeta(LayoutHints.ExpandMetaKey)
                && !((bool)node.GetMeta(LayoutHints.ExpandMetaKey)))
            {
                node.SizeFlagsHorizontal &= ~Control.SizeFlags.Expand;
            }

            foreach (var child in node.GetChildren())
            {
                if (child is Control c) GrowAll(c);
            }
        }
    }
}
