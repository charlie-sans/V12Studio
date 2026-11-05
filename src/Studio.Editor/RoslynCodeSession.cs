using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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

        // Game/script projects build with <ImplicitUsings>enable</ImplicitUsings>, so
        // System, LINQ, etc. are in scope without a directive. The editor has no such
        // switch, so mirror the default .NET implicit global usings here to keep the
        // analysis identical to the real build (e.g. MathF.PI without `using System;`).
        private static readonly SyntaxTree ImplicitUsingsTree = CSharpSyntaxTree.ParseText(
            SourceText.From(
                "global using System;\n" +
                "global using System.Collections.Generic;\n" +
                "global using System.IO;\n" +
                "global using System.Linq;\n" +
                "global using System.Net.Http;\n" +
                "global using System.Threading;\n" +
                "global using System.Threading.Tasks;\n"),
            ParseOptions, path: "__ImplicitUsings.g.cs");

        private static readonly Lazy<IReadOnlyList<MetadataReference>> References =
            new(BuildReferences, LazyThreadSafetyMode.ExecutionAndPublication);

        private static readonly Dictionary<string, (DateTime Write, SyntaxTree Tree)> ProjectTrees =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly object CacheLock = new();

        // Types declared by referenced assemblies, indexed once so unimported type
        // suggestions do not walk every metadata symbol on each keystroke.
        private static IReadOnlyList<TypeEntry>? _referenceTypes;
        private static readonly object TypeIndexLock = new();

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

            var trees = new List<SyntaxTree> { tree, ImplicitUsingsTree };
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
                try
                {
                    var (compilation, tree) = Compile(ct);
                    var model = compilation.GetSemanticModel(tree);
                    var root = tree.GetRoot(ct);
                    string text = Text;
                    int position = Math.Clamp(offset, 0, root.FullSpan.End);
                    int wordStart = WordStart(text, position);

                    // Never offer completions inside comments or string literals.
                    if (IsInNonCode(root, position)) return null;

                    var items = new List<POrigami.CodeCompletionItem>(256);
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    var seenNames = new HashSet<string>(StringComparer.Ordinal);

                    // 1) `using ...;` and `namespace ...` directive names: complete
                    //    namespaces (and, for `using static`, types/static members).
                    if (TryGetDirectiveContext(root, text, position, out int nameStart, out int mode))
                    {
                        string path = ComputePath(text, nameStart, wordStart);
                        var symbol = ResolvePath(compilation.GlobalNamespace, path, ct);
                        if (symbol != null)
                        {
                            bool includeTypes = mode == 1;
                            bool includeStatic = mode == 1;
                            AddNamespaceOrTypeMembers(symbol, includeTypes, includeStatic, items, seen, seenNames, ct);
                        }
                        return Done(items, wordStart);
                    }

                    // 2) After a dot: list the receiver's members or the namespace's
                    //    contents, instead of every symbol merely in scope.
                    if (TryResolveDotContext(model, root, text, position, wordStart, ct,
                            out var namespaceOrType, out var receiverType, out bool staticAccess))
                    {
                        if (namespaceOrType != null)
                        {
                            bool includeTypes = namespaceOrType is INamespaceSymbol;
                            AddNamespaceOrTypeMembers(namespaceOrType, includeTypes, true, items, seen, seenNames, ct);
                        }
                        else if (receiverType != null)
                        {
                            AddMemberCompletions(model, position, receiverType, staticAccess, compilation, items, seen, seenNames, ct);
                        }
                        return Done(items, wordStart);
                    }

                    // 3) General scope completion: locals, parameters, members, types,
                    //    keywords, plus unimported types in type positions.
                    AddGeneralCompletions(compilation, model, root, text, position, wordStart, items, seen, seenNames, ct);
                    return Done(items, wordStart);
                }
                catch (OperationCanceledException) { return null; }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Roslyn] completion failed: {ex}");
                    return null;
                }
            }, ct);

        private static POrigami.CodeCompletionList Done(
            List<POrigami.CodeCompletionItem> items, int spanStart)
            => new()
            {
                Items = items,
                SpanStart = spanStart,
            };

        public override Task<POrigami.CodeQuickInfo?> GetCompletionDescriptionAsync(
            POrigami.CodeCompletionItem item, CancellationToken ct)
        {
            if (item.Data is UnimportedTypeRef unimported)
            {
                string display = string.IsNullOrEmpty(unimported.Namespace)
                    ? unimported.TypeName
                    : unimported.Namespace + "." + unimported.TypeName;
                return Task.FromResult<POrigami.CodeQuickInfo?>(new POrigami.CodeQuickInfo
                {
                    Signature = new[] { new POrigami.CodeTaggedText(display, POrigami.CodeTokenKind.Class) },
                    Documentation = new[]
                    {
                        string.IsNullOrEmpty(unimported.Namespace)
                            ? "Global namespace type."
                            : $"Adds 'using {unimported.Namespace};' when committed.",
                    },
                });
            }
            return Task.FromResult(item.Data is ISymbol symbol ? QuickInfo(symbol, 0, 0) : null);
        }

        /// <summary>
        /// Commits a completion. Imported-type suggestions insert the missing
        /// `using` directive as well as the type name; everything else falls back
        /// to the editor's default replace-the-word behaviour.
        /// </summary>
        public override POrigami.CodeCompletionCommit? ResolveCompletion(
            POrigami.CodeCompletionList list, POrigami.CodeCompletionItem item, int caretOffset, char commitChar)
        {
            if (item.Data is not UnimportedTypeRef unimported) return null;
            if (string.IsNullOrEmpty(unimported.Namespace)) return null; // global: default insert is enough

            string text = Text;
            var root = CSharpSyntaxTree.ParseText(SourceText.From(text), ParseOptions, path: _path).GetRoot();

            int start = Math.Clamp(list.SpanStart, 0, text.Length);
            int end = Math.Clamp(caretOffset, start, text.Length);
            // The editor replaces the whole word under the caret; match that here.
            while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_')) end++;

            var edits = new List<POrigami.CodeTextEdit>
            {
                new(start, end - start, unimported.TypeName),
            };

            int usingLength = 0;
            int insertPos = -1;
            if (!HasUsing(root, unimported.Namespace))
            {
                insertPos = UsingInsertPosition(root, text);
                if (insertPos >= 0)
                {
                    string newline = insertPos > 0 && text[insertPos - 1] != '\n' ? "\n" : "";
                    string directive = newline + "using " + unimported.Namespace + ";\n";
                    edits.Add(new POrigami.CodeTextEdit(insertPos, 0, directive));
                    usingLength = directive.Length;
                }
            }

            int caret = start + unimported.TypeName.Length
                + (usingLength > 0 && insertPos <= start ? usingLength : 0);

            return new POrigami.CodeCompletionCommit
            {
                Edits = edits,
                CaretOffset = caret,
            };
        }

        private static string ComputePath(string text, int nameStart, int wordStart)
        {
            int from = Math.Clamp(nameStart, 0, text.Length);
            int to = Math.Clamp(wordStart, from, text.Length);
            string before = text.Substring(from, to - from).Trim();
            if (before.EndsWith('.')) before = before[..^1];
            return before.Trim();
        }

        private static bool TryGetDirectiveContext(
            SyntaxNode root, string text, int position, out int nameStart, out int mode)
        {
            nameStart = -1;
            mode = 0;
            if (position <= 0) return false;

            var token = root.FindToken(position, findInsideTrivia: true);

            var usingDir = FindAncestor<UsingDirectiveSyntax>(token.Parent);
            if (usingDir == null)
                usingDir = FindAncestor<UsingDirectiveSyntax>(
                    root.FindToken(position - 1, findInsideTrivia: true).Parent);

            if (usingDir != null && position <= usingDir.Span.End)
            {
                // Completing the alias identifier (before '=') is not a namespace path.
                if (usingDir.Alias != null && position <= usingDir.Alias.Span.End)
                    return false;

                if (usingDir.Name == null || usingDir.Name.IsMissing)
                {
                    int after = usingDir.UsingKeyword.Span.End;
                    int i = after;
                    while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
                    nameStart = i;
                }
                else
                {
                    nameStart = usingDir.Name.SpanStart;
                }
                mode = usingDir.StaticKeyword.IsKind(SyntaxKind.StaticKeyword) ? 1 : 0;
                return true;
            }

            var nsDecl = FindAncestor<BaseNamespaceDeclarationSyntax>(token.Parent);
            if (nsDecl == null)
                nsDecl = FindAncestor<BaseNamespaceDeclarationSyntax>(
                    root.FindToken(position - 1, findInsideTrivia: true).Parent);

            if (nsDecl != null)
            {
                var name = nsDecl.Name;
                if (name == null || name.IsMissing)
                {
                    int after = nsDecl.NamespaceKeyword.Span.End;
                    int i = after;
                    while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
                    nameStart = i;
                    mode = 2;
                    return true;
                }

                // Only while editing the name itself, not somewhere in the namespace body.
                if (position >= name.SpanStart && position <= name.Span.End)
                {
                    nameStart = name.SpanStart;
                    mode = 2;
                    return true;
                }
            }

            return false;
        }

        private static INamespaceOrTypeSymbol? ResolvePath(
            INamespaceSymbol rootNamespace, string path, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(path)) return rootNamespace;

            INamespaceOrTypeSymbol current = rootNamespace;
            foreach (var segment in path.Split('.'))
            {
                ct.ThrowIfCancellationRequested();
                if (segment.Length == 0) continue;
                INamespaceOrTypeSymbol? next = current switch
                {
                    INamespaceSymbol ns => ns.GetMembers(segment)
                        .OfType<INamespaceOrTypeSymbol>()
                        .FirstOrDefault(),
                    INamedTypeSymbol type => type.GetTypeMembers(segment).FirstOrDefault(),
                    _ => null,
                };
                if (next == null) return null;
                current = next;
            }
            return current;
        }

        private static void AddNamespaceOrTypeMembers(
            INamespaceOrTypeSymbol symbol, bool includeTypes, bool includeStaticMembers,
            List<POrigami.CodeCompletionItem> items,
            HashSet<string> seen, HashSet<string> seenNames, CancellationToken ct)
        {
            if (symbol is INamespaceSymbol ns)
            {
                foreach (var child in ns.GetNamespaceMembers())
                {
                    ct.ThrowIfCancellationRequested();
                    AddRawItem(items, seen, seenNames,
                        child.Name, POrigami.CodeSymbolKind.Namespace,
                        string.IsNullOrEmpty(child.ToDisplayString()) ? "global" : child.ToDisplayString(),
                        "2" + child.Name, child);
                }

                if (includeTypes)
                {
                    foreach (var type in ns.GetTypeMembers())
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!IsAccessibleType(type)) continue;
                        AddSymbolItem(type, items, seen, seenNames);
                    }
                }
            }
            else if (symbol is INamedTypeSymbol type && includeStaticMembers)
            {
                foreach (var member in type.GetMembers())
                {
                    ct.ThrowIfCancellationRequested();
                    if (member.IsImplicitlyDeclared || string.IsNullOrEmpty(member.Name)) continue;
                    if (member is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor or MethodKind.Destructor }) continue;

                    if (member is INamedTypeSymbol)
                    {
                        AddSymbolItem(member, items, seen, seenNames);
                        continue;
                    }
                    if (!member.IsStatic) continue;
                    AddSymbolItem(member, items, seen, seenNames);
                }
            }
        }

        private static bool TryResolveDotContext(
            SemanticModel model, SyntaxNode root, string text, int position, int wordStart, CancellationToken ct,
            out INamespaceOrTypeSymbol? namespaceOrType, out ITypeSymbol? receiverType, out bool staticAccess)
        {
            namespaceOrType = null;
            receiverType = null;
            staticAccess = false;

            if (wordStart <= 0 || text[wordStart - 1] != '.') return false;
            int dotPos = wordStart - 1;
            var dotToken = root.FindToken(dotPos, findInsideTrivia: true);

            SyntaxNode? left = dotToken.Parent switch
            {
                MemberAccessExpressionSyntax ma when ma.OperatorToken == dotToken => ma.Expression,
                ConditionalAccessExpressionSyntax ca when ca.OperatorToken == dotToken => ca.Expression,
                QualifiedNameSyntax qn when qn.DotToken == dotToken => qn.Left,
                _ => null,
            };
            if (left == null) return false;

            var info = model.GetSymbolInfo(left, ct);
            if (info.Symbol is INamespaceSymbol ns)
            {
                namespaceOrType = ns;
                return true;
            }
            if (info.Symbol is ITypeSymbol typeSymbol)
            {
                namespaceOrType = typeSymbol;
                staticAccess = true;
                return true;
            }

            var type = model.GetTypeInfo(left, ct).Type;
            if (type != null && type.TypeKind != TypeKind.Error)
            {
                receiverType = type;
                return true;
            }

            return false;
        }

        private static void AddMemberCompletions(
            SemanticModel model, int position, ITypeSymbol receiverType, bool staticAccess,
            CSharpCompilation compilation,
            List<POrigami.CodeCompletionItem> items,
            HashSet<string> seen, HashSet<string> seenNames, CancellationToken ct)
        {
            // LookupSymbols needs an INamedTypeSymbol container; map arrays and type
            // parameters to a type whose members are actually callable.
            INamedTypeSymbol? named = receiverType as INamedTypeSymbol;
            if (named == null)
            {
                if (receiverType is IArrayTypeSymbol)
                    named = compilation.GetSpecialType(SpecialType.System_Array);
                else if (receiverType is ITypeParameterSymbol tp)
                    named = tp.ConstraintTypes.OfType<INamedTypeSymbol>().FirstOrDefault()
                        ?? compilation.GetSpecialType(SpecialType.System_Object);
                else
                    named = compilation.GetSpecialType(SpecialType.System_Object);
            }

            foreach (var symbol in model.LookupSymbols(position, container: named, includeReducedExtensionMethods: true))
            {
                ct.ThrowIfCancellationRequested();
                if (symbol.IsImplicitlyDeclared || string.IsNullOrEmpty(symbol.Name)) continue;
                if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor or MethodKind.Destructor })
                    continue;

                if (staticAccess)
                {
                    // `Type.`: static members and nested types only.
                    if (!symbol.IsStatic && symbol is not INamedTypeSymbol) continue;
                }
                else
                {
                    // `value.`: instance members and extension methods only.
                    if (symbol is INamedTypeSymbol) continue;
                    if (symbol.IsStatic && symbol is not IMethodSymbol { IsExtensionMethod: true }) continue;
                }

                AddSymbolItem(symbol, items, seen, seenNames);
            }
        }

        private static void AddGeneralCompletions(
            CSharpCompilation compilation, SemanticModel model, SyntaxNode root, string text,
            int position, int wordStart,
            List<POrigami.CodeCompletionItem> items,
            HashSet<string> seen, HashSet<string> seenNames, CancellationToken ct)
        {
            foreach (var symbol in model.LookupSymbols(position, includeReducedExtensionMethods: true))
            {
                ct.ThrowIfCancellationRequested();
                if (symbol.IsImplicitlyDeclared || string.IsNullOrEmpty(symbol.Name)) continue;
                AddSymbolItem(symbol, items, seen, seenNames);
                if (items.Count >= 600) break;
            }

            foreach (var keyword in Keywords)
            {
                if (!seen.Add(keyword + "|Keyword")) continue;
                seenNames.Add(keyword);
                items.Add(new POrigami.CodeCompletionItem
                {
                    Label = keyword,
                    Kind = POrigami.CodeSymbolKind.Keyword,
                    FilterText = keyword,
                    InsertText = keyword,
                    SortText = "4" + keyword,
                });
            }

            int prefixLength = Math.Clamp(position - wordStart, 0, text.Length);
            if (prefixLength >= 1 && IsTypeContext(root, text, wordStart))
            {
                string prefix = text.Substring(wordStart, prefixLength);
                AddUnimportedTypes(compilation, prefix, items, seen, seenNames, ct);
            }
        }

        private static void AddUnimportedTypes(
            CSharpCompilation compilation, string prefix,
            List<POrigami.CodeCompletionItem> items,
            HashSet<string> seen, HashSet<string> seenNames, CancellationToken ct)
        {
            var referenceTypes = GetReferenceTypes(compilation, ct);
            var sourceTypes = new List<TypeEntry>();
            CollectTypeEntries(compilation.Assembly.GlobalNamespace, sourceTypes,
                new HashSet<string>(StringComparer.Ordinal), ct);

            int added = 0;
            foreach (var entry in EnumerateEntries(referenceTypes, sourceTypes))
            {
                ct.ThrowIfCancellationRequested();
                if (added >= 150) break;
                if (!entry.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!seenNames.Add(entry.Name)) continue;

                string detail = string.IsNullOrEmpty(entry.Namespace)
                    ? "global namespace"
                    : "(using " + entry.Namespace + ")";

                items.Add(new POrigami.CodeCompletionItem
                {
                    Label = entry.Name,
                    Kind = entry.Kind,
                    Detail = detail,
                    FilterText = entry.Name,
                    InsertText = entry.Name,
                    SortText = "5" + entry.Name,
                    Data = new UnimportedTypeRef { TypeName = entry.Name, Namespace = entry.Namespace },
                });
                seen.Add(entry.Name + "|Unimported");
                added++;
            }
        }

        private static IEnumerable<TypeEntry> EnumerateEntries(
            IReadOnlyList<TypeEntry> references, IReadOnlyList<TypeEntry> source)
        {
            foreach (var entry in source) yield return entry;
            foreach (var entry in references) yield return entry;
        }

        private static IReadOnlyList<TypeEntry> GetReferenceTypes(
            CSharpCompilation compilation, CancellationToken ct)
        {
            var cached = _referenceTypes;
            if (cached != null) return cached;

            lock (TypeIndexLock)
            {
                if (_referenceTypes != null) return _referenceTypes;

                var list = new List<TypeEntry>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var reference in compilation.References)
                {
                    ct.ThrowIfCancellationRequested();
                    if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly)
                        continue;
                    CollectTypeEntries(assembly.GlobalNamespace, list, seen, ct);
                }

                _referenceTypes = list;
                return list;
            }
        }

        private static void CollectTypeEntries(
            INamespaceSymbol ns, List<TypeEntry> into, HashSet<string> seen, CancellationToken ct)
        {
            if (into.Count >= 120_000) return;
            ct.ThrowIfCancellationRequested();

            foreach (var child in ns.GetNamespaceMembers())
                CollectTypeEntries(child, into, seen, ct);

            string nsName = ns.IsGlobalNamespace ? "" : ns.ToDisplayString();
            foreach (var type in ns.GetTypeMembers())
            {
                ct.ThrowIfCancellationRequested();
                if (!IsAccessibleType(type)) continue;
                string key = type.Name + "|" + nsName;
                if (!seen.Add(key)) continue;
                into.Add(new TypeEntry
                {
                    Name = type.Name,
                    Namespace = nsName,
                    Kind = MapKind(type),
                });
            }
        }

        private static bool IsAccessibleType(INamedTypeSymbol type)
        {
            if (type.IsImplicitlyDeclared) return false;
            if (string.IsNullOrEmpty(type.Name) || type.Name[0] == '<') return false;
            return type.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal
                or Accessibility.ProtectedOrInternal;
        }

        private static bool HasUsing(SyntaxNode root, string ns)
        {
            foreach (var directive in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
            {
                if (directive.Name == null) continue;
                if (directive.Alias != null) continue;
                if (!directive.StaticKeyword.IsKind(SyntaxKind.None)) continue;
                if (string.Equals(directive.Name.ToString().Trim(), ns, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static int UsingInsertPosition(SyntaxNode root, string text)
        {
            var topUsings = root.ChildNodes().OfType<UsingDirectiveSyntax>().ToList();
            if (topUsings.Count > 0)
                return AfterDirective(text, topUsings[^1]);

            var ns = root.ChildNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
            if (ns != null)
            {
                if (ns is FileScopedNamespaceDeclarationSyntax fileScoped)
                    return AfterToken(text, fileScoped.SemicolonToken);
                if (ns is NamespaceDeclarationSyntax block)
                    return AfterToken(text, block.OpenBraceToken);
            }

            return 0;
        }

        private static int AfterDirective(string text, UsingDirectiveSyntax directive)
            => AfterToken(text, directive.SemicolonToken);

        private static int AfterToken(string text, SyntaxToken token)
        {
            int end = token.Span.End;
            if (end < text.Length && text[end] == '\r') end++;
            if (end < text.Length && text[end] == '\n') end++;
            return end;
        }

        private static bool IsInNonCode(SyntaxNode root, int position)
        {
            var trivia = root.FindTrivia(position, findInsideTrivia: true);
            if (trivia.RawKind != 0 && (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
                || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
                || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)
                || trivia.IsKind(SyntaxKind.DisabledTextTrivia)))
                return true;

            var token = root.FindToken(position, findInsideTrivia: false);
            if (position > token.SpanStart && position < token.Span.End)
            {
                if (token.IsKind(SyntaxKind.StringLiteralToken)
                    || token.IsKind(SyntaxKind.Utf8StringLiteralToken)
                    || token.IsKind(SyntaxKind.SingleLineRawStringLiteralToken)
                    || token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken)
                    || token.IsKind(SyntaxKind.Utf8SingleLineRawStringLiteralToken)
                    || token.IsKind(SyntaxKind.Utf8MultiLineRawStringLiteralToken)
                    || token.IsKind(SyntaxKind.CharacterLiteralToken))
                    return true;
            }
            return false;
        }

        private static bool IsTypeContext(SyntaxNode root, string text, int wordStart)
        {
            if (wordStart <= 0) return true;

            int i = wordStart;
            while (i > 0 && (text[i - 1] == ' ' || text[i - 1] == '\t')) i--;
            if (i <= 0) return true;

            var token = root.FindToken(i - 1, findInsideTrivia: true);
            return token.Kind() switch
            {
                SyntaxKind.NewKeyword or SyntaxKind.IsKeyword or SyntaxKind.AsKeyword
                    or SyntaxKind.TypeOfKeyword or SyntaxKind.DefaultKeyword
                    or SyntaxKind.SizeOfKeyword or SyntaxKind.StackAllocKeyword
                    or SyntaxKind.ColonToken or SyntaxKind.LessThanToken
                    or SyntaxKind.RefKeyword or SyntaxKind.OutKeyword or SyntaxKind.InKeyword
                    or SyntaxKind.PublicKeyword or SyntaxKind.PrivateKeyword
                    or SyntaxKind.ProtectedKeyword or SyntaxKind.InternalKeyword
                    or SyntaxKind.StaticKeyword or SyntaxKind.ReadOnlyKeyword
                    or SyntaxKind.ConstKeyword or SyntaxKind.AbstractKeyword
                    or SyntaxKind.SealedKeyword or SyntaxKind.PartialKeyword
                    or SyntaxKind.VirtualKeyword or SyntaxKind.OverrideKeyword
                    or SyntaxKind.ExternKeyword or SyntaxKind.UnsafeKeyword
                    or SyntaxKind.AsyncKeyword
                    or SyntaxKind.OpenBraceToken or SyntaxKind.CloseBraceToken
                    or SyntaxKind.SemicolonToken => true,
                _ => false,
            };
        }

        private static void AddSymbolItem(
            ISymbol symbol, List<POrigami.CodeCompletionItem> items,
            HashSet<string> seen, HashSet<string> seenNames)
        {
            if (symbol.IsImplicitlyDeclared || string.IsNullOrEmpty(symbol.Name)) return;
            var kind = MapKind(symbol);
            if (!seen.Add(symbol.Name + "|" + kind)) return;
            seenNames.Add(symbol.Name);

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
        }

        private static void AddRawItem(
            List<POrigami.CodeCompletionItem> items,
            HashSet<string> seen, HashSet<string> seenNames,
            string name, POrigami.CodeSymbolKind kind, string detail, string sortText, ISymbol data)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (!seen.Add(name + "|" + kind)) return;
            seenNames.Add(name);
            items.Add(new POrigami.CodeCompletionItem
            {
                Label = name,
                Kind = kind,
                Detail = detail,
                FilterText = name,
                InsertText = name,
                SortText = sortText,
                Data = data,
            });
        }

        // ── Signature help ──────────────────────────────────────────────────

        public override Task<POrigami.CodeSignatureHelp?> GetSignatureHelpAsync(int offset, CancellationToken ct)
            => Task.Run<POrigami.CodeSignatureHelp?>(() =>
            {
                try
                {
                    var (compilation, tree) = Compile(ct);
                    var model = compilation.GetSemanticModel(tree);
                    var root = tree.GetRoot(ct);
                    int position = Math.Clamp(offset, 0, root.FullSpan.End);

                    var argList = FindArgumentList(root, position);
                    if (argList == null) return null;

                    ISymbol? target = argList.Parent switch
                    {
                        InvocationExpressionSyntax invocation => model.GetSymbolInfo(invocation, ct).Symbol,
                        ObjectCreationExpressionSyntax creation => model.GetSymbolInfo(creation, ct).Symbol,
                        ImplicitObjectCreationExpressionSyntax implicitCreation => model.GetSymbolInfo(implicitCreation, ct).Symbol,
                        ConstructorInitializerSyntax initializer => model.GetSymbolInfo(initializer, ct).Symbol,
                        _ => null,
                    };

                    var methods = CollectOverloads(target, compilation);
                    if (methods.Count == 0) return null;

                    // Active parameter: each top-level comma before the caret advances it.
                    int activeParameter = 0;
                    foreach (var child in argList.ChildNodesAndTokens())
                    {
                        if (child.IsToken && child.AsToken().IsKind(SyntaxKind.CommaToken)
                            && child.Span.End <= position)
                            activeParameter++;
                    }

                    var signatures = new List<POrigami.CodeSignature>();
                    int activeSignature = 0;
                    for (int i = 0; i < methods.Count; i++)
                    {
                        bool constructor = methods[i].MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor;
                        signatures.Add(BuildSignature(methods[i], constructor));
                        if (SymbolEqualityComparer.Default.Equals(methods[i], target))
                            activeSignature = i;
                    }

                    if (activeSignature == 0 && target is IMethodSymbol resolved
                        && resolved.ReducedFrom != null)
                        activeSignature = 0;

                    return new POrigami.CodeSignatureHelp
                    {
                        Signatures = signatures,
                        ActiveSignature = Math.Clamp(activeSignature, 0, signatures.Count - 1),
                        ActiveParameter = activeParameter,
                        ArgumentListStart = argList.OpenParenToken.SpanStart,
                    };
                }
                catch (OperationCanceledException) { return null; }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Roslyn] signature help failed: {ex}");
                    return null;
                }
            }, ct);

        private static ArgumentListSyntax? FindArgumentList(SyntaxNode root, int position)
        {
            var token = root.FindToken(position, findInsideTrivia: true);
            for (var node = token.Parent; node != null; node = node.Parent)
            {
                if (node is ArgumentListSyntax list)
                {
                    if (position < list.OpenParenToken.Span.End) return null;
                    if (!list.CloseParenToken.IsMissing && position > list.CloseParenToken.SpanStart)
                        return null;
                    return list;
                }
                if (node is StatementSyntax or MemberDeclarationSyntax) break;
            }
            return null;
        }

        private static List<IMethodSymbol> CollectOverloads(ISymbol? target, CSharpCompilation compilation)
        {
            const int Max = 24;
            var methods = new List<IMethodSymbol>();

            if (target is IMethodSymbol method)
            {
                if (method.MethodKind == MethodKind.ReducedExtension)
                {
                    methods.Add(method);
                    return methods;
                }

                var container = method.ContainingType;
                if (container != null)
                {
                    foreach (var candidate in container.GetMembers(method.Name).OfType<IMethodSymbol>())
                    {
                        if (candidate.MethodKind is not (MethodKind.Ordinary or MethodKind.Constructor or MethodKind.StaticConstructor))
                            continue;
                        methods.Add(candidate);
                        if (methods.Count >= Max) break;
                    }
                }
                if (methods.Count == 0) methods.Add(method);
                return methods;
            }

            if (target is INamedTypeSymbol type)
            {
                foreach (var constructor in type.InstanceConstructors)
                {
                    methods.Add(constructor);
                    if (methods.Count >= Max) break;
                }
            }

            return methods;
        }

        private static POrigami.CodeSignature BuildSignature(IMethodSymbol method, bool constructor)
        {
            var parts = new List<POrigami.CodeTaggedText>();
            var parameters = new List<POrigami.CodeSignatureParameter>();

            string name = constructor
                ? method.ContainingType?.Name ?? method.Name
                : method.Name;

            parts.Add(new POrigami.CodeTaggedText(name,
                constructor ? POrigami.CodeTokenKind.Class : POrigami.CodeTokenKind.Method));
            parts.Add(new POrigami.CodeTaggedText("(", POrigami.CodeTokenKind.Punctuation));
            int pos = name.Length + 1;

            for (int i = 0; i < method.Parameters.Length; i++)
            {
                if (i > 0)
                {
                    parts.Add(new POrigami.CodeTaggedText(", ", POrigami.CodeTokenKind.Punctuation));
                    pos += 2;
                }

                var parameter = method.Parameters[i];
                string label = parameter.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
                    + " " + parameter.Name;

                var docs = Documentation(parameter);
                parameters.Add(new POrigami.CodeSignatureParameter
                {
                    Name = parameter.Name,
                    Documentation = docs.Count > 0 ? string.Join(" ", docs) : null,
                    LabelStart = pos,
                    LabelLength = label.Length,
                });

                parts.Add(new POrigami.CodeTaggedText(label, POrigami.CodeTokenKind.Parameter));
                pos += label.Length;
            }

            parts.Add(new POrigami.CodeTaggedText(")", POrigami.CodeTokenKind.Punctuation));

            if (!constructor && !method.ReturnsVoid)
            {
                parts.Add(new POrigami.CodeTaggedText(" : ", POrigami.CodeTokenKind.Punctuation));
                parts.Add(new POrigami.CodeTaggedText(
                    method.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    POrigami.CodeTokenKind.Class));
            }

            var methodDocs = Documentation(method);
            return new POrigami.CodeSignature
            {
                Parts = parts,
                Parameters = parameters,
                Documentation = methodDocs.Count > 0 ? string.Join(" ", methodDocs) : null,
            };
        }

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
                foreach (var candidate in root.DescendantNodes().OfType<IdentifierNameSyntax>())
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

        private static T? FindAncestor<T>(SyntaxNode? node) where T : SyntaxNode
        {
            for (; node != null; node = node.Parent)
                if (node is T match) return match;
            return null;
        }

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

        private sealed class UnimportedTypeRef
        {
            public string TypeName { get; init; } = "";
            public string Namespace { get; init; } = "";
        }

        private sealed class TypeEntry
        {
            public string Name { get; init; } = "";
            public string Namespace { get; init; } = "";
            public POrigami.CodeSymbolKind Kind { get; init; }
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
