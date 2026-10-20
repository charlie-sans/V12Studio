using System.Collections.Generic;
using System.Numerics;

namespace V12.Core.Rendering
{
    /// <summary>
    /// Per-frame viewport snapshots provided by the renderer. Backends (MonoGame,
    /// Godot, etc.) implement this to expose their viewport(s) with camera matrices
    /// and bounds. A game or editor can then query viewports, cast rays, and
    /// project points without knowing which renderer is running.
    /// </summary>
    public interface IViewportRegistry
    {
        /// <summary>
        /// Immutable snapshot of all active viewports this frame, indexed by
        /// backend-assigned id. Typically one viewport in a game, many in an
        /// editor with split panes.
        /// </summary>
        IReadOnlyList<ViewportInfo> Viewports { get; }
    }

    /// <summary>
    /// One viewport: position, size, and camera matrices.
    /// </summary>
    public readonly record struct ViewportInfo(
        long Id,
        float X,
        float Y,
        float Width,
        float Height,
        bool HasCamera,
        Matrix4x4 View,
        Matrix4x4 Projection);
}
