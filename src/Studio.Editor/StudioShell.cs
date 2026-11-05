using System;
using System.IO;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.UI;
using V12.WorldML;
using V12Studio.Core;
using UIComponents = V12.Components.UI;

namespace V12Studio.Editor
{
    /// <summary>
    /// Composes the whole editor against an <see cref="IUIProvider"/>: toolbar,
    /// hierarchy (left), game view + Project/Console/Packs tabs (center),
    /// inspector (right), status strip. Host-agnostic  -  the host supplies the
    /// provider and optionally the pre-built 3D viewport panel.
    /// </summary>
    public sealed class StudioShell
    {
        public IUIProvider UI { get; }
        public StudioSession Session { get; }
        public HierarchyOps Ops { get; }
        public ComponentCreator Creator { get; }
        public ProjectScanner Scanner { get; } = new();
        public PackService Packs { get; }
        public GamepackBuilder Builder { get; }

        /// <summary>Root directory the Project tab scans and new scripts resolve under.</summary>
        public string ProjectDir { get; }

        private readonly HierarchyPanel _hierarchy;
        private readonly InspectorPanel _inspector;
        private readonly CodePanel _code;
        private readonly bool _externalStatus;

        /// <summary>True when the status strip comes from the editor UI world
        /// (<see cref="StudioUIWorld"/>), not the imperative <see cref="BuildStatus"/>.</summary>
        public bool ExternalStatus => _externalStatus;
        private IDockSpaceWidget? _dock;
        private ITabControl? _tabs;
        private int _codeTabIndex = -1;

        /// <summary>The Inspector panel. Hosts use it for debug drives
        /// (opening a specific view behind a layout-dump flag).</summary>
        public InspectorPanel Inspector => _inspector;

        /// <summary>The Hierarchy panel. Hosts use it for debug drives
        /// (input probes).</summary>
        public HierarchyPanel Hierarchy => _hierarchy;

        /// <summary>The code editor tab (opened by double-clicking a script in the Project tab).</summary>
        public CodePanel Code => _code;

        /// <summary>Widget that renders the editor UI world's status strip. Assigned
        /// by the host before <see cref="Build"/>; used in place of BuildStatus().</summary>
        public IWidget? StatusHost { get; set; }

        /// <summary>Loads a file into the Code tab and brings that tab forward.</summary>
        public void OpenScript(string path)
        {
            _code.Open(path);
            if (_dock != null) _dock.ShowTab("Code");
            else if (_tabs != null && _codeTabIndex >= 0) _tabs.SelectedTabIndex = _codeTabIndex;
        }
        private ILabel _sceneLabel = null!;
        private ILabel _selectionLabel = null!;
        private IButton _playButton = null!;
        private IButton _stopButton = null!;
        private ILabel _gameStatusLabel = null!;
        private ILabel _gameInfoLabel = null!;
        private ILabel _modeLabel = null!;

        public StudioShell(IUIProvider ui, StudioSession session, string? projectDir = null, bool externalStatus = false)
        {
            UI = ui;
            Session = session;
            _externalStatus = externalStatus;
            Ops = new HierarchyOps(session);
            Creator = new ComponentCreator(session);
            Packs = new PackService(session.Root, session);
            Builder = new GamepackBuilder(session);
            ProjectDir = string.IsNullOrWhiteSpace(projectDir)
                ? AppContext.BaseDirectory
                : Path.GetFullPath(projectDir);
            session.ProjectDir = ProjectDir; // scenes + scripts resolve against the project
            session.CSharp.Ensure();         // compile the project's C# components once at startup

            _hierarchy = new HierarchyPanel(this);
            _inspector = new InspectorPanel(this);
            _code = new CodePanel(this);
            _hierarchy.AddComponentRequested += _inspector.ShowAddComponentMenu;
            Session.PlayModeChanged += SyncPlayMode;
            Session.HierarchyChanged += SyncPlayMode;
        }

