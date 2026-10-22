using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using V12.Core.Core.Interfaces;
using ScriptComponent = V12.Components.ScriptComponent;

namespace V12Studio.Core
{
    /// <summary>One editable script variable declared by a
    /// <c>-- @field</c> / <c>// @field</c> header line.</summary>
    public sealed record ScriptField(string Name, string Type, string? Default);

    /// <summary>
    /// Parses <c>-- @field &lt;type&gt; &lt;name&gt; [&lt;default&gt;]</c> header
    /// lines from script sources. Both comment styles are accepted so Contract
    /// (<c>//</c>) and Lua (<c>--</c>) scripts work with one parser.
    /// </summary>
    public static class ScriptFieldParser
    {
        public static IReadOnlyList<ScriptField> ParseFile(string path)
        {
            try { return ParseText(File.ReadAllText(path)); }
            catch { return Array.Empty<ScriptField>(); }
        }

        public static IReadOnlyList<ScriptField> ParseText(string text)
        {
            var fields = new List<ScriptField>();
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("--", StringComparison.Ordinal))
                    line = line.Substring(2).Trim();
                else if (line.StartsWith("//", StringComparison.Ordinal))
                    line = line.Substring(2).Trim();
                else
                    continue;

                if (!line.StartsWith("@field", StringComparison.Ordinal)) continue;
                var rest = line.Substring("@field".Length).Trim();
                var parts = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;

