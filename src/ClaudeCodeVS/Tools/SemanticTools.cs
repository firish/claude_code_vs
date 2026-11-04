using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVs.CodeModel;
using ClaudeCodeVs.Editor;
using ClaudeCodeVs.Protocol;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Tools;

/// <summary>
/// Tools for the <c>vs-semantic</c> MCP server — Roslyn semantic navigation (the static-analysis axis,
/// distinct from the runtime debugger surface). All read-only and managed (C#/VB) only. They give the model
/// Visual Studio's resolved understanding of the code — symbols, references, implementations, hierarchies —
/// instead of grep's text guesses. See <see cref="RoslynReader"/> for the workspace access + threading model.
/// </summary>
internal static class SemanticSchemas
{
    /// <summary>The shared addressing schema: a symbolId (from vs_search_symbols) OR a file+line position.</summary>
    public static JObject Target(string verb) => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject
        {
            ["symbolId"] = new JObject
            {
                ["type"] = "string",
                ["description"] = "Roslyn DocumentationCommentId from vs_search_symbols (e.g. \"M:Ns.Type.Method(System.Int32)\"). Preferred — names exactly one symbol.",
            },
            ["file"] = new JObject
            {
                ["type"] = "string",
                ["description"] = "Absolute path to a source file (alternative to symbolId): " + verb + " the symbol at this file+line.",
            },
            ["line"] = new JObject { ["type"] = "integer", ["description"] = "1-based line of the symbol/identifier (use with file)." },
            ["column"] = new JObject { ["type"] = "integer", ["description"] = "1-based column on that line (optional; defaults to 1)." },
        },
    };
}

/// <summary>
/// vs_get_selection — what the user has selected (or where the caret is) in the active VS editor, enriched
/// with the Roslyn symbol at that position. Reads the cached <see cref="SelectionService"/> snapshot (the
/// same state the dormant getCurrentSelection IDE-channel tool reads + the selection_changed push uses), so
/// no UI-thread hop for the selection itself. The symbol enrichment turns "this" into a navigable symbolId.
/// </summary>
internal sealed class VsGetSelectionTool : IIdeTool
{
    public string Name => "vs_get_selection";
    public string Description =>
        "Get what the user currently has selected (or where the caret is) in the active Visual Studio editor: "
        + "the selected text, file path, and 0-based line/character range. When that file is in a loaded C#/VB "
        + "solution, ALSO resolves the symbol at the selection and includes it under \"symbol\" with a symbolId "
        + "you can feed straight to vs_find_references / vs_go_to_definition / vs_call_hierarchy. Use this to "
        + "act on \"this\" / \"the selected code\" / \"the method I'm looking at\" without the user pasting it.";

    public JToken Schema => new JObject { ["type"] = "object", ["properties"] = new JObject() };

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        var sel = (JObject)SelectionService.CurrentAsJson();   // cached snapshot; thread-safe, no UI hop
        try
        {
            var file = (string?)sel["filePath"];
            var start = sel["selection"]?["start"];
            if (!string.IsNullOrEmpty(file) && start != null)
            {
                int line1 = ((int?)start["line"] ?? 0) + 1;        // selection range is 0-based -> 1-based
                int col1 = ((int?)start["character"] ?? 0) + 1;
                var symbol = await RoslynReader.SymbolAtPositionAsync(file!, line1, col1, ct);
                if (symbol != null) sel["symbol"] = symbol;
            }
        }
        catch { /* a Roslyn hiccup must never break the plain selection read */ }

        int len = ((string?)sel["text"])?.Length ?? 0;
        var sym = (string?)sel["symbol"]?["name"];
        Log.Info($"vs_get_selection -> {len} char(s){(sym != null ? $", symbol {sym}" : "")}");
        return sel;
    }
}