        /// <summary>
        /// Build the editor into <paramref name="container"/>. Pass the
        /// host-built game-view panel to embed it in the center; null shows a
        /// labelled placeholder so every other panel still works.
        /// </summary>
        public IWidget Build(IPanel container, IPanel? gameViewPanel = null)
        {
            if (UI is V12.UI.Paper.PaperUIProvider paperProvider)
                return BuildStatic(paperProvider, container, gameViewPanel);

            var root = UI.CreateVBox();
            root.AddChild(BuildToolbar());

            if (UI is IDockProvider dockProvider)
            {
                root.AddChild(BuildDock(dockProvider, gameViewPanel));
                AddStatusRow(root);
                container.AddChild(root);
                return root;
            }

            var outer = UI.CreateSplitter(SplitterOrientation.SideBySide);
            outer.SetFirst(_hierarchy.Panel);
            outer.SplitRatio = 0.20f;

            var mid = UI.CreateSplitter(SplitterOrientation.SideBySide);
            mid.SplitRatio = 0.85f;

            var center = UI.CreateSplitter(SplitterOrientation.Stacked);
            center.SplitRatio = 0.72f;
            if (gameViewPanel != null)
            {
                center.SetFirst(gameViewPanel);
            }
            else
            {
                var placeholder = UI.CreatePanel("GameView");
                placeholder.AddChild(UI.CreateLabel("3D viewport is not available in this host."));
                center.SetFirst(placeholder);
            }

            var tabs = UI.CreateTabControl();
            tabs.AddTab("Project", new ProjectPanel(this).Panel);
            tabs.AddTab("Console", new ConsolePanel(this).Panel);
            tabs.AddTab("Packs", new PacksPanel(this).Panel);
            tabs.AddTab("Code", _code.Panel);
            tabs.AddTab("Game", BuildGameTab());
            _tabs = tabs;
            _codeTabIndex = 3;
            center.SetSecond(tabs);

            mid.SetFirst(center);
            mid.SetSecond(_inspector.Panel);
            outer.SetSecond(mid);
            root.AddChild(outer);
            AddStatusRow(root);

            container.AddChild(root);
            return root;
        }

        /// <summary>
        /// Fixed Paper layout: hierarchy left, live viewport center, inspector right,
        /// and the Project/Console/Packs/Code tabs across the bottom. Paper's input
        /// coordinates are global here, so every descendant control is clickable.
        /// </summary>
        private IWidget BuildStatic(V12.UI.Paper.PaperUIProvider paper, IPanel container, IPanel? gameViewPanel)
        {
            var root = UI.CreateVBox();
            root.AddChild(BuildToolbar());

            var body = UI.CreateHBox();
            body.AddChild(_hierarchy.Panel);

            IPanel center;
            if (gameViewPanel != null)
            {
                center = gameViewPanel;
            }
            else
            {
                center = UI.CreatePanel("GameView");
                center.AddChild(UI.CreateLabel("3D viewport is not available in this host."));
            }
            body.AddChild(center);
            body.AddChild(_inspector.Panel);

            paper.ConfigureLayout(_hierarchy.Panel, preferredWidth: 260f, expand: false);
            paper.ConfigureLayout(_inspector.Panel, preferredWidth: 360f, expand: false);
            paper.ConfigureLayout(center, expand: true);
            paper.ConfigureLayout(body, expand: true);
            root.AddChild(body);

            var tabs = UI.CreateTabControl();
            tabs.AddTab("Project", new ProjectPanel(this).Panel);
            tabs.AddTab("Console", new ConsolePanel(this).Panel);
            tabs.AddTab("Packs", new PacksPanel(this).Panel);
            tabs.AddTab("Code", _code.Panel);
            tabs.AddTab("Game", BuildGameTab());
            _tabs = tabs;
            _codeTabIndex = 3;
            paper.ConfigureLayout(tabs, preferredHeight: 220f, expand: false);
            root.AddChild(tabs);

            AddStatusRow(root);
            paper.ConfigureLayout(root, expand: true);
            container.AddChild(root);
            return root;
        }

