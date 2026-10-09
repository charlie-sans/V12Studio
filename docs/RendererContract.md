# V12 Renderer Contract

How a renderer backend (MonoGame, Godot/nova, anything else) plugs into V12.
One rule governs everything: **V12 determines placement; renderers mirror the
tree and set locations.** No renderer computes placement from components.

## Feeds

Two feeds carry the same data in different shapes. Either is sufficient.

- **Packet** (`RenderPacket`, dirty-driven): `Meshes` (`ElementId`, mesh,
  viewport) + `Nodes` (every element: id, parent id, element-local matrix).
- **Snapshot** (`FrameSnapshot`, compared per field): entries carry
  `ElementId`, `ParentId`, `LocalTransform`, world `Transform`, mesh
  points/indices/dims, material fields. Unchanged entries are skipped.

## Placement

- Element-local matrix comes from one definition
  (`ElementPlacement.ElementLocalMatrix`): the `TransformComponent` when
  present, else `LocalTransform × ScaleComponent`.
- World matrices compose inner-to-outer, row-vector order:
  `world = local × parentWorld` (`Element.WorldTransform` is the authority).
- **Meshes carry no transforms.** A mesh's world is its element's world;
  dims live in the vertices (or the backend's mesh resource for
  `MeshComponent` shapes). There is no mesh-local matrix anywhere.
- Mirror the parent/child tree from V12 data (ids + locals), exactly like the
  Godot backend parents nodes by `ParentId`. Missing parent → identity
  fallback, never a crash. Reparent when links change.

## Mesh data

- Vertices are absolute (dims baked in). New array object = new geometry —
  never mutate in place; backends key GPU caches on array identity.
- Indices must be in range; malformed data is skipped, not crashed on.
- `MeshComponent` shapes may additionally map to backend-native primitives
  (sized from dims); everything else renders as a triangle soup.

## Materials

Read the owner's `MaterialComponent`: flat albedo color, plus `Unlit`
(skip scene lighting) and `NoDepthTest` (draw on top) flags. No material →
backend default. No other component affects appearance.

## Transient editor objects

Elements under an `EditorTransientComponent` marker (e.g. the gizmo tree):
render and pick normally, but are **excluded** from saves, network sync,
hierarchy panels, and scene selection. The check covers descendants.

## Picking

- Pick against the same matrices you draw with, or picking and pixels diverge.
- Gizmo handles are grabbable, never selectable: V12 hit-tests them
  analytically (`TransformGizmo.TryHitHandles`, fat invisible shapes — never
  the visual mesh) before world picking, and world picking skips handle parts.

## Viewports

- Meshes carry a viewport id; groups render per viewport (0 = main screen).
  Hierarchy never crosses a viewport boundary.
- There is no overlay draw path. Editor visuals are transient meshes through
  the normal pipeline (unlit, no-depth).

## Per-backend notes

- **MonoGame:** rebuild the id→world mirror on every packet; one shared
  effect, per-mesh lighting/depth/material state; GPU cache keyed on array
  identity + color.
- **Godot:** one node per entry (`MeshInstance3D`, lights, cameras…),
  reparented by `ParentId` with local transforms; `ArrayMesh` rebuilt when
  the points/indices references change; snapshots interpolate unless the node
  is editor-driven (gizmo drags, orbit camera write transforms directly).
