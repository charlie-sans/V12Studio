# V12 Studio — Editor UI Design Specification

**Status:** Design direction / implementation brief  
**Primary source:** `V12 Studio Layout.pdf` (3-page concept)

## 1. Purpose and design intent

V12 Studio should present a coherent workspace for inspecting and editing a V12 world. The proposed redesign uses a familiar desktop-editor arrangement: Hierarchy on the left, a large world viewport in the centre, contextual Inspector on the right, and Project / Console / Packs tools along the bottom.

The goal is not to copy another engine feature-for-feature. The layout uses familiar editor conventions while representing V12-specific concepts such as packs, Contract, C#, ObjektRT, and selectable viewport renderers.

## 2. Design principles

- **Clear spatial roles:** each major area has a predictable purpose.
- **Viewport first:** the world is the primary work surface.
- **Contextual inspection:** the Inspector follows the selected element.
- **Discoverable actions:** common operations are available through controls, context menus, and documented shortcuts.
- **V12-native concepts:** assets, packs, component sources, and renderer selection are visible.
- **Progressive implementation:** build the shell first, then connect panels to live world data.

## 3. Main workspace

### Top menu and toolbar

Menu items shown: `File`, `Edit`, `Assets`, `Element`, `Component`, `World`, `Build`, `Window`, `Help`.

Toolbar concepts shown:
- Local / Global transform mode.
- Snap control with visible step (example: `0.5`).
- Play control.
- Viewport renderer selector (example: `Nova`).
- Layout selector (example: `Default`).
- Project context (example: `EcoVR`).

Exact menu contents and behavior are not fully specified by the PDF.

### Left: Hierarchy

The Hierarchy presents a world as a nested tree. The example root is `Gridspace`, with groups such as `Lighting`, `Level`, and `Props`, and elements including `Sun`, `Environment`, `Ground`, `RedBox`, `BlueSphere`, `GreenCapsule`, `PlayerSpawn`, and `MainCamera`.

Interactions shown:
- Select an element to inspect it.
- Search/filter, plus/add affordance.
- Right-click actions: Rename (`F2`), Duplicate (`Ctrl+D`), Delete (`Del`), Create Empty Child, Group Selection (`Ctrl+G`), Frame in Viewport (`F`), Copy Path.
- Drag an element to reparent it.
- Search can show parents dimmed for context.

Undo/redo, multi-selection, validation, and exact filtering behavior are not specified.

### Centre: Viewport

The reference shows `Scene` and `Game` modes/tabs, plus `Perspective`, `Shaded`, and `Gizmos` controls.

Documented hints:
- RMB orbit; MMB pan; mouse wheel zoom.
- `F` frame selection.
- `T / R / S` select transform gizmo mode.
- Drag to reparent; `F2` rename; `Ctrl+D` duplicate.
- Local / Global and Snap `0.5`.

Camera behavior, gizmo math, snapping rules, selection visuals, and renderer-specific capabilities require implementation decisions.

### Right: Inspector

The Inspector reflects the selected element. The example `Sun` has Tag and Layer fields, Transform, and Directional Light properties.

- Transform: Position, Rotation, Scale, each with X/Y/Z values.
- Directional Light example: Type, Color, Energy, Shadows.
- Add Component entry point and component editing area.

Choose one consistent presentation for transform values and editable fields; avoid confusing duplication.

### Bottom: Project / Console / Packs

The bottom workspace has tabs for `Project`, `Console` (example count `2`), and `Packs`.

The Project browser shows folders for scenes, models, textures, audio, scripts, and packs. Example assets:
- `Gridspace.xml`, `Gridspace_night.xml` — scenes
- `ecovr.v12pak` — pack
- `crate.glb` — model
- `ground.png` — texture
- `Boss.contract` — Contract source
- `Health.cs` — C# source
- `Grabbable.orbt` — ObjektRT asset
- `click.wav` — audio

These are illustrative examples, not a required naming scheme. Asset drag/drop behavior is not specified.

### Status strip

The example status fields are `Ready`, selected element (`Sun selected`), element count (`11 elements`), renderer (`Nova`), and frame rate (`144 fps`). Treat these as examples; only display values that are available and meaningful.

## 4. New Component workflow

The second page proposes a component catalogue and a **New Component** dialog.

### Catalogue groups shown

