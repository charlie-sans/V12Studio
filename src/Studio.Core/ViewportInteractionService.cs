using System;
using System.Collections.Generic;
using System.Numerics;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Input;
using V12.Core.Interfaces.Renderer;
using V12.Core.Rendering;
using V12.Core.UI;
using V12.Components;

namespace V12Studio.Core
{
    /// <summary>
    /// Studio viewport interaction: a Unity-style scene-view camera (fly / pan /
    /// orbit / dolly), object picking via the engine scene query, and the
    /// translation gizmo, plus the in-app file dialog behind
    /// <see cref="IViewportInteraction.ShowFileDialog"/>.
    ///
    /// This is editor code — it lives in Studio.Core, not the V12 engine. It talks
    /// to the host renderer only through three renderer-agnostic contracts:
    /// <see cref="IViewportRegistry"/> (viewport rects + camera matrices),
    /// <see cref="IInputSource"/> (raw per-frame input) and the optional
    /// <see cref="IDirectTransformProvider"/> (latency-free transform writes).
    /// Picking goes through the engine's <see cref="SceneQuery"/>, so no renderer
    /// mesh readback is involved.
    ///
    /// Camera controls (Unity scene-view parity):
    ///   RMB-drag       look around (fly-look, rotates in place)
    ///   WASD / QE      fly (while RMB held): W/S forward-back along the look
    ///                  direction, A/D strafe, E/Q up/down; Shift boosts
    ///   wheel (RMB)    adjust fly speed
    ///   MMB-drag       pan (translate parallel to the view plane)
    ///   Alt+LMB-drag   orbit around the pivot (selection, or a point ahead)
    ///   wheel          dolly along the look direction
    ///   LMB            pick (gizmo handle first, then the scene)
    ///   T / R / S      gizmo mode: translate / rotate / scale
    ///
    /// Runs as an <see cref="IGameService"/> so it ticks on the game thread inside
    /// <c>GameRoot.Update</c>.
    /// </summary>
    public sealed class ViewportInteractionService : IViewportInteraction, IGameService
    {
        private const float DegreesPerPixel = 0.25f;
        private const float MinPitchDeg = -89f;
        private const float MaxPitchDeg = 89f;
        private const float DefaultOrbitDist = 7f;
        private const float DefaultFlySpeed = 6f;
        private const float MinFlySpeed = 0.25f;
        private const float MaxFlySpeed = 200f;
        private const float FlyBoostMultiplier = 4f;
        private const float DollyPerNotchOfDist = 0.12f;
        private const float PanScalePerDist = 0.0022f;

        private readonly IViewportRegistry _registry;
        private readonly IInputSource _input;
        private readonly IDirectTransformProvider? _direct;
        private readonly HashSet<IWorldElement> _directDriven = new(ReferenceEqualityComparer.Instance);
        private readonly ViewportFileDialog _dialog;
        private readonly GameRoot _root;

        private Action<IWorldElement?>? _selectHandler;
        private IWorldElement? _editorCam;
        private IWorldElement? _gizmoTarget;
        private GizmoMode _mode = GizmoMode.Translate;

        // Free-fly camera pose (position + yaw/pitch). The pivot is the orbit
        // centre; it tracks the camera through fly/pan/dolly and snaps to the
        // selection so Alt+orbit swings around what you picked.
        private Vector3 _pos;
        private Vector3 _pivot;
        private float _yawDeg;
        private float _pitchDeg;
        private float _orbitRadius = DefaultOrbitDist;
        private float _flySpeed = DefaultFlySpeed;

        // Active camera gesture (drives the direct-transform fast path; commit to
        // the world once the gesture ends).
        private bool _orbiting;   // Alt+LMB
        private bool _looking;    // RMB fly-look
        private bool _panning;    // MMB
        private bool _cameraWorldDirty;
        private Vector2 _prevMouse;
        private int _lastWheel;
        private bool _wheelInit;

        // Edge tracking against the previous input snapshot.
        private HashSet<int>? _prevKeys;
        private bool _prevLeftDown;

