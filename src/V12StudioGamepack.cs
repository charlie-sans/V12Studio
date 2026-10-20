using V12;
using V12.Basic;
using V12Studio.Core;

public class V12StudioGamepack : IV12Gamepack
{
    public string Name => "V12 Studio";

    public void Initialize()
    {
        // The engine's standard service registrations (rendering, scripting,
        // audio…). Safe to double-register — Registry.Register ignores
        // existing names.
        var root = V12.Core.GameRoot.Instance;
        if (root == null) return;
        BasicRegistry.RegisterAll(root);

        // Host-agnostic editor session. The pak builds no UI; hosts resolve
        // IEditor from the registry and compose the editor against it.
        var session = new StudioSession(root);
        Registry.Register<IEditor>(session);
    }

    public void OnStart()
    {
        // Scene world + demo content is created by the host after it has
        // registered the viewport interaction service, so the orbit camera
        // and gizmo are live from the first visible frame.
    }
}
