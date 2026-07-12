using V12;
using V12.UI;
using V12.Rendering;

namespace V12Studio.Editor
{
    public class Editor : IEditor
    {
        public void CreateMainWindow()
        {
            var ui = Registry.Resolve<IUIProvider>();
            var rtf = Registry.Resolve<IRenderTargetFactory>();

            // Create a dock for the game view
            var gameViewDock = ui.CreateDock("Game View", DockPosition.Center);

            // Create a viewport host
            var viewportHost = ui.CreateViewportHost();

            // Create a render target (this will be a SubViewport via Godot)
            var target = rtf.Create(1920, 1080);

            // Attach the render target to the host
            viewportHost.SetRenderTarget(target);

            // Add the viewport host to the dock
            gameViewDock.AddChild(viewportHost);

            // Get the main window and add the dock
            var window = ui.RootWindow;
            // In a real implementation you'd manage layout, but for now:
            // window.AddChild(gameViewDock); // depends on how IWindow works
        }
    }
}