/// <summary>
/// vs_solution_model - the evaluated project graph: real target frameworks, resolved package versions
/// (transitive included), project references, the active configuration, the analyzer set and evaluated
/// MSBuild properties. The answer to "what does this project actually build with", which reading the
/// .csproj cannot give once Directory.Build.props or Central Package Management is in play.
/// </summary>
internal sealed class VsSolutionModelTool : IIdeTool
{
    public string Name => "vs_solution_model";
    public string Description =>
        "Get Visual Studio's EVALUATED model of the open solution - what MSBuild and NuGet actually resolved, "
        + "not what the project files say. Per project: real target frameworks, project references, NuGet "
        + "packages with their RESOLVED versions, the active configuration/platform, and the analyzer count. "
        + "Use this instead of reading .csproj files: values inherited from Directory.Build.props / "
        + "Directory.Build.targets, versions supplied by Central Package Management (Directory.Packages.props) "
        + "and every transitive package are invisible in the project file. Omit 'project' for the whole-solution "
        + "overview. Pass project = a project name or the path of any file in it for full detail: the transitive "
        + "package closure with what pulled each one in, the analyzer set grouped by origin, and the evaluated "
        + "MSBuild properties (LangVersion, Nullable, TreatWarningsAsErrors, NoWarn, ...). 'properties' evaluates "
        + "any other MSBuild property by name. Package data comes from the last NuGet restore; C++ and other "
        + "non-Roslyn projects report configuration and properties only.";

    public JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject
        {
            ["project"] = new JObject
            {
                ["type"] = "string",
                ["description"] = "Read one project in full. Accepts a project name (or a substring of one) or "
                                + "the path of any file inside it. Omit for the solution overview.",
            },
            ["properties"] = new JObject
            {
                ["type"] = "array",
                ["items"] = new JObject { ["type"] = "string" },
                ["description"] = "Extra MSBuild property names to evaluate, e.g. [\"WarningLevel\", \"DefineConstants\"]. "
                                + $"Up to {SolutionModelReader.MaxExtraPropertiesProject} with 'project', "
                                + $"{SolutionModelReader.MaxExtraPropertiesSolution} across the whole solution.",
            },
        },
    };

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        var project = (string?)args?["project"];
        var props = new System.Collections.Generic.List<string>();
        if (args?["properties"] is JArray requested)
            foreach (var p in requested)
                if ((string?)p is { Length: > 0 } name) props.Add(name);

        JObject result;
        try
        {
            result = await SolutionModelReader.ReadAsync(project, props, ct);
        }
        catch (System.Exception e)
        {
            Log.Warn($"vs_solution_model failed: {e.Message}");
            return new JObject { ["available"] = true, ["error"] = e.Message };
        }

        Log.Info($"vs_solution_model(project={project ?? "*"}) -> "
               + ((bool?)result["available"] == false ? "unavailable"
                  : result["error"] != null ? $"error: {(string?)result["error"]}"
                  : $"{(int?)result["projectCount"] ?? 0} project(s)"));
        return result;
    }
}

/// <summary>vs_decompile — a framework/NuGet symbol with no source → decompiled C# (the Go-To-Definition path).</summary>
internal sealed class VsDecompileTool : IIdeTool
{
    public string Name => "vs_decompile";
    public string Description =>
        "Decompile a framework or NuGet symbol that ships WITHOUT source - a method or type in a referenced "
        + "DLL - to C#, the way Go-To-Definition does (ILSpy under the hood). This is the one thing reading the "
        + "repo can't give you: the actual body of a library call ('what does this method really do?'). Returns "
        + "real implementation bodies, not reference-assembly stubs. Address the symbol by symbolId (e.g. "
        + "T:System.Linq.Enumerable, or the symbolId from vs_go_to_definition / vs_get_selection on a usage) or "
        + "by file+line on a call site. By default returns just the requested MEMBER's source; pass "
        + "wholeType:true for the entire containing type. Core BCL types (String, Int32, …) only decompile to "
        + "a signature stub ({\"bodyAvailable\":false}) - the tool then auto-retries via SourceLink to fetch the "
        + "REAL .NET source when online; pass preferSource:true to force that source-first. C#/VB only; capped + signaled.";

