using System;
using System.Collections.Generic;
using System.Numerics;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Input;
using V12.Core.Interfaces.Renderer;
using V12.Core.Rendering;
using V12.Components;
using V12.Components.Renderables;
using V12.Studio.Input;
using DrawingColor = System.Drawing.Color;

namespace V12.Studio.UI
{
    /// <summary>
    /// Editor viewport interaction (orbit camera, picking, gizmo transforms).
    /// Now uses generic engine interfaces: IViewportRegistry, IInputSource,
    /// IDirectTransformProvider. No editor-specific code in V12.Core.
    ///
    /// Input: raw key set from IInputSource; this service maps T/R/S to modes.
    /// Output: viewport math via ViewportMath (engine-agnostic).
    /// Transforms: via IDirectTransformProvider (optional fast-path).
    /// </summary>
    public sealed class ViewportInteractionService : IGameService
    {
        private const float DegreesPerPixel = 0.25f;
        private const float MinPitchDeg = -89f;
        private const float MaxPitchDeg = 89f;
        private const float MinDist = 1f;
        private const float MaxDist = 200f;
        private const float DefaultDist = 7f;

        private readonly IViewportRegistry _viewportRegistry;
        private readonly IInputSource _input;
        private readonly IDirectTransformProvider? _transformProvider;
        private readonly HashSet<IWorldElement> _directlyDrivenElements = new(ReferenceEqualityComparer.Instance);
        private readonly GameRoot _root;

        private Action<IWorldElement?>? _selectHandler;
        private IWorldElement? _editorCam;
        private IWorldElement? _gizmoTarget;
        private GizmoMode _mode = GizmoMode.Translate;

        // Orbit state
        private float _yawDeg;
        private float _pitchDeg;
        private float _dist = DefaultDist;
        private Vector3 _focus;
        private bool _orbiting;
        private bool _cameraWorldDirty;
        private Vector2 _prevMouse;
        private int _lastWheel;
        private bool _wheelInit;

        // Edge tracking
        private bool _prevLeftDown;
        private bool _prevT;
        private bool _prevR;
        private bool _prevS;
        private bool _prevEsc;

        // Active drag
        private int _dragAxis = -1;
        private long _dragViewportId;
        private long _gizmoViewportId;
        private Vector3 _dragStartHit;
        private Vector3 _dragStartPos;
        private Matrix4x4 _dragStartWorld;
        private Vector3 _dragStartScale = Vector3.One;
        private float _dragRefLen = 1f;

        private readonly List<ViewportInfo> _viewportBuffer = new();
        private readonly List<TransformGizmo.GizmoHandleSpec> _specScratch = new();
        private readonly GizmoEntitySync _gizmo = new();

        // Editor keybindings (Studio defines these, not Core)
        private const int KeyT = (int)System.Windows.Forms.Keys.T;
        private const int KeyR = (int)System.Windows.Forms.Keys.R;
        private const int KeyS = (int)System.Windows.Forms.Keys.S;
        private const int KeyEscape = (int)System.Windows.Forms.Keys.Escape;

        public ViewportInteractionService(
            GameRoot root,
            IViewportRegistry viewportRegistry,
            IInputSource input)
        {
            _root = root;
            _viewportRegistry = viewportRegistry;
            _input = input;
            _transformProvider = null; // Optional; could be wired up later
            _gizmo = new GizmoEntitySync();
        }

        public void Initialize(GameRoot gameRoot) { }

        public void Update(float deltaTime) => Tick();

        public void Update(GameRoot gameRoot) { }

        public void SetSelectHandler(Action<IWorldElement?> handler) => _selectHandler = handler;