        // Active axis drag: pinned to the viewport the grab happened in so the
        // drag keeps tracking after the cursor leaves the rect. Rotate/scale pin
        // the target's full world matrix at grab time so the whole gesture is a
        // delta from the press, never a re-read of whatever the element is mid-drag.
        private int _dragAxis = -1;
        private long _dragViewportId;
        private long _gizmoViewportId;
        private Vector3 _dragStartHit;
        private Vector3 _dragStartPos;
        private Matrix4x4 _dragStartWorld;
        private Vector3 _dragStartScale = Vector3.One;
        private float _dragRefLen = 1f;

        private readonly List<TransformGizmo.GizmoHandleSpec> _specScratch = new();

        // Transient gizmo entities, shared with nova's SceneGizmo: one
        // "_EditorGizmo" root per target world with a child element per handle
        // mesh. They render and pick through the normal pipeline; the transient
        // marker keeps them out of saves and network sync.
        private readonly GizmoEntitySync _gizmo = new();

        public ViewportInteractionService(
            GameRoot root,
            IViewportRegistry registry,
            IInputSource input,
            IDirectTransformProvider? direct = null)
        {
            _root = root;
            _registry = registry;
            _input = input;
            _direct = direct;
            _dialog = new ViewportFileDialog(root);
        }

        public void Initialize(GameRoot gameRoot) { }

        public void Update(float deltaTime) => Tick(deltaTime);

        public void Update(GameRoot gameRoot) { }

        public void SetSelectHandler(Action<IWorldElement?> handler) => _selectHandler = handler;

        /// <summary>Register the camera element to drive. The fly pose is seeded
        /// from its current pose (pivot a point ahead of it), so the opening
        /// framing is preserved exactly.</summary>
        public void SetEditorCamera(IWorldElement? cameraElement)
        {
            if (_editorCam != null && !ReferenceEquals(_editorCam, cameraElement))
                ReleaseDirect(_editorCam);
            _editorCam = cameraElement;
            _orbiting = _looking = _panning = false;
            if (_editorCam == null) return;
            _directDriven.Add(_editorCam);

            var w = _editorCam.WorldTransform;
            var pos = new Vector3(w.M41, w.M42, w.M43);
            var fwd = new Vector3(-w.M31, -w.M32, -w.M33);
            if (fwd.LengthSquared() < 1e-8f) fwd = -Vector3.UnitZ;
            fwd = Vector3.Normalize(fwd);

            _pos = pos;
            _yawDeg = MathF.Atan2(fwd.Z, fwd.X) * 180f / MathF.PI;
            _pitchDeg = MathF.Asin(Math.Clamp(fwd.Y, -1f, 1f)) * 180f / MathF.PI;
            _pivot = pos + fwd * DefaultOrbitDist;
            _orbitRadius = DefaultOrbitDist;
            _cameraWorldDirty = true;
        }

        /// <summary>Show the gizmo around <paramref name="el"/> (null hides it).
        /// Recenters the orbit pivot on the element so Alt+orbit swings around the
        /// selection; the camera pose itself does not move (Unity parity). A new
        /// target aborts any active drag.</summary>
        public void SetGizmoTarget(IWorldElement? el)
        {
            if (!ReferenceEquals(el, _gizmoTarget))
            {
                _dragAxis = -1;
                if (_gizmoTarget != null) ReleaseDirect(_gizmoTarget);
            }
            _gizmoTarget = el;
            if (el == null) { _gizmo.Destroy(); return; }
            if (_orbiting) return;
            var w = el.WorldTransform;
            _pivot = new Vector3(w.M41, w.M42, w.M43);
            _orbitRadius = MathF.Max(0.25f, Vector3.Distance(_pos, _pivot));
        }

        public void SetGizmoMode(GizmoMode mode)
        {
            _mode = mode;
            if (mode != GizmoMode.Translate) _dragAxis = -1;
        }

        public void ShowFileDialog(bool save, string title, string initialPath, Action<string?> onComplete)
            => _dialog.Open(save, title, initialPath, onComplete);

        // ── Per-frame ─────────────────────────────────────────────────────

