using System.Numerics;
using V12.Core.Core.Interfaces;

namespace V12.Core.Rendering
{
    /// <summary>
    /// Optional fast path for setting transforms without going through the
    /// component system snapshot. Used by editor gizmos and first-person cameras
    /// to avoid lag during interactive drags/orbits.
    ///
    /// Implementations apply transforms directly to world state and optionally
    /// skip the next snapshot interpolation for smooth interaction.
    /// </summary>
    public interface IDirectTransformProvider
    {
        /// <summary>
        /// Queue a local-space transform to be applied directly (this frame or
        /// next, depending on implementation). Used during interactive gestures
        /// where latency matters.
        /// </summary>
        void SetElementTransformDirect(IWorldElement element, Matrix4x4 localTransform);

        /// <summary>
        /// Release direct control of an element (resume normal snapshot-driven
        /// updates). Called when a gesture (gizmo drag, orbit) ends.
        /// </summary>
        void ReleaseElementTransformDirect(IWorldElement element);
    }
}