        public void SetEditorCamera(IWorldElement? cameraElement)
        {
            if (_editorCam != null && !ReferenceEquals(_editorCam, cameraElement))
                ReleaseDirectTransform(_editorCam);

            _editorCam = cameraElement;
            _orbiting = false;

            if (_editorCam == null) return;

            _directlyDrivenElements.Add(_editorCam);

            var w = _editorCam.WorldTransform;
            var pos = new Vector3(w.M41, w.M42, w.M43);
            var fwd = new Vector3(-w.M31, -w.M32, -w.M33);
            if (fwd.LengthSquared() < 1e-8f) fwd = -Vector3.UnitZ;
            fwd = Vector3.Normalize(fwd);

            _dist = DefaultDist;
            _focus = pos + fwd * _dist;
            _cameraWorldDirty = true;

            var dir = fwd;
            _yawDeg = MathF.Atan2(dir.Z, dir.X) * 180f / MathF.PI;
            _pitchDeg = MathF.Asin(Math.Clamp(dir.Y, -1f, 1f)) * 180f / MathF.PI;
        }

        public void SetGizmoTarget(IWorldElement? el)
        {
            if (!ReferenceEquals(el, _gizmoTarget))
            {
                _dragAxis = -1;
                if (_gizmoTarget != null) ReleaseDirectTransform(_gizmoTarget);
            }

            _gizmoTarget = el;
            if (el != null) _cameraWorldDirty = true;
            if (el == null) _gizmo.Destroy();
            if (el == null || _orbiting) return;

            var w = el.WorldTransform;
            _focus = new Vector3(w.M41, w.M42, w.M43);
            _cameraWorldDirty = true;
        }

        public void SetGizmoMode(GizmoMode mode)
        {
            _mode = mode;
            if (mode != GizmoMode.Translate) _dragAxis = -1;
        }

        // ── Per-frame input ───────────────────────────────────────────────

        private void Tick()
        {
            TickInput();

            bool commitCamera = _transformProvider == null || (!_orbiting && _cameraWorldDirty);
            ApplyToCamera(commitToWorld: commitCamera);
            if (commitCamera) _cameraWorldDirty = false;

            var target = _gizmoTarget;
            if (target != null)
            {
                var world = _root.GetWorldForElement(target);
                if (world != null && TryGetGizmoOriginAndSize(_gizmoViewportId, out var origin, out var size))
                    _gizmo.Sync(world, target, _mode, origin, size);

                if (_dragAxis >= 0)
                    QueueDirectTransform(target);
            }
            else
            {
                _gizmo.Destroy();
            }
        }

        private void TickInput()
        {
            var s = _input.GetSnapshot();

            if (_editorCam == null || !s.WindowActive || s.MouseLocked)
                return;

            if (!_wheelInit) { _wheelInit = true; _lastWheel = s.Wheel; }

            _viewportBuffer.Clear();
            _viewportBuffer.AddRange(_viewportRegistry.Viewports);

            var inViewport = ViewportMath.FindViewportAt(_viewportBuffer, s.MousePosition);
            if (inViewport.HasValue && _dragAxis < 0)
                _gizmoViewportId = inViewport.Value.Id;

            // Mode switching
            bool hasKeyT = s.PressedKeys?.Contains(KeyT) ?? false;
            bool hasKeyR = s.PressedKeys?.Contains(KeyR) ?? false;
            bool hasKeyS = s.PressedKeys?.Contains(KeyS) ?? false;

            if (hasKeyT && !_prevT) SetGizmoMode(GizmoMode.Translate);
            else if (hasKeyR && !_prevR) SetGizmoMode(GizmoMode.Rotate);
            else if (hasKeyS && !_prevS) SetGizmoMode(GizmoMode.Scale);

            _prevT = hasKeyT;
            _prevR = hasKeyR;
            _prevS = hasKeyS;

            // Orbit
            if (s.RightDown && !_orbiting && inViewport.HasValue)
            {
                _orbiting = true;
                _prevMouse = s.MousePosition;
            }
            else if (!s.RightDown)
            {
                _orbiting = false;
            }

            if (_orbiting)
            {
                float dx = s.MousePosition.X - _prevMouse.X;
                float dy = s.MousePosition.Y - _prevMouse.Y;
                _prevMouse = s.MousePosition;

                if (dx != 0 || dy != 0)
                {
                    _yawDeg += dx * DegreesPerPixel;
                    _pitchDeg = Math.Clamp(_pitchDeg + dy * DegreesPerPixel, MinPitchDeg, MaxPitchDeg);
                    _cameraWorldDirty = true;
                }
            }

            // Wheel
            int wheelDelta = s.Wheel - _lastWheel;
            _lastWheel = s.Wheel;
            if (wheelDelta != 0 && (inViewport.HasValue || _orbiting))
            {
                _dist = Math.Clamp(_dist * MathF.Pow(1.1f, -(wheelDelta / 120f)), MinDist, MaxDist);
                _cameraWorldDirty = true;
            }

            // Left button
            bool leftDown = s.LeftDown;
            bool leftPressed = leftDown && !_prevLeftDown;

            if (!leftDown && _dragAxis >= 0)
            {
                _dragAxis = -1;
                if (_gizmoTarget != null) ReleaseDirectTransform(_gizmoTarget);
            }

            _prevLeftDown = leftDown;

            if (leftPressed && inViewport.HasValue && !_orbiting)
            {
                if (_gizmoTarget != null && TryGrabAxis(inViewport.Value.Id, s.MousePosition))
                {
                    // Gizmo hit
                }
                else
                {
                    var el = Pick(inViewport.Value.Id, s.MousePosition);
                    _selectHandler?.Invoke(el);
                    SetGizmoTarget(el);
                }
            }

            if (leftDown && _dragAxis >= 0)
                UpdateDrag(s.MousePosition);
        }