        private void Tick(float dt)
        {
            TickInput(dt);

            // Keep the camera responsive during a gesture without producing a full
            // world snapshot for every worker tick. Commit once at gesture release.
            bool cameraGesture = _orbiting || _looking || _panning;
            bool commitCamera = _direct == null || (!cameraGesture && _cameraWorldDirty);
            ApplyToCamera(commitToWorld: commitCamera);
            if (commitCamera) _cameraWorldDirty = false;

            // Keep the transient gizmo entities on the target (constant screen size
            // while dollying). Runs after the camera so sizing is exact.
            var target = _gizmoTarget;
            if (target != null)
            {
                var world = _root.GetWorldForElement(target);
                if (world == null) _gizmo.Destroy();
                else if (GizmoOriginAndSize(_gizmoViewportId, out var origin, out var size))
                    _gizmo.Sync(world, target, _mode, origin, size);
                if (_dragAxis >= 0) QueueDirect(target);
            }
        }

        private void TickInput(float dt)
        {
            var s = _input.GetSnapshot();
            var keys = s.PressedKeys ?? EmptyKeys;

            // While the file dialog is open the viewport is inert: no camera, no
            // wheel, no picking through the dialog. Esc cancels the dialog.
            if (_dialog.IsOpen)
            {
                _orbiting = _looking = _panning = false;
                _dragAxis = -1;
                if (KeyPressedNow(keys, InputKeys.Escape)) _dialog.Cancel();
                FinishInput(s, keys);
                return;
            }

            if (_editorCam == null || !s.WindowActive || s.MouseLocked)
            {
                if (s.LeftDown && !_prevLeftDown)
                    Console.WriteLine($"[Viewport] click dropped by guard: cam={_editorCam != null} active={s.WindowActive} lockMouse={s.MouseLocked}");
                _orbiting = _looking = _panning = false;
                FinishInput(s, keys);
                return;
            }

            if (!_wheelInit) { _wheelInit = true; _lastWheel = s.Wheel; }

            var vps = _registry.Viewports;
            var hit = ViewportMath.FindViewportAt(vps, s.MousePosition);
            bool inViewport = hit.HasValue;
            long vpId = hit?.Id ?? 0;
            if (inViewport && _dragAxis < 0) _gizmoViewportId = vpId;

            bool alt = KeyDown(keys, InputKeys.Alt);
            bool shift = KeyDown(keys, InputKeys.Shift);

            // T / R / S switch gizmo mode on the key's press edge (editor
            // convention). Edge, not held, so a held T can't shadow a later R.
            if (KeyPressedNow(keys, InputKeys.T)) SetGizmoMode(GizmoMode.Translate);
            else if (KeyPressedNow(keys, InputKeys.R)) SetGizmoMode(GizmoMode.Rotate);
            else if (KeyPressedNow(keys, InputKeys.S)) SetGizmoMode(GizmoMode.Scale);

            // ── RMB: fly-look ────────────────────────────────────────────
            bool rmb = s.RightDown;
            if (rmb && !_looking)
            {
                if (inViewport) { _looking = true; _prevMouse = s.MousePosition; }
            }
            else if (!rmb)
            {
                _looking = false;
            }
            if (_looking)
            {
                float dx = s.MousePosition.X - _prevMouse.X;
                float dy = s.MousePosition.Y - _prevMouse.Y;
                _prevMouse = s.MousePosition;
                if (dx != 0 || dy != 0)
                {
                    // Drag right → look right; drag down → look down (FPS/Unity
                    // non-inverted). Yaw matches the orbit convention; pitch is the
                    // FPS sign so RMB-look feels like a first-person camera.
                    _yawDeg += dx * DegreesPerPixel;
                    _pitchDeg = Math.Clamp(_pitchDeg - dy * DegreesPerPixel, MinPitchDeg, MaxPitchDeg);
                    _cameraWorldDirty = true;
                }
                FlyMove(keys, shift, dt);
            }

            // ── MMB: pan ─────────────────────────────────────────────────
            bool mmb = s.MiddleDown;
            if (mmb && !_panning)
            {
                if (inViewport) { _panning = true; _prevMouse = s.MousePosition; }
            }
            else if (!mmb)
            {
                _panning = false;
            }
            if (_panning)
            {
                float dx = s.MousePosition.X - _prevMouse.X;
                float dy = s.MousePosition.Y - _prevMouse.Y;
                _prevMouse = s.MousePosition;
                if (dx != 0 || dy != 0)
                {
                    var look = LookDir();
                    var right = Vector3.Normalize(Vector3.Cross(look, Vector3.UnitY));
                    var up = Vector3.Normalize(Vector3.Cross(right, look));
                    float scale = MathF.Max(0.5f, Vector3.Distance(_pos, _pivot)) * PanScalePerDist;
                    // Drag right → scene follows cursor (camera moves left); drag
                    // down → scene follows down (camera moves up).
                    var delta = (-right * dx + up * dy) * scale;
                    _pos += delta;
                    _pivot += delta;
                    _cameraWorldDirty = true;
                }
            }

            // ── Wheel: fly-speed while looking, else dolly ───────────────
            int wheelDelta = s.Wheel - _lastWheel;
            _lastWheel = s.Wheel;
            if (wheelDelta != 0 && (inViewport || _orbiting || _looking || _panning))
            {
                if (_looking)
                {
                    _flySpeed = Math.Clamp(_flySpeed * MathF.Pow(1.15f, wheelDelta / 120f), MinFlySpeed, MaxFlySpeed);
                }
                else
                {
                    float step = (wheelDelta / 120f) * MathF.Max(0.5f, Vector3.Distance(_pos, _pivot)) * DollyPerNotchOfDist;
                    var look = LookDir();
                    _pos += look * step;
                    _pivot += look * step;
                }
                _cameraWorldDirty = true;
            }

            // ── LMB: Alt+orbit, else gizmo grab / pick ───────────────────
            bool leftDown = s.LeftDown;
            bool leftPressed = leftDown && !_prevLeftDown;
            if (!leftDown && _orbiting)
            {
                _orbiting = false;
            }
            if (!leftDown && _dragAxis >= 0)
            {
                _dragAxis = -1;
                if (_gizmoTarget != null) ReleaseDirect(_gizmoTarget);
            }

            if (leftPressed && inViewport && !_looking && !_panning)
            {
                if (alt)
                {
                    _orbiting = true;
                    _prevMouse = s.MousePosition;
                    if (_gizmoTarget != null)
                    {
                        var w = _gizmoTarget.WorldTransform;
                        _pivot = new Vector3(w.M41, w.M42, w.M43);
                    }
                    _orbitRadius = MathF.Max(0.25f, Vector3.Distance(_pos, _pivot));
                }
                else if (_gizmoTarget != null && TryGrabAxis(vpId, s.MousePosition))
                {
                    Console.WriteLine($"[Viewport] grabbed handle {_dragAxis} of '{_gizmoTarget.Name}' vp={vpId}");
                }
                else
                {
                    var el = Pick(vpId, s.MousePosition);
                    Console.WriteLine($"[Viewport] press vp={vpId} at ({s.MousePosition.X},{s.MousePosition.Y}) target='{el?.Name ?? "(none)"}'");
                    _selectHandler?.Invoke(el);
                    SetGizmoTarget(el);
                }
            }

            if (_orbiting)
            {
                float dx = s.MousePosition.X - _prevMouse.X;
                float dy = s.MousePosition.Y - _prevMouse.Y;
                _prevMouse = s.MousePosition;
                if (dx != 0 || dy != 0)
                {
                    // Orbit: scene follows the cursor (yaw/pitch signs as the shipped
                    // orbit model). Recompute the position on the sphere around pivot.
                    _yawDeg += dx * DegreesPerPixel;
                    _pitchDeg = Math.Clamp(_pitchDeg + dy * DegreesPerPixel, MinPitchDeg, MaxPitchDeg);
                    _pos = _pivot - LookDir() * _orbitRadius;
                    _cameraWorldDirty = true;
                }
            }

            if (leftDown && _dragAxis >= 0) UpdateDrag(s.MousePosition);

            FinishInput(s, keys);
        }