                var type = parts[0];
                var name = parts[1];
                var def = parts.Length >= 3 ? string.Join(' ', parts.Skip(2)) : null;
                fields.Add(new ScriptField(name, type, def));
            }
            return fields;
        }
    }

    /// <summary>Request for the New Component dialog.</summary>
    public sealed class CreateComponentRequest
    {
        /// <summary>Built-in entry to attach; null = create a custom script.</summary>
        public CatalogEntry? Builtin;

        /// <summary>Custom component name (C# identifier). Ignored for built-ins.</summary>
        public string ComponentName = "";

        /// <summary>One of <see cref="ComponentCatalog.ScriptLanguages"/>.</summary>
        public string Language = "";

        /// <summary>Relative (or absolute) path for the new script file, with or
        /// without extension — the dialog fills this from the component name.</summary>
        public string CreatesPath = "";

        /// <summary>Optional pack association (recorded in the file header only).</summary>
        public string Pack = "";

        public IWorldElement? Target;

        /// <summary>Fields the dialog's editor grid collected; written as the
        /// script's @field header.</summary>
        public List<ScriptField> Fields = new();
    }

    /// <summary>Result of a create-and-attach attempt. <paramref name="Attached"/>
    /// is true only when a component actually landed on the target: a C#
    /// skeleton writes the file but attaches nothing, so the dialog stays open
    /// to explain that (spec §5 — success shows the component, anything else
    /// explains itself and keeps the input).</summary>
    public sealed record ComponentCreateResult(bool Success, string Message, bool Attached = false);

    /// <summary>
    /// Validates and performs the New Component dialog's action: attach a
    /// built-in component, or write a script skeleton (with @field header) and
    /// attach it through a ScriptComponent. C# files are written but not
    /// attached — V12 does not compile C# at runtime and says so honestly.
    /// </summary>
    public sealed class ComponentCreator
    {
        private readonly StudioSession _session;

        public ComponentCreator(StudioSession session)
        {
            _session = session;
        }

        /// <summary>Validate a request without performing it. Returns null when
        /// valid, otherwise the user-facing error.</summary>
        public string? Validate(CreateComponentRequest req)
        {
            if (req.Target == null || !_session.IsSceneElement(req.Target))
                return "Select an element in the scene first.";

            if (req.Builtin != null)
            {
                if (req.Builtin.ScriptPath != null)
                    return ValidateExistingScript(req);
                if (req.Builtin.ComponentType == null)
                    return $"'{req.Builtin.DisplayName}' is a script entry — pick a language to create it.";
                if (req.Builtin.SingleInstance &&
                    req.Target.Components.Any(c => req.Builtin.ComponentType!.IsInstanceOfType(c)))
                    return $"'{req.Target.Name}' already has a {req.Builtin.DisplayName} component.";
                return null;
            }

            if (!ComponentCatalog.IsValidIdentifier(req.ComponentName))
                return "Component name must be a valid identifier (letters, digits, underscore; not starting with a digit).";
            if (string.IsNullOrWhiteSpace(req.Language))
                return "Choose a script language.";
            if (string.IsNullOrWhiteSpace(req.CreatesPath))
                return "Choose where the script file is created.";

            var lang = ComponentCatalog.ScriptLanguages.FirstOrDefault(l =>
                string.Equals(l.Language, req.Language, StringComparison.OrdinalIgnoreCase));
            if (lang.Language == null)
                return $"Unknown language '{req.Language}'.";
            if (string.Equals(lang.Extension, ".orbt", StringComparison.OrdinalIgnoreCase))
                return "ObjektRT components are precompiled modules and cannot be created from the editor. Use Contract or Lua.";
            if (string.Equals(lang.Extension, ".cs", StringComparison.OrdinalIgnoreCase) && !IsUnderScripts(req))
                return "C# components must be created under scripts/ — they compile together into one assembly.";

            return null;
        }

        /// <summary>True when the requested path lands in the project's scripts folder.</summary>
        private bool IsUnderScripts(CreateComponentRequest req)
        {
            try
            {
                string root = _session.ProjectDir ?? AppDomain.CurrentDomain.BaseDirectory;
                string scripts = Path.GetFullPath(Path.Combine(root, "scripts"));
                string full = _session.ResolvePath(req.CreatesPath.Trim());
                return full.StartsWith(scripts + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(full, scripts, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Validate attaching an existing discovered script file
        /// ("Your Components" catalogue entries).</summary>
        private static string? ValidateExistingScript(CreateComponentRequest req)
        {
            var entry = req.Builtin!;
            if (!File.Exists(entry.ScriptPath!))
                return $"Script file not found: {Path.GetFileName(entry.ScriptPath!)}.";

            // C# components attach as real CLR types, not through a ScriptComponent.
            if (string.Equals(Path.GetExtension(entry.ScriptPath), ".cs", StringComparison.OrdinalIgnoreCase))
            {
                string typeName = Path.GetFileNameWithoutExtension(entry.ScriptPath!);
                if (req.Target!.Components.Any(c => string.Equals(c.GetType().Name, typeName, StringComparison.OrdinalIgnoreCase)))
                    return $"'{req.Target.Name}' already has a {entry.DisplayName} component.";
                return null;
            }

            static string Norm(string p)
            {
                try { return Path.GetFullPath(p); }
                catch { return p; }
            }

            var want = Norm(entry.ScriptPath!);
            var already = req.Target!.Components
                .OfType<ScriptComponent>()
                .Any(c => !string.IsNullOrEmpty(c.Source)
                    && string.Equals(Norm(c.Source), want, StringComparison.OrdinalIgnoreCase));
            if (already)
                return $"'{req.Target.Name}' already runs {entry.DisplayName}.";
            return null;
        }

        /// <summary>Validate and perform the request.</summary>
        public ComponentCreateResult CreateAndAttach(CreateComponentRequest req)
        {
            var validation = Validate(req);
            if (validation != null)
                return new ComponentCreateResult(false, validation);

            if (req.Builtin != null)
                return req.Builtin.ScriptPath != null ? AttachScript(req) : AttachBuiltin(req);

            return CreateScript(req);
        }

        /// <summary>Attach an existing script file ("Your Components") through
        /// a ScriptComponent pointing at it.</summary>
        private ComponentCreateResult AttachScript(CreateComponentRequest req)
        {
            var entry = req.Builtin!;
            if (string.Equals(Path.GetExtension(entry.ScriptPath), ".cs", StringComparison.OrdinalIgnoreCase))
                return AttachCSharp(req, Path.GetFileNameWithoutExtension(entry.ScriptPath!), entry.ScriptPath!);

            try
            {
                var sc = new ScriptComponent { Source = entry.ScriptPath!, ScriptText = null };
                req.Target!.AddComponent(sc);
                _session.NotifyHierarchyChanged();
                var msg = $"Attached {entry.DisplayName} to '{req.Target.Name}'.";
                _session.Log.Info(msg);
                return new ComponentCreateResult(true, msg, true);
            }
            catch (Exception ex)
            {
                var msg = $"Failed to attach {entry.DisplayName}: {ex.Message}";
                _session.Log.Error(msg);
                return new ComponentCreateResult(false, msg);
            }
        }

        /// <summary>
        /// Compiles the project's C# sources and attaches
        /// <paramref name="typeName"/>. A compile error keeps the previous
        /// assembly live and reports the diagnostics instead of failing silently.
        /// </summary>
        private ComponentCreateResult AttachCSharp(CreateComponentRequest req, string typeName, string displayPath)
        {
            var target = req.Target!;
            try
            {
                if (!_session.CSharp.Attach(target, typeName, out var error))
                {
                    var failed = $"{displayPath}: {error}";
                    _session.Log.Error(failed);
                    return new ComponentCreateResult(false, failed);
                }

                var msg = $"Attached {typeName} to '{target.Name}'.";
                _session.Log.Info(msg);
                return new ComponentCreateResult(true, msg, true);
            }
            catch (Exception ex)
            {
                var msg = $"Failed to attach {typeName}: {ex.Message}";
                _session.Log.Error(msg);
                return new ComponentCreateResult(false, msg);
            }
        }

        private ComponentCreateResult AttachBuiltin(CreateComponentRequest req)
        {
            var entry = req.Builtin!;
            try
            {
                var component = entry.Factory!();
                if (component == null)
                    return new ComponentCreateResult(false, $"'{entry.DisplayName}' could not be created.");
                req.Target!.AddComponent(component);
                _session.NotifyHierarchyChanged();
                var msg = $"Added {entry.DisplayName} to '{req.Target.Name}'.";
                _session.Log.Info(msg);
                return new ComponentCreateResult(true, msg, true);
            }
            catch (Exception ex)
            {
                var msg = $"Failed to add {entry.DisplayName}: {ex.Message}";
                _session.Log.Error(msg);
                return new ComponentCreateResult(false, msg);
            }
        }

        private ComponentCreateResult CreateScript(CreateComponentRequest req)
        {
            var lang = ComponentCatalog.ScriptLanguages.First(l =>
                string.Equals(l.Language, req.Language, StringComparison.OrdinalIgnoreCase));

            var rel = req.CreatesPath.Trim().Replace('\\', '/');
            if (!Path.HasExtension(rel))
                rel += lang.Extension;
            else if (!string.Equals(Path.GetExtension(rel), lang.Extension, StringComparison.OrdinalIgnoreCase))
                rel = Path.ChangeExtension(rel, lang.Extension);

            var full = _session.ResolvePath(rel); // project folder: scripts/ is where discovery scans

            if (File.Exists(full))
            {
                var msg = $"'{rel}' already exists.";
                _session.Log.Warn(msg);
                return new ComponentCreateResult(false, msg);
            }

            try
            {
                var dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var isCSharp = string.Equals(lang.Extension, ".cs", StringComparison.OrdinalIgnoreCase);
                var source = BuildSkeleton(req, lang.Language, lang.Extension, isCSharp);
                File.WriteAllText(full, source, new UTF8Encoding(false));

                if (isCSharp)
                    return AttachCSharp(req, req.ComponentName, rel);

                var sc = new ScriptComponent { Source = full, ScriptText = null };
                req.Target!.AddComponent(sc);
                _session.NotifyHierarchyChanged();
                var ok = $"Created {rel} and attached it to '{req.Target.Name}'.";
                _session.Log.Info(ok);
                return new ComponentCreateResult(true, ok, true);
            }
            catch (Exception ex)
            {
                var msg = $"Failed to create {rel}: {ex.Message}";
                _session.Log.Error(msg);
                return new ComponentCreateResult(false, msg);
            }
        }

        /// <summary>Map a dialog field type to a C# field type; null skips it.</summary>
        private static string? CSharpFieldType(string type) => (type ?? "").Trim().ToLowerInvariant() switch
        {
            "number" or "float" or "double" or "decimal" => "float",
            "int" or "integer" or "long" => "int",
            "bool" or "boolean" => "bool",
            "string" or "text" => "string",
            "vector2" or "vec2" => "Vector2",
            "vector3" or "vec3" => "Vector3",
            "vector4" or "vec4" => "Vector4",
            _ => null,
        };

        private static string BuildSkeleton(CreateComponentRequest req, string language, string extension, bool isCSharp)
        {
            var sb = new StringBuilder();
            if (isCSharp || string.Equals(language, "Contract", StringComparison.OrdinalIgnoreCase))
                sb.AppendLine($"// {req.ComponentName} — created by V12 Studio ({language})");
            else
                sb.AppendLine($"-- {req.ComponentName} — created by V12 Studio ({language})");
            if (!string.IsNullOrWhiteSpace(req.Pack))
            {
                if (isCSharp || string.Equals(language, "Contract", StringComparison.OrdinalIgnoreCase))
                    sb.AppendLine($"// Pack: {req.Pack.Trim()}");
                else
                    sb.AppendLine($"-- Pack: {req.Pack.Trim()}");
            }
            foreach (var f in req.Fields)
            {
                var line = $"@field {f.Type} {f.Name}{(string.IsNullOrWhiteSpace(f.Default) ? "" : " " + f.Default.Trim())}";
                sb.AppendLine(isCSharp || language == "Contract" ? "// " + line : "-- " + line);
            }
            sb.AppendLine();

            if (isCSharp)
            {
                sb.AppendLine("[ComponentEntry(\"GAMEPLAY\", \"" + req.ComponentName + "\")]");
                sb.AppendLine($"public sealed class {req.ComponentName} : ScriptableComponent");
                sb.AppendLine("{");
                foreach (var f in req.Fields)
                {
                    var type = CSharpFieldType(f.Type);
                    if (type == null) continue;
                    var init = type == "string" && !string.IsNullOrWhiteSpace(f.Default)
                        ? " = \"" + f.Default.Trim().Replace("\"", "\\\"") + "\";"
                        : "";
                    sb.AppendLine($"    public {type} {f.Name}{init}");
                }
                sb.AppendLine();
                sb.AppendLine("    public override void Update(float deltaTime)");
                sb.AppendLine("    {");
                sb.AppendLine("    }");
                sb.AppendLine("}");
            }
            else if (string.Equals(language, "Contract", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine($"Contract {req.ComponentName}");
                sb.AppendLine("{");
                sb.AppendLine("    fn on_init() { }");
                sb.AppendLine("    fn on_update(float dt) { }");
                sb.AppendLine("}");
            }
            else // Lua
            {
                sb.AppendLine("function on_init() end");
                sb.AppendLine("function on_update(dt) end");
            }
            return sb.ToString();
        }
    }
}
