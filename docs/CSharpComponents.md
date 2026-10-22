# C# Component Scripting in V12

This document describes the runtime C# component system implemented in the engine and how it is used by the studio.

## Overview

V12 now compiles user-authored `.cs` files into real CLR assemblies at runtime (or at load time) and loads them as collectible `AssemblyLoadContext`s. Components written this way are plain classes implementing `IComponent` (typically deriving from `ScriptableComponent`/`ComponentBase`) and behave exactly like built-in components: the world calls `Update(float)`, they serialize like other components, they can be attached via the API/UI, and they can be referenced from WorldML and from Contract scripts.

Key pieces:
- **Compiler**: `V12.Bindings.CSharpScriptCompiler` uses Roslyn (4.11.0, only referenced by `V12.Bindings`) to compile sources into a DLL. It injects a small set of global usings, includes all currently loaded assemblies plus `V12LinkedAssemblies` as references, and stores the PE image for in-memory referencing.
- **Load context**: `V12.Bindings.CSharpScriptLoadContext` is a collectible `AssemblyLoadContext` that resolves shared assemblies from the default context or `V12LinkedAssemblies` to maintain type identity with the engine.
- **Registry**: `V12.Bindings.CSharpComponentRegistry` discovers concrete `IComponent` types with parameterless constructors, tracks them by simple/qualified/alias names, and creates/attaches instances.
- **Host (hot reload)**: `V12.Bindings.CSharpScriptHost` compiles a directory, swaps assemblies on success, removes old registrations, and supports debounced watch/pump on the game thread.
- **Paks**: `V12.Pak.V12PakLoader` loads dependencies recursively, compiles `scripts/*.cs` (or manifest `CSharpScripts`), registers types before worlds load, and links assemblies into `V12LinkedAssemblies`. `V12.Pak.V12PakBuilder` scans for `scripts/*.cs` and `dependencies/*.v12pak` and embeds dependency paks.
- **WorldML**: `WorldMLParser` now sets both properties and public fields, so C# fields are serialized/restored from world files.
- **Studio**: `StudioSession.CSharp`, `CSharpComponentService`, `ComponentCatalog`, `ComponentCreator`, and `NewComponentForm` provide create/attach/compile/reload and menu integration.

## Quick start (in the Studio)

1. Open/create a project (the project folder is used for resolution).
2. Create a component: **Add Component → New Script Component…** → choose language **C#** → name it (e.g. `SpinComponent`). It will be created under `<project>/scripts/` and compiled + attached immediately.
3. Edit the file in your editor; to pick up changes, re-open the add menu or trigger a compile (the studio compiles on demand). Failed compiles keep the previous assembly live and report errors in the console.
4. Use it from WorldML like any other component: `<Component type="SpinComponent" Ticks="0" />`.

## Writing a C# component

Derive from `ScriptableComponent` (recommended) or `ComponentBase`. Use `[ComponentEntry("CATEGORY", "Display Name", SortOrder=...)]` to control menu placement. Public fields and public writable properties are settable from WorldML/inspectors.

```csharp
using V12.Components;

[ComponentEntry("GAMEPLAY", "Spin", SortOrder = 1)]
public sealed class SpinComponent : ScriptableComponent
{
    public float Speed = 1.0f;
    public Vector3 Axis = Vector3.UnitY;

    public override void Update(float deltaTime)
    {
        var t = Owner?.GetOrAddTransform();
        if (t == null) return;
        t.Rotation *= Quaternion.CreateFromAxisAngle(Axis, Speed * deltaTime);
    }
}
```

Notes:
- The studio skeleton includes the attribute and common usings (global usings are also injected by the compiler).
- Components must have a public parameterless constructor to be registered.
- Types from the engine, `V12.Basic`, and any loaded pak DLL/C# assemblies are available.

## Pak integration

### Manifest

`V12PakManifest` now includes:
```json
{
  "Dependencies": ["dependencies/Base.v12pak"],
  "CSharpScripts": ["scripts/SharedComponent.cs"]
}
```

### Building a pak

`V12PakBuilder.Build` scans:
- `scripts/*.ct`, `*.orbt`, `*.oil` → `Scripts`
- `scripts/*.cs` → `CSharpScripts`
- `dependencies/*.v12pak` → `Dependencies` (also embedded into the archive)

### Loading a pak

`V12PakLoader.Load` with `V12PakOptions`:
- `LoadDependencies` (default true) — load dependencies recursively with cycle detection; searches pak dir, `DependencySearchPaths`, cwd, and can resolve paks embedded in the parent archive
- `LoadDlls` — load `paks/*.dll`, link into `V12LinkedAssemblies`, register `IComponent` types
- `LoadCSharpScripts` — compile `CSharpScripts` (fallback: all `scripts/*.cs`) in memory, load via `CSharpScriptLoadContext`, register types, link and cache PE image
- Component types are registered **before** worlds are parsed so worlds can reference them
- The loader tracks loaded paks in `GameRoot.Registry["V12LoadedPaks"]` to avoid reloading

### Cross-assembly references

Runtime-compiled assemblies have no file location; `CSharpScriptCompiler` references their PE images via `CSharpScriptImages`. `CSharpScriptLoadContext` resolves shared framework/engine assemblies to the default context to preserve type identity. `V12LinkedAssemblies` is updated for Contract script linking.

## Runtime API

- `CSharpScriptCompiler.CompileDirectory(sourceDir, assemblyName, ...)` / `CompileFiles(...)` → `CSharpCompileResult` (`Success`, `AssemblyPath`, `Image`, `Errors`, `Warnings`)
- `CSharpScriptLoadContext(name)` → collectible ALC with `LoadFrom(path)` (stream-based) and shared-assembly resolution
- `CSharpComponentRegistry.GetOrCreate()` / `RegisterAssembly` / `RegisterType` / `RemoveSource` / `TryGet` / `Create` / `Attach`
- `CSharpScriptHost(sourceDir, assemblyName, outputPath)` → `Reload()`, `Watch()`, `Pump()`; swaps assemblies on success
- `CSharpComponentRegistry` service key: `"CSharpComponents"`

## Studio API

- `StudioSession.CSharp` (`CSharpComponentService`) — `Ensure()`, `Reload()`, `Attach(element, typeName, out message)`, `Entries`
- `ComponentCatalog.DiscoverCSharpComponents()` — returns catalogue entries for runtime-compiled types (tagged `(C#)`, placed in their `[ComponentEntry]` category)
- `ComponentCreator` — creates C# files under `scripts/`, compiles + attaches; reports compiler errors on failure; enforces single-instance rules
- `PackService` enables `LoadCSharpScripts = true` and reports dependency/C# counts

## Limitations / notes

- C# components are compiled in-memory/debug with `Nullable enable`, `LangVersion Latest`. They must have a public parameterless constructor.
- The studio compiles on demand (no file watching by default). Hot reload is supported via `CSharpScriptHost.Watch()`/`Pump()` for custom hosts.
- Roslyn is only referenced by `V12.Bindings` to keep the engine/core lean.
- Assemblies loaded into collectible ALCs are not always visible via `AppDomain.GetAssemblies()` — the compiler/loader use `V12LinkedAssemblies` and `CSharpScriptImages` to bridge references.
- If a compile fails, the previous assembly/registrations remain active.
