using System;
using V12.Components;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.UI.Paper;
using V12Studio.Core;

namespace V12Studio.Editor
{
    /// <summary>
    /// Host-level switch for the studio UI. A host that builds its own editor
    /// chrome (the custom Godot studio host) sets this before creating the V12
    /// runtime; every other renderer leaves it false and the pak builds the
    /// renderer-neutral engine-UI shell.
    /// </summary>
    public static class StudioEditorBootstrap
    {
        public const string HostOwnsUiEnvironmentVariable = "V12STUDIO_HOST_UI";
        private static bool _hostOwnsUi;

        /// <summary>
        /// True when a host builds the editor chrome itself. The setter also
        /// publishes a process environment variable so the flag crosses Godot's
        /// plugin load context (the gamepak and the host can otherwise hold
        /// separate copies of this static).
        /// </summary>
        public static bool HostOwnsUi
        {
            get => _hostOwnsUi
                || string.Equals(Environment.GetEnvironmentVariable(HostOwnsUiEnvironmentVariable), "1", StringComparison.Ordinal);
            set
            {
                _hostOwnsUi = value;
                Environment.SetEnvironmentVariable(HostOwnsUiEnvironmentVariable, value ? "1" : null);
            }
        }
    }

    /// <summary>
    /// Root-level studio bootstrap component.
    ///
    /// Attach it to an element in the persistent world. It creates and registers
    /// the host-agnostic <see cref="StudioSession"/> (so any host can resolve
    /// <see cref="IEditor"/>), and on the first world tick it starts the session
    /// and builds the component-backed <see cref="StudioShell"/> unless a host has
    /// taken over the editor chrome.
    ///
    /// This is the component form of the studio gamepak boot: the UI is ordinary
    /// V12 elements (canvas, dock space, tables, viewport), so the engine's
    /// Paper/Origami renderer picks it up on any V12 renderer.
    /// </summary>
    public sealed class StudioEditorComponent : ComponentBase
    {
        private GameRoot? _root;
        private V12.UI.IUIProvider? _provider;
        private StudioShell? _shell;

        public override string Name => "V12 Studio Editor";
        public override string Description => "Root bootstrap for the renderer-neutral studio shell";

        /// <summary>Project directory the UI scans and builds into.</summary>
        public string ProjectDir { get; set; } = AppContext.BaseDirectory;

        /// <summary>Set false to register the session without building UI.</summary>
        public bool BuildUi { get; set; } = true;

        public StudioSession? Session { get; private set; }

        public override void OnAttach(IWorldElement worldElement)
        {
            base.OnAttach(worldElement);
            IsPersistent = false;
            EnsureSession();
            BuildShell();
        }

        private void EnsureSession()
        {
            if (Session != null) return;

            _root = GameRoot.Instance;
            if (_root == null) return;

            Session = new StudioSession(_root) { ProjectDir = ProjectDir };
            Session.Interaction = _root.Registry.Get<V12.Core.UI.IViewportInteraction>("IViewportInteraction");

            _root.Registry.RegisterOrReplace("IEditor", Session);
        }

        private void BuildShell()
        {
            if (_shell != null || Session == null || _root == null) return;
            if (!BuildUi || StudioEditorBootstrap.HostOwnsUi) return;

            try
            {
                if (Session.SceneWorld == null)
                    Session.Start();

                V12.UI.IUIProvider provider;
                if (_root.Registry.Get<V12.Core.Interfaces.IUIRenderer>() is V12.Core.UI.Paper.ProwlPaperRenderer paperRenderer)
                {
                    var prowl = new V12.Basic.UI.ProwlUIProvider(new StudioProwlHost(paperRenderer, Session, _root));
                    prowl.Font = paperRenderer.FallbackFont;
                    paperRenderer.UIOverlay = prowl.Emit;
                    provider = prowl;
                }
                else
                {
                    provider = _root.Registry.Get<V12.UI.IUIProvider>() as PaperUIProvider
                               ?? new V12.Basic.UI.PaperUIProvider(_root);
                }

                var rootPanel = provider.CreatePanel("Root");
                if (provider is PaperUIProvider paperProvider) paperProvider.AttachRoot(rootPanel);
                var gameView = provider is V12.Basic.UI.ProwlUIProvider
                    ? provider.CreatePanel("GameView")
                    : ((PaperUIProvider)provider).CreateViewportPanel();
                var shell = new StudioShell(provider, Session, ProjectDir);
                shell.Build(rootPanel, gameView);
                _provider = provider;
                _shell = shell;
                GameRoot.Log?.Information("[V12Studio] Component shell built for '{ProjectDir}'", ProjectDir);
            }
            catch (Exception ex)
            {
                GameRoot.Log?.Error(ex, "[V12Studio] Failed to build the component shell");
            }
        }

        public override void Update(float deltaTime) { }

        private sealed class StudioProwlHost : V12.Basic.UI.IProwlHost
        {
            private readonly V12.Core.UI.Paper.ProwlPaperRenderer _renderer;
            private readonly StudioSession _session;
            private readonly GameRoot _root;

            public StudioProwlHost(V12.Core.UI.Paper.ProwlPaperRenderer renderer, StudioSession session, GameRoot root)
            {
                _renderer = renderer;
                _session = session;
                _root = root;
            }

            public object? GameViewTexture => _session.ViewportElement is { } viewport
                ? _renderer.GetViewportTexture(viewport.Id)
                : null;

            public void SetGameViewRect(global::Prowl.Vector.Rect rect)
            {
                if (_session.ViewportElement is { } viewport)
                    _renderer.SetViewportRectOverride(viewport.Id, rect);
            }

            public V12.UI.IFilePicker? CreateFilePicker() => _root.Registry.Get<V12.UI.IFilePicker>("EditorFilePicker");
            public V12.UI.IModal? CreateModal(string title, string content) => null;
            public V12.UI.IWindow? CreateWindow(string title, V12.UI.WindowOptions options) => null;
        }

        public override void OnDestroy()
        {
            if (_provider is PaperUIProvider paperProvider && _root != null)
            {
                try { _root.PersistentWorld.RemoveElement(paperProvider.RootCanvas); } catch { }
            }
            _shell = null;
            _provider = null;

            if (Session != null && _root != null)
            {
                var registered = _root.Registry.Get("IEditor");
                if (ReferenceEquals(registered?.ServiceInstance, Session))
                    _root.Registry.Unregister("IEditor");
            }
            Session = null;
        }
    }
}






