# Changelog

## 1.20.0 - 2026-08-25

The compile loop, closed. Two new tools on the `vs-debug` server, both ungated. Full reference: [`docs/BUILD.md`](docs/BUILD.md).

### Features

- **`vs_build` - Claude builds with Visual Studio's own build.** Not a separate `dotnet build` in a terminal, but the build the IDE runs: your solution's active configuration, your .NET Framework and C++ projects, your `Directory.Build.props` chain, your analyzers. It returns structured errors and warnings as `{file, line, column, message, project}` plus the tail of the build log. The reason this matters more than it sounds: the Error List that `getDiagnostics` reads is populated by the *IDE's* build, so until now Claude's view of your compiler errors was whatever your last manual `Ctrl+Shift+B` left behind - possibly from before the edit it had just made. Pass `project` to build one project, by name or by **the path of any file inside it**. The build runs asynchronously so the IDE stays responsive, and a build that outlasts the timeout keeps going: calling `vs_build` again attaches to the same build instead of starting a second one.
- **`vs_read_output` - Claude reads the Output window.** Under F5 a lot of what your program says never reaches a terminal: `Debug`/`Trace` output, assembly-binding failures, Hot Reload messages, and first-chance exception notices *even when the exception is caught and swallowed*. All of it goes to the Output window's Debug pane, which no CLI can see. This reads any pane - `debug`, `build`, `general`, or one by name - tailed to a bounded number of lines with an optional `contains` filter. The swallowed exception is the case worth having it for: no stdout, no failing test, exit code zero, and the Debug pane has been recording it the whole time.

Diagnostics are read from the Error List rather than by parsing the build log, deliberately: MSBuild's `error` and `warning` keywords are localized, so a regex over that text would work in an English Visual Studio and nowhere else. For the same reason the three pane aliases are matched by pane id, not display name - the Build pane is `生成` in a Chinese VS. The raw log still ships alongside the structured rows, because MSBuild-level failures (a failed restore, a missing SDK or target) do not always produce Error List rows, and when the build fails with no rows the response says so and points at the log.

New fixture: [`demo/BuildBreak`](demo/BuildBreak) - two projects, one deliberately broken with a compile error and a warning, one that builds clean and swallows an exception only the Debug pane records.

### Fixes

