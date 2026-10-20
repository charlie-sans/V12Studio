using System;
using System.Collections.Generic;
using Godot;
using V12.Core.Core.Interfaces;
using V12.Core.Interfaces.Renderer;
using V12.Core.Rendering;
using V12.Core.UI;
using V12Studio.Core;
using V12TwoDog.Godot;
using NVec3 = System.Numerics.Vector3;

namespace V12Studio.Godot
{
    /// <summary>
    /// Debug aid (--probe-gizmo-scale): answers "why doesn't the gizmo hold
    /// constant screen size?" with runtime numbers instead of code reading.
    ///
    /// Stage A (frame 95): select a subject if none, then log — camera pose,
    /// the viewport info the interaction service consumes (count / hasCamera),
    /// the computed gizmo size (ViewportMath.TryGetScreenSize, the exact call
    /// the service makes) plus an independent Godot-camera reference
    /// (ScreenSizeFromFov), and the ACTUAL rendered gizmo: every _EditorGizmo
    /// root (two roots = the Studio service and nova's SceneGizmo each spawned
    /// one) with its part world positions and spread.
    ///
    /// Stage B (frame 100): teleport the editor camera straight back along its
    /// own view axis and re-seed the fly pose via SetEditorCamera, so the
    /// subject stays framed at ~8x the distance.
    ///
    /// Stage C (frame 130): log again. Constant screen size means the computed
    /// world size AND the rendered spread both grow ~8x. A frozen size pins the
    /// bug on GizmoOriginAndSize/Sync; a grown size with frozen spread pins it
    /// on the mesh sync/render path.
    /// </summary>
    internal static class GizmoScaleProbe
    {
        public static void Tick(int frame, StudioSession session, V12TwoDog.Renderer renderer,
            GodotViewportAdapter adapter, long viewportElementId)
        {
            if (frame == 95)
            {
                SelectSubject(session);
                Dump("A", session, renderer, adapter, viewportElementId);
            }
            else if (frame == 100)
            {
                TeleportBack(session);
            }
            else if (frame == 130)
            {
                Dump("B", session, renderer, adapter, viewportElementId);
            }
            else if (frame % 10 == 0)
            {
                // Continuous trace: is the computed size actually tracking the
                // camera every frame, or frozen on a stale matrix?
                Trace(frame, session, adapter, viewportElementId);
            }
        }

        private static void Trace(int frame, StudioSession session,
            GodotViewportAdapter adapter, long viewportElementId)
        {
            var sel = session.Selected;
            if (sel == null) return;
            ViewportInfo vp = default;
            bool found = false;
            foreach (var v in adapter.Viewports)
                if (v.Id == viewportElementId) { vp = v; found = true; break; }
            if (!found) return;
            var sw = sel.WorldTransform;
            var subject = new NVec3(sw.M41, sw.M42, sw.M43);
            bool ok = ViewportMath.TryGetScreenSize(vp, subject, TransformGizmo.ScreenFraction, out float size);
            GD.Print($"[GizmoProbe] f{frame}: hasCam={vp.HasCamera} ok={ok} size={size:F4} " +
                     $"viewT=({vp.View.M41:F2},{vp.View.M42:F2},{vp.View.M43:F2})");
        }

        private static void SelectSubject(StudioSession session)
        {
            if (session.Selected != null) return;
            var world = session.SceneWorld;
            if (world == null) return;
            foreach (var el in world.Root)
            {
                if (el.Name == "EditorViewport" || el.Name == "ViewportCamera") continue;
                session.Select(el);
                GD.Print($"[GizmoProbe] selected subject '{el.Name}' (id {el.Id})");
                return;
            }
            GD.PrintErr("[GizmoProbe] no selectable subject found.");
        }

        private static void TeleportBack(StudioSession session)
        {
            var camEl = session.FindSceneElement("ViewportCamera");
            var sel = session.Selected;
            if (camEl == null)
            {
                GD.PrintErr("[GizmoProbe] no ViewportCamera element to move.");
                return;
            }

            var w = camEl.WorldTransform;
            var pos = new NVec3(w.M41, w.M42, w.M43);
            var fwd = new NVec3(-w.M31, -w.M32, -w.M33);
            if (fwd.LengthSquared() < 1e-8f) fwd = new NVec3(0, 0, -1);
            fwd = NVec3.Normalize(fwd);

            // Aim point: the selection (what the gizmo pivots on), else a point
            // ahead of the camera; move straight back so the subject stays framed.
            NVec3 aim = pos + fwd * 8f;
            if (sel != null)
            {
                var sw = sel.WorldTransform;
                aim = new NVec3(sw.M41, sw.M42, sw.M43);
            }
            float dist = NVec3.Distance(pos, aim);
            float newDist = MathF.Max(40f, dist * 8f);
            var newPos = aim - fwd * newDist;

            var lt = camEl.LocalTransform;
            camEl.LocalTransform = new TRS
            {
                Position = new NVec3(newPos.X, newPos.Y, newPos.Z),
                Rotation = lt.Rotation,
                Scale = lt.Scale,
            };
            // Re-seed the fly/orbit pose from the new element pose, otherwise the
            // service's internal _pos overwrites the teleport next tick.
            session.Interaction?.SetEditorCamera(camEl);
            GD.Print($"[GizmoProbe] teleported camera: dist {dist:F2} -> {newDist:F2} " +
                     $"pos=({newPos.X:F1},{newPos.Y:F1},{newPos.Z:F1})");
        }

