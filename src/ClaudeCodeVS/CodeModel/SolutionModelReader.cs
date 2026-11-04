using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVs.Building;
using ClaudeCodeVs.Protocol;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;   // await TaskScheduler.Default
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RoslynProject = Microsoft.CodeAnalysis.Project;
using RoslynSolution = Microsoft.CodeAnalysis.Solution;

namespace ClaudeCodeVs.CodeModel;

/// <summary>
/// The EVALUATED project graph behind <c>vs_solution_model</c>: what MSBuild and NuGet actually resolved
/// for the open solution, as opposed to what the project files say. Reading a .csproj as text misses
/// everything that matters in a modern repo - values inherited from Directory.Build.props, versions
/// supplied by Central Package Management, and the whole transitive package closure - because none of it
/// is written in the file the model would open.
///
/// Three sources, each the authority for its slice, stitched together by project file path:
/// * <b>EnvDTE + IVsBuildPropertyStorage</b> (UI thread): the project list, the active
///   configuration/platform per project, and EVALUATED MSBuild properties. GetPropertyValue returns the
///   value after every import has been applied, which is exactly the Directory.Build.props answer.
/// * <b>The Roslyn workspace</b> (free-threaded snapshot): language, effective language version,
///   project-to-project references and the analyzer set - the same objects the vs-semantic tools hold.
/// * <b>obj/project.assets.json</b>: NuGet's own resolved graph from the last restore. It is the only
///   place the resolved version of a transitive package exists at all, and it records which direct
///   dependencies had their version supplied centrally. Located via the evaluated ProjectAssetsFile
///   property, so a repo that redirects obj/ is still found.
///
/// Threading follows <see cref="RoslynReader"/>: take everything COM-bound on the UI thread in one pass,
/// then hop off it for the Roslyn walk and the JSON parsing.
/// </summary>
internal static class SolutionModelReader
{
    private const int MaxProjects = 150;
    private const int MaxDirectPackages = 80;
    private const int MaxTransitivePackages = 300;
    private const int MaxAnalyzerGroups = 60;
    private const int MaxRequestedBy = 3;
    public const int MaxExtraPropertiesSolution = 8;
    public const int MaxExtraPropertiesProject = 40;

    // Read for EVERY project. Each is one COM call on the UI thread, so the list stays short.
    private static readonly string[] CoreProps =
    {
        "TargetFramework", "TargetFrameworks", "TargetFrameworkMoniker", "OutputType",
        "UsingMicrosoftNETSdk", "ProjectAssetsFile", "MSBuildProjectExtensionsPath",
        "ManagePackageVersionsCentrally",
        "DirectoryBuildPropsPath", "DirectoryBuildTargetsPath", "DirectoryPackagesPropsPath",
    };

    // Added when one project is asked for by name: the properties that most often differ from what the
    // project file appears to say, plus the ones that decide how strict the analyzers are.
    private static readonly string[] DetailProps =
    {
        "AssemblyName", "RootNamespace", "LangVersion", "Nullable", "ImplicitUsings",
        "TreatWarningsAsErrors", "WarningsAsErrors", "NoWarn",
        "AnalysisLevel", "AnalysisMode", "EnableNETAnalyzers", "EnforceCodeStyleInBuild",
        "PlatformTarget", "RuntimeIdentifier", "RuntimeIdentifiers",
        "OutputPath", "CentralPackageTransitivePinningEnabled", "RestorePackagesWithLockFile",
    };

    private const string MiscFilesKind = "{66A2671D-8FB5-11D2-AA7E-00C04F688DDE}";
    private const string UnloadedKind = "{67294A52-A4F0-11D2-AA88-00C04F688DDE}";

    private static readonly Regex PropertyName = new(@"^[A-Za-z_][A-Za-z0-9_.\-]*$", RegexOptions.Compiled);
    private static readonly Regex TfmSuffix = new(@"\s*\(([^)]+)\)\s*$", RegexOptions.Compiled);

