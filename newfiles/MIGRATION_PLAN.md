# V12 Viewport Separation Migration Plan

## Goal
Extract editor-specific viewport logic from V12.Core into a new V12.Studio assembly. V12.Core becomes renderer-agnostic; V12.Studio is a game built on V12 that happens to be an editor.

## New Engine Interfaces (in V12.Core)

These replace editor-specific code and are usable by any game:

1. **IViewportRegistry** (V12.Core.Rendering)
   - One viewport per backend view (game: 1, editor: many)
   - Immutable per-frame snapshot: rect + camera matrices
   - Replaces `IViewportInteractionHost` and `IViewportGizmoMetrics`

2. **ViewportMath** (V12.Core.Rendering, static class)
   - `TryUnprojectRay()` — ray through a screen point
   - `TryProjectToScreen()` — world point to screen
   - `TryGetScreenSize()` — constant-screen-size sizing
   - `FindViewportAt()` — hit-test viewport under cursor
   - Pure math, no editor knowledge

3. **IInputSource** (V12.Core.Input)
   - Generic per-frame input: mouse, buttons, wheel, key set
   - No hardcoded T/R/S — just raw state
   - Replaces `IEditorInputSource` + hardcoded `EditorInputSnapshot`

4. **IDirectTransformProvider** (V12.Core.Rendering)
   - Optional fast-path for interactive drags/orbits
   - Any game wanting low-latency transforms can use it
   - Replaces `IEditorDrivenTransformSink` (renamed, generalized)

## Code Moving to V12.Studio

Create new assembly: `V12.Studio.Core` → new folder `V12Studio` alongside other Nova libs.

**Move these files:**
- ViewportInteractionService.cs
- TransformGizmo.cs → can stay in core if used by both, but keep it editor-focused
- GizmoEntitySync.cs
- GizmoMode.cs → editor concept, move it
- GizmoHandleComponent.cs (when found)
- ViewportFileDialog.cs
- DefaultDashboard.cs
- InspectorService.cs + InspectorBuilder.cs + InspectorState.cs
- V12ElementInspector.cs

**Contracts staying in core:**
- UI widgets (IButton, ICheckbox, etc.) — these are UI primitives, not editor-specific
- UIBuilder, UITheme, UIProvider — generic UI infrastructure
- GizmoMode → actually, this is editor terminology; move it
- ViewportFileDialog → editor-specific, move it

## Breaking Changes in Core

1. `IViewportInteractionHost` → delete
2. `IEditorInputSource` + `EditorInputSnapshot` → replace with `IInputSource` + `InputSnapshot`
3. `IEditorDrivenTransformSink` → rename to `IDirectTransformProvider` (generalize)
4. `IViewportGizmoMetrics` → delete (fold `TryGetGizmoSize` into `ViewportMath`)
5. Delete hardcoded hotkeys from core (T/R/S become Studio keybindings)
6. `EditorTransientComponent` → `NonPersistentComponent` (engine-level marker)

## Godot Host Changes

**GodotViewportInteractionHost** shrinks to just:

```csharp
public class GodotViewportAdapter : IViewportRegistry, IInputSource, IDirectTransformProvider
{
    // Per-frame: fill IViewportRegistry.Viewports from Godot's SubViewports
    // Per-frame: fill IInputSource snapshot from Godot's Input
    // On demand: apply IDirectTransformProvider transforms to nodes
}
```

No more:
- Mesh readback (engine does raycasts now)
- RID tracking / array cache
- `_pickMembershipDirty` / `RebuildPickMembership()`
- Matrix conversion (already in new code)
- The socket between gizmo and renderer

## Engine Raycast (for future work)

Once the engine has scene queries, the Godot host stops extracting mesh data:

```csharp
// In ViewportInteractionService (moved to Studio):
public IWorldElement? Pick(long vpId, Vector2 mouse)
{
    if (!viewport.TryUnprojectRay(vpId, mouse, out var ro, out var rd)) return null;
    
    // Use engine's scene query, not Godot's mesh arrays
    var world = _root.GetWorldForElement(target);
    var hit = world.Raycast(ro, rd, exclude: _gizmo);
    return hit?.Element;
}
```

## Migration Steps

1. ✅ Create new engine interfaces (IViewportRegistry, ViewportMath, IInputSource, IDirectTransformProvider)
2. Create V12.Studio.Core assembly
3. Move Studio-only code to V12.Studio.Core
4. Update ViewportInteractionService to use new engine interfaces
5. Shrink GodotViewportInteractionHost to just registry + input + transform adapter
6. Fix all compile errors (should be ~20-30 signature changes)
7. Delete old interfaces from core
8. Test (editor pans, orbits, picks, drags gizmo)

## Files Created This Session

- IViewportRegistry.cs
- ViewportMath.cs
- IInputSource.cs
- IDirectTransformProvider.cs

Next: Create V12.Studio.Core project and move code.