        private static void Dump(string tag, StudioSession session, V12TwoDog.Renderer renderer,
            GodotViewportAdapter adapter, long viewportElementId)
        {
            // ── Subject + camera pose ──
            var sel = session.Selected;
            NVec3 subject = default;
            string selName = "(none)";
            if (sel != null)
            {
                var sw = sel.WorldTransform;
                subject = new NVec3(sw.M41, sw.M42, sw.M43);
                selName = sel.Name ?? "(unnamed)";
            }

            // ── Viewport info exactly as the interaction service reads it ──
            ViewportInfo vp = default;
            bool foundVp = false;
            foreach (var v in adapter.Viewports)
            {
                if (v.Id == viewportElementId) { vp = v; foundVp = true; break; }
            }
            if (!foundVp && adapter.Viewports.Count > 0)
            {
                vp = adapter.Viewports[0];
                foundVp = true;
            }

            // ── What actually renders: SubViewport + its current camera ──
            SubViewport? sub = null;
            foreach (var (id, s) in renderer.SceneViewports)
            {
                if (id == viewportElementId && GodotObject.IsInstanceValid(s)) { sub = s; break; }
            }
            Camera3D? cam = sub != null ? sub.GetCamera3D() : null;

            float camDist = 0f;
            NVec3 camPos = default;
            if (cam != null)
            {
                camPos = new NVec3(cam.GlobalPosition.X, cam.GlobalPosition.Y, cam.GlobalPosition.Z);
                camDist = NVec3.Distance(camPos, subject);
            }

            GD.Print($"[GizmoProbe] {tag}: subject='{selName}' dist={camDist:F2} " +
                     $"camPos=({camPos.X:F1},{camPos.Y:F1},{camPos.Z:F1}) " +
                     $"vpCount={adapter.Viewports.Count} foundVp={foundVp} " +
                     $"hasCam={(foundVp ? vp.HasCamera.ToString() : "-")} " +
                     $"camNode={(cam == null ? "null" : cam.Name)}");

            // ── The captured ViewportInfo matrices vs the live camera node ──
            // Catches which piece lies when the computed size mismatches the
            // reference: stale view (wrong translation/rows), stale projection
            // (wrong M22), or a different camera captured than the one live.
            if (foundVp)
            {
                GD.Print($"[GizmoProbe] {tag}: vp.View  t=({vp.View.M41:F2},{vp.View.M42:F2},{vp.View.M43:F2}) " +
                         $"zrow=({vp.View.M31:F3},{vp.View.M32:F3},{vp.View.M33:F3}) " +
                         $"xrow=({vp.View.M11:F3},{vp.View.M12:F3},{vp.View.M13:F3})");
                GD.Print($"[GizmoProbe] {tag}: vp.Proj  M22={vp.Projection.M22:F4} M34={vp.Projection.M34:F4} " +
                         $"M44={vp.Projection.M44:F4} M33={vp.Projection.M33:F4}");
            }
            if (cam != null)
            {
                var ct = cam.GlobalTransform;
                GD.Print($"[GizmoProbe] {tag}: camNode id={cam.GetInstanceId()} " +
                         $"t=({ct.Origin.X:F2},{ct.Origin.Y:F2},{ct.Origin.Z:F2}) " +
                         $"zrow=({ct.Basis.Z.X:F3},{ct.Basis.Z.Y:F3},{ct.Basis.Z.Z:F3}) " +
                         $"fov={cam.Fov:F2} proj={cam.Projection} size={cam.Size:F2} " +
                         $"current={cam.Current}");
            }

            // ── The captured ViewportInfo matrices vs the live camera node ──
            // Authoritative cross-check: do our OWN matrices agree with Godot's
            // camera? If TryProjectToScreen(vp, ...) and Godot's UnprojectPosition
            // disagree for the same world point, the View or Projection we built
            // is inverted/transposed and every downstream size is wrong.
            if (foundVp && vp.HasCamera && cam != null && sel != null)
            {
                var worldPt = new global::Godot.Vector3(subject.X, subject.Y, subject.Z);
                var gPix = cam.UnprojectPosition(worldPt);
                bool okOurs = ViewportMath.TryProjectToScreen(vp, subject, out var oPix);
                GD.Print($"[GizmoProbe] {tag}: project ours={(okOurs ? $"({oPix.X:F1},{oPix.Y:F1})" : "FAIL")} " +
                         $"godot=({gPix.X:F1},{gPix.Y:F1}) " +
                         $"delta={(okOurs ? $"({oPix.X - gPix.X:F1},{oPix.Y - gPix.Y:F1})" : "-")} " +
                         $"vpRect=({vp.X:F1},{vp.Y:F1} {vp.Width:F1}x{vp.Height:F1}) " +
                         $"subSize={(sub != null ? sub.Size.ToString() : "-")}");
            }

            // ── Computed gizmo size: the service's own math + a reference ──
            float sizeOurs = float.NaN, sizeFov = float.NaN;
            if (foundVp && vp.HasCamera && sel != null
                && ViewportMath.TryGetScreenSize(vp, subject, TransformGizmo.ScreenFraction, out float s1))
            {
                sizeOurs = s1;
            }
            if (cam != null && sel != null)
                sizeFov = TransformGizmo.ScreenSizeFromFov(camDist, cam.Fov);

            // Independently recompute the service's formula from the raw
            // matrices so we can see which term disagrees.
            if (foundVp && sel != null)
            {
                var vpPos = System.Numerics.Vector4.Transform(
                    new System.Numerics.Vector4(subject, 1f), vp.View);
                float m22 = MathF.Abs(vp.Projection.M22);
                float depth = -vpPos.Z;
                float recomputed = TransformGizmo.ScreenFraction * depth / m22;
                GD.Print($"[GizmoProbe] {tag}: recompute viewPos=({vpPos.X:F3},{vpPos.Y:F3},{vpPos.Z:F3}) " +
                         $"depth={depth:F4} m22={m22:F4} size={recomputed:F4}");
            }

            GD.Print($"[GizmoProbe] {tag}: sizeOurs={sizeOurs:F4} sizeFromFov={sizeFov:F4} " +
                     $"screenFraction={TransformGizmo.ScreenFraction:F2}");

            // ── Actual rendered gizmo tree(s): roots, parts, spread ──
            var world = session.SceneWorld;
            if (world == null) { GD.Print($"[GizmoProbe] {tag}: no scene world"); return; }

            int roots = 0;
            foreach (var r in world.Root)
                WalkGizmos(r, tag, renderer, ref roots);
            if (roots == 0)
                GD.Print($"[GizmoProbe] {tag}: gizmoRoots=0 (gizmo tree absent!)");
        }

