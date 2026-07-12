using V12.UI;

namespace V12Studio.Editor
{
    /// <summary>
    /// Abstracts an editor that can build its main window using platform-agnostic
    /// UI and rendering services resolved from the registry.
    /// </summary>
    public interface IEditor
    {
        IWidget CreateMainWindow();
    }
}