| Category | Examples |
|---|---|
| Rendering | Mesh, Mesh Renderer, Material, Camera |
| Lighting | Directional Light, Point Light, Spot Light |
| Physics | Collider, Rigid Body |
| Gameplay | Tag, Health, Locomotion |
| Your Components | BossArena (Contract), Grabbable (ObjektRT), SpinForever (C#) |

### Dialog fields shown

- Name
- Language: Contract, C#, ObjektRT
- Pack (example: `ecovr.v12pak`)
- Creates path (example: `scripts/BossArena.contract`)
- Fields table with Name, Type, Default; examples include `float`, `int`, `asset`
- Add field
- Target element (example: `BlueSphere`)
- Cancel / Create & Attach

Validation, supported field types, default-value syntax, duplicate names, compilation, and packaging behavior are not specified. The UI must not promise capabilities the current V12 SDK/runtime cannot complete.

## 5. Core user journeys

### Inspect an element
1. Select an element in Hierarchy.
2. Show identity, Transform, and available components in Inspector.
3. Edit a supported property and reflect the result in the world.

### Reorganize the world tree
1. Drag an element to a valid parent.
2. Use context actions for create child, group, rename, duplicate, delete, frame, or copy path.
3. Keep hierarchy, selection, and Inspector state consistent.

### Create and attach a component
1. Open Add Component / New Component.
2. Choose a catalogue entry or custom component workflow.
3. Enter name, language, pack, and fields as applicable.
4. Confirm target and choose Create & Attach.
5. On success, show the component in Inspector; on failure, explain the error and preserve input.

### Find an asset
1. Browse folders in Project.
2. Identify asset type from its label/icon and name.
3. Expose relevant actions. Exact import/open/drag behavior remains to be designed.

## 6. Visual direction

The concept suggests a restrained dark desktop-tool aesthetic, compact spacing, subtle panel boundaries, and a clear viewport-first hierarchy. Prioritize layout, alignment, spacing, and consistent controls over decoration.

- Use a consistent spacing scale.
- Align panel headers and content edges.
- Make selection and active-tool states clear but not overpowering.
- Keep the toolbar focused on common actions.
- Support long names, large trees/lists, scrolling, resizing, and collapsed panels.
- Define narrow-window behavior and keyboard focus states.

The PDF does not specify exact colors, fonts, spacing tokens, icons, minimum size, or responsive breakpoints.

## 7. Suggested implementation decomposition

This is implementation guidance inferred from the concept, not a claim that these modules already exist.

1. **Editor shell:** menu, toolbar, panel layout, status strip, saved layouts.
2. **Hierarchy:** tree, filtering, selection, context menus, rename, reparent.
3. **Viewport host:** renderer surface, Scene/Game modes, viewport controls, gizmo state.
4. **Inspector:** selected-element context, Transform editor, component views, Add Component.
5. **Project:** folders, asset listing, type labels, selection.
6. **Console:** messages, severity/filtering, navigation to diagnostics.
7. **Packs:** pack visibility and pack-related actions.
8. **Component dialog:** catalogue, language, fields, target, validation.
9. **Editor state/services:** shared selection, active world/renderer, commands, error reporting.

## 8. Suggested implementation sequence

1. Layout shell with placeholder content.
2. Selection loop: Hierarchy selection drives Inspector; handle no selection.
3. Transform inspection/editing connected to world state.
4. Hierarchy operations and shortcuts.
5. Project browser and asset-type presentation.
6. Component catalogue and creation dialog, limited to supported capabilities.
7. Polish: keyboard navigation, resizing, panel persistence, empty/error/loading states, usability tests.

## 9. Acceptance criteria

- Hierarchy, Viewport, Inspector, and bottom tools are visually distinct.
- Viewport is the largest region at typical desktop dimensions.
- Selecting an element updates Inspector; no selection has a deliberate empty state.
- Transform fields use a consistent X/Y/Z layout and show trustworthy values.
- Context actions and shortcuts match implemented behavior.
- Reparenting updates the tree without stale selection or Inspector data.
- Assets are identifiable by name and type; long lists remain usable.
- New Component makes target, language, pack, fields, and final action clear.
- Failures are reported without silently discarding user input.
- Resizing, long content, and no-world/no-selection states remain usable.

## 10. Decisions still required

- UI framework and docking/panel strategy.
- Panel resizing, visibility, and layout persistence.
- Exact meaning of Scene vs Game and renderer feature support.
- Undo/redo and command model.
- Selection, multi-selection, drag/drop, and parent validation.
- Component and field types currently supported by runtime/SDK.
- Whether C#, Contract, and ObjektRT need language-specific creation steps.
- Pack creation, selection, loading, and update workflows.
- First-release responsibilities of Console and Packs.
- Visual tokens: colors, fonts, spacing, icons, focus states, minimum window size.

## 11. Prompt for an AI design assistant

> Design a desktop editor UI for V12 Studio using the attached V12 Studio Layout concept as the primary source. Preserve its main structure: top menu and toolbar; left Hierarchy tree; large central Scene/Game viewport; right contextual Inspector; bottom Project, Console, and Packs tabs. Include the documented component creation dialog and hierarchy context-menu interactions. This is an editor for V12, so represent its asset and component ecosystem directly, including packs and the Contract, C#, and ObjektRT source formats shown in the concept. Use a restrained dark desktop-tool aesthetic, strong alignment, compact but readable controls, and a viewport-first layout. Do not invent engine capabilities as facts. Mark unspecified interactions and visual tokens as proposals. Produce a coherent screen design, component breakdown, interaction states, and a prioritized implementation plan. Distinguish requirements present in the source concept from new suggestions.

## 12. Source and interpretation notes

Primary source: `V12 Studio Layout.pdf`, three-page concept. The main workspace and sample entities/assets are described from page 1; the New Component dialog and catalogue from page 2; hierarchy context-menu, drag-to-reparent, and search-filter concepts from page 3. Implementation guidance, acceptance criteria, and open decisions are recommendations added to make the concept usable as a design brief; they are not claims that those behaviors are already implemented.