        // ── Orbit ──────────────────────────────────────────────────────

        private void ApplyToCamera(bool commitToWorld)
        {
            if (_editorCam == null) return;

            float cp = _pitchDeg * MathF.PI / 180f;
            float cy = _yawDeg * MathF.PI / 180f;
            var dir = new Vector3(MathF.Cos(cp) * MathF.Cos(cy), MathF.Sin(cp), MathF.Cos(cp) * MathF.Sin(cy));
            var pos = _focus - dir * _dist;

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
            var localTransform = new TRS
            {
                Position = pos,
                Rotation = q,
                Scale = Vector3.One
            };

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

            QueueDirectTransform(_editorCam, localMatrix);
        }

        private void QueueDirectTransform(IWorldElement element, Matrix4x4? localTransform = null)
        {
            if (_transformProvider == null) return;

            _directlyDrivenElements.Add(element);
            _transformProvider.SetElementTransformDirect(
                element, localTransform ?? ElementPlacement.ElementLocalMatrix(element));
        }

        private void ReleaseDirectTransform(IWorldElement element)
        {
            if (!_directlyDrivenElements.Remove(element)) return;
            _transformProvider?.ReleaseElementTransformDirect(element);
        }

        // ── Picking ────────────────────────────────────────────────────

        private IWorldElement? Pick(long vpId, Vector2 mouse)
        {
            var vp = FindViewport(vpId);
            if (!vp.HasValue) return null;

            if (!ViewportMath.TryUnprojectRay(vp.Value, mouse, out var ro, out var rd))
                return null;

            // TODO: Use engine's scene raycast when available
            // For now, fall back to original mesh-based picking
            return null;
        }

        // ── Gizmo ──────────────────────────────────────────────────────

        private bool TryGetGizmoOriginAndSize(long viewportId, out Vector3 origin, out float size)
        {
            origin = Vector3.Zero;
            size = 0f;

            if (_gizmoTarget == null || _editorCam == null) return false;

            if (viewportId == 0)
                viewportId = FindFirstViewportId();

            if (viewportId == 0) return false;

            var w = _gizmoTarget.WorldTransform;
            origin = new Vector3(w.M41, w.M42, w.M43);

            var vp = FindViewport(viewportId);
            if (!vp.HasValue) return false;

            return ViewportMath.TryGetScreenSize(vp.Value, origin, TransformGizmo.ScreenFraction, out size);
        }