- **The single gate stops double-prompting when Claude runs from a parent folder.** Open a solution from a subfolder (`demo\BuildBreak.slnx`) but run `claude` from above it (`demo\`), and the session-ownership check called that session foreign: it required the session's folder to be *inside* the workspace and did not accept the reverse. `/permission` then answered "ask", which sends the decision back to the CLI's own permission prompt - and because the CLI is connected to the IDE, it renders that prompt as its own diff. The visible result was the extension appearing to break in two ways at once: the panel's **auto-accept** toggle did nothing (the gate refuses before auto-accept is ever consulted), and accepting the diff left the terminal's Accept/Reject prompt still sitting there, because that prompt was the CLI's, not ours. Ownership is now containment in **either** direction, and an edit whose target file lives inside the workspace is owned outright regardless of where the session was started. Two genuinely unrelated folders are still refused, which is the multi-instance case the check exists for. The four hook scripts and the MCP shim rank bridges the same way, so discovery and gating agree; the shim also gets the separator-aware match the hooks already had, so `C:\work\app` no longer matches `C:\work\app-service`.
- **Diff staging files stop polluting the diagnostics.** Every diff stages a `claudediff_*.tmp` / `claudeperm_*.tmp`, and opening one in the diff viewer gets it analyzed as a *miscellaneous* file. Those Error List rows outlived the file: they survived its deletion and later builds, accumulating a fresh set per edit, so error and warning counts drifted upward and pointed at temp paths that no longer existed. Both `getDiagnostics` and `vs_build` now filter them at the read, which also clears rows already accumulated in a running session.
- **Closing a diff's tab no longer hangs the edit.** Accept, Reject, and dismissing the info bar all completed the edit; closing the diff *window* itself - the tab's X, Ctrl+F4, Close All Documents - did not. Closing the frame does not raise the info bar's own closed event, so the reply the CLI was waiting on never came: the edit sat unanswered indefinitely with its staged temp file still on disk, which reads as Claude having frozen mid-tool. Closing the window now counts as a reject, the same as dismissing the bar.
- **`vs_build` always returns the whole build log.** The log had been read as a delta from a bookmark taken before the build, which cannot be made to work: Visual Studio clears the pane at the start of every build, and no bookmark distinguishes that reliably from the log simply growing. The symptom was a log that started halfway through, or came back missing entirely when two consecutive builds were the same length. The pane is now cleared before the build starts and read whole, which is deterministic. The structured errors and warnings were never affected (they come from the Error List), but the raw log is the only place MSBuild-level failures appear, which is the whole reason it is there.
- **The panel's run-wild checkbox stops lying about the CLI's mode.** The checkbox locks itself checked and disabled while the CLI session is in a mode that pre-approves edits, since unchecking it could not re-gate what the CLI had already approved. But the mode was only ever sampled from the edit-gate hook, which fires on edits alone - so a session that switched into auto mode, made one edit, then switched back out left the checkbox stuck checked and unclickable indefinitely, with the tooltip pointing at a terminal toggle that appeared to do nothing. The mode is now re-sampled from every prompt and every turn end, so it catches up within one exchange.
- **A friendly message in Open Folder mode.** `vs_build` with a folder open but no solution returned EnvDTE's raw `Value cannot be null. Parameter name: pSlnCfg`. It now detects the missing build configuration up front and says what to do about it.

## 1.19.1 - 2026-08-20

Two attach-tray fixes, both from [@DaveTseng2019](https://github.com/DaveTseng2019) auditing the 1.19.0 panel.

### Fixes

- **Re-attaching the same file stops stacking duplicate chips** ([#40](https://github.com/firish/claude_code_vs/pull/40)). Dropping or pasting a file from *outside* the workspace twice staged a second copy (`foo.png`, then `foo-2.png`): two files in `.claude\attachments`, two `@`-mentions, two of the tray's twenty slots. The tray deduped references but not staged copies. Attachments now match on the original file's path **and its last-write time**, checked before any copying, so a repeat re-mentions the chip you already have - while a file *edited* since it was staged still comes in as a new attachment, since the staged copy froze the old bytes. Re-dropping a `.bmp` also finds the PNG transcoded the first time instead of transcoding it again. Clipboard images and composer text still never dedupe: with no source file, each one is genuinely new content.
- **Compose stays reachable in a narrow panel** ([#39](https://github.com/firish/claude_code_vs/pull/39)). The attach card's header row did not wrap, so at a docked panel width the hint plus Paste / Compose / Clear overflowed the card - the panel grew a horizontal scrollbar and **Compose** sat half outside it, clickable only if you thought to scroll sideways. The row now wraps like the toolbar above it, keeping every button whole at any width.
- **A screenshot can no longer evict a pending attachment.** The capture path still used the old tray-trim, so a capture arriving at a full tray could drop a mention that had not been delivered yet.

## 1.19.0 - 2026-08-14

A community release: four of these six changes are PRs from [@DaveTseng2019](https://github.com/DaveTseng2019), and a fifth came from his bug report.

### Features

- **Add to Chat in Solution Explorer** ([#35](https://github.com/firish/claude_code_vs/pull/35), closes [#30](https://github.com/firish/claude_code_vs/issues/30)). Select any number of files - or folders, or a selection spanning projects - right-click, and each is `@`-mentioned whole-file. Folders are mentioned as themselves rather than expanded, so `@Rules` costs one reference and the CLI walks the tree. Every reference arrives as a tray chip with its token estimate and click-to-re-mention.
- **The panel follows VS's Environment Font** ([#25](https://github.com/firish/claude_code_vs/pull/25)). Font sizes were hardcoded, so the panel ignored Tools > Options > Environment > Fonts and Colors and stayed tiny (or huge) for anyone who had scaled the IDE. Every panel font, and the Compose dialog, now derives from the shell's size and updates live. The redundant "Claude Code" heading inside the panel is gone (the tool window's own tab already says it), and the toolbar wraps instead of clipping Launch mid-text at narrow widths.

### Fixes

- **Launch stops stacking terminals** ([#26](https://github.com/firish/claude_code_vs/pull/26)). Pressing Launch with a session already connected opened another terminal and another `claude` process every time; it now no-ops with a feed line, and a brief cooldown covers the seconds between the click and the WebSocket handshake, when a connection check alone still lets a second click through. The "hooks & tools didn't load" banner's **Relaunch** bypasses that guard by design, but refuses when no folder is open - previously it spawned another equally-unpinned session that tripped the same banner, inviting an endless click-relaunch loop. **External console** is exempt, and **Tools > Launch Claude Code** now opens the panel too.
- **Context-action `@`-mentions survive a cold start** ([#36](https://github.com/firish/claude_code_vs/issues/36)). With no session running, a right-click action's staged note was delivered on connect but the `@`-mention beside it was dropped - so Claude received "explain this file" with no file attached. Mentions now go through the attachment tray's queue, which means they also survive the CLI's known habit of dropping references sent mid-turn (click the chip to re-send), and **Add to Chat** launches a session like its siblings instead of doing nothing.
- **The attach tray resets when the open solution changes** ([#37](https://github.com/firish/claude_code_vs/pull/37)). Chips are workspace-scoped - a workspace-relative mention path and a staged copy under that workspace - so carrying them into the next solution pushed references the new session could not resolve, or worse, resolved to a same-named file. Same semantics as the panel's Clear button: staged copies are deleted, files referenced in place are only unlisted.
- **The "tools didn't load" banner no longer false-alarms on a slow start.** The MCP servers reach the bridge through a PowerShell shim, so their handshake waits on two cold PowerShell starts; the 10s grace window was inside the range an ordinary cold start takes, which raised the banner and then silently retracted it - leaving a scary, no-longer-true line in the activity feed. The window is now 30s, and a late handshake says so explicitly instead of vanishing without comment.

## 1.18.2 - 2026-08-14

### Fixes

- **Shift+tab auto mode is respected again** ([#38](https://github.com/firish/claude_code_vs/issues/38)). The CLI's permission-mode vocabulary grew: shift+tab's **auto mode** reports `auto`, and `dontAsk` joined the set, while the extension still recognized only `acceptEdits` and `bypassPermissions`. Anything else fell through to the diff, so a session the user had explicitly waved through kept stopping for review (the CLI's own `--permission-mode acceptEdits` launch path was unaffected, which is why the panel's run-wild checkbox always worked). All pre-approving modes are honored now, and the panel's run-wild checkbox reflects and locks to any of them.
- **An unrecognized permission mode now says so.** The mode list is a deliberate allow-list: an unknown mode still opens the diff (the safe failure is the visible one), but logs a warning naming the mode and pointing at the issue tracker - so the next vocabulary change arrives as a bug report instead of silence.

## 1.18.1 - 2026-08-13

### Features

- **A one-time "what's new" note, and the machinery behind it.** VS shows nothing when an extension updates, so headline features stay invisible to existing users. This release adds a deliberately rare notice surface: a single sticky InfoBar with a Release-notes link, shown **once per user, only on releases that explicitly arm it** - routine releases stay silent on purpose. This release arms it to point at the 1.18 right-click actions and the attach tray, the two most discovery-gated features in the extension.

## 1.18.0 - 2026-08-13

The context menu grows up ([#34](https://github.com/firish/claude_code_vs/pull/34), closing [#29](https://github.com/firish/claude_code_vs/issues/29) and [#31](https://github.com/firish/claude_code_vs/issues/31)), informed by a competitive scan of the official Claude Code VS Code extension, Copilot's VS 2026 context actions, and Cursor.

### Features

- **Four edit actions in the "Claude Code" flyout**, below a separator that splits *give Claude context* from *ask Claude to change the file*: **Fix Errors** (stages the selection/file with its *actual* Error List diagnostics and asks for the smallest correct fix, re-verified via `getDiagnostics`), **Generate Documentation** and **Add Comments** (resolve the **function at the caret** via Roslyn - no selection needed; accessors resolve to their property; VB works too - and mention its exact line span with style-matching instructions), and **Fix This Test** (appears in test files; hands Claude the `vs_run_test` → debug-at-the-throw / catch-flaky → fix → re-run loop addressing the real FQN). Every edit still arrives through the diff gate.
- **`Alt+K` on Add to Chat** (text-editor scope) - the official VS Code extension's exact shortcut for inserting an `@`-mention of the current file and selection.
- **Focus follows intent.** After every context action, a chip click, or the composer's Attach, the claude terminal (docked tab or external console) takes keyboard focus - Enter sends immediately. Raw paste/drop into the panel deliberately keeps focus in the panel for batch staging; `docs/QOL.md` documents the split ("Where focus goes, by design").
- **Explain polish**: whole-file explain when nothing is selected, and self-describing staged filenames (`explain-Program.cs-L17-20.txt` instead of `paste-<timestamp>.txt`), so the composer reference says what it is.
- **Actions launch a session when none is running** - staged items deliver on connect instead of the click dying into a feed warning.

## 1.17.1 - 2026-08-12

Same-day patch: Visual Studio 18.9 (released today) silently broke the docked terminal. Both changes contributed by [@DaveTseng2019](https://github.com/DaveTseng2019).

### Fixes

- **The docked "Claude Code" terminal works on VS 18.9** ([#32](https://github.com/firish/claude_code_vs/pull/32)). VS 18.9 removed the brokered-service descriptor the launcher acquired `ITerminalService` through, so every Launch silently fell back to the external console — the docked tab just disappeared. The launcher now tries the 18.9 route first (`SVsTerminalService`, the terminal's new classic-service home) and falls back to the brokered route on older VS including VS 2022; everything downstream is unchanged, and the external-console safety net still backstops both. Live-verified on 18.9.
- **Docs: the paste-focus trap** ([#33](https://github.com/firish/claude_code_vs/pull/33)): if the `@` reference appears in the CLI's input box but Enter does nothing, keyboard focus is still in the panel (easy to hit with a floating panel overlapping the terminal) — click the terminal's input line. Also documented that pasting a screenshot *directly into the terminal* can never work (terminals pass text, not bitmaps — upstream [#26679](https://github.com/anthropics/claude-code/issues/26679)), and annotated that upstream #31208 (the MCP image-block token waste behind our paths-not-pixels rule) was bot-closed as stale, not fixed.

## 1.17.0 - 2026-08-12

The first release built on community PRs — both headline changes started as contributions from [@DaveTseng2019](https://github.com/DaveTseng2019).

### Features

- **A "Claude Code" submenu on the editor right-click menu** ([#27](https://github.com/firish/claude_code_vs/pull/27)): **Explain** stages the selected code as a text attachment with an instruction header ("Explain this code from Foo.cs (lines 12-30):") — insert-not-submit, so it lands in the CLI composer for you to send; **Add to Chat** `@`-mentions the current file and line range in place (or the whole file with nothing selected) — no more typing paths to point Claude at code. Works offline too: Explain's attachment stages as a chip and delivers when Claude connects.

### Fixes

- **Hook traffic from other workspaces' sessions is now ignored** ([#28](https://github.com/firish/claude_code_vs/pull/28) — reported and diagnosed by @DaveTseng2019, reworked to the routing layer in review). The hooks route by workspace match but fall back to *any* listening VS bridge when nothing matches, so an unrelated session could raise this instance's notifications, open review diffs for foreign edits, pollute the panel's token stats, and read this instance's debugger state into its own context. Every hook POST now carries the session's folder and the bridge ignores foreign sessions in one place, ahead of all four hook endpoints; a foreign edit falls back to the CLI's own permission prompt (`ask`), never auto-allowed. Plain-terminal sessions in *this* workspace keep full notifications — the fix distinguishes "whose session" rather than "is anything connected".
- **Workspace matching is separator-aware**: a session in `C:\work\app-service` no longer matches an instance open on `C:\work\app`.

## 1.16.0 - 2026-08-06

### Features

- **简体中文界面 / Simplified Chinese UI** ([#20](https://github.com/firish/claude_code_vs/issues/20)). The panel, dialogs, diff Accept/Reject bar, tooltips, and notifications now follow **Visual Studio's own display language** (Tools > Options > Environment > International Settings): run VS in 中文(简体) and the extension matches its surroundings - no setting on our side, and any string a future release hasn't translated yet falls back to English individually rather than breaking. English VS is byte-for-byte unchanged. Deliberately still English: the activity feed / Output-pane diagnostics (so bug reports stay greppable and answerable), and the `claude` CLI's own terminal chrome (upstream). The translation is maintained as part of every release; corrections are very welcome via issue or PR against `src/ClaudeCodeVS/Resources/Strings.zh-Hans.resx`.

### Fixes

- **A cold-start Launch can no longer open two Claude sessions.** When the native-terminal attempt stalled past its 10s timeout (ServiceHub still warming up), the external-console fallback launched - and the stalled attempt could then complete late and open a *second* claude in the docked terminal. The brokered-service acquisitions ignore cancellation during a cold start, so the launcher now gates explicitly at each boundary: a late-running attempt aborts before creating anything, with an activity-feed line saying the fallback won.
- **Troubleshooting for the post-CLI-update "hooks didn't load" trap.** `claude` 2.1.222+ ties project hooks to workspace trust, and an upstream Windows bug (duplicate case-variant project entries in `~/.claude.json`, [anthropics/claude-code#46586](https://github.com/anthropics/claude-code/issues/46586)) can drop a workspace's trust record during an update - sessions then connect but load no hooks and no vs-* tools (the panel banner catches it). The remedy (`/hooks`, re-accept trust, dedupe the case-twin entries) is now in the README and getting-started troubleshooting. Protocol smoke-tested against `claude` 2.1.223 (spike self-test 14/14 + live IDE handshake); the spike's CLI probe now finds `claude.exe`/`claude.cmd` on Windows and notes that headless `-p` no longer opens the IDE channel on modern CLIs.

## 1.15.0 - 2026-08-04

All four points of [#17](https://github.com/firish/claude_code_vs/issues/17) (v1.14.4 feedback), plus two features that came out of the same testing loop.

### Features

- **A multi-line composer for text attachments.** Paste (or drag) multi-line text onto the panel's attach card and it opens in an editor dialog - review it, trim it, add line breaks, live token estimate - then **Attach** (Ctrl+Enter) stages it as a `.txt` with a visible chip and an `@`-mention in the CLI composer. A **Compose** button opens the same editor empty, which is the pleasant way to write any multi-line prompt material from scratch. (The CLI's own `[Pasted text +N lines]` chip is display collapse, not loss, and `\`+Enter types a manual newline - but for genuinely large text, a staged file the model can Read or Grep is the better vehicle.)
- **Run-wild and the CLI session mode now agree.** Checked at Launch, new sessions start in `--permission-mode acceptEdits`; checked mid-session, the bridge auto-allows immediately and an InfoBar tip names the CLI-side lever (Shift+Tab) since a running session's mode can't be changed from outside. When the CLI side goes permissive (shift+tab auto-accept, `bypassPermissions`), the checkbox reflects it - checked and locked, since unchecking could not re-gate edits already approved at the CLI level - and unlocks when the mode returns to default or the session ends.

### Fixes

- **The extension now honors the CLI's own permission mode.** When a session runs with `acceptEdits` or `bypassPermissions` (e.g. auto-accept / "dangerously skip permissions"), edits are pre-approved at the CLI level - the diff gate no longer overrides that choice, so nothing halts. The hook passes `permission_mode` through and the bridge allows with a feed line. Default-mode sessions behave exactly as before.
- **The missing-script guard now FIXES the common case instead of hiding it.** Hook commands resolve the script cwd-relative first, then anchored to `$CLAUDE_PROJECT_DIR` - expanded by the POSIX shell the CLI runs hook commands through (bash, even on Windows), which is also why the one-liner contains no PowerShell `$`-syntax at all: bash eats unescaped `$` tokens before PowerShell ever runs (caught live as parse errors on every prompt). A session started in a subfolder of the workspace now finds and runs the hooks. Only a genuinely absent script no-ops, and install-on-connect re-materializes those.
- **Accepted edits no longer apply twice.** In the terminal model the CLI applies an approved edit itself, so the IDE-protocol diff's Accept write-back was a second writer - visible on append-style edits, which doubled (live-verified on `claude` 2.1.221). The `openDiff` review is now review-only, like the permission path: `DIFF_ACCEPTED` closes the review and the CLI is the sole writer.
- **Local script edits are no longer clobbered.** Every managed script's first line carries a `vs:auto-managed` marker; the installers overwrite a script only while that line is present. Delete the line to take ownership - the extension logs the skip and leaves your copy alone permanently. (Pre-1.14.5 copies are recognized by their old header and still receive this update.)
- **Menu cleanup after upgrades** - the panel opener moved to an explicit **View > Other Windows > Claude Code** entry (the old entry relied on VS auto-listing registered tool windows, which proved unreliable across in-place updates), and **Tools** now holds exactly one entry, **Launch Claude Code** - no more two similar items opening same-named windows. Both entries carry image-catalog icons now.
- **The config-not-loaded banner now tells the whole story.** When a connected session never loaded the workspace's `.claude` configuration (started outside or in a subfolder of the workspace), the hooks AND the MCP servers die together - the banner now says so (edit-review diff, notifications, and the vs-* tools all inactive) and names both remedies.
- **A running-but-never-connected session is no longer silent.** Hook POSTs arriving at the bridge while the IDE WebSocket has never connected are the fingerprint of `claude` launched outside the extension (workspace hooks alive, IDE channel dark - the panel used to sit in idle "Waiting for CLI" while token stats quietly ticked up). The panel now raises a banner naming the fix: run `/ide` in that terminal (works from any folder inside the workspace) or relaunch from the panel. Cleared the moment a session connects.

## 1.14.4 - 2026-07-31

Three fixes straight from Marketplace feedback.

### Fixes

- **Sessions no longer stall on Claude's own scratch/memory writes.** The diff gate now skips the CLI's working files - the `~/.claude` memory/config tree, temp-dir scratch files, and the workspace's `.claude/` internals - each skip visible in the activity feed. Project code is unchanged: every create or edit under the workspace still opens the diff. (Reported as "Accept/Reject when creating scratch/temporary files halts the session".)
- **No more "-File does not exist" hook errors.** Two halves: hook/MCP scripts now install whenever a CLI session *connects* to the bridge (not only via the Launch button - covers manual `claude` + `/ide` and fresh clones whose committed `settings.json` references our hooks), and registered hook commands are now `Test-Path`-guarded so a genuinely missing script is a silent no-op instead of a per-prompt error. Existing settings.json entries are migrated to the guarded form in place.
- **Docs now open with where the extension lives** (**View > Other Windows > Claude Code**) - it was documented but buried.

## 1.14.3 - 2026-07-31

**Experimental ARM64 support** — the extension now installs on ARM64 Visual Studio (Windows on ARM: Surface devices, Parallels on Apple silicon). Requested via the Marketplace Q&A.

- The extension is managed/AnyCPU and every reflected VS surface (Roslyn, Test Explorer, the terminal service) binds to the host's own copies, so the core — diff gate, diagnostics, semantic navigation, tests, debugger reads/drive, attachments, screen capture, notifications — runs natively on ARM64 devenv. The enabling change is a second `arm64` installation target in the manifest.
- **Two x64-only pieces now refuse with clear messages on ARM64 instead of failing confusingly:** the six ClrMD tools (`vs_wait_chains`, `vs_async_stacks`, `vs_heap_stats`, `vs_threadpool`, `vs_gc_roots`, `vs_heap_diff` — the bundled worker is x64 and ClrMD must match the debuggee's architecture) and `vs_set_data_breakpoint` (managed data breakpoints are an x64-only .NET runtime/debugger capability).
- "Experimental" means exactly this: architecture-neutral by construction, verified in-house on x64 only — ARM64 confirmation is community-sourced. Reports welcome on the tracker.

## 1.14.2 - 2026-07-30

### Fixes

- **The docked terminal now works on VS 2022** ([#12](https://github.com/firish/claude_code_vs/issues/12) follow-up: 1.14.1 fixed VS 2026 but VS 2022 fell back with "Terminal types not found"). Root cause, established by dumping 17.14's `Microsoft.VisualStudio.Terminal.dll` headlessly: VS 2022 ships the same brokered terminal service with an **older contract** — no `TerminalWindowOptions`, no `CreateTerminalWindowAsync`. The launcher now detects which surface is present and, on 17.x, calls the legacy `CreateTerminalAsync(ct, name, ProfileConfig, workingDirectory)` (same `ProfileConfig` ctor on both) with a best-effort `ShowAsync` for focus parity. The launch log names which surface was used. The external-console fallback is unchanged for anything older or stranger.

## 1.14.1 - 2026-07-29

### Fixes

- **Native terminal launch failed with `AmbiguousMatchException` on updated VS builds** ([#12](https://github.com/firish/claude_code_vs/issues/12) — VS 2022 and VS 2026, falling back to the external console). The launcher addressed undocumented Terminal/ServiceHub members with bare `Type.GetMethod(name)`, which *throws* the moment a servicing update adds an overload of that member — and the identical failure on both VS versions points at `IServiceBroker.GetProxyAsync`, whose assembly ships to both. All four by-name lookups (`GetProxyAsync`, `CreateTerminalWindowAsync`, `Add/RemoveCachedProfile`) now enumerate overloads and select by the exact call shape — `CreateTerminalWindowAsync` additionally tolerates either parameter order — so a richer overload added by a future VS update can never break the launch again. Side-by-side VS 2022 + 2026 installs were not the cause.

## 1.14.0 - 2026-07-28

**Screen capture — giving Claude eyes** ([`docs/VISION.md`](docs/VISION.md)). Two new `vs-debug` tools let Claude take its own screenshots instead of asking you to paste one: `vs_capture_window` (the debugged app's window; the VS window; or any window by title — the browser showing your site) and `vs_capture_screen` (one monitor or all). Gated behind a new **Allow screen capture** panel toggle — default off, in-memory, resets each session, and it gates *every* target, since a title-addressed capture can already see anything on the desktop.

### Features

- **Capture core**: `PrintWindow` with `PW_RENDERFULLCONTENT` (renders occluded/DWM-composed windows), with a blank-frame fallback — bring the window forward, ~350 ms settle, re-read the rect, screen-region copy — proven live against hardware-accelerated browsers (Edge, VS Code).
- **Audit trail by construction**: every capture lands in the attachment tray as a visible chip (with its token estimate) plus a `capture:` activity-feed line, and the tool returns the staged PNG's **path** for a native-cost Read — never MCP image blocks (Claude Code counts those as text at ~10–20× the tokens, upstream #31208).
- **Failure modes that steer**: an unmatched title returns the list of capturable windows (same eligibility filter as the matcher — DWM-cloaked shell ghosts excluded, minimized windows suffixed `(minimized)`); a matched-but-minimized window gets a restore-first error instead of a capture of its ~136×39 taskbar-preview proxy (filtered by a minimum-size floor); a windowless web debuggee is pointed at the browser-by-title flow; a bogus `pid` returns the real debugged-pid list.

### Fixes

- **Non-ASCII in every MCP tool result arrived as mojibake** (`Microsoft™ Edge` → `Microsoftâ„¢ Edge`): the bridge's HTTP responses declared no charset, so the PowerShell 5.1 shim decoded UTF-8 as Latin-1. Fixed on both ends — responses now declare `charset=utf-8`, and the shim decodes raw response bytes as UTF-8 itself. Affects all `vs-debug` / `vs-semantic` output, not just window titles; the updated shim ships on the next panel Launch.
- **The panel's toggles now sit on their own wrapping row** — four checkboxes stopped fitting beside the buttons at docked width and were invisible until the panel was widened.

## 1.13.0 - 2026-07-22

**Native terminal launch** — "Launch Claude Code" now opens `claude` inside VS's own docked Terminal tool window (the engine behind `View > Terminal`) instead of a separate `cmd.exe` console, so the CLI lives inside the IDE window like Developer PowerShell does.

### Features

- **`ITerminalService.CreateTerminalWindowAsync`, not a hand-rolled terminal.** VS 2026 exposes the real ConPTY-backed terminal engine as a public, ServiceHub-brokered service — undocumented (no NuGet package, no Learn page) but genuinely public types. Reached the same way this codebase already reaches VS's internal TestWindow engine (`Testing/TestRunner.cs`): reflection-load `Microsoft.VisualStudio.Terminal.dll` from the install dir at runtime, ship zero of its DLLs in the `.vsix`. The brokered-service plumbing itself (`IBrokeredServiceContainer`/`IServiceBroker`/`ServiceRpcDescriptor`) is a normal compile-time reference — already a transitive dependency of the VS SDK package. New: `Terminal/VsTerminalLauncher.cs`.
- **`claude`'s env vars are baked into the launch command**, since `TerminalWindowOptions`/`ProfileConfig` expose no `EnvironmentVariables` property: `cmd.exe /K set ENABLE_IDE_INTEGRATION=true&&set CLAUDE_CODE_SSE_PORT=<port>&&claude`, same `/K` trick as before so the tab keeps its scrollback after `claude` exits. The profile has to be registered via `ITerminalService.AddCachedProfile` first — without it, `TerminalWindowOptions.Profile` is silently ignored and the terminal opens with the default shell instead.
- **Falls back to the external `cmd.exe` console on any failure.** This is an undocumented surface that could change or vanish across a VS update, so `BridgeHost.LaunchClaudeAsync` tries the native terminal first and only falls through to today's `Process.Start(cmd.exe)` path if anything about the reflection call fails — logged via `Log.Warn`, never silent.
- **A 10s timeout guards the native attempt, so a hang also falls back.** A stalled brokered-service acquisition (ServiceHub on a cold VS start) would otherwise leave "Launch Claude Code" hanging forever — the fallback only ran on *failure*, not on a stall. The attempt is raced against a hard timeout with a linked cancellation token, so a late completion is cancelled rather than opening a second terminal. Also hardened: the `ITerminalService` proxy is disposed after use, and `ProfileConfig`'s constructor is selected by shape (4-arg) instead of reflection order.
- **Behavior change:** `claude` now lives inside the IDE, so closing Visual Studio closes `claude` with it. The old external console outlived a VS exit; if the native path falls back to the external console, that older behavior still applies.
- **An "External console" button keeps the standalone option.** The panel's primary button uses the docked native terminal; a second button launches the old separate console window on demand - for a second monitor, or a `claude` session that should survive closing VS. (`BridgeStatus.LaunchExternalAction` -> `LaunchClaudeAsync(forceExternal: true)`.)
- **The "Claude Code" terminal profile is deregistered right after launch** (`RemoveCachedProfile`), so it never accumulates in the terminal's profile dropdown. VS's terminal-tab restore doesn't use it anyway: a restored tab after a VS restart comes back as the default shell (Developer PowerShell), not `claude` - which is correct, since the old session's bridge port would be stale. Just close the leftover tab and hit Launch again.
- **New doc: `docs/QOL.md`** - the quality-of-life reference covering the integrated terminal (this release), notifications (1.11.0), and attachments (1.12.0), linked from the README, the Marketplace overview, and the getting-started guide.

## 1.12.0 - 2026-07-17

**Attachments** — paste a screenshot or drop files onto the panel and an `@` reference lands directly in the CLI's input box. Closes the gap where the Windows CLI cannot paste images at all (upstream anthropics/claude-code#26679) and nobody wants to type absolute paths.

### Features

- **Attach tray on the panel** — drop files from Explorer, or **Paste** / Ctrl+V for clipboard screenshots (saved as PNGs) and copied files. Staged items render as chips: click to @-mention again (the recovery for the CLI dropping references sent mid-turn), ✕ removes (deletes our staged copy, never an in-place original), Clear empties. Items attached before Claude connects show ⏳ and flush on connect (200 ms settle, 25 ms spacing — claudecode.nvim's proven pacing).
- **Delivery is the IDE protocol's `at_mentioned` notification** (`{filePath, lineStart?, lineEnd?}`, insert-not-submit) — the message behind the official plugins' Alt+K. Spike-verified against the live CLI before building: an at-mentioned image path delivers **real pixels** to the model; workspace-relative and absolute paths both resolve. The spike harness gained `m`/`M`/`t` hotkeys + a probe-file generator (`--gen-attach-files`) so this stays regression-testable on CLI bumps, and its manual-connect hint now prints the PowerShell `$env:` form on Windows.
- **Token estimates before you send** — each chip's tooltip and a tray total show what reading the attachments will roughly cost (images by Anthropic's (w×h)/750 formula after the 1568 px downscale, so ~1.6k max per image; text at ~4 bytes/token; PDFs honestly show no estimate). Makes "crop your screenshot" and "have Claude grep the big log instead of reading it" visible before the tokens are spent.
- **One framework for every format.** Images (≤5 MB) / PDFs / text are read directly. BMPs transcode to vision-ready PNGs automatically. Everything else — Excel, video, archives — still stages and mentions, labeled 🧰: Claude gets the path and reaches for a script/tool (PowerShell, ffmpeg, …) since Read can't parse them. Oversized images attach with a downscale note; out-of-workspace files over 50 MB are @-mentioned in place instead of copied.
- **Staging that stays out of your way** — in-workspace files are referenced in place; screenshots and out-of-workspace files are copied to `<workspace>\.claude\attachments\` (so reads never hit an out-of-project permission prompt) behind a self-ignoring `*` gitignore, pruned after 7 days.

## 1.11.0 - 2026-07-10

**Notifications** — an in-IDE heads-up when Claude finishes a turn or needs your input, for anyone working in another window while it cooks.

### Features

- **"Claude finished responding."** — when a turn ends, an InfoBar appears across the top of the Visual Studio main window (auto-dismisses after 15s), and if VS isn't the foreground app its taskbar button flashes a few times. No new hook needed: the existing `Stop` usage hook's `/usage` POST doubles as the turn-end signal (a new `IdeWebSocketServer.StopReceived` event, raised before the transcript parse so a slow usage read never delays the notification).
- **"Claude needs your input."** — a new `Notification` hook (`vs-notify-hook.ps1`, same bridge-discovery boilerplate as the usage hook) POSTs the CLI's message to a new `/notify` endpoint when Claude hits a terminal permission prompt or goes idle waiting for input. This one stays up until dismissed or superseded, and it lands in the panel's activity feed.
- **A `Notify` panel toggle** mutes both. Default ON (it's a convenience, not a safety gate — unlike the two safety toggles), in-memory per session.

### Fixes

- **The hook/MCP installers silently dropped additions when merging into an EXISTING file.** Json.NET clones an already-parented `JToken` on re-assignment, so `root["hooks"] = hooks` detached the local reference and every subsequent mutation landed on an orphan — the file was rewritten without the new entry while the log claimed `ADDED`. Every prior rollout happened to hit the fresh-file path, so this first bit when 1.11.0 added the `Notification` hook to workspaces with an existing `settings.json`. Fixed in `PermissionHookInstaller` (both levels) and `McpInstaller` (`mcpServers` — this one mattered for marketplace upgraders with an existing `.mcp.json`): assign only when creating the token, mutate in place otherwise.
- **The Stop hook no longer waits on the transcript parse.** `/usage` held the hook's POST open until the whole transcript was parsed, which on a long conversation could blow the CLI's 10s hook budget (the `userHookTimeout` warning). The hook is observe-only, so the bridge now responds immediately and parses in the background.

### Notes

- One notification at a time: a new one supersedes the previous InfoBar (`Ui/Notifier.cs`, the same `IVsInfoBarUIFactory` machinery as the diff gate, hosted on the main window via `VSSPROPID_MainWindowInfoBarHost`).
- The taskbar flash is bounded (a few blinks, then the button stays highlighted) — deliberately *not* flash-until-focused, which would nag through a whole terminal conversation. It's skipped entirely when this VS instance is already foreground.
- Turn-end events log at `Event` level (Output pane only) so the panel feed doesn't gain a line per turn; needs-input logs at `Info` (visible in the feed).

## 1.10.1 - 2026-07-06

Two reliability fixes for the bridge.

### Fixes

- **The panel now warns when the pull-MCP tools didn't load.** The IDE WebSocket auto-connects at CLI startup, but the `vs-debug` / `vs-semantic` / test tools only work if the CLI *also* loaded our MCP servers over the stdio shim — which silently doesn't happen when Claude is launched outside the workspace folder, or the project MCP servers weren't approved. `BridgeHost` now arms a 10s grace window on connect and, if no `/mcp` handshake arrives, raises a panel banner with the remedy (relaunch from the panel / approve the project servers) instead of the tools just being mysteriously absent. Backed by a new `IdeWebSocketServer.McpActivity` signal; sticky per bridge, so a WebSocket reconnect of an already-proven session never re-warns.
- **The break-state hook no longer gets killed when VS's UI thread is busy.** The `UserPromptSubmit` debug-context hook hops to the main thread to read break state; if the UI thread was busy (a build, an F5 deploy, a modal dialog) that hop could block past the CLI's 10s hook budget and the hook's output was discarded. It now caps the hop at 2s and fails open with `{"mode":"unknown"}` (a busy UI thread means we're not paused, so there's nothing to inject anyway), and the PowerShell-side timeout drops 5s→4s to stay under the hook budget.

## 1.10.0 - 2026-07-02

**Test integration** — Visual Studio's Test Explorer engine as a closed **discover → run → debug → catch** loop, wired to the live debugger. `dotnet test` runs your tests; this lets Claude *stop inside a failing one* and *reproduce a heisenbug on purpose*. Full reference: [`docs/TESTING.md`](docs/TESTING.md).

### Features

- **`vs_list_tests`** — discover tests via Roslyn (methods marked `[Fact]`/`[Theory]`/`[Test]`/`[TestMethod]`/`[TestCase]`) → real fully-qualified names. No build needed just to list.
- **`vs_run_test`** — run one (by FQN) or all through Test Explorer's engine; returns real per-test `{outcome, errorMessage, errorStackTrace, durationMs}`, not a text blob. `collectCoverage:true` attaches a `.coverage` file. Self-builds first.
- **`vs_rerun_failed`** — re-run only the tests that failed in the last run (`Scope.ForState(Failed)`) — the classic fix-verify move.
- **`vs_debug_test`** — launch one test under the Visual Studio debugger; pair with `vs_break_on_thrown` to stop at the throw site with `$exception` and locals live.
- **`vs_hunt_flaky`** / **`vs_hunt_result`** / **`vs_hunt_cancel`** — force-reproduce an intermittent failure by hammering a test until it fails, capturing each failing run's real outcome/message/stack. Runs in the **background** (async start+poll: returns a `huntId` when it exceeds a ~40s inline window); `measureRate:true` estimates the failure rate.
- **`vs_catch_flaky`** — **catch a transient bug red-handed**: loop a test under the debugger with break-on-thrown armed until the failing iteration halts at the throw, paused inside the failure for inspection. Auto-learns the exception type (or arms the framework assertion base type for a bare assert). Gated behind the debugger-drive toggle.

### Notes

- The test tools live on the **`vs-debug` MCP server** (not a new server) — co-located with the debugger, because the headline feature composes with it. Backed by `Testing/TestRunner.cs` (+ `HuntState`, `TestResultCallback`) and `Tools/TestTools.cs`; discovery by `RoslynReader.FindTestMethodsAsync`.
- **Real per-test results come through an emitted callback.** The engine's `RunTestsAsync` return is identical for pass and fail; per-test outcome/message/stack come only through the internal `ITestWindowDataCallback`, which can't be implemented in C#/`DispatchProxy` — so we `Reflection.Emit` a type implementing it with `[IgnoresAccessChecksTo]`. The engine is acquired **in-proc via MEF** (`IRequestFactory`); the `.vsix` ships zero TestWindow DLLs.
- **Long hunts are async (start + poll), not deferred.** The `/mcp` shim has a ~60s HTTP timeout, so a multi-minute hunt runs on a background task and is polled — the `openDiff` deferred-reply pattern only works on the persistent WebSocket.
- New `demo/TestLab` fixture (net10 xUnit): a pass, a failed assertion, a throw, and two ~1-in-3 intermittent tests for the flaky-hunter/catcher. Verified end-to-end via the CLI tools and a raw `/mcp` suite.
- **Managed (.NET) test projects**, loaded solution required. Coverage works; **profiling is deferred** (needs a Diagnostics-Hub `ProfilerToolId`); the debug/flaky-catch tools are opt-in behind the debugger-drive toggle. Follow-ups: run-tests-affected-by-a-change, profiling, and an `IOperationState` engine-idle wait to make rate measurement robust.
- Removed the internal `vs_test_probe` diagnostic (a development-time acquisition canary) and the standalone `spike-concord/` proof-of-concept directory (the shipped data-breakpoint component lives in `src/ClaudeCodeVS.DataBpComponent/`).

## 1.9.0 - 2026-06-29

**Semantic code navigation** — Visual Studio's resolved understanding of your code (Roslyn), exposed as read-only tools so Claude navigates by ground truth instead of grepping text. The third knowledge axis after runtime state (debugger) and diagnostics. Full reference: [`docs/SEMANTIC.md`](docs/SEMANTIC.md).

### Features

- **`vs_get_selection`** — what the user currently has selected (or where the caret is) in the active editor: text, file, range — **plus the Roslyn symbol at that position with its `symbolId`** when the file is in the loaded solution. Lets Claude act on "this" / "the selected code" and navigate straight from it (selection → `symbolId` → references/callers). Reuses the existing `SelectionService` (which already fed the dormant `getCurrentSelection` IDE-channel tool); the text read works in any language, the symbol enrichment is C#/VB.
- **`vs_search_symbols`** — find declared symbols by name across the loaded C#/VB solution; each result carries a stable `symbolId` (Roslyn DocumentationCommentId) the other tools consume. The addressing primitive *and* the semantic "where is X declared."
- **`vs_find_references`** — semantic Find-All-References: resolves through interfaces, overrides, partial classes, generics, and explicit interface implementations; excludes comments/strings. The ground-truth "where is this used."
- **`vs_go_to_definition`** — the *right* definition among overloads / many same-named types. Address by `symbolId` or by `file`+`line` (cursor-style — disambiguates a specific call site).
- **`vs_find_implementations`** — concrete implementors of an interface/member, overrides of an abstract/virtual member, or derived classes of a base. Exact (grep's `: IFoo` misses indirect + explicit implementations).
- **`vs_call_hierarchy`** — `callers` (default): who **transitively** calls a method, as a depth-limited, cycle-guarded tree with call sites (impact analysis). `callees`: what it directly calls.
- **`vs_type_hierarchy`** — `derived` (default): subtypes/implementors; `base`: the base-class chain + implemented interfaces.
- **`vs_decompile`** — **read the body of a method in a referenced DLL** (framework or NuGet) that ships with no source — the one thing reading the repo fundamentally can't do. Decompiles to C# the way Go-To-Definition does (ILSpy), returning real implementation bodies. Returns just the requested member (`wholeType:true` for the whole type); marks `bodyAvailable` + `source` (`decompiled`/`source`). Core BCL types (forwarded to `System.Private.CoreLib`) only decompile to a stub, so it **auto-retries via SourceLink** to fetch the real `dotnet/runtime` source (bounded 20s; `preferSource:true` to force source-first).

### Notes

- New **`vs-semantic` MCP server** at `POST /mcp-semantic`, served by the *same* `vs-mcp-shim.ps1` parameterized with `-Route`. `McpInstaller` now registers both `vs-debug` and `vs-semantic` in `.mcp.json` (a one-time CLI trust prompt for the new server). Backed by `CodeModel/RoslynReader.cs` + `Tools/SemanticTools.cs`, wired via `BridgeHost.BuildSemanticTools()` → `IdeWebSocketServer.SemanticMcp`.
- **All read-only and ungated** (no execution, no mutation) — unlike the debugger drive tools, there's no toggle. **Managed (C#/VB) only**; returns `{"available":false}` when no project is loaded. Works any time a solution is open — no debug session required.
- **Roslyn binds in-proc.** `Microsoft.VisualStudio.LanguageServices` is referenced `ExcludeAssets="runtime"` (compile-time only → bind to devenv's own copy); the `.vsix` ships zero Roslyn DLLs. `VisualStudioWorkspace` is the supported in-proc entry point, so this works where in-proc ClrMD didn't. Queries run **off** the UI thread (the Roslyn `Solution` is an immutable, free-threaded snapshot) so navigation never stalls the editor.
- New `demo/RefMaze` fixture — an `IShape` reference maze (three implementors incl. an explicit interface implementation, an overload set, a call chain) where each tool returns something text search gets wrong. Verified end-to-end via the CLI tools and a raw `/mcp-semantic` suite.
- Output is bounded but signaled (`{"truncated":true}`), matching the debugger reader's convention. `callees` is direct-only for now (transitive callees + rename/refactor are on the roadmap).

## 1.8.1 - 2026-06-29

**Managed data breakpoints** — "break (or trace) when a value changes." This has *no* EnvDTE/automation surface and VS's own UI can't set it programmatically; we reach it with a bundled **Concord (debug-engine) component** driven over file-IPC. Full reference: [`docs/DEBUGGER.md`](docs/DEBUGGER.md).

### Features

- **`vs_set_data_breakpoint`** — watch a managed instance field (`owner.field`) while paused; streams every change (old→new). Optional `condition` (`> 700`, `== 0`, `!= 5`, …) and `stopOnChange` break execution on **each** matching change so you can inspect locals at the mutation. Multiple watches run concurrently — even several on the same value.
- **`vs_get_data_changes`** — the structured mutation timeline for a watch: `changes: [{previous, current, type}]` plus `broke`/`breakCount`. The "how did this value get here" trace — find the offending write, then set a normal breakpoint at that site.
- **`vs_remove_data_breakpoint`** — disarm a watch (Closes the engine binding).

### Notes

- New `src/ClaudeCodeVS.DataBpComponent/` — an IDE-level Concord component shipped in the VSIX as a `DebuggerEngineExtension` asset. It arms from the **request thread** (`IDkmCallStackFilter`), evaluates owner→field child for `GetDataBreakpointInfo`, and uses its **own** breakpoint SourceId (never the engine's — that crashes the breakpoint manager). The extension-side `DataBreakpointBridge` drives it over file-IPC under `%TEMP%\claude-codevs-databp\` and halts via EnvDTE `Break()` on a matching change (the engine can't halt from its hit notification). **One engine binding per address with fan-out**, so concurrent watches on the same value all fire and apply their conditions independently.
- **32** `vs-debug` tools total. `vs_set_data_breakpoint` is gated behind "Allow Claude to drive debugger"; `vs_get_data_changes` (read) and `vs_remove_data_breakpoint` (disarm) are not.
- **Managed instance fields only** — statics, stack locals and struct fields are unsupported by the engine. Debuggee must be .NET Core 3.0+ / .NET 5.0.3+, x64. The stop lands **one statement after** the write (the data breakpoint fires once the write completes — read the stack and set a normal breakpoint at the write site for an exact landing).
- Proven end-to-end first in `spike-concord/` (the full make-or-break ladder — Rung 0 component-loads → the cracked `DkmPendingDataBreakpoint`/`GetDataBreakpointInfo` arm chain → crash fix → halt-via-extension), then productized into the extension and verified live against the `DataBpTarget` fixture (conditional, recurring, multi-watch, disarm).

## 1.6.0 - 2026-06-27

ClrMD memory / GC / ThreadPool diagnostics — four new read tools on the same out-of-process worker. Full reference: [`docs/DEBUGGER.md`](docs/DEBUGGER.md).

### Features

- **`vs_heap_stats`** — memory snapshot: top managed types by total bytes (count + size), bytes per GC generation (gen0/1/2/LOH/POH), GC mode (server/workstation, regions, background), GC-handle counts by kind, and the finalizer-queue size + top finalizable types. The "what's using memory / what looks off" overview.
- **`vs_threadpool`** — ThreadPool health: worker counts (min/max/existing/busy/goal), queued work-item backlog, and a `starved` flag. Diagnoses the classic "async app hangs but nothing is deadlocked" bug — pool threads blocked (often sync-over-async) while work piles up. Pair with `vs_async_stacks`.
- **`vs_gc_roots`** — "why is this object alive?": give a type name or `0x`-address → the retention path from a GC root to an instance (each frame references the next), with `rootKind` (static field / thread-stack local / strong-or-pinned handle / finalizer queue). The leak root-cause tool.
- **`vs_heap_diff`** — leak finder: the first call baselines the heap; later calls report what GREW (per-type count/byte deltas, biggest first). A type climbing across repeated calls is the leak; then `vs_gc_roots` it. `reset` starts a fresh baseline.

### Notes

- **29** `vs-debug` tools total (14 read, ungated + 15 drive, gated). All four new tools are ungated reads on the existing out-of-process `ClrMdWorker.exe` (the snapshot is a `PssCaptureSnapshot` fork, so it coexists with the live VS session) — no new in-proc binding risk.
- New `demo/MemLoad` fixture leaks `byte[]` and starves the threadpool, exercising all four end-to-end.
- ClrMD heap walks (stats/roots/diff) can take longer than a lock read; the worker is given a 60 s budget and caps large enumerations with a `{truncated:true}` marker.
- Managed (.NET) only; threadpool stats need a .NET 6+ target. x64 targets.
- Tested against `claude` 2.1.191.

## 1.5.0 - 2026-06-27

ClrMD-powered structured concurrency analysis: exact lock ownership and logical async call stacks, run **out-of-process** so they coexist with the live VS debug session. Full reference: [`docs/DEBUGGER.md`](docs/DEBUGGER.md).

### Features

- **`vs_wait_chains`** — structured deadlock triage from a ClrMD process snapshot: every held monitor with its **owner thread + waiter count**, each thread's held locks and blocked state, and **`deadlockSuspects`** (threads that hold a lock *and* are blocked entering a monitor — the cycle members). Exact ownership, not parsed from stack text — a structured upgrade over 1.4.0's `lockOwnerThreadId`. Pair with `vs_threads` for the explicit "waiting on lock owned by thread X" edge. Live-verified cornering the LockJam 3-way deadlock.
- **`vs_async_stacks`** — logical async call-stack reconstruction: walks the heap's async state-machine boxes and returns each in-flight async chain (innermost first) with its await-point `state` — the `RunAsync → ComputeAsync → InnerAsync` chain the *physical* `MoveNext`/`ThreadPool` stack hides. The modern `dotnet/diagnostics` `!dumpasync` approach ported to ClrMD. Live-verified on AsyncTrace.

### Notes

- **Out-of-process by design.** ClrMD can't load in-proc in devenv — ClrMD 4.0 binds `System.Collections.Immutable` 10.0.0.7, but devenv ships its own Immutable versions and unifies them through a binding policy an in-proc extension can't override (`MissingMethodException` on `DataTarget.get_ClrVersions`). So a bundled **`ClrMdWorker.exe`** (net48/x64, with its own `.exe.config`) takes the snapshot in a separate process and returns JSON; the extension shells out and parses it. The snapshot is a `PssCaptureSnapshot` **fork**, so it reads a clone and **coexists** with the live VS debug session (verified at a Break All — VS continues cleanly).
- **25** `vs-debug` tools total (10 read, ungated + 15 drive, gated). Both new tools are ungated reads.
- The snapshot/VS-coexistence approach was proven end-to-end against a live VS-attached session before integration.
- Managed (.NET) only; x64 targets (the worker matches devenv's bitness; an x86 target would need an out-of-process x86 helper — future).
- Tested against `claude` 2.1.191.

## 1.4.0 - 2026-06-25

Deadlock-triage follow-ups to the 1.3.0 debugger surface — all pure EnvDTE, no AD7.

### Features

- **`vs_break_all`** — pause a **running or hung** debuggee (Break All / Ctrl+Alt+Break) and return the new state. The way into a deadlock, which never *hits* a breakpoint so there's nothing to stop on. A gated drive tool; rides the same await-break engine (`Debugger.Break(false)` → `OnModeChange`) as continue/step.
- **Per-thread inspection** — `vs_get_frame_locals`, `vs_evaluate`, and `vs_expand` all take an optional `threadId` (from `vs_threads`): they switch `Debugger.CurrentThread` to that thread, read/evaluate, and restore — so you can read a *non-current* thread's args/locals or drill `from.Id` on each thread in a deadlock, without it being the stopped thread. Reads stay ungated.
- **Lock-chain ownership in `vs_threads`** — a thread blocked on a *contended* lock now carries `lockOwnerThreadId` (the holder), parsed from Concord's `[Waiting on lock owned by Thread 0x..]` stack annotation and converted to decimal so it cross-references another thread's `id`. Follow the chain across threads → the deadlock cycle, straight from the flags.

### Notes

- **23** `vs-debug` tools total (8 read, ungated + 15 drive, gated). Tested against `claude` 2.1.191.
- New fixtures: **`demo/LockJam`** (five threads, a 3-node deadlock cycle buried in noise — a busy thread and an idle semaphore-waiter as negative controls) and **`demo/AsyncTrace`** (cross-await inspection: locals/`vs_evaluate` on an async continuation, and characterizing how much of the logical async call stack surfaces).
- **Live-verified on LockJam (Windows VS 2026):** `vs_break_all` paused the hang, `lockOwnerThreadId` formed the cycle, and per-thread `vs_evaluate('from.Id', threadId:…)` read each account — a fully tool-grounded deadlock diagnosis. Finding: a contended lock does **not** surface a `Monitor.Enter` frame (Just-My-Code or not) — Concord replaces it with the `[Waiting on lock owned by Thread]` annotation, which the heuristic now matches.

## 1.3.0 - 2026-06-24

Headline: **debug real, running apps.** Attach to a live process — a hosted web app, a service, an already-running desktop app — instead of only F5-launching a startup project, and break at the *origin* of an exception instead of where a generic catch swallows it. Builds on the 1.2.0 debugger surface; full reference: [`docs/DEBUGGER.md`](docs/DEBUGGER.md).

### Features

- **Attach to a running process** — `vs_attach` (by pid or name) + `vs_list_processes` (name-filtered) + `vs_detach`. Debug a hosted ASP.NET app (Kestrel / IIS `w3wp`), a Windows service, or an already-running desktop app — the real-app case F5 can't cover. Plain `Process.Attach()` selects the managed engine.
- **Break-on-thrown (first-chance exceptions)** — `vs_break_on_thrown` stops at the **throw site** of a named managed exception (e.g. `System.NullReferenceException`), even when a generic `catch` swallows it, so you see where it originates. Implemented via the managed `EnvDTE90.Debugger3.ExceptionGroups` API (not the low-level AD7 path).
- **Inspect `$exception`** — `vs_exception` returns the in-scope exception's type, message, and an expanded tree (incl. `InnerException` + stack) at a first-chance break or inside a catch block.
- **Function breakpoints** — `vs_set_breakpoint` now accepts a `function` name (e.g. `Namespace.Type.Method`) as an alternative to file:line — break wherever a method is entered, no source location needed. Conditions are supported.
- **Multi-process session shape** — `vs_debug_state` now reports `debuggedProcesses` (what you're attached to), surfaced in run mode too.
- **Concurrency triage** — `vs_threads` flags threads parked on a lock/wait (`waiting` / `waitOn`) to point at deadlock/contention suspects.

### Notes

- New fixture `demo/WebQuote` (ASP.NET Core) exercises attach + break-on-thrown end-to-end — verified live: the model attaches, arms break-on-thrown, triggers the request itself, lands at the throw site, inspects, and detaches.
- **22** `vs-debug` tools total (8 read, ungated + 14 drive, gated). Reading runtime state stays ungated; attach/detach, break-on-thrown, and execution control are gated behind the "Allow Claude to drive debugger" toggle.
- Tested against `claude` 2.1.186.

---

## 1.2.0 - 2026-06-17

Headline: **live debugger integration** — Claude can now see your program's runtime state, and (opt-in) drive the debugger to corner a bug instead of guessing from source. Full reference: [`docs/DEBUGGER.md`](docs/DEBUGGER.md).

### Features

- **Debugger awareness (push)** - when you submit a prompt while paused at a breakpoint, a `UserPromptSubmit` hook injects the live break state (stop location, call stack, current-frame arguments and locals with values) into Claude's context. No tool call needed; gated on break mode so normal turns stay quiet.
- **Runtime inspection (pull)** - a second MCP server, `vs-debug`, exposes on-demand read tools the model can call mid-turn: `vs_debug_state`, `vs_evaluate`, `vs_expand` (drill into an object graph), `vs_get_frame_locals`, `vs_list_breakpoints`, `vs_threads`. Reached via a tiny stdio shim auto-registered in the workspace `.mcp.json`; the tool logic runs in-proc against EnvDTE.
- **Drive the debugger (opt-in)** - behind a new **"Allow Claude to drive debugger"** panel toggle (default off, resets each session): `vs_continue`, `vs_step_over`/`into`/`out`, `vs_run_to_line`, `vs_set_breakpoint` (with condition + hit count), `vs_remove_breakpoint`, `vs_freeze_thread`, `vs_set_next_statement`, and `vs_start_debugging`/`vs_stop_debugging`. An await-break engine (`IVsDebuggerEvents.OnModeChange` + a parked completion) returns the new state after each step without blocking the UI thread.
- **Truncation signaling** - capped results (call stack, locals, threads, expanded members) now carry a `{truncated: true, …}` marker so Claude knows data was cut and can narrow its query, instead of silently seeing a partial picture.
- **Panel debugger stat** - the dockable panel's stats card now shows session attribution: *N inspected · M driven*.
- **Multi-instance + reconnect hardening** - hooks pick the most-specific workspace lockfile whose port is actually listening (defeats parent-folder shadowing and zombie lockfiles); lockfiles record `pidStartTime` so a recycled PID can't make a dead instance look alive; orphaned diffs are rejected and closed when the CLI disconnects.

### Known limitations

- Managed (.NET) debugging only; native/C++ runtime inspection is out of scope.
- `vs_evaluate` can't evaluate LINQ/lambda expressions (VS evaluator limitation).
- `vs_threads` gives per-thread stacks but not lock/wait-chain ownership.
- Break-on-thrown exceptions and native tracepoints are not yet implemented (planned - see `ROADMAP.md`).
- Tested against `claude` 2.1.181.

---

## 1.0.1 - 2026-06-15

### Fixes

- Fixed README demo GIF path to absolute URL so it renders on the VS Marketplace listing.
- Added VS Marketplace and GitHub Releases links to README.
- Added VSIX icon and preview image.
- Corrected manifest publisher display name and version range floor for Marketplace compliance.

---

## 1.0.0 - 2026-06-15

Initial release. Implements the full Claude Code IDE-integration protocol for Visual Studio 2026.

### Features

- **Native diff with single-gate accept/reject** - Claude's proposed edits open in Visual Studio's diff viewer (not a terminal y/n). A PreToolUse hook intercepts every Edit/Write/MultiEdit and routes it through the diff; the CLI writes the file only after you accept. No duplicate terminal prompt.
- **Reject with feedback** - an InfoBar action in the diff that prompts for a reason; the reason is returned to the CLI as `permissionDecisionReason` so Claude reconsiders.
- **Run wild (auto-accept)** - a panel checkbox that allows edits without opening the diff, for unattended sessions. Resets each VS session.
- **Diagnostics sharing** - `getDiagnostics` reads the VS Error List (Roslyn for C#, MSVC toolchain for C++) and returns LSP-shaped diagnostics so Claude can see and fix your build errors.
- **Selection context** - a `selection_changed` notification (150 ms debounce) keeps Claude aware of the active file and highlighted lines in real time.
- **One-click Launch** - *Tools -> Launch Claude Code* opens a terminal pre-wired with `ENABLE_IDE_INTEGRATION` and the bridge port, working directory set to the solution root. Auto-installs the permission and usage hooks on first launch.
- **Dockable Claude Code panel** - connection status pill, accept/reject edit counts, token usage (input / output / cached) and estimated cost (latest call + cumulative session), pending-diff strip, and a curated activity feed. VS-theme-aware (dark/light).
- **Full 12-tool parity** - all tools advertised in `tools/list`: `openFile`, `openDiff`, `getCurrentSelection`, `getLatestSelection`, `getDiagnostics`, `getOpenEditors`, `getWorkspaceFolders`, `checkDocumentDirty`, `saveDocument`, `close_tab`, `closeAllDiffTabs`. `executeCode` returns an honest MCP error (no VS equivalent).
- **RDT-aware write-back** - accepting an edit updates an open editor buffer in place (no reload prompt) via `IVsRunningDocumentTable`.
- **Lockfile lifecycle** - stale (dead-PID) lockfiles are reaped on startup; the lockfile is deleted on clean shutdown and on an unexpected server fault.
- **WorkspaceWatcher** - keeps the lockfile's `workspaceFolders` in sync as solutions open/close so `/ide` always matches the current working directory.

### Known limitations

- Visual Studio 2026 only (VS 2022 backfill planned - see `ROADMAP.md`).
- Diagnostic ranges are point ranges (Error List only exposes line/column); Roslyn-precise spans are a future enhancement.
- The IDE-integration protocol is undocumented and version-fragile. Tested against `claude` 2.1.173.
- Token stats refresh on edits (the reliable hook trigger); a chat-only turn may not update them immediately.
- Cost figures are estimates (hardcoded per-tier list prices), not billing.
