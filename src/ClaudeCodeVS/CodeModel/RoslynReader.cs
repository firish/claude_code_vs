using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVs.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Host;       // HostWorkspaceServices / HostServices
using Microsoft.CodeAnalysis.Operations;   // IInvocationOperation / IObjectCreationOperation (callees walk)
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;   // TaskScheduler.GetAwaiter — await TaskScheduler.Default hops off the UI thread
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.CodeModel;

/// <summary>
/// The semantic-model reader behind the <c>vs-semantic</c> MCP server — the static-analysis twin of
/// <see cref="ClaudeCodeVs.Debugging.DebuggerReader"/>. Where the debugger reader exposes RUNTIME state,
/// this exposes Visual Studio's resolved understanding of the CODE: symbols, references, implementations,
/// and hierarchies, straight from the live <see cref="VisualStudioWorkspace"/> Roslyn snapshot. It replaces
/// the CLI's text/grep guesses with ground truth that resolves through interfaces, overrides, partials,
/// and generics.
///
/// Threading: only acquiring the workspace + reading <see cref="VisualStudioWorkspace.CurrentSolution"/>
/// needs the UI thread (a COM service grab). The Roslyn <see cref="Solution"/> is an immutable,
/// free-threaded snapshot, so the heavy <see cref="SymbolFinder"/> queries run off-thread without
/// stalling the editor — a cleaner story than the EnvDTE debugger path, which is UI-thread-bound end to end.
///
/// Addressing: every navigation takes a stable <c>symbolId</c> (a Roslyn DocumentationCommentId such as
/// "M:Ns.Type.Method(System.Int32)") OR a <c>file</c>+<c>line</c>(+<c>column</c>) position. The first comes
/// from <see cref="SearchSymbolsAsync"/>; the second is the cursor-style fallback. See <see cref="ResolveSymbolAsync"/>.
///
/// Managed (C#/VB) only: Roslyn has no C++ model. Loose files in Open-Folder mode have no workspace, so
/// queries return {"available":false} — the model can tell "no project loaded" from a real empty result.
/// </summary>
internal static class RoslynReader
{
    // Output caps (mirror the debugger reader's "bounded but signaled" convention). When a cap truncates,
    // the payload carries {"truncated":true,...} so the model knows data was cut and can narrow its query.
    private const int MaxSearchResults = 80;
    private const int MaxReferences = 200;
    private const int MaxImplementations = 120;
    private const int MaxHierarchyNodes = 120;
    private const int MaxCallerDepth = 3;
    private const int SnippetChars = 200;

