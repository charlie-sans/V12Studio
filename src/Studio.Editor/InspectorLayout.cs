using V12.Core.UI;
using V12.UI;

namespace V12Studio.Editor
{
    /// <summary>
    /// Shared label column for the Inspector panel. Every label+field row
    /// (main inspector rows, component BuildInspector rows, the New Component
    /// form) sizes its label to the same minimum width, so all fields start
    /// on one x instead of stepping with each label's text width. Section
    /// headers, notes and read-only combined labels are not row labels and
    /// keep their natural width.
    /// </summary>
    internal static class InspectorLayout
    {
        /// <summary>
        /// Width of the label column in every label+field row. Just above the
        /// widest catalogue label ("MeshTargetName", 113px at the default
        /// theme font); longer labels (script @field rows) exceed it and keep
        /// their natural width rather than clipping.
        /// </summary>
        public const float LabelWidth = 130f;

        /// <summary>A row label sized to the shared column.</summary>
        public static ILabel RowLabel(IUIProvider ui, string text)
        {
            var label = ui.CreateLabel(text);
            label.MinWidth = LabelWidth;
            return label;
        }
    }
}
