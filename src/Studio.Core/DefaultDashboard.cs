using V12.Core;
using V12.Core.UI;
using System;
using V12.Components;
using V12.Components.UI;
using V12.Core.Core.Interfaces;
using V12.Core.Input;

namespace V12Studio.Core
{
    /// <summary>
    /// Simple default dashboard implementation that builds a small tabbed
    /// UI using an `IUIBuilder`. Intended as a fallback if the host does not
    /// provide a front-end dashboard implementation.
    /// </summary>
    public class DefaultDashboard : IDashboard
    {
        private IUIBuilder _builder;
        private GameRoot _gameRoot;
        private bool _isOpen;
        private string _activeTab = "World";
        private V12.Core.Core.Interfaces.IWorldElement? _mainContainer;
        private V12.Core.Core.Interfaces.IWorldElement? _contentPanel;
        private V12.Core.Core.Interfaces.IWorldElement? _tabRow;
        
        // Pre-built content panels for each tab so we can toggle visibility
        private V12.Core.Core.Interfaces.IWorldElement? _worldPanel;
        private V12.Core.Core.Interfaces.IWorldElement? _playerPanel;
        private V12.Core.Core.Interfaces.IWorldElement? _systemPanel;

        private string _lastEvent = "None";
        
        // Diagnostic fields
        private V12.Core.Core.Interfaces.IWorldElement? _fpsLabelEl;
        private V12.Core.Core.Interfaces.IWorldElement? _objCountLabelEl;
        private V12.Core.Core.Interfaces.IWorldElement? _inputLabelEl;

        public void Initialize(IUIBuilder builder, GameRoot gameRoot)
        {
            _builder = builder ?? throw new ArgumentNullException(nameof(builder));
            _gameRoot = gameRoot ?? throw new ArgumentNullException(nameof(gameRoot));
            
            var inputService = _gameRoot.Registry.Get<IInputService>();
            if (inputService != null)
            {
                inputService.RegisterHandler(new DashboardInputHandler(this));
            }
            BuildUI();
        }

        public void Initialize(IUIBuilder builder) => Initialize(builder, new GameRoot()); // Legacy support
        
        public void UpdateUI(InputEvent e)
        {
            _lastEvent = $"{e.Name}: {e.Type} ({e.Value:F2})";
        }

        private class DashboardInputHandler : IInputHandler
        {
            private readonly DefaultDashboard _dashboard;
            public DashboardInputHandler(DefaultDashboard dashboard) => _dashboard = dashboard;
            public void OnInputEvent(InputEvent evt) => _dashboard.UpdateUI(evt);
        }

        void BuildUI()
        {
            try
            {
                var root = _builder.Root;

                // Main vertical container: content area on top, tabs at the bottom
                _mainContainer = _builder.VLayout(root, "DashboardMain", spacing: 8f, padding: 6f);

                // Content panel — use a rect as a simple background container
                _contentPanel = _builder.Rect(_mainContainer, "ContentPanel", width: 0f, height: 0f, backgroundColor: "#101010");
                _contentPanel.AddComponent(new LayoutElementComponent { FlexibleWidth = 1f, FlexibleHeight = 1f });
                // Tab row at the bottom — place buttons left-to-right
                _tabRow = _builder.HLayout(_mainContainer, "TabRow", spacing: 8f);
                _builder.Button(_tabRow, "World",  () => SwitchToTab("World"));
                _builder.Button(_tabRow, "Player", () => SwitchToTab("Player"));
                _builder.Button(_tabRow, "System", () => SwitchToTab("System"));

                // Build content for each tab once, then show only the active one.
                // World tab content
                _worldPanel = _builder.VLayout(_contentPanel, "WorldContent", spacing: 4f, padding: 4f);
                _builder.Label(_worldPanel, "WorldTitle", "World Switcher");
                foreach (var world in _gameRoot.Worlds)
                {
                    _builder.Button(_worldPanel, $"Switch to {world.WorldName}", () => _gameRoot.SelectWorld(world));
                }

                // Player tab content
                _playerPanel = _builder.VLayout(_contentPanel, "PlayerContent", spacing: 4f, padding: 4f);
                _builder.Label(_playerPanel, "PlayerTitle", "Player");
                _builder.Label(_playerPanel, "PlayerInfo", "Player settings and controls.");
                _builder.Slider(_playerPanel, "PlayerSpeed", 0f, 10f, 4f, v => { /* no-op sample */ });

                // System tab content
                _systemPanel = _builder.VLayout(_contentPanel, "SystemContent", spacing: 4f, padding: 4f);
                _builder.Label(_systemPanel, "SystemTitle", "System Diagnostics");
                
                _fpsLabelEl = _builder.Label(_systemPanel, "FPS", "FPS: 0");
                _fpsLabelEl.AddComponent(new UILabelComponent { Text = "FPS: 0" });
                
                _objCountLabelEl = _builder.Label(_systemPanel, "ObjCount", "Objects: 0");
                _objCountLabelEl.AddComponent(new UILabelComponent { Text = "Objects: 0" });
                
                _inputLabelEl = _builder.Label(_systemPanel, "InputEvents", "Last Input: None");
                _inputLabelEl.AddComponent(new UILabelComponent { Text = "Last Input: None" });

                // Detach all panels except the active one so we only show the active content.
                if (_contentPanel != null)
                {
                    try { if (_activeTab != "World") _contentPanel.RemoveChild(_worldPanel); } catch { }
                    try { if (_activeTab != "Player") _contentPanel.RemoveChild(_playerPanel); } catch { }
                    try { if (_activeTab != "System") _contentPanel.RemoveChild(_systemPanel); } catch { }
                }
            }
            catch { }
        }

        void SwitchToTab(string tab)
        {
            _activeTab = tab;

            try
            {
                if (_contentPanel == null) return;

                // Detach all known panels first
                try { if (_worldPanel != null) _contentPanel.RemoveChild(_worldPanel); } catch { }
                try { if (_playerPanel != null) _contentPanel.RemoveChild(_playerPanel); } catch { }
                try { if (_systemPanel != null) _contentPanel.RemoveChild(_systemPanel); } catch { }

                // Attach only the active panel
                switch (_activeTab)
                {
                    case "World":
                        if (_worldPanel != null) _contentPanel.AddChild(_worldPanel);
                        break;
                    case "Player":
                        if (_playerPanel != null) _contentPanel.AddChild(_playerPanel);
                        break;
                    case "System":
                        if (_systemPanel != null) _contentPanel.AddChild(_systemPanel);
                        break;
                    default:
                        // nothing
                        break;
                }
            }
            catch { }
        }

        public void Open()
        {
            _isOpen = true;
        }

        public void Close()
        {
            _isOpen = false;
        }

        public void Update(double deltaSeconds)
        {
            if (!_isOpen) return;
            //Console.WriteLine(IsOpen);
            if (_fpsLabelEl != null)
                _fpsLabelEl.GetComponent<UILabelComponent>().Text = $"FPS: {1.0 / deltaSeconds:F1}";

            if (_gameRoot.SelectedWorld != null && _objCountLabelEl != null)
                _objCountLabelEl.GetComponent<UILabelComponent>().Text = $"Objects: {_gameRoot.SelectedWorld.Root.Count}";

            if (_inputLabelEl != null)
                _inputLabelEl.GetComponent<UILabelComponent>().Text = $"Last Input: {_lastEvent}";
        }

        public bool IsOpen => _isOpen;
    }
}