    public JToken Schema
    {
        get
        {
            var s = SemanticSchemas.Target("decompile");
            var props = (JObject)s["properties"]!;
            props["wholeType"] = new JObject
            {
                ["type"] = "boolean",
                ["description"] = "Return the whole containing type instead of just the target member (default false).",
            };
            props["preferSource"] = new JObject
            {
                ["type"] = "boolean",
                ["description"] = "Fetch real source via SourceLink/embedded sources first (real .NET/library source, needs network), instead of decompiling. Default false - a stub auto-retries SourceLink anyway.",
            };
            return s;
        }
    }

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        var result = await RoslynReader.DecompileAsync(args ?? new JObject(), ct);
        Log.Info($"vs_decompile -> scope={(string?)result["scope"] ?? "?"}, len={((string?)result["code"])?.Length ?? 0}, err={(string?)result["error"]}");
        return result;
    }
}

/// <summary>vs_search_symbols — name → candidate symbols with stable ids (the addressing primitive).</summary>
internal sealed class VsSearchSymbolsTool : IIdeTool
{
    public string Name => "vs_search_symbols";
    public string Description =>
        "Search the loaded C#/VB solution for declared symbols (types, methods, properties, fields) whose "
        + "name contains the query (case-insensitive). Returns candidates each with a stable symbolId "
        + "(Roslyn DocumentationCommentId) plus kind, signature, container, and source file:line. This is the "
        + "semantic 'where is X declared', and the symbolId it returns is what the other vs-semantic tools take "
        + "to name one exact symbol. C#/VB only; needs a loaded project (returns {\"available\":false} otherwise).";

    public JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject
        {
            ["query"] = new JObject { ["type"] = "string", ["description"] = "Name or substring to match against declared symbol names (case-insensitive)." },
        },
        ["required"] = new JArray("query"),
    };

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        var query = (string?)args?["query"] ?? "";
        var result = await RoslynReader.SearchSymbolsAsync(query, ct);
        Log.Info($"vs_search_symbols('{query}') -> {Summary(result, "symbols")}");
        return result;
    }

    internal static string Summary(JObject r, string arrayKey)
    {
        if ((bool?)r["available"] == false) return "unavailable (no project)";
        if (r["error"] != null) return $"error: {(string?)r["error"]}";
        int n = (r[arrayKey] as JArray)?.Count ?? (int?)r["count"] ?? (int?)r["referenceCount"] ?? (int?)r["implementationCount"] ?? 0;
        return $"{n} result(s)";
    }
}

/// <summary>vs_find_references — semantic Find-All-References (not grep).</summary>
internal sealed class VsFindReferencesTool : IIdeTool
{
    public string Name => "vs_find_references";
    public string Description =>
        "Find ALL references to a symbol across the loaded C#/VB solution — the semantic, ground-truth answer to "
        + "'where is this used'. Unlike text search it resolves through interfaces, overrides, partial classes, "
        + "generics, and aliases, and excludes matches in comments/strings. Address the symbol by symbolId (from "
        + "vs_search_symbols, preferred) or by file+line. Returns each use as file:line:column with a code snippet.";

    public JToken Schema => SemanticSchemas.Target("find references to");

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        var result = await RoslynReader.FindReferencesAsync(args ?? new JObject(), ct);
        Log.Info($"vs_find_references -> {VsSearchSymbolsTool.Summary(result, "references")}");
        return result;
    }
}

/// <summary>vs_go_to_definition — resolve to the one definition among overloads/duplicates.</summary>
internal sealed class VsGoToDefinitionTool : IIdeTool
{
    public string Name => "vs_go_to_definition";
    public string Description =>
        "Resolve a symbol to its definition — the RIGHT one among overloads or many same-named types, where grep "
        + "would return all of them. Address by symbolId (from vs_search_symbols) or by file+line (cursor-style: "
        + "resolves the symbol referenced at that position and jumps to where it's declared). Returns the "
        + "declaration location(s), signature, and XML doc. C#/VB only.";

