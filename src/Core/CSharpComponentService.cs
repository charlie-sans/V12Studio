using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using V12.Bindings;
using V12.Core.Core.Interfaces;

namespace V12Studio.Core
{
    /// <summary>
    /// Compiles the project's C# component sources (<c>&lt;project&gt;/scripts</c>)
    /// into a live assembly and attaches the resulting components.
    /// <para>
    /// This is the studio side of the runtime C# path: <see cref="CSharpScriptHost"/>
    /// does the Roslyn compile and the collectible load,
    /// <see cref="CSharpComponentRegistry"/> owns the discovered types. Compiling is
    /// on demand (project open, New Component, "Your Components" attach) rather
    /// than watched, so an in-progress edit never swaps code under the scene.
    /// </para>
    /// </summary>
    public sealed class CSharpComponentService : IDisposable
    {
        public const string AssemblyName = "V12ProjectScripts";

        private readonly StudioSession _session;
        private CSharpScriptHost? _host;
        private string? _directory;

        public CSharpComponentService(StudioSession session)
        {
            _session = session;
        }

        /// <summary>Absolute directory being compiled (<c>&lt;project&gt;/scripts</c>).</summary>
        public string? SourceDirectory => _directory;

        /// <summary>Result of the most recent compile, or null when none ran yet.</summary>
        public CSharpCompileResult? LastResult { get; private set; }

        /// <summary>True when the source directory exists and holds at least one <c>.cs</c> file.</summary>
        public bool HasSources => _host?.HasSources() == true;

        /// <summary>Compiled component types available to the editor, sorted for menus.</summary>
        public IReadOnlyList<CSharpComponentRegistry.TypeEntry> Entries
        {
            get
            {
                var registry = CSharpComponentRegistry.FromGameRoot();
                if (registry == null) return Array.Empty<CSharpComponentRegistry.TypeEntry>();
                return registry.Entries
                    .OrderBy(e => e.SortOrder)
                    .ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        /// <summary>Compiles once per project directory; returns null when there is nothing to compile.</summary>
        public CSharpCompileResult? Ensure()
        {
            var host = Host();
            if (host == null || !host.HasSources()) return null;
            if (host.Assembly != null) return LastResult;
            return Reload();
        }

        /// <summary>Forces a recompile of the project's C# sources.</summary>
        public CSharpCompileResult? Reload()
        {
            var host = Host();
            if (host == null) return null;

            // Ensure engine assemblies are discoverable to the compiler
            try { _ = typeof(V12.Components.ComponentBase).Assembly; } catch { }
            try { _ = typeof(V12.Core.GameRoot).Assembly; } catch { }
            try { _ = typeof(V12.Bindings.V12LinkedAssemblies).Assembly; } catch { }

            var result = host.Reload();
            LastResult = result;
            return result;
        }

        /// <summary>Drops the cached host so the next <see cref="Ensure"/> picks up a new project folder.</summary>
        public void Invalidate()
        {
            _host?.Dispose();
            _host = null;
            _directory = null;
            LastResult = null;
        }

        /// <summary>
        /// Compiles the project's C# sources and attaches the component type
        /// <paramref name="typeName"/>. Returns false with a user-facing message
        /// on a compile error or an unknown type.
        /// </summary>
        public bool Attach(IWorldElement element, string typeName, out string message)
        {
            message = "";
            if (element == null)
            {
                message = "Select an element in the scene first.";
                return false;
            }

            var compile = Reload();
            if (compile == null)
            {
                message = $"No C# sources found under '{_directory ?? "scripts"}'.";
                return false;
            }
            if (!compile.Success)
            {
                message = "The C# sources did not compile:\n" + compile.ErrorText;
                return false;
            }

            var registry = CSharpComponentRegistry.FromGameRoot();
            var component = registry?.Create(typeName);
            if (component == null)
            {
                message = registry == null || registry.Entries.Count == 0
                    ? $"Compiled, but no components were registered from the project scripts. " +
                      $"Check that '{typeName}' derives from ScriptableComponent."
                    : $"Compiled, but no component named '{typeName}' was found. " +
                      $"Name the class '{typeName}' and derive it from ScriptableComponent.";
                return false;
            }

            element.AddComponent(component);
            _session.NotifyHierarchyChanged();
            message = $"Attached {typeName} to '{element.Name}'.";
            return true;
        }

        /// <summary>Starts file watching so <see cref="CSharpScriptHost.Pump"/> reloads on change.</summary>
        public void StartWatching()
        {
            Host()?.Watch();
        }

        /// <summary>Call on the game thread to process pending file changes.</summary>
        public void Pump() => _host?.Pump();

        private CSharpScriptHost? Host()
        {
            string root = _session.ProjectDir ?? AppDomain.CurrentDomain.BaseDirectory;
            string directory = Path.GetFullPath(Path.Combine(root, "scripts"));

            if (_host != null && string.Equals(_directory, directory, StringComparison.OrdinalIgnoreCase))
                return _host;

            _host?.Dispose();
            _host = null;
            _directory = directory;
            LastResult = null;

            if (!Directory.Exists(directory)) return null;

            _host = new CSharpScriptHost(directory, AssemblyName);
            return _host;
        }

        public void Dispose() => Invalidate();
    }
}