        private static void WalkGizmos(IWorldElement el, string tag,
            V12TwoDog.Renderer renderer, ref int roots)
        {
            if (el.Name == "_EditorGizmo")
            {
                roots++;
                var pts = new List<NVec3>();
                foreach (var part in el.Children)
                {
                    var m = part.WorldTransform;
                    pts.Add(new NVec3(m.M41, m.M42, m.M43));
                }
                GD.Print($"[GizmoProbe] {tag}: gizmoRoots={roots} (so far) parts={pts.Count}");

                if (pts.Count > 0)
                {
                    var min = pts[0]; var max = pts[0];
                    float maxR = 0f;
                    var ctr = new NVec3(0, 0, 0);
                    foreach (var p in pts)
                    {
                        min = NVec3.Min(min, p);
                        max = NVec3.Max(max, p);
                        ctr += p;
                    }
                    ctr /= pts.Count;
                    foreach (var p in pts)
                        maxR = MathF.Max(maxR, NVec3.Distance(p, ctr));
                    var spread = max - min;
                    GD.Print($"[GizmoProbe] {tag}: partSpread=({spread.X:F3},{spread.Y:F3},{spread.Z:F3}) " +
                             $"maxRadius={maxR:F3} firstPart=({pts[0].X:F2},{pts[0].Y:F2},{pts[0].Z:F2})");
                }

                // Renderer truth: does the ROOT node carry the uniform scale,
                // and does a PART node stay at unit size under it? This is what
                // actually decides whether the gizmo renders at constant screen
                // size (element math alone doesn't prove the node scale landed).
                var rootNode = renderer.GetNode(el.Id) as Node3D;
                GD.Print($"[GizmoProbe] {tag}: rootNode={(rootNode == null ? "null" : "ok")} " +
                         $"origin=({rootNode?.GlobalTransform.Origin.X:F2},{rootNode?.GlobalTransform.Origin.Y:F2},{rootNode?.GlobalTransform.Origin.Z:F2}) " +
                         $"scale=({rootNode?.GlobalTransform.Basis.X.Length():F3},{rootNode?.GlobalTransform.Basis.Y.Length():F3},{rootNode?.GlobalTransform.Basis.Z.Length():F3})");
                foreach (var part in el.Children)
                {
                    var pNode = renderer.GetNode(part.Id) as Node3D;
                    if (pNode == null) continue;
                    GD.Print($"[GizmoProbe] {tag}: part '{part.Name}' " +
                             $"localScale=({pNode.Transform.Basis.X.Length():F3},{pNode.Transform.Basis.Y.Length():F3},{pNode.Transform.Basis.Z.Length():F3}) " +
                             $"globalScale=({pNode.GlobalTransform.Basis.X.Length():F3},{pNode.GlobalTransform.Basis.Y.Length():F3},{pNode.GlobalTransform.Basis.Z.Length():F3})");
                    break; // one representative part is enough
                }
                return; // gizmo parts never nest further gizmos
            }
            foreach (var c in el.Children)
                WalkGizmos(c, tag, renderer, ref roots);
        }
    }
}
