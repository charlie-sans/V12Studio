using System;
using System.IO;
using System.Threading;
using V12.Components.UI;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.WorldML;
using V12Studio.Core;

namespace V12Studio.Editor
{
    /// <summary>
    /// Loads the editor's own chrome (status bar, later inspector cards) from
    /// <c>ui/V12StudioUI.worldml</c> into a dedicated <see cref="World"/> held by
    /// <see cref="GameRoot.EditorUIWorld"/>, and watches the file for live edits.
    ///
    /// The world is rendered by the editor's Paper layer through a shell host
    /// widget (see ProwlUIProvider), not the engine UI pass. Labels are
    /// bound to the session here because WorldML has no binding syntax; sub-
    /// scribing once and re-resolving the labels after a reload keeps the wiring
    /// stable while element ids change.
    /// </summary>
    public sealed class StudioUIWorld : IDisposable
    {
        private readonly GameRoot _root;
        private readonly StudioSession _session;
        private readonly string _projectDir;
        private readonly HierarchyOps _ops;

        private FileSystemWatcher? _watcher;
        private long _lastChangeTicks;
        private int _pending;
        private bool _disposed;

        private LabelComponent? _projectLabel;
        private LabelComponent? _sceneLabel;
        private LabelComponent? _selectionLabel;

        public string FilePath { get; }
        public World? World { get; private set; }

        /// <summary>Raised on the main thread after a successful file reload.</summary>
        public event Action? Reloaded;

        public StudioUIWorld(GameRoot root, StudioSession session, string projectDir, string filePath)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _projectDir = projectDir;
            _ops = new HierarchyOps(session);
            FilePath = Path.GetFullPath(filePath);

            _session.ScenePathChanged += UpdateScene;
            _session.SelectionChanged += UpdateSelection;
        }

        /// <summary>Parse the worldml and install it as the editor UI world.</summary>
        public bool Load()
        {
            if (_disposed || !File.Exists(FilePath)) return false;

            try
            {
                var parser = new WorldMLParser { TemplateProvider = new WorldTemplateProvider() };
                var contents = parser.ParseFile(FilePath);
                var world = new World(contents.Name ?? "V12StudioUI");
                world.AddElement(contents);

                World = world;
                _root.EditorUIWorld = world;
                BindLabels();
                _root.MarkRenderDirty();
                GameRoot.Log?.Information("[StudioUIWorld] loaded '{Path}'", FilePath);
                return true;
            }
            catch (Exception ex)
            {
                GameRoot.Log?.Warning("[StudioUIWorld] failed to load '{Path}': {Message}", FilePath, ex.Message);
                return false;
            }
        }

        /// <summary>Watch the source file for edits (debounced, polled from Tick).</summary>
        public void StartWatching()
        {
            if (_disposed || _watcher != null) return;

            var dir = Path.GetDirectoryName(FilePath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

            _watcher = new FileSystemWatcher(dir, Path.GetFileName(FilePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, __) => MarkDirty();
            _watcher.Created += (_, __) => MarkDirty();
            _watcher.Deleted += (_, __) => MarkDirty();
            _watcher.Renamed += (_, __) => MarkDirty();
        }

        private void MarkDirty()
        {
            Interlocked.Exchange(ref _lastChangeTicks, Environment.TickCount64);
            Interlocked.Exchange(ref _pending, 1);
        }

        /// <summary>Main-thread pump: reloads after a 250 ms quiet period.</summary>
        public void Tick()
        {
            if (_disposed || Volatile.Read(ref _pending) == 0) return;
            if (Environment.TickCount64 - Interlocked.Read(ref _lastChangeTicks) < 250) return;

            Interlocked.Exchange(ref _pending, 0);
            if (Load()) Reloaded?.Invoke();
        }

        private void BindLabels()
        {
            _projectLabel = FindLabel(World, "ProjectLabel");
            _sceneLabel = FindLabel(World, "SceneLabel");
            _selectionLabel = FindLabel(World, "SelectionLabel");

            UpdateProject();
            UpdateScene();
            UpdateSelection();
        }

        private void UpdateProject()
        {
            if (_projectLabel == null) return;
            _projectLabel.Text = $"Project: {ProjectName()}";
        }

        private void UpdateScene()
        {
            if (_sceneLabel == null) return;
            _sceneLabel.Text = $"Scene: {_session.ScenePath ?? "(unsaved)"}";
        }

        private void UpdateSelection()
        {
            if (_selectionLabel == null) return;

            var sel = _session.Selected;
            if (sel == null)
            {
                _selectionLabel.Text = "";
                return;
            }

            var text = $"Selected: {sel.Name ?? "(unnamed)"} ({sel.Components.Count} component(s))";
            _selectionLabel.Text = text + $"  -  {_ops.GetPath(sel)}";
        }

        private string ProjectName()
        {
            var trimmed = _projectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.IsNullOrEmpty(trimmed) ? _projectDir : Path.GetFileName(trimmed);
        }

        private static LabelComponent? FindLabel(World? world, string name)
        {
            if (world == null) return null;
            foreach (var root in world.Root)
            {
                var found = FindLabel(root, name);
                if (found != null) return found;
            }
            return null;
        }

        private static LabelComponent? FindLabel(IWorldElement? element, string name)
        {
            if (element == null) return null;

            if (string.Equals(element.Name, name, StringComparison.Ordinal))
                return element.GetComponent<LabelComponent>();

            foreach (var child in element.Children)
            {
                var found = FindLabel(child, name);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// Locate the default editor UI file: a project-local <c>ui/</c> copy
        /// first, then the source/runtime copies found by walking up from the
        /// executable (the Godot host's <c>ui/</c> folder lives there).
        /// </summary>
        public static string? FindDefaultPath(string? projectDir)
        {
            if (!string.IsNullOrEmpty(projectDir))
            {
                var local = Path.Combine(projectDir, "ui", "V12StudioUI.worldml");
                if (File.Exists(local)) return local;
            }

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "ui", "V12StudioUI.worldml");
                if (File.Exists(candidate)) return candidate;

                var source = Path.Combine(dir.FullName, "src", "Studio.Editor", "UI", "V12StudioUI.worldml");
                if (File.Exists(source)) return source;
            }

            return null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _session.ScenePathChanged -= UpdateScene;
            _session.SelectionChanged -= UpdateSelection;

            _watcher?.Dispose();
            _watcher = null;

            if (World != null && ReferenceEquals(_root.EditorUIWorld, World))
                _root.EditorUIWorld = null;
        }
    }
}
