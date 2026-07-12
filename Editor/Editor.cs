using V12;
using V12.UI;
using V12.Rendering;

namespace V12Studio.Editor
{
    public class Editor : IEditor
    {
        public IWidget CreateMainWindow()
        {
            var ui = Registry.Resolve<IUIProvider>();
            if (ui == null) return null;

            // ── Root layout ──
            var root = ui.CreateVBox();

            // ── Toolbar ──
            var toolbar = ui.CreateToolbar("MainToolbar");
            var playBtn = ui.CreateButton("Play", () => { });
            var stopBtn = ui.CreateButton("Stop", () => { });
            var pauseBtn = ui.CreateButton("Pause", () => { });
            toolbar.AddItem(playBtn);
            toolbar.AddItem(stopBtn);
            toolbar.AddItem(pauseBtn);
            root.AddChild(toolbar);

            // ── Main content (center area) ──
            var mainRow = ui.CreateHBox();

            // Left panel — scene hierarchy placeholder
            var hierarchyPanel = ui.CreatePanel("Hierarchy");
            var hierarchyLabel = ui.CreateLabel("Scene Hierarchy");
            hierarchyPanel.AddChild(hierarchyLabel);

            // Add some test entities to the hierarchy
            var entityList = ui.CreateVBox();
            foreach (var name in new[] { "Player", "Ground", "Light", "Camera" })
            {
                var item = ui.CreateLabel($"  {name}");
                entityList.AddChild(item);
            }
            hierarchyPanel.AddChild(entityList);
            mainRow.AddChild(hierarchyPanel);

            // Center — game viewport
            var viewportPanel = ui.CreatePanel("Game View");
            var rtf = Registry.Resolve<IRenderTargetFactory>();
            if (rtf != null)
            {
                var viewportHost = ui.CreateViewportHost();
                var target = rtf.Create(1280, 720);
                viewportHost.SetRenderTarget(target);
                viewportPanel.AddChild(viewportHost);
            }
            else
            {
                viewportPanel.AddChild(ui.CreateLabel("(No render target factory available)"));
            }
            mainRow.AddChild(viewportPanel);

            // Right panel — inspector placeholder
            var inspectorPanel = ui.CreatePanel("Inspector");
            var inspectorTitle = ui.CreateLabel("Properties");
            inspectorPanel.AddChild(inspectorTitle);

            var propBox = ui.CreateVBox();
            propBox.AddChild(ui.CreateLabel("Position: 0, 0, 0"));
            propBox.AddChild(ui.CreateLabel("Rotation: 0, 0, 0"));
            propBox.AddChild(ui.CreateLabel("Scale: 1, 1, 1"));

            var nameField = ui.CreateTextField("Entity name", (text) => { });
            propBox.AddChild(nameField);

            var addCompBtn = ui.CreateButton("Add Component", () => { });
            propBox.AddChild(addCompBtn);

            inspectorPanel.AddChild(propBox);
            mainRow.AddChild(inspectorPanel);

            root.AddChild(mainRow);

            // ── Bottom status bar ──
            var statusBar = ui.CreateHBox();
            statusBar.AddChild(ui.CreateLabel("V12 Studio — Test Bed"));
            statusBar.AddChild(ui.CreateLabel("FPS: --"));
            root.AddChild(statusBar);

            return root;
        }
    }
}