    /// <summary>Grab the live workspace. MUST be called on the UI thread (COM service acquisition).</summary>
    internal static VisualStudioWorkspace? GetWorkspace()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var cm = ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) as IComponentModel;
        return cm?.GetService<VisualStudioWorkspace>();
    }

    /// <summary>
    /// Take the workspace handle + immutable solution snapshot on the UI thread, then hop OFF it so the heavy
    /// SymbolFinder work never blocks the editor. Returns null if there's no loaded C#/VB solution.
    /// </summary>
    private static async Task<Solution?> GetSolutionOffThreadAsync(CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var solution = GetWorkspace()?.CurrentSolution;
        await TaskScheduler.Default;   // immutable snapshot is free-threaded from here
        return solution;
    }

    /// <summary>{"available":false} envelope when there's no loaded C#/VB solution to query.</summary>
    private static JObject Unavailable() =>
        new JObject
        {
            ["available"] = false,
            ["reason"] = "No C#/VB solution is loaded (Roslyn workspace empty). Open the code as a project, not a loose file.",
        };

    private static JObject Err(string message) => new JObject { ["available"] = true, ["error"] = message };

    // ---- Test discovery (for the vs-test tools) --------------------------------------------------------

    private static readonly HashSet<string> TestAttributeNames = new(StringComparer.Ordinal)
    {
        "FactAttribute", "TheoryAttribute",                              // xUnit
        "TestAttribute", "TestCaseAttribute", "TestCaseSourceAttribute", // NUnit
        "TestMethodAttribute", "DataTestMethodAttribute",                // MSTest
    };

    // ---- Enclosing-function lookup (for the editor context actions) -------------------------------

    /// <summary>Caret → enclosing-member result for the context-menu edit actions.</summary>
    internal sealed class EnclosingFunction
    {
        public string DisplayName = "";        // e.g. "QuoteFor(Order)" - for instruction headers
        public string FullyQualifiedName = ""; // e.g. "RefMaze.Program.QuoteFor" - what vs_run_test addresses
        public int StartLine;                  // 0-based, whole declaration span
        public int EndLine;                    // 0-based inclusive
        public bool IsTest;                    // carries a known test attribute
    }

    /// <summary>
    /// The function-shaped member (method / ctor / local function; an accessor reports its property)
    /// enclosing file:line:column, with its full declaration span. Null when no project is loaded, the
    /// file isn't in the solution, or the caret isn't inside a member. Language-agnostic on purpose
    /// (GetEnclosingSymbol + DeclaringSyntaxReferences - no C#-only syntax types, so VB works too).
    /// </summary>
    public static async Task<EnclosingFunction?> GetEnclosingFunctionAsync(string filePath, int line, int column, CancellationToken ct)
    {
        try
        {
            var solution = await GetSolutionOffThreadAsync(ct);
            if (solution == null || !solution.Projects.Any()) return null;

            var norm = filePath.Replace('/', '\\');
            var docId = solution.GetDocumentIdsWithFilePath(norm).FirstOrDefault()
                     ?? solution.Projects.SelectMany(p => p.Documents)
                            .FirstOrDefault(d => string.Equals(d.FilePath?.Replace('/', '\\'), norm, StringComparison.OrdinalIgnoreCase))?.Id;
            var doc = docId == null ? null : solution.GetDocument(docId);
            if (doc == null) return null;

            var text = await doc.GetTextAsync(ct);
            if (line < 0 || line >= text.Lines.Count) return null;
            var tl = text.Lines[line];
            int position = Math.Min(tl.Start + Math.Max(0, column), tl.End);

            var model = await doc.GetSemanticModelAsync(ct);
            if (model == null) return null;

            IMethodSymbol? method = null;
            for (var s = model.GetEnclosingSymbol(position, ct); s != null; s = s.ContainingSymbol)
                if (s is IMethodSymbol m) { method = m; break; }
            if (method == null) return null;
            ISymbol target = method.AssociatedSymbol is IPropertySymbol prop ? prop : method;

            // Locations on a method symbol span only the identifier; the declaration syntax carries
            // the real body extent the mention/edit should cover.
            var syntaxRef = target.DeclaringSyntaxReferences.FirstOrDefault()
                         ?? method.DeclaringSyntaxReferences.FirstOrDefault();
            if (syntaxRef == null) return null;
            var node = await syntaxRef.GetSyntaxAsync(ct);
            var span = node.SyntaxTree.GetLineSpan(node.Span);

            var containing = target.ContainingType?.ToDisplayString();
            return new EnclosingFunction
            {
                DisplayName = target.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                FullyQualifiedName = string.IsNullOrEmpty(containing) ? target.Name : containing + "." + target.Name,
                StartLine = span.StartLinePosition.Line,
                EndLine = span.EndLinePosition.Line,
                IsTest = method.GetAttributes().Any(a => a.AttributeClass != null && TestAttributeNames.Contains(a.AttributeClass.Name)),
            };
        }
        catch (Exception e)
        {
            Log.Warn($"enclosing-function lookup failed: {e.Message}");
            return null;
        }
    }

    // ---- Test context (for the Generate Tests context action) ------------------------------------

    internal sealed class TestProjectInfo
    {
        public string Name = "";
        public string Framework = "";      // xUnit | NUnit | MSTest | TUnit
        public string? Path;
        public bool ReferencesTarget;      // already references the project under test
    }

    internal sealed class TestContext
    {
        public string? TargetProject;
        public readonly List<TestProjectInfo> TestProjects = new();
        public readonly List<string> ExistingTests = new(); // FQNs of test methods that already call the target
    }

    /// <summary>
    /// What "Generate Tests" needs to compose a good instruction: which projects in the solution are test
    /// projects (and on which framework), whether they already reference the project under test, and which
    /// existing tests already exercise the member at the caret (so Claude extends rather than duplicates).
    /// Framework detection reads the compiler's reference list, not package files, so it works for
    /// packages.config and SDK projects alike. Bounded: a huge solution's caller search is cut off rather
    /// than making a right-click wait.
    /// </summary>
    public static async Task<TestContext> GetTestContextAsync(string filePath, int line, int column, CancellationToken ct)
    {
        var result = new TestContext();
        try
        {
            var solution = await GetSolutionOffThreadAsync(ct);
            if (solution == null || !solution.Projects.Any()) return result;

            var doc = FindDocument(solution, filePath);
            var target = doc?.Project;
            result.TargetProject = target?.Name;
            string? targetPath = target?.FilePath;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var project in solution.Projects)
            {
                var framework = TestFrameworkOf(project);
                if (framework == null) continue;
                var key = project.FilePath ?? project.Name;
                if (!seen.Add(key)) continue; // one entry per project, not per target framework
                result.TestProjects.Add(new TestProjectInfo
                {
                    Name = Regex.Replace(project.Name, @"\s*\([^)]+\)\s*$", ""),
                    Framework = framework,
                    Path = project.FilePath,
                    ReferencesTarget = targetPath != null && project.ProjectReferences.Any(r =>
                        string.Equals(solution.GetProject(r.ProjectId)?.FilePath, targetPath, StringComparison.OrdinalIgnoreCase)),
                });
            }

            if (doc == null) return result;
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                var text = await doc.GetTextAsync(bounded.Token);
                if (line < 0 || line >= text.Lines.Count) return result;
                var tl = text.Lines[line];
                int position = Math.Min(tl.Start + Math.Max(0, column), tl.End);
                var model = await doc.GetSemanticModelAsync(bounded.Token);
                ISymbol? symbol = null;
                for (var s = model?.GetEnclosingSymbol(position, bounded.Token); s != null; s = s.ContainingSymbol)
                    if (s is IMethodSymbol or IPropertySymbol) { symbol = s; break; }
                if (symbol is IMethodSymbol { AssociatedSymbol: IPropertySymbol prop }) symbol = prop;
                if (symbol == null) return result;

                var callers = await SymbolFinder.FindCallersAsync(symbol, solution, bounded.Token).ConfigureAwait(false);
                foreach (var c in callers)
                {
                    if (c.CallingSymbol is not IMethodSymbol m) continue;
                    if (!m.GetAttributes().Any(a => a.AttributeClass != null && TestAttributeNames.Contains(a.AttributeClass.Name))) continue;
                    var fqn = m.ContainingType.ToDisplayString() + "." + m.Name;
                    if (!result.ExistingTests.Contains(fqn)) result.ExistingTests.Add(fqn);
                    if (result.ExistingTests.Count >= 10) break;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Log.Event("test context: caller search timed out (4s) - proceeding without existing-test hints");
            }
        }
        catch (Exception e)
        {
            Log.Warn($"test context lookup failed: {e.Message}");
        }
        return result;
    }

    /// <summary>The test framework a project compiles against, by its reference assemblies; null if none.</summary>
    private static string? TestFrameworkOf(Project project)
    {
        foreach (var r in project.MetadataReferences)
        {
            var name = System.IO.Path.GetFileName((r as PortableExecutableReference)?.FilePath ?? r.Display ?? "");
            if (name.Length == 0) continue;
            if (name.Equals("xunit.core.dll", StringComparison.OrdinalIgnoreCase) || name.Equals("xunit.v3.core.dll", StringComparison.OrdinalIgnoreCase)) return "xUnit";
            if (name.Equals("nunit.framework.dll", StringComparison.OrdinalIgnoreCase)) return "NUnit";
            if (name.Equals("Microsoft.VisualStudio.TestPlatform.TestFramework.dll", StringComparison.OrdinalIgnoreCase)) return "MSTest";
            if (name.Equals("TUnit.Core.dll", StringComparison.OrdinalIgnoreCase)) return "TUnit";
        }
        return null;
    }

    /// <summary>
    /// Discover unit tests WITHOUT Test Explorer's internal result callback: scan the Roslyn model for
    /// methods carrying a known test attribute ([Fact]/[Theory]/[Test]/[TestMethod]/[TestCase]/…) and return
    /// their real fully-qualified names (Namespace.Type.Method) — exactly what the run/debug tools address
    /// by. Optional <paramref name="filter"/> = an FQN substring. Reuses the vs-semantic workspace/threading.
    /// </summary>
    public static async Task<JObject> FindTestMethodsAsync(string? filter, CancellationToken ct)
    {
        var solution = await GetSolutionOffThreadAsync(ct);
        if (solution == null || !solution.Projects.Any()) return Unavailable();

        var tests = new JArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in solution.Projects)
        {
            var comp = await project.GetCompilationAsync(ct).ConfigureAwait(false);
            if (comp == null) continue;
            foreach (var type in AllTypes(comp.GlobalNamespace))
            {
                foreach (var m in type.GetMembers().OfType<IMethodSymbol>())
                {
                    if (!m.GetAttributes().Any(a => a.AttributeClass != null && TestAttributeNames.Contains(a.AttributeClass.Name)))
                        continue;
                    string fqn = m.ContainingType.ToDisplayString() + "." + m.Name;
                    if (!seen.Add(fqn)) continue;
                    if (!string.IsNullOrEmpty(filter) && fqn.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    tests.Add(new JObject
                    {
                        ["fullyQualifiedName"] = fqn,
                        ["displayName"] = m.Name,
                        ["className"] = m.ContainingType.ToDisplayString(),
                        ["project"] = project.Name,
                        ["source"] = m.Locations.FirstOrDefault(l => l.IsInSource)?.GetLineSpan().Path,
                    });
                    if (tests.Count >= 500) return new JObject { ["available"] = true, ["truncated"] = true, ["count"] = tests.Count, ["tests"] = tests };
                }
            }
        }
        return new JObject { ["available"] = true, ["count"] = tests.Count, ["tests"] = tests };
    }

    /// <summary>All named types in a namespace, including nested, recursively.</summary>
    private static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol ns)
    {
        foreach (var t in ns.GetTypeMembers())
        {
            yield return t;
            foreach (var nested in NestedTypes(t)) yield return nested;
        }
        foreach (var child in ns.GetNamespaceMembers())
            foreach (var t in AllTypes(child)) yield return t;
    }

    private static IEnumerable<INamedTypeSymbol> NestedTypes(INamedTypeSymbol type)
    {
        foreach (var n in type.GetTypeMembers())
        {
            yield return n;
            foreach (var deeper in NestedTypes(n)) yield return deeper;
        }
    }

    // ---- Search (the addressing primitive) -------------------------------------------------------------

    /// <summary>
    /// Symbol search → candidate declarations, each carrying a stable <c>symbolId</c> the navigation tools
    /// take to name one exact symbol. Case-insensitive substring match across the whole solution.
    /// </summary>
    public static async Task<JObject> SearchSymbolsAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return Err("query is required");
        var solution = await GetSolutionOffThreadAsync(ct);
        if (solution == null || !solution.Projects.Any()) return Unavailable();

        var matches = await SymbolFinder.FindSourceDeclarationsAsync(
            solution,
            name => name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0,
            SymbolFilter.TypeAndMember,
            ct).ConfigureAwait(false);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new JArray();
        bool truncated = false;
        foreach (var sym in matches)
        {
            ct.ThrowIfCancellationRequested();
            var id = sym.GetDocumentationCommentId();
            if (id == null || !seen.Add(id)) continue;
            if (results.Count >= MaxSearchResults) { truncated = true; break; }
            results.Add(DescribeSymbol(sym, id));
        }

        var payload = new JObject
        {
            ["available"] = true,
            ["query"] = query,
            ["count"] = results.Count,
            ["symbols"] = results,
        };
        Mark(payload, truncated, MaxSearchResults, "results");
        return payload;
    }

    // ---- Navigation ------------------------------------------------------------------------------------

    /// <summary>Semantic Find-All-References: resolves through interfaces, overrides, partials, generics.</summary>
    public static async Task<JObject> FindReferencesAsync(JToken args, CancellationToken ct)
    {
        var solution = await GetSolutionOffThreadAsync(ct);
        if (solution == null || !solution.Projects.Any()) return Unavailable();
        var (symbol, error) = await ResolveSymbolAsync(solution, args, ct);
        if (symbol == null) return error!;

        var found = await SymbolFinder.FindReferencesAsync(symbol, solution, ct).ConfigureAwait(false);
        var cache = new Dictionary<DocumentId, SourceText>();
        var locations = new JArray();
        bool truncated = false;
        foreach (var rs in found)
        {
            foreach (var loc in rs.Locations)
            {
                ct.ThrowIfCancellationRequested();
                if (locations.Count >= MaxReferences) { truncated = true; break; }
                if (loc.Location.IsInSource)
                    locations.Add(await LocationJsonAsync(loc.Document, loc.Location, cache, loc.IsImplicit, ct));
            }
            if (truncated) break;
        }

        var payload = new JObject
        {
            ["available"] = true,
            ["symbol"] = DescribeSymbol(symbol, symbol.GetDocumentationCommentId()),
            ["referenceCount"] = locations.Count,
            ["references"] = locations,
        };
        Mark(payload, truncated, MaxReferences, "reference locations");
        return payload;
    }

    /// <summary>Go to Definition: the resolved symbol's declaration(s) — the right one, not grep's ten.</summary>
    public static async Task<JObject> GoToDefinitionAsync(JToken args, CancellationToken ct)
    {
        var solution = await GetSolutionOffThreadAsync(ct);
        if (solution == null || !solution.Projects.Any()) return Unavailable();
        var (symbol, error) = await ResolveSymbolAsync(solution, args, ct);
        if (symbol == null) return error!;

        var cache = new Dictionary<DocumentId, SourceText>();
        var defs = new JArray();
        foreach (var loc in symbol.Locations.Where(l => l.IsInSource))
        {
            var doc = solution.GetDocument(loc.SourceTree);
            defs.Add(await LocationJsonAsync(doc, loc, cache, false, ct));
        }

        var payload = new JObject
        {
            ["available"] = true,
            ["symbol"] = DescribeSymbol(symbol, symbol.GetDocumentationCommentId()),
            ["definitions"] = defs,
        };
        // Metadata-only symbols (framework/3rd-party) have no source location — say so rather than empty.
        if (defs.Count == 0)
            payload["note"] = $"'{symbol.Name}' is defined in metadata (assembly {symbol.ContainingAssembly?.Name}), not in source.";
        var xml = symbol.GetDocumentationCommentXml(cancellationToken: ct);
        if (!string.IsNullOrWhiteSpace(xml)) payload["xmlDoc"] = Cap(xml.Trim(), 600);
        return payload;
    }

    /// <summary>Find Implementations / overrides: interfaces &amp; abstract/virtual members → concrete code.</summary>
    public static async Task<JObject> FindImplementationsAsync(JToken args, CancellationToken ct)
    {
        var solution = await GetSolutionOffThreadAsync(ct);
        if (solution == null || !solution.Projects.Any()) return Unavailable();
        var (symbol, error) = await ResolveSymbolAsync(solution, args, ct);
        if (symbol == null) return error!;

        var cache = new Dictionary<DocumentId, SourceText>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new JArray();
        bool truncated = false;

        async Task AddAsync(IEnumerable<ISymbol> syms)
        {
            foreach (var s in syms)
            {
                ct.ThrowIfCancellationRequested();
                var id = s.GetDocumentationCommentId();
                if (id != null && !seen.Add(id)) continue;
                if (results.Count >= MaxImplementations) { truncated = true; return; }
                results.Add(DescribeSymbol(s, id));
            }
        }

        if (symbol is INamedTypeSymbol nt && nt.TypeKind == TypeKind.Interface)
        {
            await AddAsync(await SymbolFinder.FindImplementationsAsync(symbol, solution, cancellationToken: ct).ConfigureAwait(false));
            if (!truncated)
                await AddAsync((await SymbolFinder.FindDerivedInterfacesAsync(nt, solution, cancellationToken: ct).ConfigureAwait(false)).Cast<ISymbol>());
        }
        else if (symbol is INamedTypeSymbol cls && cls.TypeKind == TypeKind.Class)
        {
            // "Implementations" of an abstract/base class = the classes that derive from it.
            await AddAsync((await SymbolFinder.FindDerivedClassesAsync(cls, solution, cancellationToken: ct).ConfigureAwait(false)).Cast<ISymbol>());
        }
        else if (symbol is IMethodSymbol or IPropertySymbol or IEventSymbol)
        {
            // Interface-member implementors…
            await AddAsync(await SymbolFinder.FindImplementationsAsync(symbol, solution, cancellationToken: ct).ConfigureAwait(false));
            // …and, if it's polymorphic, the overrides.
            if (!truncated && (symbol.IsVirtual || symbol.IsAbstract || symbol.IsOverride))
                await AddAsync(await SymbolFinder.FindOverridesAsync(symbol, solution, cancellationToken: ct).ConfigureAwait(false));
        }
        else
        {
            return Err($"'{symbol.Name}' ({symbol.Kind}) isn't an interface, class, or overridable member; nothing to find implementations for.");
        }

        var payload = new JObject
        {
            ["available"] = true,
            ["symbol"] = DescribeSymbol(symbol, symbol.GetDocumentationCommentId()),
            ["implementationCount"] = results.Count,
            ["implementations"] = results,
        };
        Mark(payload, truncated, MaxImplementations, "implementations");
        return payload;
    }

    /// <summary>Type hierarchy: base chain + interfaces (direction "base"), or derived types ("derived").</summary>
    public static async Task<JObject> TypeHierarchyAsync(JToken args, CancellationToken ct)
    {
        var solution = await GetSolutionOffThreadAsync(ct);
        if (solution == null || !solution.Projects.Any()) return Unavailable();
        var (symbol, error) = await ResolveSymbolAsync(solution, args, ct);
        if (symbol == null) return error!;
        if (symbol is not INamedTypeSymbol type)
            return Err($"'{symbol.Name}' is a {symbol.Kind}, not a type — type hierarchy needs a class/interface/struct.");

        var direction = ((string?)args?["direction"] ?? "derived").ToLowerInvariant();
        var nodes = new JArray();
        bool truncated = false;

        if (direction == "base")
        {
            for (var bt = type.BaseType; bt != null && bt.SpecialType != SpecialType.System_Object; bt = bt.BaseType)
            {
                if (nodes.Count >= MaxHierarchyNodes) { truncated = true; break; }
                var n = DescribeSymbol(bt, bt.GetDocumentationCommentId());
                n["relation"] = "base";
                nodes.Add(n);
            }
            foreach (var iface in type.AllInterfaces)
            {
                if (nodes.Count >= MaxHierarchyNodes) { truncated = true; break; }
                var n = DescribeSymbol(iface, iface.GetDocumentationCommentId());
                n["relation"] = "interface";
                nodes.Add(n);
            }
        }
        else
        {
            IEnumerable<INamedTypeSymbol> derived = type.TypeKind == TypeKind.Interface
                ? (await SymbolFinder.FindImplementationsAsync(type, solution, cancellationToken: ct).ConfigureAwait(false)).OfType<INamedTypeSymbol>()
                    .Concat(await SymbolFinder.FindDerivedInterfacesAsync(type, solution, cancellationToken: ct).ConfigureAwait(false))
                : await SymbolFinder.FindDerivedClassesAsync(type, solution, cancellationToken: ct).ConfigureAwait(false);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in derived)
            {
                var id = d.GetDocumentationCommentId();
                if (id != null && !seen.Add(id)) continue;
                if (nodes.Count >= MaxHierarchyNodes) { truncated = true; break; }
                nodes.Add(DescribeSymbol(d, id));
            }
        }

        var payload = new JObject
        {
            ["available"] = true,
            ["type"] = DescribeSymbol(type, type.GetDocumentationCommentId()),
            ["direction"] = direction,
            ["count"] = nodes.Count,
            ["types"] = nodes,
        };
        Mark(payload, truncated, MaxHierarchyNodes, "types");
        return payload;
    }

    /// <summary>
    /// Call hierarchy. "callers": who transitively calls this method (recursive to a depth, cycle-guarded) —
    /// the high-value impact-analysis direction. "callees": what this method directly calls (depth 1, via the
    /// language-agnostic IOperation tree).
    /// </summary>
    public static async Task<JObject> CallHierarchyAsync(JToken args, CancellationToken ct)
    {
        var solution = await GetSolutionOffThreadAsync(ct);
        if (solution == null || !solution.Projects.Any()) return Unavailable();
        var (symbol, error) = await ResolveSymbolAsync(solution, args, ct);
        if (symbol == null) return error!;

        var direction = ((string?)args?["direction"] ?? "callers").ToLowerInvariant();
        int nodeBudget = MaxHierarchyNodes;
        bool truncated = false;

        if (direction == "callees")
        {
            var cache = new Dictionary<DocumentId, SourceText>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var callees = new JArray();
            foreach (var sref in symbol.DeclaringSyntaxReferences)
            {
                var node = await sref.GetSyntaxAsync(ct).ConfigureAwait(false);
                var doc = solution.GetDocument(node.SyntaxTree);
                if (doc == null) continue;
                var model = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false);
                var op = model?.GetOperation(node, ct);
                if (op == null) continue;
                // Explicit IOperation walk (ChildOperations) rather than the Descendants() extension, which
                // collides with an unrelated Descendants<T> overload. Language-agnostic: works for C# and VB.
                var stack = new Stack<IOperation>();
                stack.Push(op);
                while (stack.Count > 0)
                {
                    var d = stack.Pop();
                    foreach (var child in d.ChildOperations) stack.Push(child);
                    IMethodSymbol? target = d switch
                    {
                        IInvocationOperation io => io.TargetMethod,
                        IObjectCreationOperation oco => oco.Constructor,
                        _ => null,
                    };
                    if (target == null) continue;
                    var id = target.GetDocumentationCommentId();
                    if (id != null && !seen.Add(id)) continue;
                    if (callees.Count >= nodeBudget) { truncated = true; break; }
                    callees.Add(DescribeSymbol(target, id));
                }
                if (truncated) break;
            }
            var p = new JObject
            {
                ["available"] = true,
                ["method"] = DescribeSymbol(symbol, symbol.GetDocumentationCommentId()),
                ["direction"] = "callees",
                ["note"] = "Direct callees only (depth 1).",
                ["count"] = callees.Count,
                ["callees"] = callees,
            };
            Mark(p, truncated, nodeBudget, "callees");
            return p;
        }

        // direction == callers (default): transitive, depth-limited, cycle-guarded.
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var cacheC = new Dictionary<DocumentId, SourceText>();

        async Task<JArray> CallersOfAsync(ISymbol sym, int depth)
        {
            var arr = new JArray();
            if (depth > MaxCallerDepth || nodeBudget <= 0) return arr;
            var callers = await SymbolFinder.FindCallersAsync(sym, solution, ct).ConfigureAwait(false);
            foreach (var c in callers)
            {
                ct.ThrowIfCancellationRequested();
                if (nodeBudget <= 0) { truncated = true; break; }
                nodeBudget--;
                var id = c.CallingSymbol.GetDocumentationCommentId();
                var node = DescribeSymbol(c.CallingSymbol, id);
                var sites = new JArray();
                foreach (var loc in c.Locations.Where(l => l.IsInSource).Take(5))
                {
                    var doc = solution.GetDocument(loc.SourceTree);
                    sites.Add(await LocationJsonAsync(doc, loc, cacheC, false, ct));
                }
                if (sites.Count > 0) node["callSites"] = sites;
                if (id != null && visited.Add(id))
                {
                    var sub = await CallersOfAsync(c.CallingSymbol, depth + 1);
                    if (sub.Count > 0) node["callers"] = sub;
                }
                else if (id != null)
                {
                    node["recursionElided"] = true;   // already expanded elsewhere — don't loop
                }
                arr.Add(node);
            }
            return arr;
        }

        var tree = await CallersOfAsync(symbol, 1);
        var payload = new JObject
        {
            ["available"] = true,
            ["method"] = DescribeSymbol(symbol, symbol.GetDocumentationCommentId()),
            ["direction"] = "callers",
            ["maxDepth"] = MaxCallerDepth,
            ["callers"] = tree,
        };
        Mark(payload, truncated, MaxHierarchyNodes, "caller nodes");
        return payload;
    }

    /// <summary>
    /// The symbol at a 1-based file position (the caret / selection start), described like a search result
    /// (incl. its <c>symbolId</c>) - or null if no solution/file/symbol. Used to enrich vs_get_selection so
    /// "what I have selected" comes back with a navigable symbolId.
    /// </summary>
    public static async Task<JObject?> SymbolAtPositionAsync(string file, int line1Based, int col1Based, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        var solution = await GetSolutionOffThreadAsync(ct);
        if (solution == null) return null;
        var doc = FindDocument(solution, file);
        if (doc == null) return null;
        var text = await doc.GetTextAsync(ct).ConfigureAwait(false);
        if (line1Based < 1 || line1Based > text.Lines.Count) return null;
        var line = text.Lines[line1Based - 1];
        int pos = Math.Min(line.Start + Math.Max(0, col1Based - 1), Math.Max(line.Start, line.EndIncludingLineBreak - 1));
        var sym = await SymbolFinder.FindSymbolAtPositionAsync(doc, pos, ct).ConfigureAwait(false);
        return sym == null ? null : DescribeSymbol(sym, sym.GetDocumentationCommentId());
    }

    // ---- Decompile (metadata -> C#) -- Path A: VS's IMetadataAsSourceFileService (the Go-To-Definition path) --

    private const int MaxMemberChars = 8000;
    private const int MaxTypeChars = 16000;
    private const int SourceLinkTimeoutSec = 20;   // bound the SourceLink retry so an offline symbol server can't hang the call

    /// <summary>
    /// Decompile a metadata symbol (a framework/NuGet method or type that ships with no source) to C#, the way
    /// Go-To-Definition does. Uses VS's own metadata-as-source service with NavigateToDecompiledSources=true
    /// (ILSpy under the hood) - pure reflection against the already-loaded Roslyn Features (no new package),
    /// and the host's DecompilationMetadataAsSourceFileProvider resolves ref-vs-impl assemblies for us (so a
    /// framework method's REAL body comes back even though the compile-time reference is the bodyless ref
    /// assembly). For a member, returns just that member's declaration (+ doc comments) via IdentifierLocation;
    /// for a type, the whole type (capped). Each reflection hop is step-labelled so failures are diagnosable.
    /// </summary>
    public static async Task<JObject> DecompileAsync(JToken args, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var workspace = GetWorkspace();
        var solution = workspace?.CurrentSolution;
        await TaskScheduler.Default;   // hop OFF the UI thread; decompilation is heavy and VS runs it in the background too
        if (workspace == null || solution == null || !solution.Projects.Any()) return Unavailable();

        // Resolve symbol + the project it was viewed from (GetGeneratedFileAsync needs a source Project).
        var (symbol, project, resolveErr) = await ResolveWithProjectAsync(solution, args, ct);
        if (symbol == null) return resolveErr!;

        bool preferSource = (bool?)args?["preferSource"] ?? false;
        bool wholeType = (bool?)args?["wholeType"] ?? false;
        bool isType = symbol is INamedTypeSymbol;

        string step = "init";
        try
        {
            step = "load Microsoft.CodeAnalysis.Features";
            var featuresAsm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Microsoft.CodeAnalysis.Features")
                              ?? System.Reflection.Assembly.Load("Microsoft.CodeAnalysis.Features");

            step = "get IMetadataAsSourceFileService / MetadataAsSourceOptions types";
            var svcType = featuresAsm.GetType("Microsoft.CodeAnalysis.MetadataAsSource.IMetadataAsSourceFileService", throwOnError: true)!;
            var optType = featuresAsm.GetType("Microsoft.CodeAnalysis.MetadataAsSource.MetadataAsSourceOptions", throwOnError: true)!;

            // It's a MEF export (not an IWorkspaceService) and IMefHostExportProvider is internal too,
            // so do the whole hop via reflection: HostServices implements it at runtime.
            step = "get IMefHostExportProvider type + HostServices";
            var workspacesAsm = typeof(Solution).Assembly;   // Microsoft.CodeAnalysis.Workspaces
            var mefIfaceType = workspacesAsm.GetType("Microsoft.CodeAnalysis.Host.Mef.IMefHostExportProvider", throwOnError: true)!;
            object hostServices = workspace.Services.HostServices;
            if (!mefIfaceType.IsInstanceOfType(hostServices))
                return Err("Workspace HostServices does not implement IMefHostExportProvider.");

            step = "GetExports<IMetadataAsSourceFileService>()";
            var getExports = mefIfaceType.GetMethods()
                .First(m => m.Name == "GetExports" && m.IsGenericMethodDefinition
                            && m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 0)
                .MakeGenericMethod(svcType);
            var lazies = (System.Collections.IEnumerable)getExports.Invoke(hostServices, null)!;
            object? svc = null;
            foreach (var lazy in lazies) { svc = lazy.GetType().GetProperty("Value")!.GetValue(lazy); break; }
            if (svc == null) return Err("No IMetadataAsSourceFileService MEF export found in the host.");

            var getFile = svcType.GetMethod("GetGeneratedFileAsync")!;

            // Shape one generated file (decompiled OR SourceLink/embedded) into the result + a body verdict.
            (JObject json, bool? body) Shape(string raw, Location? idLocation, bool sourceLinkAttempt)
            {
                var fullCode = StripDecompilationLog(raw ?? "");   // drop ILSpy's "#if false // Decompilation log" trailer
                bool decompiled = fullCode.Contains("// Decompiled with ICSharpCode");
                string code; string scope; bool truncated;

                if (fullCode.Length == 0) { code = ""; scope = "none"; truncated = false; }
                else if (!wholeType && !isType && idLocation != null && idLocation.SourceSpan.Length > 0
                         && TryExtractMember(fullCode, idLocation.SourceSpan, ct, out var member))
                {
                    truncated = member.Length > MaxMemberChars;
                    code = truncated ? member.Substring(0, MaxMemberChars) + "\n// ...truncated; pass wholeType:true for the full type" : member;
                    scope = "member";
                }
                else
                {
                    truncated = fullCode.Length > MaxTypeChars;
                    code = truncated ? fullCode.Substring(0, MaxTypeChars) + "\n// ...truncated (large type); decompile a specific member instead" : fullCode;
                    scope = isType ? "type" : "type (member extraction unavailable)";
                }

                // Real body, or a ref-assembly stub? Core BCL types forwarded to System.Private.CoreLib come
                // back signature-only from decompilation; the SourceLink retry can upgrade them to real source.
                bool? body = fullCode.Length == 0 ? false : AssessBody(code, symbol, scope);
                string source = fullCode.Length == 0 ? "none" : (decompiled ? "decompiled" : "source");

                var j = new JObject
                {
                    ["available"] = true,
                    ["symbol"] = DescribeSymbol(symbol, symbol.GetDocumentationCommentId()),
                    ["fromMetadata"] = symbol.Locations.Any(l => l.IsInMetadata),
                    ["assembly"] = symbol.ContainingAssembly?.Identity?.ToString(),
                    ["scope"] = scope,
                    ["source"] = source,   // "decompiled" | "source" (SourceLink/embedded) | "none"
                    ["code"] = code,
                };
                if (body.HasValue) j["bodyAvailable"] = body.Value;
                if (truncated) j["truncated"] = true;
                j["note"] = body == false
                    ? (sourceLinkAttempt
                        ? "Only the reference-assembly signature was recoverable; neither decompilation nor SourceLink produced a body (offline, or the implementation isn't available)."
                        : "Only the reference-assembly signature was recoverable (common for core BCL types forwarded to System.Private.CoreLib).")
                    : (source == "source"
                        ? "Real source via SourceLink / embedded sources."
                        : "Bodies decompiled (ILSpy) from the implementation; for framework types the assembly identity shown is the compile-time reference assembly.");
                return (j, body);
            }

            // One GetGeneratedFileAsync attempt. NavigateToDecompiledSources always on; +SourceLink when asked.
            async Task<(JObject json, bool? body)> GenerateAsync(bool sourceLink, CancellationToken token)
            {
                var options = Activator.CreateInstance(optType)!;
                optType.GetProperty("NavigateToDecompiledSources")!.SetValue(options, true);
                if (sourceLink)
                {
                    optType.GetProperty("NavigateToSourceLinkAndEmbeddedSources")?.SetValue(options, true);
                    optType.GetProperty("AlwaysUseDefaultSymbolServers")?.SetValue(options, true);
                }
                var t = (Task)getFile.Invoke(svc, new object?[] { workspace, project, symbol, false, options, token })!;
                await t.ConfigureAwait(false);
                var file = t.GetType().GetProperty("Result")!.GetValue(t)!;
                var filePath = (string)file.GetType().GetProperty("FilePath")!.GetValue(file)!;
                var idLocation = file.GetType().GetProperty("IdentifierLocation")!.GetValue(file) as Location;
                var raw = File.Exists(filePath) ? File.ReadAllText(filePath) : "";
                return Shape(raw, idLocation, sourceLink);
            }

            // Attempt 1: decompile (or SourceLink-first if the caller forced it via preferSource).
            step = "GetGeneratedFileAsync";
            var (json, body) = await GenerateAsync(preferSource, ct);

            // Auto-retry on a ref-assembly stub: try SourceLink (real BCL source from symbol servers), bounded
            // so a slow/offline server can't hang the call. Skipped when the caller already forced source.
            if (!preferSource && body == false)
            {
                step = "SourceLink retry";
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(SourceLinkTimeoutSec));
                try
                {
                    var (json2, body2) = await GenerateAsync(true, timeout.Token);
                    if (body2 == true) return json2;             // upgraded to a real body / source
                    json["sourceLinkTried"] = true;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    json["sourceLinkTried"] = true;
                    json["note"] = (string?)json["note"] + " (SourceLink retry timed out.)";
                }
                catch { json["sourceLinkTried"] = true; }
            }
            return json;
        }
        catch (Exception e)
        {
            return Err($"decompile failed at step [{step}]: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// Extract just the member declaration (with leading doc comments / attributes) that contains
    /// <paramref name="span"/> from decompiled C# text. Uses the already-loaded Microsoft.CodeAnalysis.CSharp
    /// via reflection only to (a) parse and (b) type-test MemberDeclarationSyntax; the tree/node walk is all
    /// base Roslyn (Common) API. Dependency-free; returns false (caller falls back to the whole type) on any miss.
    /// </summary>
    private static bool TryExtractMember(string code, TextSpan span, CancellationToken ct, out string member)
    {
        member = "";
        try
        {
            var csharpAsm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Microsoft.CodeAnalysis.CSharp");
            var treeType = csharpAsm?.GetType("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree");
            var memberType = csharpAsm?.GetType("Microsoft.CodeAnalysis.CSharp.Syntax.MemberDeclarationSyntax");
            if (treeType == null || memberType == null) return false;

            var parse = treeType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .First(m => m.Name == "ParseText" && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == typeof(string));
            var ps = parse.GetParameters();
            var argv = new object?[ps.Length];
            argv[0] = code;
            for (int i = 1; i < ps.Length; i++)
                argv[i] = ps[i].ParameterType == typeof(CancellationToken) ? ct
                        : ps[i].HasDefaultValue ? ps[i].DefaultValue
                        : ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null;

            var tree = (SyntaxTree)parse.Invoke(null, argv)!;          // base Common type
            var root = tree.GetRoot(ct);
            if (span.End > root.FullSpan.End) return false;
            SyntaxNode? node = root.FindNode(span);
            while (node != null && !memberType.IsInstanceOfType(node)) node = node.Parent;
            if (node == null) return false;
            member = node.ToFullString().Trim();                       // includes leading doc comments / attributes
            return member.Length > 0;
        }
        catch { return false; }
    }

    /// <summary>Strip ILSpy's trailing "#if false // Decompilation log … #endif" noise block.</summary>
    private static string StripDecompilationLog(string code)
    {
        var m = Regex.Match(code, @"\r?\n#if false\s*//\s*Decompilation log[\s\S]*$");
        return m.Success ? code.Substring(0, m.Index).TrimEnd() + "\n" : code;
    }

    /// <summary>
    /// Did we recover a real implementation body, or only a ref-assembly stub? Two stub forms: signature-only
    /// (declaration ends without a block/expression body) and ILSpy's "{ throw null; }". Returns null when a
    /// body verdict isn't meaningful (e.g. a field, or a non-method member).
    /// </summary>
    private static bool? AssessBody(string code, ISymbol symbol, string scope)
    {
        bool throwNull = Regex.IsMatch(code, @"\{\s*throw null;\s*\}");
        if (scope == "member")
        {
            if (symbol is not IMethodSymbol m || m.IsAbstract) return null;   // only assess methods
            bool hasBody = code.Contains("=>") || code.Contains("{");
            return hasBody && !throwNull;
        }
        // type scope: a signatures-only type has no "method() {" bodies at all.
        if (throwNull) return false;
        return Regex.IsMatch(code, @"\)\s*\r?\n?\s*\{") || code.Contains("=>");
    }

    /// <summary>Like ResolveSymbolAsync but also returns the project the symbol was resolved from.</summary>
    private static async Task<(ISymbol? symbol, Project? project, JObject? error)> ResolveWithProjectAsync(Solution solution, JToken args, CancellationToken ct)
    {
        var symbolId = (string?)args?["symbolId"];
        if (!string.IsNullOrWhiteSpace(symbolId))
        {
            foreach (var project in solution.Projects)
            {
                ct.ThrowIfCancellationRequested();
                var comp = await project.GetCompilationAsync(ct).ConfigureAwait(false);
                if (comp == null) continue;
                var sym = DocumentationCommentId.GetFirstSymbolForDeclarationId(symbolId, comp);
                if (sym != null) return (sym, project, null);
            }
            return (null, null, Err($"No symbol found for symbolId '{symbolId}'."));
        }

        var file = (string?)args?["file"];
        var line = (int?)args?["line"];
        if (!string.IsNullOrWhiteSpace(file) && line.HasValue)
        {
            var doc = FindDocument(solution, file!);
            if (doc == null) return (null, null, Err($"File not in the solution: {file}"));
            var text = await doc.GetTextAsync(ct).ConfigureAwait(false);
            if (line.Value < 1 || line.Value > text.Lines.Count) return (null, null, Err($"line {line} out of range."));
            int col = (int?)args?["column"] ?? 1;
            var ln = text.Lines[line.Value - 1];
            int pos = Math.Min(ln.Start + Math.Max(0, col - 1), Math.Max(ln.Start, ln.EndIncludingLineBreak - 1));
            var sym = await SymbolFinder.FindSymbolAtPositionAsync(doc, pos, ct).ConfigureAwait(false);
            if (sym == null) return (null, null, Err($"No symbol at {file}:{line}."));
            return (sym, doc.Project, null);
        }

        return (null, null, Err("Provide 'symbolId' or 'file'+'line'."));
    }

    // ---- Symbol resolution (symbolId | file:line) ------------------------------------------------------

    /// <summary>
    /// Resolve the tool args to exactly one <see cref="ISymbol"/>: prefer a <c>symbolId</c>
    /// (DocumentationCommentId), else a <c>file</c>+<c>line</c>(+<c>column</c>) position. Returns
    /// (symbol, null) on success or (null, errorJson) with a model-actionable message.
    /// </summary>
    private static async Task<(ISymbol? symbol, JObject? error)> ResolveSymbolAsync(Solution solution, JToken args, CancellationToken ct)
    {
        var symbolId = (string?)args?["symbolId"];
        if (!string.IsNullOrWhiteSpace(symbolId))
        {
            foreach (var project in solution.Projects)
            {
                ct.ThrowIfCancellationRequested();
                var comp = await project.GetCompilationAsync(ct).ConfigureAwait(false);
                if (comp == null) continue;
                var sym = DocumentationCommentId.GetFirstSymbolForDeclarationId(symbolId, comp);
                if (sym != null) return (sym, null);
            }
            return (null, Err($"No symbol found for symbolId '{symbolId}'. Get a valid id from vs_search_symbols."));
        }

        var file = (string?)args?["file"];
        var line = (int?)args?["line"];
        if (!string.IsNullOrWhiteSpace(file) && line.HasValue)
        {
            int col = (int?)args?["column"] ?? 1;
            var doc = FindDocument(solution, file!);
            if (doc == null) return (null, Err($"File is not part of the loaded solution: {file}"));
            var text = await doc.GetTextAsync(ct).ConfigureAwait(false);
            if (line.Value < 1 || line.Value > text.Lines.Count)
                return (null, Err($"line {line} is out of range for {file} (1..{text.Lines.Count})."));
            var lineStart = text.Lines[line.Value - 1];
            int pos = Math.Min(lineStart.Start + Math.Max(0, col - 1), lineStart.EndIncludingLineBreak - 1);
            var sym = await SymbolFinder.FindSymbolAtPositionAsync(doc, pos, ct).ConfigureAwait(false);
            if (sym == null) return (null, Err($"No symbol at {file}:{line}:{col}. Point at an identifier."));
            return (sym, null);
        }

        return (null, Err("Provide either 'symbolId' (from vs_search_symbols) or 'file'+'line' (+optional 'column')."));
    }

    /// <summary>Find a document by path — exact first, then a case-insensitive fallback (Windows paths).</summary>
    private static Document? FindDocument(Solution solution, string filePath)
    {
        // Exact match first (fast path). Then retry normalized: agents naturally pass forward-slash paths
        // (C:/…/X.cs) but Roslyn stores backslash paths, so a literal compare misses. Normalize separators
        // and compare case-insensitively (Windows paths) so either slash style resolves.
        var ids = solution.GetDocumentIdsWithFilePath(filePath);
        if (!ids.IsEmpty) return solution.GetDocument(ids[0]);

        var norm = NormalizePath(filePath);
        ids = solution.GetDocumentIdsWithFilePath(norm);   // backslash form may hit the index directly
        if (!ids.IsEmpty) return solution.GetDocument(ids[0]);

        foreach (var project in solution.Projects)
            foreach (var doc in project.Documents)
                if (doc.FilePath != null && string.Equals(NormalizePath(doc.FilePath), norm, StringComparison.OrdinalIgnoreCase))
                    return doc;
        return null;
    }

    /// <summary>Canonicalize a file path for comparison: forward slashes -> backslashes, no trailing slash.</summary>
    private static string NormalizePath(string path) =>
        string.IsNullOrEmpty(path) ? "" : path.Replace('/', '\\').TrimEnd('\\');

    // ---- Shaping helpers -------------------------------------------------------------------------------

    /// <summary>Compact, model-friendly description of one symbol + its primary source location.</summary>
    private static JObject DescribeSymbol(ISymbol sym, string? symbolId)
    {
        var loc = sym.Locations.FirstOrDefault(l => l.IsInSource);
        var obj = new JObject
        {
            ["name"] = sym.Name,
            ["kind"] = sym.Kind.ToString(),
            ["signature"] = sym.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            ["container"] = sym.ContainingSymbol?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            ["symbolId"] = symbolId,
        };
        if (loc != null)
        {
            var span = loc.GetLineSpan();
            obj["file"] = span.Path;
            obj["line"] = span.StartLinePosition.Line + 1;     // 1-based for the model / editor
            obj["column"] = span.StartLinePosition.Character + 1;
        }
        return obj;
    }

    /// <summary>{file, line, column, snippet} for a source location, with a per-call text cache.</summary>
    private static async Task<JObject> LocationJsonAsync(
        Document? doc, Location loc, Dictionary<DocumentId, SourceText> cache, bool isImplicit, CancellationToken ct)
    {
        var span = loc.GetLineSpan();
        int lineIdx = span.StartLinePosition.Line;
        var obj = new JObject
        {
            ["file"] = span.Path,
            ["line"] = lineIdx + 1,
            ["column"] = span.StartLinePosition.Character + 1,
        };
        if (isImplicit) obj["implicit"] = true;
        if (doc != null)
        {
            if (!cache.TryGetValue(doc.Id, out var text))
            {
                text = await doc.GetTextAsync(ct).ConfigureAwait(false);
                cache[doc.Id] = text;
            }
            if (lineIdx >= 0 && lineIdx < text.Lines.Count)
                obj["snippet"] = Cap(text.Lines[lineIdx].ToString().Trim(), SnippetChars);
        }
        return obj;
    }

    private static void Mark(JObject payload, bool truncated, int cap, string unit)
    {
        if (!truncated) return;
        payload["truncated"] = true;
        payload["note"] = $"capped at {cap} {unit}; narrow the query for more.";
    }

    private static string Cap(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "…";
}