        private void FinishInput(InputSnapshot s, HashSet<int> keys)
        {
            _prevLeftDown = s.LeftDown;
            _prevKeys = keys;
        }

        private static readonly HashSet<int> EmptyKeys = new();

        /// <summary>Key currently down (held).</summary>
        private static bool KeyDown(HashSet<int> keys, int key) => keys.Contains(key);

        /// <summary>Key press edge: down now, up in the previous snapshot.</summary>
        private bool KeyPressedNow(HashSet<int> keys, int key)
            => keys.Contains(key) && (_prevKeys == null || !_prevKeys.Contains(key));

        // ── Camera pose ───────────────────────────────────────────────────

        /// <summary>Unit look direction from the current yaw/pitch.</summary>
        private Vector3 LookDir()
        {
            float cp = _pitchDeg * MathF.PI / 180f;
            float cy = _yawDeg * MathF.PI / 180f;
            return new Vector3(MathF.Cos(cp) * MathF.Cos(cy), MathF.Sin(cp), MathF.Cos(cp) * MathF.Sin(cy));
        }

        /// <summary>Fly translation from WASD/QE relative to the look basis (W/S
        /// follow the full look direction so you can fly up/down by pitching; E/Q
        /// are world up/down). Shift boosts.</summary>
        private void FlyMove(HashSet<int> keys, bool boost, float dt)
        {
            var look = LookDir();
            var right = Vector3.Normalize(Vector3.Cross(look, Vector3.UnitY));
            var move = Vector3.Zero;
            if (KeyDown(keys, InputKeys.W)) move += look;
            if (KeyDown(keys, InputKeys.S)) move -= look;
            if (KeyDown(keys, InputKeys.D)) move += right;
            if (KeyDown(keys, InputKeys.A)) move -= right;
            if (KeyDown(keys, InputKeys.E)) move += Vector3.UnitY;
            if (KeyDown(keys, InputKeys.Q)) move -= Vector3.UnitY;
            if (move.LengthSquared() < 1e-8f) return;
            move = Vector3.Normalize(move);
            float speed = _flySpeed * (boost ? FlyBoostMultiplier : 1f) * Math.Clamp(dt, 0f, 0.1f);
            var delta = move * speed;
            _pos += delta;
            _pivot += delta;
            _cameraWorldDirty = true;
        }

