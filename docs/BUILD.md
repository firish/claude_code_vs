# Build and output: closing the compile loop

`dotnet build` compiles your code, and the `claude` CLI can already shell out to it. What it cannot do is run **the build Visual Studio itself runs** - your solution's active configuration, your .NET Framework and C++ projects, your `Directory.Build.props` chain, your analyzer set - or read the **Output window**, where a debugged app's own logging and every first-chance exception go and no terminal ever sees them.

Those are the two tools here. Together they do for compiling what the test tools did for testing: change code, build, read real errors, fix, verify.

| Axis | What it is | Surfaced by |
|---|---|---|
| **Runtime state** | execution point, variable values, threads, heap | the debugger and ClrMD tools ([`DEBUGGER.md`](DEBUGGER.md)) |
| **Semantic model** | symbols, references, implementations, hierarchies | the `vs-semantic` tools ([`SEMANTIC.md`](SEMANTIC.md)) |
| **Tests** | discover, run, debug, force-reproduce | the test tools ([`TESTING.md`](TESTING.md)) |
| **Compiler** | build the solution, read the errors it produced | `vs_build`, this doc |
| **Output panes** | the build log, and the debuggee's own output | `vs_read_output`, this doc |

**Jump to:** [Why not just `dotnet build`](#why-not-just-dotnet-build) · [The Debug pane](#the-debug-pane-is-invisible-to-a-terminal) · [Watch it work](#watch-it-work) · [Tool catalog](#tool-catalog) · [How it works](#how-it-works) · [Limitations](#limitations)

---

## Why not just `dotnet build`

Three reasons, in increasing order of how much they bite.

**1. It is a different build.** `dotnet build` is the SDK's MSBuild. Visual Studio's is not always the same one, and the gap shows up exactly where it hurts: .NET Framework projects, C++ projects, solution-level configurations and platforms, custom targets, and the `Directory.Build.props` / `Directory.Packages.props` inheritance chain. A build that is green in one and red in the other is a genuinely confusing half hour.

**2. It is a different error list.** Visual Studio's Error List is populated by the IDE's build. `getDiagnostics` reads that Error List. So before this tool existed, Claude's view of your compiler errors was **whatever the last manual `Ctrl+Shift+B` left behind** - possibly hours old, possibly from before the edit it just made. `vs_build` is what makes `getDiagnostics` honest.

**3. It keeps the IDE in sync.** After `vs_build`, what Claude sees and what you see in the Error List are the same thing, because they are the same thing.

## The Debug pane is invisible to a terminal

Under F5, a surprising amount of what your program says never reaches stdout:

- `Debug.WriteLine` / `Trace.WriteLine` output, and most `ILogger` configurations in a debugged ASP.NET app
- **first-chance exception notices** - `Exception thrown: 'System.DivideByZeroException' in App.dll` - which appear *even when the exception is caught and swallowed*
- assembly binding and load failures
- Hot Reload messages, and the debugger's own diagnostics

All of it goes to the **Debug** pane of the Output window. A CLI agent running your app in its own terminal sees none of it. `vs_read_output` does.

The swallowed exception is the case worth dwelling on. A `catch` block that eats an exception produces no stdout, no non-zero exit code, and no test failure. It is one of the harder classes of bug to find by reading code. The Debug pane has been recording it the whole time.

## Watch it work

[`demo/BuildBreak`](../demo/BuildBreak) is a two-project solution built for exactly this. `BuildBreak.Core` is deliberately broken with one compile error and one warning; `BuildBreak.App` is fine, and writes only to the Debug pane.

| Project | Designed to |
|---|---|
| `BuildBreak.Core` | fail to compile: `CS0029` (decimal to string) in `Pricing.Describe`, plus a `CS0219` unused-variable warning |
| `BuildBreak.App` | build and run cleanly, logging to the Debug pane, and swallow a `DivideByZeroException` in a `catch` |

A build reports one project failed, not two, with the error attributed to `BuildBreak.Core`:

```
vs_build  ->  { ok: false, projectsFailed: 1, errorCount: 1, warningCount: 1,
                errors: [{ file: "...\Pricing.cs", line: 22, column: 24,
                           message: "Cannot implicitly convert type 'decimal' to 'string'",
                           project: "BuildBreak.Core" }] }
```

Fix `Describe` to return `amount.ToString("0.00")`, build again, and it is green. That is the whole loop, and it is one tool call per side.

Then run `BuildBreak.App` under F5 and ask for the Debug pane:

```
vs_read_output { pane: "debug", contains: "Exception" }
  ->  Exception thrown: 'System.DivideByZeroException' in BuildBreak.App.dll
```

The program printed `starting` and `done` and exited zero. Nothing else knew.

## Tool catalog

Both tools are on the **`vs-debug`** MCP server, alongside the debugger and test tools. Neither is gated: building and reading logs are not execution of your app, and the CLI could shell out to MSBuild on its own regardless. The same reasoning as `vs_run_test`.

### `vs_build`

Builds the solution (or one project) and returns structured diagnostics plus the raw log tail.

| Parameter | Meaning |
|---|---|
| `project` | Build only this project. Takes a project name, a substring of one, or **the path of any file inside it** - "build the project that owns the file I just edited" needs no lookup. Omit for the whole solution. |
| `rebuild` | Clean first, then build, for when an incremental build is lying about stale output. Solution-scope only (EnvDTE has no per-project clean). |
| `includeWarnings` | Include warning rows as well as errors. Default true; the count is reported either way. |
| `timeoutSeconds` | How long to wait inline. Default 45, max 55, because the MCP transport times out at 60. |

Returns `ok`, `projectsFailed`, `errorCount` / `warningCount`, `errors` / `warnings` as `{file, line, column, message, project}`, and `output` (the tail of the build log).

**A build that outlasts the timeout keeps running.** The response comes back with `stillBuilding: true`, and calling `vs_build` again **attaches to the same build** rather than starting a second one. There is no separate poll tool to remember.

### `vs_read_output`

Reads one pane of the Output window.

| Parameter | Meaning |
|---|---|
| `pane` | `build` (default), `debug`, `general`, or any pane's display name - `Tests`, or `Claude Code` for this extension's own diagnostics. |
| `tail` | Lines from the end. Default 200, max 5000. A long-running log stays a bounded number of tokens. |
| `contains` | Keep only lines containing this text, case-insensitive, within the tail window. |
| `maxChars` | Hard character cap, newest text kept. Default 20000. |

The three aliases are matched by **pane id, not display name**, so they work in a non-English Visual Studio - where the Build pane is called `生成`.

## How it works

**The build is asynchronous.** `SolutionBuild.Build(false)` starts it and returns; the extension polls `BuildState` from a background task, hopping to the UI thread only for the state read. The blocking `Build(true)` form would freeze Visual Studio for the entire build - acceptable as one step inside a test run, not acceptable for a tool the model reaches for constantly.

**Diagnostics come from the Error List, not from parsing the build log.** This is the load-bearing decision. Parsing MSBuild's output with a regex for `error` and `warning` works in an English Visual Studio and nowhere else, because those keywords are localized. The Error List hands back a severity *enum*. The raw log still ships in `output`, because MSBuild-level failures (a failed package restore, a missing SDK or target, a pre-build step) do not always produce Error List rows - when the build fails with no error rows, the response says so and points at the log.

**The Build pane is cleared before the build starts**, so `output` is this build's log and not a scrollback of the whole session. Visual Studio was about to clear it anyway - it wipes that pane at the start of every build - so this only makes the timing ours. Attaching to a build already in progress skips the clear, since that build did it when it started.

**Everything here is UI-thread bound.** EnvDTE and the Error List are both apartment-bound, so every call switches first (convention #1 in `CLAUDE.md`).

## Limitations

- **Needs a real solution.** A folder opened without a solution has nothing for the IDE to build, and the Error List will be empty. This is the same constraint `getDiagnostics` and the semantic tools already have.
- **The Error List merges build output with live IntelliSense analysis.** An error row can therefore refer to a file that was not part of this build. The response carries a `diagnosticsNote` saying so whenever it returns rows. Scoping a build to a project also scopes the diagnostics to that project.
- **A project-scoped build clears the Error List's build rows for the whole solution.** That is Visual Studio's behavior, not the tool's: after `vs_build` with a `project`, `getDiagnostics` reports nothing for the *other* projects until the next solution-wide build, even though their errors are still real. If you want the full picture, build the solution.
- **Error codes are not broken out.** The Error List exposes the message text, and MSBuild-sourced rows usually carry the code inside it, but there is no separate `code` field.
- **`rebuild` is solution-scope.** EnvDTE exposes a solution clean and no per-project one, so `rebuild` with a `project` is reported as ignored rather than silently doing something else.
- **Panes are created lazily.** The Build pane does not exist until the first build and the Debug pane not until a debug session starts, so `vs_read_output` can legitimately answer "no such pane" on a freshly opened solution. The error lists the panes that do exist.
- **`contains` filters within the tail window**, not the whole pane. Raise `tail` to search further back.

## Fixtures to try

| Fixture | Exercises |
|---|---|
| [`demo/BuildBreak`](../demo/BuildBreak) | the full loop: a scoped build failure, the fix, the green build, and a swallowed exception visible only in the Debug pane |
| [`demo/CheckoutDemoCpp`](../demo/CheckoutDemoCpp) | a C++ project, where the IDE build and `dotnet build` are not even the same tool |
| [`demo/TestLab`](../demo/TestLab) | `vs_build` feeding the test loop: build, run, read failures ([`TESTING.md`](TESTING.md)) |
