using System;
using System.IO;
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
        private SubViewport? _sceneSub;
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
        private string? _projectPath;
        private string? _newProjectTemplate;
        private string? _newProjectPath;
        private bool _probePick;
        private bool _probeGizmoScale;
        private bool _probeDrag;
        private bool _probeInput;
        private bool _probeTreeDrag;
        private bool _probeShared;
        private bool _probeInspector;
        private StudioShell? _shell;
        private int _frameCount;

        public override void _Ready()
        {

            // Args can arrive two ways in Godot: engine-level args (what the
            // exported exe is handed, e.g. `V12Studio.exe --project=...`) show
            // up in GetCmdlineArgs(), while only args after a `--` separator
            // land in GetCmdlineUserArgs(). Read both.
            var args = OS.GetCmdlineArgs().Concat(OS.GetCmdlineUserArgs()).ToArray();

            // Value options accept both `--name=value` and `--name value`. The
            // spaced form is easy to type by accident and used to be dropped
            // silently, so every flag below understands both spellings.
            string? Option(string name)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                        return args[i].Substring(name.Length + 1);
                    if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                        return i + 1 < args.Length ? args[i + 1] : null;
                }
                return null;
            }
            bool Flag(string name) =>
                args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

            _screenshotPath = Option("--screenshot");
            _layoutDumpPath = Option("--layout-dump");
            _selectName = Option("--select");
            _formName = Option("--new-component-form");
            _menuName = Option("--add-component-menu");
            _saveScenePath = Option("--save-scene");
            _loadScenePath = Option("--load-scene");
            _projectPath = Option("--project");
            _newProjectTemplate = Option("--new-project");
            _newProjectPath = Option("--new-project-path");
            _probePick = Flag("--probe-pick");
            _probeGizmoScale = Flag("--probe-gizmo-scale");
            _probeDrag = Flag("--probe-drag");
            _probeInput = Flag("--probe-input");
            _probeTreeDrag = Flag("--probe-tree-drag");
            _probeShared = Flag("--probe-shared");
            _probeInspector = Flag("--probe-inspector");

            var options = new V12RuntimeOptions
            {
                GamepakAssemblies = new[] { typeof(V12StudioGamepack).Assembly },
                ShowLauncher = false,
                CreateEmptyWorldIfNoPak = true,
                // Desktop editor: no XR, no networking, no first-person input,
                // the mouse stays visible for the UI, and no DebugGameService
                // Label3D dump floating in the 3D viewport.
                EnableXR = false,
                EnableNetworking = false,
                EnableInput = false,
                LockMouseOnStart = false,
                EnableDebugOverlay = false,
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
            session.Dispatch = a => Callable.From(a).CallDeferred();
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
            _sceneSub = sub;
            _renderer.RegisterSceneViewport(vpElement.Id, sub);

            // Loading a scene wipes the scene world's roots — including the
            // EditorViewport element — and EnsureViewportElement creates a NEW
            // one with a new id. Re-bind the SubViewport to that id or the
            // renderer never reparents scene nodes into it: the game view goes
            // grey with no camera (and picking reports "ray failed").
            session.ViewportElementChanged += () =>
                Callable.From(RebindSceneViewport).CallDeferred();

            // Editor window: the Godot MAIN window. Keeping the UI in the main
            // window means input events, focus and mouse coordinates are the
            // same space the rest of the host works in (and avoids a second,
            // empty OS window behind the editor).
            _ui = new GodotUIProvider(GetWindow());
            var window = (Window)_ui.RootWindow.NativeControl;
            window.Title = "V12 Studio";
            StudioThemeManager.Load();        // restores saved palette (safe to call more than once)
            StudioThemeManager.Bind(window);  // sets window.Theme now + on every palette change
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

            // Resolve the project folder before the shell is built. StudioShell
            // captures its ProjectDir for the Project panel, status bar, asset
            // scanner and default save path, so it must be handed the requested
            // project here — patching the session afterwards would leave all of
            // that pointing at the export directory (res://).
            string? projectDir = null;
            if (!string.IsNullOrEmpty(_projectPath))
            {
                if (Directory.Exists(_projectPath))
                {
                    projectDir = Path.GetFullPath(_projectPath);
                }
                else
                {
                    // Fail loudly instead of silently falling back to res://:
                    // a mistyped --project used to look like "the arg is ignored".
                    GD.PrintErr($"[StudioHost] --project: no such directory '{_projectPath}'; " +
                        "falling back to the export directory.");
                }
            }
            projectDir ??= ProjectSettings.GlobalizePath("res://");
            GD.Print($"[StudioHost] project directory: '{projectDir}'");

            // Pack-root asset resolution: mount the project directory as the
            // default v12:// mount so v12://textures/crate.png (and the
            // folder-named form v12://<ProjectName>/…) resolve from the pack
            // root at render time. First mount becomes the resolver default.
            if (_runtime.Root.Registry.Get<V12.Core.Interfaces.IAssetResolver>()
                    is not V12.Core.V12AssetResolver assetResolver)
            {
                assetResolver = new V12.Core.V12AssetResolver();
                _runtime.Root.Registry.Register("AssetResolver", assetResolver);
            }
            var packMount = Path.GetFileName(projectDir.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(packMount)) packMount = "project";
            assetResolver.Mount(packMount, projectDir);

            var shell = new StudioShell(_ui, session, projectDir);
            _shell = shell;
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

            if (_screenshotPath != null || _probePick || _probeGizmoScale || _probeDrag || _probeInput || _probeTreeDrag || _probeShared || _probeInspector)
            {
                _frameCount++;
                // Gizmo constant-screen-size diagnosis: select, log, teleport the
                // camera back, log again (GizmoScaleProbe.cs for the stages).
                if (_probeGizmoScale && _runtime != null && _session != null
                    && _renderer != null && _viewportAdapter != null)
                {
                    GizmoScaleProbe.Tick(_frameCount, _session, _renderer,
                        _viewportAdapter, _viewportElementId);
                }

                if (_probeInput && _editorWindow != null && GodotObject.IsInstanceValid(_editorWindow))
                {
                    if (_frameCount == 60)
                        InputProbeNode.Push(_editorWindow, new Vector2(400, 400));
                    else if (_frameCount == 63)
                        InputProbeNode.Report();
                }

                if (_probeShared && _editorWindow != null && GodotObject.IsInstanceValid(_editorWindow))
                {
                    if (_frameCount == 60)
                        SharedDispatchProbe.Start(_editorWindow);
                    else if (_frameCount == 62)
                        SharedDispatchProbe.Push(_editorWindow, new Vector2(400, 400));
                    else if (_frameCount == 66)
                        SharedDispatchProbe.Report();
                }

                // Drag probe is frame-driven: it dispatches one event per frame
                // (PushInput delivers on the following frame) and reports quit
                // when the last scenario settles. Needs a warm-up frame so the
                // shell layout has real item rects first.
                if (_probeDrag && _frameCount > 60 && _session != null && _editorWindow != null
                    && GodotObject.IsInstanceValid(_editorWindow))
                {
                    if (DragProbe.Tick(_shell!, _session))
                    {
                        GetTree().Quit();
                        return;
                    }
                }

                if (_probeTreeDrag && _editorWindow != null && GodotObject.IsInstanceValid(_editorWindow))
                {
                    if (TreeDragIsolation.Tick(_editorWindow, _frameCount))
                    {
                        GetTree().Quit();
                        return;
                    }
                }

                // Inspector probe: pure assertions against the real panel
                // (variations, foldout round-trip, remove) — one shot once the
                // shell has warmed up.
                if (_probeInspector && _frameCount > 60 && _session != null && _shell != null)
                {
                    if (InspectorProbe.Tick(_shell, _session))
                    {
                        GetTree().Quit();
                        return;
                    }
                }

                int quitAt = _probeGizmoScale ? 150 : _probeInput ? 66 : _probeShared ? 68 : _probeDrag || _probeTreeDrag || _probeInspector ? 100000 : 120;
                if (_frameCount >= quitAt && _editorWindow != null
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

        /// <summary>Re-register the game-view SubViewport under the session's
        /// current viewport element id (raised via CallDeferred so any thread
        /// that swapped the element ends up on the main thread). Unregisters
        /// the stale id so the adapter, picking and the renderer's reparent
        /// pass all agree on one registration.</summary>
        private void RebindSceneViewport()
        {
            if (_sceneSub == null || !GodotObject.IsInstanceValid(_sceneSub)) return;
            if (_renderer == null || _session == null) return;

            var newId = _session.ViewportElement?.Id ?? 0;
            if (newId == _viewportElementId) return;

            var oldId = _viewportElementId;
            if (oldId != 0)
                _renderer.UnregisterSceneViewport(oldId);
            _viewportElementId = newId;
            if (newId != 0)
                _renderer.RegisterSceneViewport(newId, _sceneSub);
            GD.Print($"[StudioHost] Scene viewport re-bound: {oldId} -> {newId}");
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
            else if (node is PopupMenu popupMenu)
            {
                // Popup menus are Windows, not Controls — the walker above
                // skips them, and their items are not nodes at all. Write the
                // item captions so a dump can verify menu contents.
                var indent = new string(' ', depth * 2);
                var items = new System.Collections.Generic.List<string>();
                for (int i = 0; i < popupMenu.ItemCount; i++)
                    items.Add(popupMenu.GetItemText(i));
                writer.WriteLine($"{indent}PopupMenu name='{popupMenu.Name}' pos={popupMenu.Position} size={popupMenu.Size} visible={popupMenu.Visible} items=[{string.Join(" | ", items)}]");
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