        /// <summary>Recompute the camera element's transform from the fly pose.
        /// The camera looks along the yaw/pitch direction from its position.</summary>
        private void ApplyToCamera(bool commitToWorld)
        {
            if (_editorCam == null) return;

            var dir = LookDir();
            var pos = _pos;

            // Camera looks along +dir with up ≈ +Y: build the rotation from an
            // orthonormal basis (matches BuildViewProjection's -Z-forward read).
            var fwd = Vector3.Normalize(dir);
            var right = Vector3.Cross(fwd, Vector3.UnitY);
            if (right.LengthSquared() < 1e-8f) right = Vector3.UnitX;
            right = Vector3.Normalize(right);
            var up = Vector3.Cross(right, fwd);
            var m = new Matrix4x4(
                right.X, right.Y, right.Z, 0f,
                up.X, up.Y, up.Z, 0f,
                -fwd.X, -fwd.Y, -fwd.Z, 0f,
                pos.X, pos.Y, pos.Z, 1f);
            var q = Quaternion.CreateFromRotationMatrix(m);
            var localTransform = new TRS { Position = pos, Rotation = q, Scale = Vector3.One };

            // The camera is normally a scene root; tolerate nesting by pushing the
            // pose through the parent's inverse when there is one.
            var parent = _editorCam.Parent;
            if (parent != null && Matrix4x4.Invert(parent.WorldTransform, out var invParent))
            {
                var local = m * invParent;
                if (Matrix4x4.Decompose(local, out var s, out var lq, out var lp))
                    localTransform = new TRS { Position = lp, Rotation = lq, Scale = s };
            }
            var localMatrix = Matrix4x4.CreateScale(localTransform.Scale)
                * Matrix4x4.CreateFromQuaternion(localTransform.Rotation)
                * Matrix4x4.CreateTranslation(localTransform.Position);
            if (commitToWorld)
                _editorCam.LocalTransform = localTransform;
            QueueDirect(_editorCam, localMatrix);
        }

        private void QueueDirect(IWorldElement element, Matrix4x4? localTransform = null)
        {
            if (_direct == null) return;
            _directDriven.Add(element);
            _direct.SetElementTransformDirect(
                element, localTransform ?? ElementPlacement.ElementLocalMatrix(element));
        }

        private void ReleaseDirect(IWorldElement element)
        {
            if (!_directDriven.Remove(element)) return;
            _direct?.ReleaseElementTransformDirect(element);
        }