    /// <summary>What the UI-thread pass captures per project; everything after works from these.</summary>
    private sealed class ProjectSnap
    {
        public string Name = "";
        public string UniqueName = "";
        public string? Path;
        public bool Unloaded;
        public string? Configuration, Platform;
        public bool? Builds;
        public readonly Dictionary<string, string> Props = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class Pkg
    {
        public string Id = "", Version = "";
        public string? Requested;
        public bool CentrallyManaged, Implicit;
        public List<string>? RequestedBy;
    }

    private sealed class FrameworkModel
    {
        public string Tfm = "";
        public readonly List<Pkg> Direct = new();
        public readonly List<Pkg> Transitive = new();
    }

    public static async Task<JObject> ReadAsync(string? projectArg, IReadOnlyList<string> extraProps, CancellationToken ct)
    {
        // ---- 1. UI thread: everything COM-bound, in one pass ---------------------------------------
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        var dte = ServiceProvider.GlobalProvider.GetService(typeof(SDTE)) as EnvDTE.DTE;
        var dteSolution = dte?.Solution;
        if (dteSolution == null || !dteSolution.IsOpen)
            return Unavailable("no solution is open in Visual Studio");

        string? onlyUnique = null;
        if (!string.IsNullOrWhiteSpace(projectArg))
        {
            var (unique, _) = SolutionBuilder.ResolveProject(dteSolution, projectArg!.Trim());
            if (unique == null)
            {
                var names = new JArray();
                foreach (var p in SolutionBuilder.AllProjects(dteSolution))
                    try { if (!string.IsNullOrEmpty(p.Name)) names.Add(p.Name); } catch { }
                return new JObject
                {
                    ["available"] = true,
                    ["error"] = $"no project in the solution matching '{projectArg}'",
                    ["projects"] = names,
                };
            }
            onlyUnique = unique;
        }
        bool detail = onlyUnique != null;

        var propNames = new List<string>(CoreProps);
        if (detail) propNames.AddRange(DetailProps);
        var extras = extraProps
            .Where(n => !string.IsNullOrWhiteSpace(n) && PropertyName.IsMatch(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(detail ? MaxExtraPropertiesProject : MaxExtraPropertiesSolution)
            .ToList();
        propNames.AddRange(extras);

        var result = new JObject { ["available"] = true };
        try { result["solution"] = dteSolution.FullName; } catch { }

        var contexts = ReadSolutionContexts(dteSolution, result);
        var vsSolution = ServiceProvider.GlobalProvider.GetService(typeof(SVsSolution)) as IVsSolution;

        var snaps = new List<ProjectSnap>();
        bool projectsTruncated = false;
        foreach (var p in SolutionBuilder.AllProjects(dteSolution))
        {
            var snap = Snapshot(p);
            if (snap == null) continue;
            if (onlyUnique != null && !string.Equals(snap.UniqueName, onlyUnique, StringComparison.OrdinalIgnoreCase)) continue;
            if (snaps.Count >= MaxProjects) { projectsTruncated = true; break; }

            if (contexts.TryGetValue(snap.UniqueName, out var c))
            {
                snap.Configuration = c.config;
                snap.Platform = c.platform;
                snap.Builds = c.builds;
            }
            if (!snap.Unloaded) ReadProperties(vsSolution, snap, propNames);
            snaps.Add(snap);
        }

        if (snaps.Count == 0)
        {
            return Unavailable("the solution has no projects (a folder opened without a solution has no "
                             + "evaluated project model - open the .sln/.slnx)");
        }

        var roslyn = RoslynReader.GetWorkspace()?.CurrentSolution;

        // ---- 2. Off the UI thread: Roslyn walk + project.assets.json ---------------------------------
        await TaskScheduler.Default;

        var roslynByPath = new Dictionary<string, List<RoslynProject>>(StringComparer.OrdinalIgnoreCase);
        if (roslyn != null)
        {
            foreach (var rp in roslyn.Projects)
            {
                if (string.IsNullOrEmpty(rp.FilePath)) continue;
                var key = Norm(rp.FilePath!);
                if (!roslynByPath.TryGetValue(key, out var list)) roslynByPath[key] = list = new List<RoslynProject>();
                list.Add(rp);
            }
        }

        var projects = new JArray();
        foreach (var snap in snaps)
        {
            ct.ThrowIfCancellationRequested();
            List<RoslynProject>? rps = null;
            if (snap.Path != null) roslynByPath.TryGetValue(Norm(snap.Path), out rps);
            projects.Add(Describe(snap, rps, roslyn, detail, extras));
        }

        result["scope"] = detail ? "project" : "solution";
        result["projectCount"] = projects.Count;
        result["projects"] = projects;
        if (projectsTruncated)
        {
            result["truncated"] = true;
            result["truncatedNote"] = $"capped at {MaxProjects} projects - pass project= to read one in full";
        }
        if (!detail)
            result["hint"] = "pass project = a project name or the path of any file in it for the transitive "
                           + "package closure, the analyzer set and the full evaluated-property list";
        return result;
    }

    private static JObject Unavailable(string reason) => new JObject { ["available"] = false, ["reason"] = reason };

    // ---------------- UI-thread capture ----------------

    /// <summary>
    /// The active solution configuration (top-level, into <paramref name="result"/>) and what it maps each
    /// project to. A project's own configuration can differ from the solution's - "Release" at the top
    /// routinely builds one project as "Debug", or skips it - so both levels are reported.
    /// </summary>
    private static Dictionary<string, (string config, string platform, bool builds)> ReadSolutionContexts(
        EnvDTE.Solution solution, JObject result)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var map = new Dictionary<string, (string, string, bool)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var sb = solution.SolutionBuild;
            var active = sb?.ActiveConfiguration;
            if (active == null) return map;

            result["activeConfiguration"] = active.Name;
            if (active is EnvDTE80.SolutionConfiguration2 sc2) result["activePlatform"] = sc2.PlatformName;

            foreach (EnvDTE.SolutionContext ctx in active.SolutionContexts)
            {
                try { map[ctx.ProjectName] = (ctx.ConfigurationName, ctx.PlatformName, ctx.ShouldBuild); }
                catch { /* a context for an unloaded project can throw on read */ }
            }

            if (sb!.StartupProjects is Array startup && startup.Length > 0)
            {
                var arr = new JArray();
                foreach (var s in startup)
                    if (s is string unique) arr.Add(System.IO.Path.GetFileNameWithoutExtension(unique));
                if (arr.Count > 0) result["startupProjects"] = arr;
            }
        }
        catch (Exception e)
        {
            Log.Warn($"vs_solution_model: could not read the solution configuration: {e.Message}");
        }
        return map;
    }