        /// <summary>
        /// Docking layout for hosts with a real dock surface (Paper/Origami): the
        /// hierarchy and inspector are drag/tab targets, the game view sits above the
        /// Project/Console/Packs tabs, and every divider is a live splitter.
        /// </summary>
        private IWidget BuildDock(IDockProvider provider, IPanel? gameViewPanel)
        {
            var dock = provider.CreateDockSpace();

            IDockNode viewport;
            if (gameViewPanel != null)
            {
                viewport = dock.Tabs(new DockTab("Viewport", gameViewPanel, closable: false));
            }
            else
            {
                var placeholder = UI.CreatePanel("GameView");
                placeholder.AddChild(UI.CreateLabel("3D viewport is not available in this host."));
                viewport = dock.Tabs(new DockTab("Viewport", placeholder, closable: false));
            }

            var bottom = dock.Tabs(
                new DockTab("Project", new ProjectPanel(this).Panel, closable: false),
                new DockTab("Console", new ConsolePanel(this).Panel, closable: false),
                new DockTab("Packs", new PacksPanel(this).Panel, closable: false),
                new DockTab("Code", _code.Panel, closable: false),
                new DockTab("Game", BuildGameTab(), closable: false));

            var center = dock.Split(DockOrientation.Vertical, 0.72f, viewport, bottom);

            var right = dock.Tabs(new DockTab("Inspector", _inspector.Panel, closable: false));
            var mid = dock.Split(DockOrientation.Horizontal, 0.72f, center, right);

            var left = dock.Tabs(new DockTab("Hierarchy", _hierarchy.Panel, closable: false));
            var root = dock.Split(DockOrientation.Horizontal, 0.20f, left, mid);

            dock.SetRoot(root);
            _dock = dock;
            return dock;
        }