        // ── Picking ───────────────────────────────────────────────────────

        /// <summary>Cast a ray through the mouse and return the nearest owning
        /// element, or null for empty space (null → the editor deselects). Uses the
        /// engine scene query; gizmo (transient) and directly-driven elements are
        /// never picked.</summary>
        private IWorldElement? Pick(long vpId, Vector2 mouse)
        {
            if (!TryUnproject(vpId, mouse, out var ro, out var rd))
            {
                Console.WriteLine($"[Viewport] pick vp={vpId}: ray failed");
                return null;
            }
            var hit = SceneQuery.Raycast(_root, vpId, ro, rd, exclude: el => _directDriven.Contains(el));
            Console.WriteLine($"[Viewport] pick vp={vpId} at ({mouse.X},{mouse.Y}) -> {hit?.Element.Name ?? "(none)"}{(hit != null ? $" dist={hit.Value.Distance:F2}" : "")}");
            return hit?.Element;
        }

        // ── Registry lookups ──────────────────────────────────────────────

        private bool TryFindViewport(long id, out ViewportInfo vp)
        {
            foreach (var v in _registry.Viewports)
                if (v.Id == id) { vp = v; return true; }
            vp = default;
            return false;
        }

        private long FindFirstViewportId()
        {
            var vps = _registry.Viewports;
            return vps.Count == 0 ? 0 : vps[0].Id;
        }

        private bool TryUnproject(long vpId, Vector2 mouse, out Vector3 origin, out Vector3 direction)
        {
            origin = default;
            direction = default;
            if (!TryFindViewport(vpId, out var vp)) return false;
            return ViewportMath.TryUnprojectRay(vp, mouse, out origin, out direction);
        }

        private bool TryGetView(long vpId, out Matrix4x4 view)
        {
            if (TryFindViewport(vpId, out var vp) && vp.HasCamera) { view = vp.View; return true; }
            view = default;
            return false;
        }

        // ── Gizmo: sizing + grab + drag ───────────────────────────────────

        /// <summary>Gizmo pivot (target world position) and constant-screen size
        /// from the editor camera. False when there is no target or no camera yet.</summary>
        private bool GizmoOriginAndSize(long viewportId, out Vector3 origin, out float size)
        {
            origin = Vector3.Zero;
            size = 0f;
            if (_gizmoTarget == null || _editorCam == null) return false;
            if (viewportId == 0) viewportId = FindFirstViewportId();
            if (viewportId == 0) return false;
            if (!TryFindViewport(viewportId, out var vp) || !vp.HasCamera) return false;
            var w = _gizmoTarget.WorldTransform;
            origin = new Vector3(w.M41, w.M42, w.M43);
            return ViewportMath.TryGetScreenSize(vp, origin, TransformGizmo.ScreenFraction, out size);
        }

        /// <summary>Analytic test of the gizmo handles — ray against the canonical
        /// fat pick shapes; on a hit, pins the drag state and returns true (caller
        /// skips world picking). The visual mesh is never raycast: what you see is
        /// what you can drag.</summary>
        private bool TryGrabAxis(long vpId, Vector2 mouse)
        {
            if (_gizmoTarget == null) return false;
            if (!TryUnproject(vpId, mouse, out var ro, out var rd)) return false;
            if (rd.LengthSquared() < 1e-12f) return false;
            rd = Vector3.Normalize(rd);

            if (!GizmoOriginAndSize(vpId, out var origin, out var size)) return false;
            TransformGizmo.BuildHandleSpecs(_mode, origin, size, _specScratch);
            if (!TransformGizmo.TryHitHandles(ro, rd, _specScratch, out int bestHandle, out _))
                return false;

            // Pin the drag to this viewport and this camera pose relationship.
            var w = _gizmoTarget.WorldTransform;
            _dragAxis = bestHandle;
            _dragViewportId = vpId;
            _dragStartPos = origin;
            _dragStartWorld = w;

            if (!TryGetView(vpId, out var view)) return false;
            if (!Matrix4x4.Invert(view, out var invView)) return false;

            if (_mode == GizmoMode.Rotate)
            {
                // Rotation works in the plane through the pivot whose normal is the
                // grabbed axis — not the camera-facing drag plane.
                _dragStartHit = RayAxisPlaneHit(origin, TransformGizmo.AxisDirs[bestHandle], ro, rd);
            }
            else
            {
                var camFwd = CameraForward(invView);
                _dragStartHit = RayAxisHit(ro, rd, origin, camFwd);
                _dragRefLen = MathF.Max(1e-3f, size);
            }

            if (_mode == GizmoMode.Scale)
            {
                // Rendering scales a mesh via Width×ScaleComponent and ignores
                // Element.LocalTransform.Scale, so the gizmo must drive the
                // ScaleComponent (created on first scale drag).
                var sc = _gizmoTarget.GetComponent<ScaleComponent>();
                _dragStartScale = sc != null
                    ? new Vector3(sc.ScaleX, sc.ScaleY, sc.ScaleZ)
                    : Vector3.One;
            }
            return true;
        }

