using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using System.Reflection.Metadata;
using POrigami = Prowl.OrigamiUI;

namespace V12Studio.Editor
{
    /// <summary>
    /// Roslyn-backed code session for the C# code tab: the current document plus
    /// the project's other C# files are compiled against every assembly the editor
    /// has loaded, which gives real diagnostics, member completion, hover info and
    /// go-to-definition (including user components/scripts).
    /// </summary>
    internal sealed class RoslynCodeSession : POrigami.CodeLanguageSession
    {
        private static readonly CSharpParseOptions ParseOptions =
            new(LanguageVersion.Latest, kind: SourceCodeKind.Regular);

        private static readonly Lazy<IReadOnlyList<MetadataReference>> References =
            new(BuildReferences, LazyThreadSafetyMode.ExecutionAndPublication);

        private static readonly Dictionary<string, (DateTime Write, SyntaxTree Tree)> ProjectTrees =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly object CacheLock = new();

        private readonly POrigami.CodeDocument _document;
        private readonly string _path;
        private readonly string? _projectDir;

        public RoslynCodeSession(POrigami.CodeDocument document, string path, string? projectDir)
        {
            _document = document ?? throw new ArgumentNullException(nameof(document));
            _path = string.IsNullOrEmpty(path) ? "Script.cs" : path;
            _projectDir = projectDir;
        }

        private string Text => _document.GetText("\n");

        // ── Compilation ─────────────────────────────────────────────────────

        private (CSharpCompilation Compilation, SyntaxTree Tree) Compile(CancellationToken ct)
        {
            var tree = CSharpSyntaxTree.ParseText(
                SourceText.From(Text), ParseOptions, path: _path, cancellationToken: ct);

            var trees = new List<SyntaxTree> { tree };
            if (!string.IsNullOrWhiteSpace(_projectDir))
                trees.AddRange(ProjectSyntaxTrees(ct));

            var options = new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable,
                optimizationLevel: OptimizationLevel.Debug);

            var compilation = CSharpCompilation.Create(
                "V12Studio.CodeAnalysis", trees, References.Value, options);
            return (compilation, tree);
        }

        private IEnumerable<SyntaxTree> ProjectSyntaxTrees(CancellationToken ct)
        {
            var files = new List<string>();
            try
            {
                foreach (var file in Directory.EnumerateFiles(_projectDir!, "*.cs", SearchOption.AllDirectories))
                {
                    if (ct.IsCancellationRequested) yield break;
                    if (IsBuildFolder(file)) continue;
                    if (string.Equals(Path.GetFullPath(file), _path, StringComparison.OrdinalIgnoreCase)) continue;
                    FileInfo info;
                    try { info = new FileInfo(file); } catch { continue; }
                    if (info.Length > 2 * 1024 * 1024) continue;
                    files.Add(file);
                    if (files.Count >= 200) break;
                }
            }
            catch { yield break; }

            foreach (var file in files)
            {
                if (ct.IsCancellationRequested) yield break;
                yield return CachedTree(file, ct);
            }
        }

        private static bool IsBuildFolder(string path)
            => path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{Path.DirectorySeparatorChar}.godot{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

        private static SyntaxTree CachedTree(string path, CancellationToken ct)
        {
            DateTime write;
            try { write = File.GetLastWriteTimeUtc(path); }
            catch { write = DateTime.MinValue; }

            lock (CacheLock)
            {
                if (ProjectTrees.TryGetValue(path, out var cached) && cached.Write == write)
                    return cached.Tree;
            }

            string text = "";
            try { text = File.ReadAllText(path); } catch { }
            var tree = CSharpSyntaxTree.ParseText(SourceText.From(text), ParseOptions, path, ct);

            lock (CacheLock)
                ProjectTrees[path] = (write, tree);
            return tree;
        }

        private static IReadOnlyList<MetadataReference> BuildReferences()
        {
            var byKey = new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);

            void AddPath(string? path)
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
                string full;
                try { full = Path.GetFullPath(path); } catch { return; }
                if (byKey.ContainsKey(full)) return;
                try { byKey[full] = MetadataReference.CreateFromFile(full); } catch { }
            }