    private static ProjectSnap? Snapshot(EnvDTE.Project p)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            string kind = "";
            try { kind = p.Kind ?? ""; } catch { }
            if (string.Equals(kind, MiscFilesKind, StringComparison.OrdinalIgnoreCase)) return null;

            var snap = new ProjectSnap { Name = p.Name ?? "", UniqueName = p.UniqueName ?? "" };
            snap.Unloaded = string.Equals(kind, UnloadedKind, StringComparison.OrdinalIgnoreCase);
            try
            {
                var full = p.FullName;
                if (!string.IsNullOrEmpty(full) && File.Exists(full)) snap.Path = full;
            }
            catch { /* some project kinds have no backing file */ }
            return snap.Name.Length == 0 ? null : snap;
        }
        catch { return null; }
    }

    /// <summary>
    /// Evaluated MSBuild properties through the project's own hierarchy. This is the read that answers
    /// "what is LangVersion here" when the project file never mentions it: the value comes back with
    /// every import - SDK props, Directory.Build.props, a repo-wide .targets - already applied.
    /// </summary>
    private static void ReadProperties(IVsSolution? vsSolution, ProjectSnap snap, List<string> names)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (vsSolution == null || snap.UniqueName.Length == 0) return;
        try
        {
            if (ErrorHandler.Failed(vsSolution.GetProjectOfUniqueName(snap.UniqueName, out IVsHierarchy hier))
                || hier is not IVsBuildPropertyStorage storage)
                return;

            foreach (var name in names)
            {
                try
                {
                    // "" = the project's active configuration.
                    if (ErrorHandler.Succeeded(storage.GetPropertyValue(name, "", (uint)_PersistStorageType.PST_PROJECT_FILE, out string value))
                        && !string.IsNullOrEmpty(value))
                        snap.Props[name] = value;
                }
                catch { /* an unknown property is a failed HRESULT or a throw, depending on the project system */ }
            }
        }
        catch (Exception e)
        {
            Log.Warn($"vs_solution_model: property read failed for {snap.Name}: {e.Message}");
        }
    }

    // ---------------- assembling one project ----------------

    private static JObject Describe(ProjectSnap snap, List<RoslynProject>? rps, RoslynSolution? roslyn,
                                    bool detail, List<string> extras)
    {
        var o = new JObject { ["name"] = snap.Name };
        if (snap.Path != null) o["path"] = snap.Path;
        if (snap.Unloaded) { o["unloaded"] = true; return o; }

        string? P(string name) => snap.Props.TryGetValue(name, out var v) ? v : null;

        var first = rps?.FirstOrDefault();
        o["language"] = first?.Language ?? LanguageFromExtension(snap.Path);
        if (string.Equals(P("UsingMicrosoftNETSdk"), "true", StringComparison.OrdinalIgnoreCase)) o["sdkStyle"] = true;

        if (snap.Configuration != null) o["configuration"] = snap.Configuration;
        if (snap.Platform != null) o["platform"] = snap.Platform;
        if (snap.Builds == false) o["builds"] = false; // excluded from the active solution configuration
        if (P("OutputType") is { } outputType) o["outputType"] = outputType;

        // ---- NuGet's resolved graph ----
        var assetsPath = LocateAssetsFile(snap, P);
        List<FrameworkModel>? frameworks = null;
        bool cpm = string.Equals(P("ManagePackageVersionsCentrally"), "true", StringComparison.OrdinalIgnoreCase);
        if (assetsPath != null)
        {
            try { frameworks = ParseAssets(assetsPath, ref cpm); }
            catch (Exception e) { Log.Warn($"vs_solution_model: could not parse {assetsPath}: {e.Message}"); }
        }

        // ---- target frameworks: NuGet's list, else the evaluated properties ----
        var tfms = frameworks?.Select(f => f.Tfm).ToList() ?? new List<string>();
        if (tfms.Count == 0)
        {
            var multi = P("TargetFrameworks");
            if (!string.IsNullOrEmpty(multi))
                tfms = multi!.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToList();
            else if (P("TargetFramework") is { } single) tfms.Add(single);
            else if (P("TargetFrameworkMoniker") is { } moniker) tfms.Add(ShortTfm(moniker));
        }
        o["targetFrameworks"] = new JArray(tfms.Cast<object>().ToArray());

        // ---- Roslyn: references between projects, effective language version, analyzers ----
        if (rps != null && roslyn != null)
        {
            var refs = rps.SelectMany(rp => rp.ProjectReferences)
                          .Select(r => roslyn.GetProject(r.ProjectId)?.Name)
                          .Where(n => !string.IsNullOrEmpty(n))
                          .Select(n => TfmSuffix.Replace(n!, ""))
                          .Distinct(StringComparer.OrdinalIgnoreCase)
                          .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                          .ToList();
            o["projectReferences"] = new JArray(refs.Cast<object>().ToArray());

            if (EffectiveLanguageVersion(first!) is { } lang) o["languageVersion"] = lang;
        }

        // ---- packages ----
        bool truncated = false;
        if (frameworks != null)
        {
            o["packages"] = Merge(frameworks, f => f.Direct, MaxDirectPackages, ref truncated, withParents: false);
            int transitive = frameworks.SelectMany(f => f.Transitive).Select(p => p.Id)
                                       .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            o["transitivePackageCount"] = transitive;
            if (detail)
            {
                o["transitivePackages"] = Merge(frameworks, f => f.Transitive, MaxTransitivePackages, ref truncated, withParents: true);
                o["assetsFile"] = assetsPath;
                try { o["restoredAt"] = File.GetLastWriteTime(assetsPath!).ToString("yyyy-MM-dd HH:mm:ss"); } catch { }
            }
        }
        else if (rps != null)
        {
            // No restore output (packages.config, or never restored): fall back to what the compiler was
            // actually handed. Coarser - no direct/transitive split - but never silently empty.
            o["packages"] = PackagesFromReferences(rps, ref truncated);
            o["packagesNote"] = "no project.assets.json was found (packages.config project, or NuGet restore has "
                              + "not run), so this list is derived from the assembly references the compiler "
                              + "received - it cannot tell direct packages from transitive ones";
        }
        if (cpm)
        {
            o["centralPackageManagement"] = true;
            if (P("DirectoryPackagesPropsPath") is { } dpp) o["directoryPackagesProps"] = dpp;
        }

        // ---- what this project inherits without saying so ----
        var inherits = new JObject();
        if (P("DirectoryBuildPropsPath") is { } dbp && File.Exists(dbp)) inherits["directoryBuildProps"] = dbp;
        if (P("DirectoryBuildTargetsPath") is { } dbt && File.Exists(dbt)) inherits["directoryBuildTargets"] = dbt;
        if (inherits.Count > 0) o["inherits"] = inherits;

        // ---- analyzers ----
        if (rps != null)
        {
            var analyzerPaths = rps.SelectMany(rp => rp.AnalyzerReferences)
                                   .Select(a => a.FullPath ?? a.Display ?? "")
                                   .Where(s => s.Length > 0)
                                   .Distinct(StringComparer.OrdinalIgnoreCase)
                                   .ToList();
            o["analyzerCount"] = analyzerPaths.Count;
            if (detail) o["analyzers"] = GroupAnalyzers(analyzerPaths, ref truncated);
        }

        // ---- evaluated properties ----
        if (detail)
        {
            var props = new JObject();
            foreach (var name in DetailProps.Concat(extras))
                if (P(name) is { } v) props[name] = v;
            if (first?.ParseOptions?.PreprocessorSymbolNames is { } symbols)
            {
                // The TFM ladder (NET5_0_OR_GREATER, NET6_0_OR_GREATER, ...) is derivable noise; keep the rest.
                var defines = symbols.Where(s => !s.EndsWith("_OR_GREATER", StringComparison.Ordinal)).ToList();
                if (defines.Count > 0) props["DefineConstants (effective)"] = string.Join(";", defines);
            }
            o["properties"] = props;
            var missing = extras.Where(n => P(n) == null).ToList();
            if (missing.Count > 0) o["propertiesNotSet"] = new JArray(missing.Cast<object>().ToArray());
        }
        else if (extras.Count > 0)
        {
            var props = new JObject();
            foreach (var name in extras)
                if (P(name) is { } v) props[name] = v;
            o["properties"] = props;
        }

        if (truncated) o["truncated"] = true;
        return o;
    }

    private static string LanguageFromExtension(string? path) =>
        (System.IO.Path.GetExtension(path ?? "") ?? "").ToLowerInvariant() switch
        {
            ".vcxproj" => "C++",
            ".fsproj" => "F#",
            ".vbproj" => "Visual Basic",
            ".csproj" => "C#",
            ".sqlproj" => "SQL",
            var ext when ext.Length > 1 => ext.TrimStart('.'),
            _ => "unknown",
        };

    /// <summary>
    /// The language version the compiler is ACTUALLY using - "latest" or an absent LangVersion resolves
    /// to a number here. Read reflectively: LanguageVersion lives on the C#/VB-specific parse options and
    /// this project compiles against the language-agnostic Roslyn surface only.
    /// </summary>
    private static string? EffectiveLanguageVersion(RoslynProject project)
    {
        try
        {
            var po = project.ParseOptions;
            var raw = po?.GetType().GetProperty("LanguageVersion")?.GetValue(po)?.ToString();
            if (string.IsNullOrEmpty(raw)) return null;
            // CSharp12 -> 12, CSharp7_3 -> 7.3, VisualBasic16_9 -> 16.9
            var trimmed = raw!.Replace("CSharp", "").Replace("VisualBasic", "").Replace('_', '.');
            return trimmed.Length > 0 ? trimmed : raw;
        }
        catch { return null; }
    }

    // ---------------- project.assets.json ----------------

    private static string? LocateAssetsFile(ProjectSnap snap, Func<string, string?> prop)
    {
        var candidates = new List<string>();
        string? dir = snap.Path != null ? System.IO.Path.GetDirectoryName(snap.Path) : null;

        string Rooted(string p) => System.IO.Path.IsPathRooted(p) || dir == null ? p : System.IO.Path.Combine(dir, p);

        if (prop("ProjectAssetsFile") is { } declared) candidates.Add(Rooted(declared));
        if (prop("MSBuildProjectExtensionsPath") is { } ext) candidates.Add(System.IO.Path.Combine(Rooted(ext), "project.assets.json"));
        if (dir != null) candidates.Add(System.IO.Path.Combine(dir, "obj", "project.assets.json"));

        foreach (var c in candidates)
            try { if (File.Exists(c)) return System.IO.Path.GetFullPath(c); } catch { }
        return null;
    }

    private static List<FrameworkModel> ParseAssets(string path, ref bool cpm)
    {
        JObject root;
        using (var sr = new StreamReader(path))
        using (var jr = new JsonTextReader(sr) { DateParseHandling = DateParseHandling.None })
            root = JObject.Load(jr);

        if ((bool?)root["project"]?["restore"]?["centralPackageVersionsManagementEnabled"] == true) cpm = true;

        var declared = root["project"]?["frameworks"] as JObject;
        var groups = root["projectFileDependencyGroups"] as JObject;
        var models = new List<FrameworkModel>();

        foreach (var target in (root["targets"] as JObject)?.Properties() ?? Enumerable.Empty<JProperty>())
        {
            if (target.Name.IndexOf('/') >= 0) continue; // "net8.0/win-x64": the RID-specific repeat of a target
            if (target.Value is not JObject libs) continue;

            var fwKey = MatchFramework(declared, target.Name);
            var model = new FrameworkModel { Tfm = fwKey ?? ShortTfm(target.Name) };

            // What the project itself asked for, with the requested range and the CPM flag.
            var direct = new Dictionary<string, JObject?>(StringComparer.OrdinalIgnoreCase);
            if (fwKey != null && declared?[fwKey]?["dependencies"] is JObject deps)
            {
                foreach (var d in deps.Properties())
                    if (!string.Equals((string?)d.Value["target"], "Project", StringComparison.OrdinalIgnoreCase))
                        direct[d.Name] = d.Value as JObject;
            }
            else if (groups?[target.Name] is JArray group)
            {
                foreach (var g in group)
                {
                    var id = ((string?)g ?? "").Split(' ')[0];
                    if (id.Length > 0) direct[id] = null;
                }
            }

            // Who pulled each library in, so a transitive entry can say why it is there.
            var parents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var lib in libs.Properties())
            {
                var parentId = lib.Name.Split('/')[0];
                if (lib.Value["dependencies"] is not JObject libDeps) continue;
                foreach (var dep in libDeps.Properties())
                {
                    if (!parents.TryGetValue(dep.Name, out var list)) parents[dep.Name] = list = new List<string>();
                    if (list.Count < MaxRequestedBy) list.Add(parentId);
                }
            }

            foreach (var lib in libs.Properties())
            {
                if (!string.Equals((string?)lib.Value["type"], "package", StringComparison.OrdinalIgnoreCase)) continue;
                var parts = lib.Name.Split('/');
                if (parts.Length != 2) continue;

                var pkg = new Pkg { Id = parts[0], Version = parts[1] };
                if (direct.TryGetValue(pkg.Id, out var decl))
                {
                    pkg.Requested = SimplifyRange((string?)decl?["version"]);
                    pkg.CentrallyManaged = (bool?)decl?["versionCentrallyManaged"] == true;
                    pkg.Implicit = (bool?)decl?["autoReferenced"] == true;
                    model.Direct.Add(pkg);
                }
                else
                {
                    if (parents.TryGetValue(pkg.Id, out var by)) pkg.RequestedBy = by;
                    model.Transitive.Add(pkg);
                }
            }
            models.Add(model);
        }
        return models;
    }

    /// <summary>
    /// Pair a "targets" key with its "project.frameworks" entry. Newer NuGet keys both by the alias
    /// ("net8.0"); older output and non-SDK projects key targets by the full moniker
    /// (".NETFramework,Version=v4.8") and frameworks by the short name ("net48").
    /// </summary>
    private static string? MatchFramework(JObject? declared, string targetName)
    {
        if (declared == null) return null;
        foreach (var f in declared.Properties())
        {
            if (string.Equals(f.Name, targetName, StringComparison.OrdinalIgnoreCase)) return f.Name;
            if (string.Equals((string?)f.Value["targetAlias"], targetName, StringComparison.OrdinalIgnoreCase)) return f.Name;
        }
        var shortName = ShortTfm(targetName);
        foreach (var f in declared.Properties())
            if (string.Equals(f.Name, shortName, StringComparison.OrdinalIgnoreCase)) return f.Name;
        return declared.Count == 1 ? declared.Properties().First().Name : null;
    }

    /// <summary>".NETCoreApp,Version=v8.0" -> "net8.0"; ".NETFramework,Version=v4.7.2" -> "net472".</summary>
    internal static string ShortTfm(string moniker)
    {
        if (string.IsNullOrEmpty(moniker) || moniker[0] != '.') return moniker;
        var m = Regex.Match(moniker, @"^\.(?<id>[A-Za-z]+),Version=v(?<ver>[0-9.]+)");
        if (!m.Success) return moniker;
        var ver = m.Groups["ver"].Value;
        switch (m.Groups["id"].Value.ToLowerInvariant())
        {
            case "netframework": return "net" + ver.Replace(".", "");
            case "netstandard": return "netstandard" + ver;
            case "netcoreapp":
                return int.TryParse(ver.Split('.')[0], out int major) && major >= 5 ? "net" + ver : "netcoreapp" + ver;
            default: return moniker;
        }
    }

    /// <summary>"[13.0.3, )" is just "at least 13.0.3" - the form a plain Version="13.0.3" takes. Anything else stays as written.</summary>
    private static string? SimplifyRange(string? range)
    {
        if (string.IsNullOrEmpty(range)) return null;
        var m = Regex.Match(range!, @"^\[\s*([^,\s]+)\s*,\s*\)$");
        return m.Success ? m.Groups[1].Value : range;
    }

    /// <summary>
    /// One list across target frameworks. A package that resolves identically everywhere appears once with
    /// no qualifier; one that is absent from, or differs in, some framework names the frameworks it applies to.
    /// </summary>
    private static JArray Merge(List<FrameworkModel> frameworks, Func<FrameworkModel, List<Pkg>> pick,
                                int cap, ref bool truncated, bool withParents)
    {
        var merged = new Dictionary<string, (Pkg pkg, List<string> tfms)>(StringComparer.OrdinalIgnoreCase);
        foreach (var fw in frameworks)
        {
            foreach (var pkg in pick(fw))
            {
                var key = pkg.Id + "/" + pkg.Version;
                if (!merged.TryGetValue(key, out var entry)) merged[key] = entry = (pkg, new List<string>());
                entry.tfms.Add(fw.Tfm);
            }
        }

        var arr = new JArray();
        foreach (var (pkg, tfms) in merged.Values.OrderBy(e => e.pkg.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (arr.Count >= cap) { truncated = true; break; }
            var o = new JObject { ["id"] = pkg.Id, ["version"] = pkg.Version };
            // Only worth a field when resolution MOVED the version: a floating range, or a transitive
            // dependency that forced a higher one than the project asked for.
            if (pkg.Requested != null && !string.Equals(pkg.Requested, pkg.Version, StringComparison.OrdinalIgnoreCase))
                o["requested"] = pkg.Requested;
            if (pkg.CentrallyManaged) o["centrallyManaged"] = true;
            if (pkg.Implicit) o["implicit"] = true; // added by the SDK, not written in any project file
            if (withParents && pkg.RequestedBy is { Count: > 0 } by) o["requestedBy"] = new JArray(by.Cast<object>().ToArray());
            if (tfms.Count != frameworks.Count) o["frameworks"] = new JArray(tfms.Cast<object>().ToArray());
            arr.Add(o);
        }
        return arr;
    }

    // ---------------- reference-path fallbacks ----------------

    // <root>\packages\<id>\<version>\...   (global packages folder, wherever NUGET_PACKAGES points)
    private static readonly Regex GlobalFolderPackage =
        new(@"\\packages\\(?<id>[^\\]+)\\(?<ver>\d[^\\]*)\\", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // <solution>\packages\<Id>.<version>\...   (packages.config)
    private static readonly Regex PackagesConfigPackage =
        new(@"\\packages\\(?<id>[^\\]+?)\.(?<ver>\d+(?:\.\d+)+[^\\]*)\\", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TargetingPack =
        new(@"\\dotnet\\packs\\(?<id>[^\\]+)\\(?<ver>[^\\]+)\\", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SdkFolder =
        new(@"\\dotnet\\sdk\\(?<ver>[^\\]+)\\Sdks\\(?<id>[^\\]+)\\", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static (string id, string version)? PackageFromPath(string path)
    {
        var m = GlobalFolderPackage.Match(path);
        if (!m.Success) m = PackagesConfigPackage.Match(path);
        return m.Success ? (m.Groups["id"].Value, m.Groups["ver"].Value) : null;
    }

    private static JArray PackagesFromReferences(List<RoslynProject> rps, ref bool truncated)
    {
        var seen = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rp in rps)
            foreach (var r in rp.MetadataReferences.OfType<Microsoft.CodeAnalysis.PortableExecutableReference>())
                if (r.FilePath != null && PackageFromPath(r.FilePath) is { } pkg)
                    seen[pkg.id] = pkg.version;

        var arr = new JArray();
        foreach (var kv in seen)
        {
            if (arr.Count >= MaxDirectPackages) { truncated = true; break; }
            arr.Add(new JObject { ["id"] = kv.Key, ["version"] = kv.Value });
        }
        return arr;
    }

    /// <summary>Analyzer assemblies grouped by where they came from - a package, a targeting pack, the SDK.</summary>
    private static JArray GroupAnalyzers(List<string> paths, ref bool truncated)
    {
        var groups = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            string origin;
            if (PackageFromPath(path) is { } pkg) origin = $"package {pkg.id} {pkg.version}";
            else if (TargetingPack.Match(path) is { Success: true } tp) origin = $"{tp.Groups["id"].Value} {tp.Groups["ver"].Value} (targeting pack)";
            else if (SdkFolder.Match(path) is { Success: true } sdk) origin = $"{sdk.Groups["id"].Value} (.NET SDK {sdk.Groups["ver"].Value})";
            else origin = System.IO.Path.GetDirectoryName(path) ?? path;

            if (!groups.TryGetValue(origin, out var list)) groups[origin] = list = new List<string>();
            list.Add(System.IO.Path.GetFileNameWithoutExtension(path));
        }

        var arr = new JArray();
        foreach (var g in groups)
        {
            if (arr.Count >= MaxAnalyzerGroups) { truncated = true; break; }
            arr.Add(new JObject
            {
                ["from"] = g.Key,
                ["assemblies"] = new JArray(g.Value.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Cast<object>().ToArray()),
            });
        }
        return arr;
    }

    private static string Norm(string path) => path.Replace('/', '\\').TrimEnd('\\');
}
