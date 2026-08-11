namespace V12Studio.Editor
{
    /// <summary>
    /// Abstracts an editor that can build its main window using platform-agnostic
    /// UI and rendering services resolved from the registry.
    /// </summary>
    public interface IEditor
    {
        /// <summary>Build the editor UI. Elements are added to the IUIBuilder's root
        /// and rendered by PaperUIBuilder each frame.</summary>
        void CreateMainWindow();
    }
}
