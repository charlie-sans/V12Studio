using System;
using V12;
using V12.Basic;
using V12.Core;
using V12Studio.Editor;

public class V12StudioGamepack : IV12Gamepack
{
    public string Name => "V12 Studio";

    public void Initialize()
    {
        // The engine's standard service registrations (rendering, scripting,
        // audio…). Safe to double-register — Registry.Register ignores
        // existing names.
        var root = GameRoot.Instance;
        if (root == null) return;
        BasicRegistry.RegisterAll(root);

        // The studio boots from a root component, not from host code: it
        // registers the editor session (IEditor) and builds the renderer-neutral
        // engine-UI shell on the first world tick. Hosts that build their own
        // chrome set StudioEditorBootstrap.HostOwnsUi before creating the runtime.
        var bootstrap = new Element("V12StudioBootstrap");
        bootstrap.AddComponent(new StudioEditorComponent
        {
            ProjectDir = AppContext.BaseDirectory,
        });
        root.PersistentWorld.AddElement(bootstrap);
    }

    public void OnStart()
    {
        // Initialization is handled by V12StudioBootstrap/StudioEditorComponent.
    }
}
