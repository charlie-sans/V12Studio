using System.Numerics;

namespace V12.Core.Rendering
{
    /// <summary>
    /// Renderer-agnostic viewport math: coordinate transformations, ray casting,
    /// and screen-space projection. Used by games and editors alike — no editor
    /// knowledge baked in.
    /// </summary>
    public static class ViewportMath
    {
        /// <summary>
        /// Cast a ray through a screen-space point in a viewport.
        /// Returns origin at near plane and unit direction towards far plane.
        /// </summary>
        public static bool TryUnprojectRay(
            ViewportInfo viewport,
            Vector2 screenPos,
            out Vector3 origin,
            out Vector3 direction)
        {
            origin = default;
            direction = default;

            // Validate viewport rect
            if (viewport.Width <= 0 || viewport.Height <= 0)
                return false;

            // Invert the view-projection matrix
            var viewProj = viewport.View * viewport.Projection;
            if (!Matrix4x4.Invert(viewProj, out var inv))
                return false;

            // Convert screen coordinates to viewport-local
            var local = new Vector2(screenPos.X - viewport.X, screenPos.Y - viewport.Y);

            // Unproject at near (z=0) and far (z=1)
            var near = UnprojectNdc(local, 0f, viewport.Width, viewport.Height, inv);
            var far = UnprojectNdc(local, 1f, viewport.Width, viewport.Height, inv);

            var delta = far - near;
            if (delta.LengthSquared() < 1e-12f)
                return false;

            origin = near;
            direction = Vector3.Normalize(delta);
            return true;
        }

        /// <summary>
        /// Project a world-space point to screen-space viewport coordinates.
        /// Returns false if the point is behind the camera.
        /// </summary>
        public static bool TryProjectToScreen(
            ViewportInfo viewport,
            Vector3 worldPosition,
            out Vector2 screenPosition)
        {
            screenPosition = default;

            // Transform to clip space
            var clip = Vector4.Transform(new Vector4(worldPosition, 1f), viewport.View * viewport.Projection);

            // Behind camera or on the plane
            if (System.MathF.Abs(clip.W) < 1e-8f)
                return false;

            // Normalize to NDC
            var ndcX = clip.X / clip.W;
            var ndcY = clip.Y / clip.W;

            // Map to screen space (viewport-relative, matching input coordinates)
            screenPosition = new Vector2(
                viewport.X + (ndcX + 1f) * 0.5f * viewport.Width,
                viewport.Y + (1f - ndcY) * 0.5f * viewport.Height);

            return true;
        }

        /// <summary>
        /// Compute the screen-space size in world units for a constant-screen-size
        /// object at a world position. Used for gizmo sizing and UI scaling.
        /// </summary>
        public static bool TryGetScreenSize(
            ViewportInfo viewport,
            Vector3 worldPosition,
            float screenFraction,
            out float worldSize)
        {
            worldSize = 0f;

            // Transform to view space
            var viewPos = Vector4.Transform(new Vector4(worldPosition, 1f), viewport.View);

            // Get projection scale (from M22, the vertical FOV term)
            float projectionScale = System.MathF.Abs(viewport.Projection.M22);
            if (!float.IsFinite(projectionScale) || projectionScale < 1e-6f)
                return false;

            // Orthographic projection (M34 ≈ 0)
            if (System.MathF.Abs(viewport.Projection.M34) < 1e-6f &&
                System.MathF.Abs(viewport.Projection.M44) > 1e-6f)
            {
                worldSize = screenFraction / projectionScale;
            }
            else
            {
                // Perspective projection: size scales with depth
                float depth = -viewPos.Z;
                if (!float.IsFinite(depth) || depth <= 1e-4f)
                    return false;

                worldSize = screenFraction * depth / projectionScale;
            }

            return float.IsFinite(worldSize) && worldSize > 0f;
        }

        /// <summary>
        /// Find the viewport at a screen-space point (smallest wins when nested).
        /// </summary>
        public static ViewportInfo? FindViewportAt(
            IReadOnlyList<ViewportInfo> viewports,
            Vector2 screenPos)
        {
            ViewportInfo? best = null;
            float bestArea = float.MaxValue;

            foreach (var vp in viewports)
            {
                // Hit test
                if (screenPos.X < vp.X || screenPos.Y < vp.Y ||
                    screenPos.X >= vp.X + vp.Width ||
                    screenPos.Y >= vp.Y + vp.Height)
                    continue;

                // Smallest viewport wins
                var area = vp.Width * vp.Height;
                if (area < bestArea)
                {
                    bestArea = area;
                    best = vp;
                }
            }

            return best;
        }

        // ── Helpers ──────────────────────────────────────────────────────

        /// <summary>
        /// Unproject a screen-local point at a given NDC depth.
        /// </summary>
        private static Vector3 UnprojectNdc(
            Vector2 localPixel,
            float ndcZ,
            float viewportWidth,
            float viewportHeight,
            Matrix4x4 invViewProj)
        {
            // Pixel → NDC
            float ndcX = (localPixel.X / viewportWidth) * 2f - 1f;
            float ndcY = 1f - (localPixel.Y / viewportHeight) * 2f;

            // Unproject
            var world = Vector4.Transform(new Vector4(ndcX, ndcY, ndcZ, 1f), invViewProj);
            var w = System.MathF.Abs(world.W) < 1e-12f ? 1f : world.W;
            return new Vector3(world.X / w, world.Y / w, world.Z / w);
        }
    }
}
