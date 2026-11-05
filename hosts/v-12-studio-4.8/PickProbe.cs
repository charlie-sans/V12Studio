using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using V12.Core.Core.Interfaces;
using V12.Core.Interfaces.Renderer;
using V12.Core.Rendering;
using V12.Core.UI;
using V12Studio.Core;
using V12TwoDog;
using V12TwoDog.Godot;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace V12Studio.Godot
{
    /// <summary>
    /// Debug aid (--probe-pick): headless diagnosis of the picking chain at
    /// frame 120. Two INDEPENDENT legs pin down where world picking corrupts:
    ///
    ///  A. Ray truth: our ViewportInfo unprojection vs the Godot camera's own
    ///     ProjectRayOrigin/ProjectRayNormal at the same viewport pixel.
    ///     A mismatch means the View/Projection conversion or the viewport
    ///     rect/mouse space is wrong (this would also explain gizmo grabs
    ///     falling through: the analytic handle test uses the same ray).
    ///
    ///  B. Geometry truth: a ray through each element's OWN projected centre
    ///     (project + unproject round-trips through the same matrices, so it
    ///     passes through the element's world centre regardless of leg A)
    ///     must hit that element via SceneQuery. A miss means the geometry
    ///     SceneQuery tests (MeshPoints + element.WorldTransform) is not where
    ///     the renderer draws it.
    ///
    /// Plus: mouse-space offsets (canvas vs window vs display), SubViewport
    /// size vs container rect, node GlobalTransform vs element WorldTransform,
    /// and the analytic gizmo handle test under both rays.
    /// </summary>
    internal static class PickProbe
    {
        public static void Run(V12Runtime runtime, GodotViewportAdapter adapter,
            V12TwoDog.Renderer renderer, StudioSession session, long viewportElementId)
        {
            try
            {
                Probe(runtime, adapter, renderer, session, viewportElementId);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[Probe] EXCEPTION {ex}");
            }
        }

        private static void Probe(V12Runtime runtime, GodotViewportAdapter adapter,
            V12TwoDog.Renderer renderer, StudioSession session, long viewportElementId)
        {
            GD.Print("[Probe] ==== pick probe start ====");

            // ── Viewport info (what the interaction service consumes) ──
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
            GD.Print($"[Probe] vp found={foundVp} id={vp.Id} rect=({vp.X:F1},{vp.Y:F1} {vp.Width:F1}x{vp.Height:F1}) " +
                     $"hasCam={vp.HasCamera}");

            // ── SubViewport + container + camera (what actually renders) ──
            SubViewport? sub = null;
            foreach (var (id, s) in renderer.SceneViewports)
            {
                if (id == viewportElementId && GodotObject.IsInstanceValid(s)) { sub = s; break; }
            }
            if (sub == null)
            {
                foreach (var (id, s) in renderer.SceneViewports)
                {
                    if (GodotObject.IsInstanceValid(s)) { sub = s; break; }
                }
            }
            SubViewportContainer? container = null;
            if (sub != null)
            {
                for (var n = sub.GetParent(); n != null; n = n.GetParent())
                {
                    if (n is SubViewportContainer c) { container = c; break; }
                }
            }
            Camera3D? cam = sub != null ? sub.GetCamera3D() : null;
            GD.Print($"[Probe] sub={(sub == null ? "null" : sub.Size.ToString())} " +
                     $"container={(container == null ? "null" : container.GetGlobalRect().ToString())} " +
                     $"cam={(cam == null ? "null" : cam.Name.ToString())} current={(cam != null ? cam.Current.ToString() : "-")} " +
                     $"fov={(cam != null ? cam.Fov.ToString("F1") : "-")}");

            // ── Mouse space: canvas (adapter) vs window vs display ──
            if (container != null)
            {
                var canvas = container.GetGlobalMousePosition();
                var win = container.GetWindow();
                if (win != null)
                {
                    var winMouse = win.GetMousePosition();
                    var disp = DisplayServer.MouseGetPosition();
                    GD.Print($"[Probe] mouse canvas=({canvas.X:F1},{canvas.Y:F1}) " +
                             $"window=({winMouse.X:F1},{winMouse.Y:F1}) display=({disp.X},{disp.Y}) " +
                             $"winPos={win.Position} " +
                             $"canvasMinusWindow=({canvas.X - winMouse.X:F1},{canvas.Y - winMouse.Y:F1})");
                }
            }

            // ── Leg A: our ray vs Godot's own ray at three viewport pixels ──
            if (foundVp && vp.HasCamera && cam != null && vp.Width > 1 && vp.Height > 1)
            {
                var pixels = new (float lx, float ly)[]
                {
                    (vp.Width * 0.5f, vp.Height * 0.5f),
                    (vp.Width * 0.25f, vp.Height / 3f),
                    (vp.Width * 0.75f, vp.Height * 0.666f),
                };
                foreach (var (lx, ly) in pixels)
                {
                    var pix = new NVec2(vp.X + lx, vp.Y + ly);
                    bool okOurs = ViewportMath.TryUnprojectRay(vp, pix, out var oo, out var od);
                    var gO = cam.ProjectRayOrigin(new Vector2(lx, ly));
                    var gD = cam.ProjectRayNormal(new Vector2(lx, ly));
                    var gon = new NVec3(gO.X, gO.Y, gO.Z);
                    var gdn = NVec3.Normalize(new NVec3(gD.X, gD.Y, gD.Z));
                    // A point 5 units along Godot's ray: if our ray is the same
                    // line, its distance to that point is ~0.
                    float lineMiss = okOurs
                        ? NVec3.Cross(gon + gdn * 5f - oo, od).Length()
                        : float.NaN;
                    float dot = okOurs ? NVec3.Dot(od, gdn) : 0f;
                    GD.Print($"[Probe] legA ray local=({lx:F0},{ly:F0}) ok={okOurs} " +
                             $"dot={dot:F4} lineMiss={lineMiss:F4} " +
                             $"oursO=({(okOurs ? oo.X.ToString("F2") : "-")},{(okOurs ? oo.Y.ToString("F2") : "-")},{(okOurs ? oo.Z.ToString("F2") : "-")}) " +
                             $"gtdO=({gO.X:F2},{gO.Y:F2},{gO.Z:F2})");
                }
            }
            else
            {
                GD.Print("[Probe] legA SKIPPED (no vp/camera)");
            }

            // ── Leg B + node/element transforms: walk the scene world ──
            var root = runtime.Root;
            var sceneWorld = session.SceneWorld;
            if (sceneWorld == null)
            {
                GD.Print("[Probe] legB SKIPPED (no scene world)");
            }
            else
            {
                void Walk(IWorldElement el)
                {
                    var wm = el.WorldTransform;
                    var ctr = new NVec3(wm.M41, wm.M42, wm.M43);
                    long elVp = renderer.GetElementViewportId(el.Id);
                    var mr = el.Components.OfType<IMeshRenderable>().FirstOrDefault();
                    GD.Print($"[Probe] el '{el.Name}' id={el.Id} vp={elVp} " +
                             $"ctr=({ctr.X:F2},{ctr.Y:F2},{ctr.Z:F2}) " +
                             $"mesh={(mr == null ? "none" : $"{mr.MeshPoints?.Length ?? 0}pts/{mr.Indices?.Length ?? 0}idx")}");

                    if (renderer.GetNode(el.Id) is Node3D n3)
                    {
                        var o = n3.GlobalTransform.Origin;
                        float dPos = NVec3.Distance(new NVec3(o.X, o.Y, o.Z), ctr);
                        GD.Print($"[Probe]   nodeOrg=({o.X:F3},{o.Y:F3},{o.Z:F3}) " +
                                 $"elOrg=({ctr.X:F3},{ctr.Y:F3},{ctr.Z:F3}) dPos={dPos:F4}");
                    }
                    else
                    {
                        GD.Print("[Probe]   node=none");
                    }

                    if (mr != null && foundVp && vp.HasCamera && cam != null)
                    {
                        if (ViewportMath.TryProjectToScreen(vp, ctr, out var px))
                        {
                            float lx = px.X - vp.X, ly = px.Y - vp.Y;
                            bool behind = cam.IsPositionBehind(new Vector3(ctr.X, ctr.Y, ctr.Z));
                            var gpx = cam.UnprojectPosition(new Vector3(ctr.X, ctr.Y, ctr.Z));

                            // Our ray through the element's own projected centre:
                            // round-trip passes through ctr by construction, so a
                            // SceneQuery miss here means geometry/transform, not
                            // matrix conversion.
                            SceneRaycastHit? oursHit = null;
                            float ctrMiss = float.NaN;
                            if (ViewportMath.TryUnprojectRay(vp, px, out var ro, out var rd))
                            {
                                oursHit = SceneQuery.Raycast(root, viewportElementId, ro, rd);
                                ctrMiss = NVec3.Cross(ctr - ro, NVec3.Normalize(rd)).Length();
                            }

                            // Godot's real ray through the same element centre.
                            SceneRaycastHit? gtdHit = null;
                            if (!behind)
                            {
                                var gO = cam.ProjectRayOrigin(gpx);
                                var gD = cam.ProjectRayNormal(gpx);
                                gtdHit = SceneQuery.Raycast(root, viewportElementId,
                                    new NVec3(gO.X, gO.Y, gO.Z),
                                    NVec3.Normalize(new NVec3(gD.X, gD.Y, gD.Z)));
                            }

                            GD.Print($"[Probe]   px ours=({lx:F1},{ly:F1}) godot=({gpx.X:F1},{gpx.Y:F1}) " +
                                     $"behind={behind} ctrMiss={ctrMiss:F4} | " +
                                     $"pick ours={oursHit?.Element.Name ?? "(none)"}" +
                                     $"{(oursHit != null ? $"@{oursHit.Value.Distance:F2}" : "")} " +
                                     $"godot={gtdHit?.Element.Name ?? (behind ? "(behind)" : "(none)")}" +
                                     $"{(gtdHit != null ? $"@{gtdHit.Value.Distance:F2}" : "")}");
                        }
                    }

                    foreach (var c in el.Children)
                        Walk(c);
                }

                foreach (var r in sceneWorld.Root)
                    Walk(r);
            }

            // ── Gizmo leg: analytic handle grab under both rays ──
            var sel = session.Selected;
            if (sel != null && foundVp && vp.HasCamera && cam != null)
            {
                var wm = sel.WorldTransform;
                var origin = new NVec3(wm.M41, wm.M42, wm.M43);
                GD.Print($"[Probe] gizmo target='{sel.Name}' origin=({origin.X:F2},{origin.Y:F2},{origin.Z:F2})");
                if (ViewportMath.TryGetScreenSize(vp, origin, TransformGizmo.ScreenFraction, out float gzSize))
                {
                    GD.Print($"[Probe] gizmo size={gzSize:F3}");
                    var specs = new List<TransformGizmo.GizmoHandleSpec>();
                    TransformGizmo.BuildHandleSpecs(GizmoMode.Translate, origin, gzSize, specs);
                    foreach (var s in specs)
                    {
                        NVec3 pt;
                        if (s.Pick.Kind == TransformGizmo.GizmoPickKind.Capsule)
                            pt = (s.Pick.A + s.Pick.B) * 0.5f;
                        else if (s.Pick.Kind == TransformGizmo.GizmoPickKind.Box)
                            pt = s.Pick.A;
                        else
                            continue;

                        if (!ViewportMath.TryProjectToScreen(vp, pt, out var px)) continue;
                        float lx = px.X - vp.X, ly = px.Y - vp.Y;

                        int ourId = -1, gtdId = -1;
                        if (ViewportMath.TryUnprojectRay(vp, px, out var ro, out var rd))
                            TransformGizmo.TryHitHandles(ro, NVec3.Normalize(rd), specs, out ourId, out _);
                        var gO = cam.ProjectRayOrigin(new Vector2(lx, ly));
                        var gD = cam.ProjectRayNormal(new Vector2(lx, ly));
                        TransformGizmo.TryHitHandles(new NVec3(gO.X, gO.Y, gO.Z),
                            NVec3.Normalize(new NVec3(gD.X, gD.Y, gD.Z)), specs, out gtdId, out _);

                        GD.Print($"[Probe]   handle {s.HandleId} px=({lx:F1},{ly:F1}) " +
                                 $"ours={(ourId >= 0 ? ourId.ToString() : "MISS")} " +
                                 $"godot={(gtdId >= 0 ? gtdId.ToString() : "MISS")}");
                    }
                }
            }
            else
            {
                GD.Print("[Probe] gizmo leg SKIPPED (no selection)");
            }

            GD.Print("[Probe] ==== pick probe end ====");
        }
    }
}