    public JToken Schema => SemanticSchemas.Target("go to the definition of");

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        var result = await RoslynReader.GoToDefinitionAsync(args ?? new JObject(), ct);
        Log.Info($"vs_go_to_definition -> {VsSearchSymbolsTool.Summary(result, "definitions")}");
        return result;
    }
}

/// <summary>vs_find_implementations — interfaces/abstract/virtual members → concrete implementors + overrides.</summary>
internal sealed class VsFindImplementationsTool : IIdeTool
{
    public string Name => "vs_find_implementations";
    public string Description =>
        "Find the concrete implementations of an interface or interface member, or the overrides of an abstract/"
        + "virtual member, or the derived classes of a base class — exact, via Roslyn (grep's ': IFoo' misses "
        + "indirect implementors and explicit interface implementations). Address by symbolId (from "
        + "vs_search_symbols) or file+line. C#/VB only.";

    public JToken Schema => SemanticSchemas.Target("find implementations of");

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        var result = await RoslynReader.FindImplementationsAsync(args ?? new JObject(), ct);
        Log.Info($"vs_find_implementations -> {VsSearchSymbolsTool.Summary(result, "implementations")}");
        return result;
    }
}

/// <summary>vs_call_hierarchy — transitive callers (impact analysis) or direct callees of a method.</summary>
internal sealed class VsCallHierarchyTool : IIdeTool
{
    public string Name => "vs_call_hierarchy";
    public string Description =>
        "Build the call hierarchy of a method. direction='callers' (default) returns who TRANSITIVELY calls it "
        + "(recursive, depth-limited, cycle-guarded) as a tree with call sites — impact analysis for a change. "
        + "direction='callees' returns what the method directly calls (depth 1). Address the method by symbolId "
        + "(from vs_search_symbols) or file+line. C#/VB only.";

    public JToken Schema
    {
        get
        {
            var s = SemanticSchemas.Target("build the call hierarchy of");
            ((JObject)s["properties"]!)["direction"] = new JObject
            {
                ["type"] = "string",
                ["enum"] = new JArray("callers", "callees"),
                ["description"] = "'callers' (default) = who calls this (transitive); 'callees' = what this calls (direct).",
            };
            return s;
        }
    }

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        var result = await RoslynReader.CallHierarchyAsync(args ?? new JObject(), ct);
        var dir = (string?)result["direction"] ?? "callers";
        Log.Info($"vs_call_hierarchy ({dir}) -> {VsSearchSymbolsTool.Summary(result, dir)}");
        return result;
    }
}

/// <summary>vs_type_hierarchy — base chain + interfaces, or derived types.</summary>
internal sealed class VsTypeHierarchyTool : IIdeTool
{
    public string Name => "vs_type_hierarchy";
    public string Description =>
        "Get a type's hierarchy. direction='derived' (default) returns the types that derive from / implement it; "
        + "direction='base' returns its base-class chain plus implemented interfaces. Exact via Roslyn "
        + "(FindDerivedClasses/Implementations), not text matching. Address the type by symbolId (from "
        + "vs_search_symbols) or file+line. C#/VB only.";

    public JToken Schema
    {
        get
        {
            var s = SemanticSchemas.Target("get the type hierarchy of");
            ((JObject)s["properties"]!)["direction"] = new JObject
            {
                ["type"] = "string",
                ["enum"] = new JArray("derived", "base"),
                ["description"] = "'derived' (default) = subtypes/implementors; 'base' = base chain + interfaces.",
            };
            return s;
        }
    }

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        var result = await RoslynReader.TypeHierarchyAsync(args ?? new JObject(), ct);
        Log.Info($"vs_type_hierarchy ({(string?)result["direction"]}) -> {VsSearchSymbolsTool.Summary(result, "types")}");
        return result;
    }
}