        /// <summary>Move / rotate / scale the drag target, always as a delta from the
        /// state pinned at grab time: translate projects onto the drag plane and keeps
        /// the axis component, rotate sweeps the angle around the axis in its own
        /// plane, scale multiplies along the axis.</summary>
        private void UpdateDrag(Vector2 mouse)
        {
            if (_dragAxis < 0 || _gizmoTarget == null) return;
            if (!TryUnproject(_dragViewportId, mouse, out var ro, out var rd)) return;
            if (!TryGetView(_dragViewportId, out var view)) return;
            if (!Matrix4x4.Invert(view, out var invView)) return;

            var camFwd = CameraForward(invView);

            if (_mode == GizmoMode.Scale && _dragAxis == TransformGizmo.CenterHandle)
            {
                // Centre cube → uniform scale. Anchor-style ratio along the look
                // direction: factor = current/start signed distance from the pivot.
                var hit = RayAxisHit(ro, rd, _dragStartPos, camFwd);
                float d0 = Vector3.Dot(_dragStartHit - _dragStartPos, camFwd);
                float d1 = Vector3.Dot(hit - _dragStartPos, camFwd);
                float factor = MathF.Abs(d0) < 1e-4f ? 1f : d1 / d0;
                factor = Math.Clamp(factor, 0.05f, 50f);

                var scCenter = _gizmoTarget.GetComponent<ScaleComponent>();
                if (scCenter == null)
                {
                    scCenter = new ScaleComponent();
                    _gizmoTarget.AddComponent(scCenter);
                }
                scCenter.ScaleX = _dragStartScale.X * factor;
                scCenter.ScaleY = _dragStartScale.Y * factor;
                scCenter.ScaleZ = _dragStartScale.Z * factor;
                return;
            }

            if (_dragAxis > 2) return;
            var axis = TransformGizmo.AxisDirs[_dragAxis];

            if (_mode == GizmoMode.Rotate)
            {
                var hit = RayAxisPlaneHit(_dragStartPos, axis, ro, rd);

                // Signed angle around the axis from the pinned start hit:
                // u = start direction (⊥ axis), v = axis × u.
                var u = _dragStartHit - _dragStartPos;
                u -= axis * Vector3.Dot(u, axis);
                if (u.LengthSquared() < 1e-10f) return;
                u = Vector3.Normalize(u);
                var v = Vector3.Cross(axis, u);
                var w = hit - _dragStartPos;
                float ang = MathF.Atan2(Vector3.Dot(w, v), Vector3.Dot(w, u));
                if (MathF.Abs(ang) < 1e-4f) return;

                var rot = Matrix4x4.CreateFromAxisAngle(axis, ang);
                var world = _dragStartWorld
                    * Matrix4x4.CreateTranslation(-_dragStartPos)
                    * rot
                    * Matrix4x4.CreateTranslation(_dragStartPos);
                SetElementWorldTransform(_gizmoTarget, world);
            }
            else if (_mode == GizmoMode.Scale)
            {
                var hit = RayAxisHit(ro, rd, _dragStartPos, camFwd);
                float delta = Vector3.Dot(hit - _dragStartHit, axis);
                float factor = MathF.Max(0.01f, 1f + delta / _dragRefLen);
                if (MathF.Abs(factor - 1f) < 1e-5f) return;

                var sc = _gizmoTarget.GetComponent<ScaleComponent>();
                if (sc == null)
                {
                    sc = new ScaleComponent();
                    _gizmoTarget.AddComponent(sc);
                }
                float sx = _dragStartScale.X, sy = _dragStartScale.Y, sz = _dragStartScale.Z;
                if (_dragAxis == 0) sx = _dragStartScale.X * factor;
                else if (_dragAxis == 1) sy = _dragStartScale.Y * factor;
                else sz = _dragStartScale.Z * factor;
                sc.ScaleX = sx;
                sc.ScaleY = sy;
                sc.ScaleZ = sz;
            }
            else
            {
                var hit = RayAxisHit(ro, rd, _dragStartPos, camFwd);
                float delta = Vector3.Dot(hit - _dragStartHit, axis);
                SetElementWorldPosition(_gizmoTarget, _dragStartPos + axis * delta);
            }
        }