        private bool TryGrabAxis(long vpId, Vector2 mouse)
        {
            if (_gizmoTarget == null) return false;

            var vp = FindViewport(vpId);
            if (!vp.HasValue) return false;

            if (!ViewportMath.TryUnprojectRay(vp.Value, mouse, out var ro, out var rd)) return false;
            if (rd.LengthSquared() < 1e-12f) return false;
            rd = Vector3.Normalize(rd);

            if (!TryGetGizmoOriginAndSize(vpId, out var origin, out var size)) return false;

            TransformGizmo.BuildHandleSpecs(_mode, origin, size, _specScratch);
            if (!TransformGizmo.TryHitHandles(ro, rd, _specScratch, out int bestHandle, out _))
                return false;

            var w = _gizmoTarget.WorldTransform;
            _dragAxis = bestHandle;
            _dragViewportId = vpId;
            _dragStartPos = origin;
            _dragStartWorld = w;

            if (!ViewportMath.TryUnprojectRay(vp.Value, mouse, out var rayOrigin, out var rayDir)) return false;

            if (_mode == GizmoMode.Rotate)
            {
                _dragStartHit = RayAxisPlaneHit(origin, TransformGizmo.AxisDirs[bestHandle], rayOrigin, rayDir);
            }
            else
            {
                var vp2 = FindViewport(vpId);
                if (vp2.HasValue)
                {
                    var invView = Matrix4x4.Invert(vp2.Value.View, out var inv) ? inv : Matrix4x4.Identity;
                    var camFwd = CameraForward(invView);
                    _dragStartHit = RayAxisHit(rayOrigin, rayDir, origin, camFwd);
                    _dragRefLen = MathF.Max(1e-3f, size);
                }
            }

            if (_mode == GizmoMode.Scale)
            {
                var sc = _gizmoTarget.GetComponent<ScaleComponent>();
                _dragStartScale = sc != null
                    ? new Vector3(sc.ScaleX, sc.ScaleY, sc.ScaleZ)
                    : Vector3.One;
            }

            return true;
        }

        private void UpdateDrag(Vector2 mouse)
        {
            if (_dragAxis < 0 || _gizmoTarget == null) return;

            var vp = FindViewport(_dragViewportId);
            if (!vp.HasValue) return;

            if (!ViewportMath.TryUnprojectRay(vp.Value, mouse, out var ro, out var rd)) return;

            var invView = Matrix4x4.Invert(vp.Value.View, out var inv) ? inv : Matrix4x4.Identity;
            var camFwd = CameraForward(invView);

            if (_mode == GizmoMode.Scale && _dragAxis == TransformGizmo.CenterHandle)
            {
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

        private static Vector3 RayAxisHit(Vector3 origin, Vector3 dir, Vector3 point, Vector3 fwd)
        {
            float denom = Vector3.Dot(dir, fwd);
            if (MathF.Abs(denom) < 1e-6f) return point;
            float t = Vector3.Dot(point - origin, fwd) / denom;
            return origin + dir * t;
        }

        private static Vector3 RayAxisPlaneHit(Vector3 point, Vector3 axis, Vector3 origin, Vector3 dir)
        {
            float denom = Vector3.Dot(dir, axis);
            if (MathF.Abs(denom) < 1e-6f) return point;
            float t = Vector3.Dot(point - origin, axis) / denom;
            return origin + dir * t;
        }

        private static Vector3 CameraForward(Matrix4x4 invView)
        {
            var fwd = new Vector3(-invView.M31, -invView.M32, -invView.M33);
            return fwd.LengthSquared() < 1e-12f ? Vector3.UnitZ : Vector3.Normalize(fwd);
        }

        private ViewportInfo? FindViewport(long vpId)
        {
            foreach (var vp in _viewportRegistry.Viewports)
                if (vp.Id == vpId) return vp;
            return null;
        }

        private long FindFirstViewportId()
        {
            var vps = _viewportRegistry.Viewports;
            return vps.Count > 0 ? vps[0].Id : 0;
        }
    }
}
