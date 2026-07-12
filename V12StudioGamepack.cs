using V12;
using V12.UI;
using V12.Rendering;
using V12Studio.Editor;

public class V12StudioGamepack : IV12Gamepack
{
    public string Name => "V12 Studio";

    public void Initialize()
    {
        var editor = new Editor();
        Registry.Register<IEditor>(editor);
    }

    public void OnStart()
    {
        var editor = Registry.Resolve<IEditor>();
        var rootWidget = editor?.CreateMainWindow();
        if (rootWidget != null)
            Registry.Register("RootWidget", rootWidget);
    }
}