            void AddAssembly(System.Reflection.Assembly? assembly)
            {
                if (assembly == null || assembly.IsDynamic) return;

                string? location = null;
                try { location = assembly.Location; } catch { }
                if (!string.IsNullOrEmpty(location))
                {
                    AddPath(location);
                    if (byKey.ContainsKey(Path.GetFullPath(location))) return;
                }

                // Assemblies loaded from a pak / in-memory have no file location.
                // Reference their metadata directly so engine types still resolve.
                try
                {
                    unsafe
                    {
                        if (assembly.TryGetRawMetadata(out byte* blob, out int length))
                        {
                            var module = ModuleMetadata.CreateFromMetadata((IntPtr)blob, length);
                            var metadata = AssemblyMetadata.Create(module);
                            string key = "asm:" + (assembly.FullName ?? assembly.GetName().Name ?? "");
                            byKey[key] = metadata.GetReference();
                        }
                    }
                }
                catch { }
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                AddAssembly(assembly);

            // The engine / host assemblies this editor is built against. These are
            // added explicitly so IntelliSense works even if the JIT has not loaded
            // them yet.
            AddAssembly(typeof(object).Assembly);
            AddAssembly(typeof(Enumerable).Assembly);
            AddAssembly(typeof(System.Numerics.Vector3).Assembly);
            AddAssembly(typeof(V12.Core.Element).Assembly);
            AddAssembly(typeof(V12.Basic.Components.CameraComponent).Assembly);
            AddAssembly(typeof(Prowl.OrigamiUI.Origami).Assembly);
            AddAssembly(typeof(RoslynCodeSession).Assembly);
            try { AddAssembly(System.Reflection.Assembly.Load("netstandard")); } catch { }
            try { AddAssembly(System.Reflection.Assembly.Load("System.Runtime")); } catch { }

            // Anything next to the loaded engine assemblies (V12.dll, V12.Basic.dll,
            // package assemblies) is a candidate too; pak-loaded games still get the
            // live assemblies above via raw metadata.
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    string? dir = Path.GetDirectoryName(assembly.Location);
                    if (!string.IsNullOrEmpty(dir)) dirs.Add(dir);
                }
                catch { }
            }
            try { dirs.Add(AppContext.BaseDirectory); } catch { }

            foreach (var dir in dirs)
            {
                try
                {
                    foreach (var dll in Directory.EnumerateFiles(dir, "*.dll"))
                    {
                        string name = Path.GetFileName(dll);
                        if (name.StartsWith("V12", StringComparison.OrdinalIgnoreCase)
                            || name.StartsWith("Prowl.", StringComparison.OrdinalIgnoreCase)
                            || name.StartsWith("GodotSharp", StringComparison.OrdinalIgnoreCase))
                        {
                            AddPath(dll);
                        }
                    }
                }
                catch { }
            }

            return byKey.Values.ToArray();
        }
        // ── Diagnostics ─────────────────────────────────────────────────────

        public override Task<IReadOnlyList<POrigami.CodeDiagnostic>> GetDiagnosticsAsync(CancellationToken ct)
            => Task.Run<IReadOnlyList<POrigami.CodeDiagnostic>>(() =>
            {
                try
                {
                    var (compilation, tree) = Compile(ct);
                    bool debug = Environment.GetEnvironmentVariable("V12_PAPER_DEBUG") == "1";
                    if (debug)
                        Console.WriteLine($"[Roslyn] analyze {Path.GetFileName(_path)} text={Text.Length} refs={References.Value.Count}");
                    var diagnostics = new List<POrigami.CodeDiagnostic>();

                    foreach (var diagnostic in compilation.GetDiagnostics(ct))
                    {
                        if (diagnostic.Location.SourceTree != tree) continue;
                        if (diagnostic.Severity == DiagnosticSeverity.Hidden) continue;
                        if (!diagnostic.Location.IsInSource) continue;

                        var span = diagnostic.Location.SourceSpan;
                        diagnostics.Add(new POrigami.CodeDiagnostic
                        {
                            Start = span.Start,
                            Length = Math.Max(1, span.Length),
                            Severity = MapSeverity(diagnostic.Severity),
                            Id = diagnostic.Id,
                            Message = diagnostic.GetMessage(CultureInfo.InvariantCulture),
                        });

                        if (diagnostics.Count >= 200) break;
                    }

                    if (debug)
                        Console.WriteLine($"[Roslyn] {diagnostics.Count} diagnostic(s)");
                    return diagnostics;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Roslyn] diagnostics failed: {ex}");
                    return Array.Empty<POrigami.CodeDiagnostic>();
                }
            }, ct);

        // ── Completion ──────────────────────────────────────────────────────

        public override Task<POrigami.CodeCompletionList?> GetCompletionsAsync(
            int offset, POrigami.CodeCompletionTrigger trigger, CancellationToken ct)
            => Task.Run<POrigami.CodeCompletionList?>(() =>
            {
                var (compilation, tree) = Compile(ct);
                var model = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot(ct);
                int position = Math.Clamp(offset, 0, root.FullSpan.End);
                int spanStart = WordStart(Text, position);

                var items = new List<POrigami.CodeCompletionItem>(256);
                var seen = new HashSet<string>(StringComparer.Ordinal);

                foreach (var symbol in model.LookupSymbols(position, includeReducedExtensionMethods: true))
                {
                    ct.ThrowIfCancellationRequested();
                    if (symbol.IsImplicitlyDeclared || string.IsNullOrEmpty(symbol.Name)) continue;

                    var kind = MapKind(symbol);
                    string key = symbol.Name + "|" + kind;
                    if (!seen.Add(key)) continue;

                    string insert = symbol is IMethodSymbol { MethodKind: not MethodKind.Constructor } method
                        ? method.Name
                        : symbol.Name;

                    items.Add(new POrigami.CodeCompletionItem
                    {
                        Label = symbol.Name,
                        Kind = kind,
                        Detail = Detail(symbol),
                        FilterText = symbol.Name,
                        InsertText = insert,
                        SortText = SortKey(symbol),
                        Data = symbol,
                    });

                    if (items.Count >= 600) break;
                }

                foreach (var keyword in Keywords)
                {
                    if (!seen.Add(keyword + "|Keyword")) continue;
                    items.Add(new POrigami.CodeCompletionItem
                    {
                        Label = keyword,
                        Kind = POrigami.CodeSymbolKind.Keyword,
                        FilterText = keyword,
                        InsertText = keyword,
                        SortText = "4" + keyword,
                    });
                }

                // Only the typed word filters the list; keep the caret's word as the span.
                int wordStart = spanStart;
                if (wordStart < position && position <= Text.Length)
                {
                    // Trim the typed prefix from the word start only when it is part of
                    // an identifier the user is extending (the editor uses spanStart for
                    // filtering and replacement).
                    wordStart = WordStart(Text, wordStart);
                }

                return (POrigami.CodeCompletionList?)new POrigami.CodeCompletionList
                {
                    Items = items,
                    SpanStart = wordStart,
                };
            }, ct);

        public override Task<POrigami.CodeQuickInfo?> GetCompletionDescriptionAsync(
            POrigami.CodeCompletionItem item, CancellationToken ct)
            => Task.FromResult(item.Data is ISymbol symbol ? QuickInfo(symbol, 0, 0) : null);

        // ── Hover / quick info ──────────────────────────────────────────────

        public override Task<POrigami.CodeQuickInfo?> GetQuickInfoAsync(int offset, CancellationToken ct)
            => Task.Run<POrigami.CodeQuickInfo?>(() =>
            {
                var (compilation, tree) = Compile(ct);
                var model = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot(ct);
                int position = Math.Clamp(offset, 0, root.FullSpan.End);
                var node = root.FindToken(position, findInsideTrivia: true).Parent;
                if (node == null) return null;

                var symbol = model.GetSymbolInfo(node, ct).Symbol ?? model.GetTypeInfo(node, ct).Type;
                if (symbol == null) return null;

                var span = node.Span;
                return (POrigami.CodeQuickInfo?)QuickInfo(symbol, span.Start, span.Length);
            }, ct);

        private static POrigami.CodeQuickInfo QuickInfo(ISymbol symbol, int start, int length)
        {
            var info = new POrigami.CodeQuickInfo
            {
                SpanStart = start,
                SpanLength = length,
                Signature = new[]
                {
                    new POrigami.CodeTaggedText(
                        symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)),
                },
                Documentation = Documentation(symbol),
            };
            return info;
        }

        // ── Definition / references ─────────────────────────────────────────

        public override Task<IReadOnlyList<POrigami.CodeLocation>> GetDefinitionAsync(
            int offset, CancellationToken ct)
            => Task.Run<IReadOnlyList<POrigami.CodeLocation>>(() =>
            {
                var (compilation, tree) = Compile(ct);
                var model = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot(ct);
                int position = Math.Clamp(offset, 0, root.FullSpan.End);
                var node = root.FindToken(position, findInsideTrivia: true).Parent;
                if (node == null) return Array.Empty<POrigami.CodeLocation>();

                var symbol = model.GetSymbolInfo(node, ct).Symbol ?? model.GetTypeInfo(node, ct).Type;
                if (symbol == null) return Array.Empty<POrigami.CodeLocation>();

                var locations = new List<POrigami.CodeLocation>();
                foreach (var location in symbol.Locations)
                {
                    if (!location.IsInSource) continue;
                    var span = location.SourceSpan;
                    var line = location.GetLineSpan();
                    bool current = string.Equals(location.SourceTree?.FilePath, _path,
                        StringComparison.OrdinalIgnoreCase);

                    locations.Add(new POrigami.CodeLocation
                    {
                        IsCurrentDocument = current,
                        Path = current ? null : location.SourceTree?.FilePath,
                        Start = current ? span.Start : 0,
                        Length = span.Length,
                        Line = line.StartLinePosition.Line,
                        Column = line.StartLinePosition.Character,
                        Display = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        Kind = MapKind(symbol),
                    });

                    if (locations.Count >= 20) break;
                }

                return locations;
            }, ct);

        public override Task<IReadOnlyList<POrigami.CodeLocation>> FindReferencesAsync(
            int offset, CancellationToken ct)
            => Task.Run<IReadOnlyList<POrigami.CodeLocation>>(() =>
            {
                var (compilation, tree) = Compile(ct);
                var model = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot(ct);
                int position = Math.Clamp(offset, 0, root.FullSpan.End);
                var token = root.FindToken(position, findInsideTrivia: true);
                if (token.Parent == null) return Array.Empty<POrigami.CodeLocation>();

                var symbol = model.GetSymbolInfo(token.Parent, ct).Symbol;
                if (symbol == null) return Array.Empty<POrigami.CodeLocation>();

                var locations = new List<POrigami.CodeLocation>();
                foreach (var candidate in root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax>())
                {
                    ct.ThrowIfCancellationRequested();
                    if (candidate.Identifier.ValueText != symbol.Name) continue;
                    var info = model.GetSymbolInfo(candidate, ct).Symbol;
                    if (!SymbolEqualityComparer.Default.Equals(info, symbol)) continue;

                    var span = candidate.Identifier.Span;
                    var line = tree.GetLineSpan(span);
                    locations.Add(new POrigami.CodeLocation
                    {
                        IsCurrentDocument = true,
                        Start = span.Start,
                        Length = span.Length,
                        Line = line.StartLinePosition.Line,
                        Column = line.StartLinePosition.Character,
                        Display = TextLine(Text, line.StartLinePosition.Line),
                        Kind = MapKind(symbol),
                    });
                    if (locations.Count >= 200) break;
                }

                return locations;
            }, ct);

        // ── Helpers ─────────────────────────────────────────────────────────

        private static int WordStart(string text, int position)
        {
            int i = Math.Clamp(position, 0, text.Length);
            while (i > 0 && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_')) i--;
            return i;
        }

        private static string Detail(ISymbol symbol)
        {
            string text = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            if (text.Length <= 60) return text;
            return text[..57] + "...";
        }

        private static string SortKey(ISymbol symbol) => symbol switch
        {
            ILocalSymbol or IParameterSymbol => "0" + symbol.Name,
            IFieldSymbol or IPropertySymbol or IMethodSymbol or IEventSymbol => "1" + symbol.Name,
            INamedTypeSymbol or INamespaceSymbol => "2" + symbol.Name,
            _ => "3" + symbol.Name,
        };

        private static POrigami.CodeSymbolKind MapKind(ISymbol symbol) => symbol switch
        {
            INamespaceSymbol => POrigami.CodeSymbolKind.Namespace,
            IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor }
                => POrigami.CodeSymbolKind.Constructor,
            IMethodSymbol { IsExtensionMethod: true } => POrigami.CodeSymbolKind.ExtensionMethod,
            IMethodSymbol => POrigami.CodeSymbolKind.Method,
            IPropertySymbol => POrigami.CodeSymbolKind.Property,
            IFieldSymbol { IsConst: true } => POrigami.CodeSymbolKind.Constant,
            IFieldSymbol => POrigami.CodeSymbolKind.Field,
            IEventSymbol => POrigami.CodeSymbolKind.Event,
            ILocalSymbol => POrigami.CodeSymbolKind.Local,
            IParameterSymbol => POrigami.CodeSymbolKind.Parameter,
            ITypeParameterSymbol => POrigami.CodeSymbolKind.TypeParameter,
            INamedTypeSymbol named => named.TypeKind switch
            {
                TypeKind.Struct => POrigami.CodeSymbolKind.Struct,
                TypeKind.Interface => POrigami.CodeSymbolKind.Interface,
                TypeKind.Enum => POrigami.CodeSymbolKind.Enum,
                TypeKind.Delegate => POrigami.CodeSymbolKind.Delegate,
                _ => named.IsRecord ? POrigami.CodeSymbolKind.Record : POrigami.CodeSymbolKind.Class,
            },
            _ => POrigami.CodeSymbolKind.Text,
        };

        private static POrigami.CodeDiagnosticSeverity MapSeverity(DiagnosticSeverity severity) => severity switch
        {
            DiagnosticSeverity.Error => POrigami.CodeDiagnosticSeverity.Error,
            DiagnosticSeverity.Warning => POrigami.CodeDiagnosticSeverity.Warning,
            DiagnosticSeverity.Info => POrigami.CodeDiagnosticSeverity.Info,
            _ => POrigami.CodeDiagnosticSeverity.Hidden,
        };

        private static IReadOnlyList<string> Documentation(ISymbol symbol)
        {
            string? xml = symbol.GetDocumentationCommentXml(cancellationToken: CancellationToken.None);
            if (string.IsNullOrWhiteSpace(xml)) return Array.Empty<string>();
            try
            {
                var doc = System.Xml.Linq.XDocument.Parse(xml);
                string? summary = doc.Root?.Element("summary")?.Value;
                if (string.IsNullOrWhiteSpace(summary)) return Array.Empty<string>();
                return summary!
                    .Split('\n')
                    .Select(line => line.Trim())
                    .Where(line => line.Length > 0)
                    .ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static string TextLine(string text, int line)
        {
            int start = 0;
            for (int i = 0; i < line && start < text.Length; i++)
            {
                int next = text.IndexOf('\n', start);
                if (next < 0) return "";
                start = next + 1;
            }
            int end = text.IndexOf('\n', start);
            if (end < 0) end = text.Length;
            return text.Substring(start, Math.Max(0, end - start)).Trim();
        }

        private static readonly string[] Keywords =
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char",
            "checked", "class", "const", "continue", "decimal", "default", "delegate",
            "do", "double", "else", "enum", "event", "explicit", "extern", "false",
            "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit",
            "in", "int", "interface", "internal", "is", "lock", "long", "namespace",
            "new", "null", "object", "operator", "out", "override", "params", "private",
            "protected", "public", "readonly", "record", "ref", "return", "sbyte",
            "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct",
            "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong",
            "unchecked", "unsafe", "ushort", "using", "var", "virtual", "void",
            "volatile", "while", "async", "await", "yield", "get", "set", "init",
        };
    }
}
