using V12;
using V12.Basic;
using V12Studio.Editor;

public class V12StudioGamepack : IV12Gamepack
{
    public string Name => "V12 Studio";

    public void Initialize()
    {
        // Parity with StudioLauncher.Monogame (which calls
        // BasicRegistry.RegisterAll(root) before LoadGamepacksFromAssembly):
        // the nova path (V12Runtime + RootLoop auto-discovery) never registers
        // these, so the gamepak registers them itself like SampleGamePack does.
        // Safe to double-register — Registry.Register ignores existing names.
        var root = V12.Core.GameRoot.Instance;
        if (root != null)
            BasicRegistry.RegisterAll(root);
        var editor = new Editor();
        Registry.Register<IEditor>(editor);
    }

    public void OnStart()
    {
        var editor = Registry.Resolve<IEditor>();
        editor?.CreateMainWindow();
    }
}