        private IWidget BuildToolbar()
        {
            var bar = UI.CreateToolbar("StudioToolbar");

            bar.AddItem(UI.CreateButton("New", Session.NewScene));

            var picker = UI.CreateFilePicker();
            bar.AddItem(UI.CreateButton("Open", () =>
                picker.ShowOpen("Open Scene", "V12 scene (worldml)", new[] { "worldml" }, path =>
                {
                    if (path != null) Session.LoadScene(path, out _);
                })));
            bar.AddItem(UI.CreateButton("Save", () =>
            {
                if (Session.ScenePath != null)
                {
                    Session.SaveScene(out _);
                    return;
                }
                // Never-saved scene: ask where it belongs in the project.
                picker.ShowSave("Save Scene", "V12 scene (worldml)", new[] { "worldml" },
                    "scene.worldml", path =>
                    {
                        if (path != null) Session.SaveScene(path, out _);
                    });
            }));

            bar.AddItem(UI.CreateButton("Project", () =>
            {
                picker.ShowFolder("Select Project Folder", path =>
                {
                    if (path != null && Directory.Exists(path))
                    {
                        Session.ProjectDir = path;
                        Session.CSharp.Invalidate();
                        Session.CSharp.Ensure();
                        // The Project panel and other UI will reflect the new project
                        // on the next scene load / Inspector refresh.
                    }
                });
            }));

            bar.AddItem(UI.CreateButton("Build", () => BuildGamepak()));
            bar.AddItem(UI.CreateButton("Reload C#", () => ReloadCSharpComponents()));
            bar.AddItem(UI.CreateButton("Watch C#", () => ToggleWatchCSharp()));

            _playButton = UI.CreateButton("Play", () => Session.EnterPlayMode(out _));
            _stopButton = UI.CreateButton("Stop", () => Session.ExitPlayMode(out _));
            _modeLabel = UI.CreateLabel("");
            SyncPlayMode();
            bar.AddItem(_playButton);
            bar.AddItem(_stopButton);
            bar.AddItem(_modeLabel);

            IDropdown add = null!;
            add = UI.CreateDropdown(new[]
            {
                "Add: Empty", "Add: Box", "Add: Sphere", "Add: Capsule", "Add: Cylinder",
            }, index =>
            {
                if (index < 0) return;
                add.SelectedIndex = -1;
                var parent = Session.Selected;
                var target = Session.IsSceneElement(parent) ? parent : null;
                switch (index)
                {
                    case 0: Ops.CreateEmpty(null, target); break;
                    case 1: Ops.CreatePrimitive(PrimitiveKind.Box, null, target); break;
                    case 2: Ops.CreatePrimitive(PrimitiveKind.Sphere, null, target); break;
                    case 3: Ops.CreatePrimitive(PrimitiveKind.Capsule, null, target); break;
                    case 4: Ops.CreatePrimitive(PrimitiveKind.Cylinder, null, target); break;
                }
            });
            add.SelectedIndex = -1;
            bar.AddItem(add);

            // Theme picker: only hosts with a real palette system offer one.
            if (UI is IPaletteProvider palettes && palettes.PaletteNames.Count > 0)
            {
                var names = palettes.PaletteNames.ToList();
                int current = Math.Max(0, names.FindIndex(n =>
                    string.Equals(n, palettes.CurrentPalette, StringComparison.OrdinalIgnoreCase)));

                IDropdown theme = null!;
                theme = UI.CreateDropdown(names, index =>
                {
                    if (index < 0 || index >= names.Count) return;
                    palettes.SetPalette(names[index]);
                    theme.SelectedIndex = index;
                });
                theme.SelectedIndex = current;
                bar.AddItem(theme);
            }

            var dup = UI.CreateButton("Duplicate", () =>
            {
                if (Session.Selected != null) Ops.Duplicate(Session.Selected, out _);
            });
            var group = UI.CreateButton("Group", () =>
            {
                if (Session.Selected != null) Ops.Group(new[] { Session.Selected }, out _);
            });
            var del = UI.CreateButton("Delete", () =>
            {
                if (Session.Selected != null) Ops.Delete(Session.Selected, out _);
            });
            bar.AddItem(dup);
            bar.AddItem(group);
            bar.AddItem(del);

            void SyncEnabled()
            {
                var has = Session.Selected != null;
                dup.Enabled = has;
                group.Enabled = has;
                del.Enabled = has;
            }
            Session.SelectionChanged += SyncEnabled;
            SyncEnabled();

            return bar;
        }

        private void SyncPlayMode()
        {
            bool playing = Session.IsPlaying;
            if (_playButton != null) _playButton.Enabled = !playing;
            if (_stopButton != null) _stopButton.Enabled = playing;
            if (_modeLabel != null) _modeLabel.Text = playing ? "Mode: Play (locked)" : "Mode: Edit";
            if (_gameStatusLabel != null) _gameStatusLabel.Text = playing ? "Playing – scene is locked. Press Stop to return." : "Edit – components are paused. Press Play to run the clone.";
            if (_gameInfoLabel != null)
            {
                var editCount = Session.EditorSceneWorld?.Root.Count ?? 0;
                var playCount = Session.PlayWorld?.Root.Count ?? 0;
                _gameInfoLabel.Text = playing
                    ? $"Editor: {editCount} roots (paused)  |  Play: {playCount} roots (ticking)  |  Viewport: PersistentWorld"
                    : $"Editor: {editCount} roots (paused)  |  Viewport: PersistentWorld  |  Press Play to clone via WorldML";
            }
        }

