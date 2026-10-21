using System;
using System.IO;
using V12.Core.Core.Interfaces;
using V12.UI;
using V12Studio.Core;

namespace V12Studio.Editor
{
    /// <summary>
    /// Composes the whole editor against an <see cref="IUIProvider"/>: toolbar,
    /// hierarchy (left), game view + Project/Console/Packs tabs (center),
    /// inspector (right), status strip. Host-agnostic — the host supplies the
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

        /// <summary>The Inspector panel. Hosts use it for debug drives
        /// (opening a specific view behind a layout-dump flag).</summary>
        public InspectorPanel Inspector => _inspector;

        /// <summary>The Hierarchy panel. Hosts use it for debug drives
        /// (input probes).</summary>
        public HierarchyPanel Hierarchy => _hierarchy;
        private ILabel _sceneLabel = null!;
        private ILabel _selectionLabel = null!;

        public StudioShell(IUIProvider ui, StudioSession session, string? projectDir = null)
        {
            UI = ui;
            Session = session;
            Ops = new HierarchyOps(session);
            Creator = new ComponentCreator(session);
            Packs = new PackService(session.Root, session);
            Builder = new GamepackBuilder(session);
            ProjectDir = string.IsNullOrWhiteSpace(projectDir)
                ? AppContext.BaseDirectory
                : Path.GetFullPath(projectDir);
            session.ProjectDir = ProjectDir; // scenes + scripts resolve against the project

            _hierarchy = new HierarchyPanel(this);
            _inspector = new InspectorPanel(this);
            _hierarchy.AddComponentRequested += _inspector.ShowAddComponentMenu;
        }

        /// <summary>
        /// Build the editor into <paramref name="container"/>. Pass the
        /// host-built game-view panel to embed it in the center; null shows a
        /// labelled placeholder so every other panel still works.
        /// </summary>
        public IWidget Build(IPanel container, IPanel? gameViewPanel = null)
        {
            var root = UI.CreateVBox();
            root.AddChild(BuildToolbar());

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
            center.SetSecond(tabs);

            mid.SetFirst(center);
            mid.SetSecond(_inspector.Panel);
            outer.SetSecond(mid);
            root.AddChild(outer);
            root.AddChild(BuildStatus());

            container.AddChild(root);
            return root;
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
                        // The Project panel and other UI will reflect the new project
                        // on the next scene load / Inspector refresh.
                    }
                });
            }));

            bar.AddItem(UI.CreateButton("Build", () => BuildGamepak()));

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
                    _selectionLabel.Text += $" — {Ops.GetPath(sel)}";
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
    }
}