        /// <summary>Write a world-space position onto the element, going through the
        /// parent's inverse when it is nested.</summary>
        private static void SetElementWorldPosition(IWorldElement el, Vector3 worldPos)
        {
            var parent = el.Parent;
            if (parent == null)
            {
                var t = el.LocalTransform;
                t.Position = worldPos;
                el.LocalTransform = t;
            }
            else if (Matrix4x4.Invert(parent.WorldTransform, out var invParent))
            {
                var t = el.LocalTransform;
                t.Position = Vector3.Transform(worldPos, invParent);
                el.LocalTransform = t;
            }
        }

        /// <summary>Write a full world matrix (rotate / scale results) onto the
        /// element: push through the parent's inverse and decompose back into
        /// position / rotation / scale.</summary>
        private static void SetElementWorldTransform(IWorldElement el, Matrix4x4 world)
        {
            var local = world;
            var parent = el.Parent;
            if (parent != null)
            {
                if (!Matrix4x4.Invert(parent.WorldTransform, out var invParent)) return;
                local = world * invParent;
            }
            if (!Matrix4x4.Decompose(local, out var s, out var q, out var p)) return;
            // The world folds ScaleComponent (see ElementPlacement): divide it back
            // out so a rotate gesture round-trips scale exactly instead of
            // double-applying it.
            var sc = el.GetComponent<ScaleComponent>();
            if (sc != null)
            {
                s = new Vector3(
                    MathF.Abs(sc.ScaleX) > 1e-6f ? s.X / sc.ScaleX : s.X,
                    MathF.Abs(sc.ScaleY) > 1e-6f ? s.Y / sc.ScaleY : s.Y,
                    MathF.Abs(sc.ScaleZ) > 1e-6f ? s.Z / sc.ScaleZ : s.Z);
            }
            el.LocalTransform = new TRS { Position = p, Rotation = q, Scale = s };
        }

        /// <summary>Intersect the ray with the plane through <paramref name="point"/>
        /// perpendicular to the camera forward — the classic drag plane.</summary>
        private static Vector3 RayAxisHit(Vector3 origin, Vector3 dir, Vector3 point, Vector3 fwd)
        {
            float denom = Vector3.Dot(dir, fwd);
            if (MathF.Abs(denom) < 1e-6f) return point;
            float t = Vector3.Dot(point - origin, fwd) / denom;
            return origin + dir * t;
        }

        /// <summary>Intersect the ray with the plane through <paramref name="point"/>
        /// whose normal is <paramref name="axis"/> — the rotation plane for a ring
        /// drag.</summary>
        private static Vector3 RayAxisPlaneHit(Vector3 point, Vector3 axis, Vector3 origin, Vector3 dir)
        {
            float denom = Vector3.Dot(dir, axis);
            if (MathF.Abs(denom) < 1e-6f) return point;
            float t = Vector3.Dot(point - origin, axis) / denom;
            return origin + dir * t;
        }

        private static Vector3 CameraForward(Matrix4x4 invView)
        {
            // invView is the camera's world matrix: row 3 is its +Z (backwards).
            var fwd = new Vector3(-invView.M31, -invView.M32, -invView.M33);
            return fwd.LengthSquared() < 1e-12f ? Vector3.UnitZ : Vector3.Normalize(fwd);
        }
    }
}