        private IPanel BuildGameTab()
        {
            var panel = UI.CreatePanel("Game");
            var box = UI.CreateVBox();

            var title = UI.CreateLabel("GAME");
            UI.ApplyVariation(title, "GroupLabel");
            box.AddChild(title);

            _gameStatusLabel = UI.CreateLabel("");
            UI.ApplyVariation(_gameStatusLabel, "MutedLabel");
            box.AddChild(_gameStatusLabel);

            var row = UI.CreateHBox();
            var play = UI.CreateButton("Play", () => Session.EnterPlayMode(out _));
            var stop = UI.CreateButton("Stop", () => Session.ExitPlayMode(out _));
            // Mirror toolbar state; separate instances for the tab
            Session.PlayModeChanged += () =>
            {
                bool playing = Session.IsPlaying;
                play.Enabled = !playing;
                stop.Enabled = playing;
            };
            play.Enabled = !Session.IsPlaying;
            stop.Enabled = Session.IsPlaying;
            row.AddChild(play);
            row.AddChild(stop);
            row.AddChild(UI.CreateLabel("WorldML clone – camera stays in shell, items come from the other world."));
            box.AddChild(row);

            _gameInfoLabel = UI.CreateLabel("");
            UI.ApplyVariation(_gameInfoLabel, "MutedLabel");
            box.AddChild(_gameInfoLabel);

            var hint = UI.CreateLabel("Tip: Scene is locked while in Play mode. TODO: disable lock after tested to allow live tweaks.");
            UI.ApplyVariation(hint, "MutedLabel");
            box.AddChild(hint);

            panel.AddChild(box);
            SyncPlayMode();
            return panel;
        }

        private void AddStatusRow(IVBox root)
        {
            if (_externalStatus && StatusHost != null) root.AddChild(StatusHost);
            else root.AddChild(BuildStatus());
        }

        private IWidget BuildStatus()
        {
            var row = UI.CreateHBox();
            var projectLabel = UI.CreateLabel($"Project: {ProjectName()}");
            _sceneLabel = UI.CreateLabel("Scene: (unsaved)");
            _selectionLabel = UI.CreateLabel("");
            row.AddChild(projectLabel);
            row.AddChild(_sceneLabel);
            row.AddChild(_selectionLabel);

            Session.ScenePathChanged += () =>
                _sceneLabel.Text = $"Scene: {Session.ScenePath ?? "(unsaved)"}";
            Session.SelectionChanged += () =>
            {
                var sel = Session.Selected;
                _selectionLabel.Text = sel == null
                    ? ""
                    : $"Selected: {sel.Name ?? "(unnamed)"} ({sel.Components.Count} component(s))";
                if (sel != null)
                    _selectionLabel.Text += $"  -  {Ops.GetPath(sel)}";
            };
            return row;
        }

        /// <summary>
        /// Build the open project into a .v12pak gamepak with the V12 SDK.
        /// Returns the outcome; progress and failures land in the console log.
        /// </summary>
        public GamepackBuildResult BuildGamepak(string? outputPath = null)
            => Builder.Build(Session.ProjectDir ?? ProjectDir, outputPath);

        /// <summary>Friendly project name: the project folder's name.</summary>
        private string ProjectName()
        {
            var trimmed = ProjectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.IsNullOrEmpty(trimmed) ? ProjectDir : Path.GetFileName(trimmed);
        }

        private void ReloadCSharpComponents()
        {
            var result = Session.CSharp.Reload();
            if (result == null)
            {
                Session.Log.Info("No C# scripts under scripts/ to reload.");
                return;
            }
            if (!result.Success)
                Session.Log.Error($"C# compile failed:\n{result.ErrorText}");
            else
                Session.Log.Info($"Reloaded C# components ({result.Warnings.Count} warning(s)).");
        }

        private bool _watchingCSharp;
        private void ToggleWatchCSharp()
        {
            if (_watchingCSharp) return;
            Session.CSharp.StartWatching();
            _watchingCSharp = true;
            Session.Log.Info("Watching scripts/ for C# changes (reloads on save).");
        }
    }
}



