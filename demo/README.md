# Demo fixture & recording guide

A tiny, reliable project for recording the launch GIF/video. `CheckoutDemo` has **one deliberate,
realistic compile error** (a tax rate typed as `string`), so the demo loop is clean:

> fetch the errors -> Claude reads diagnostics -> native diff opens -> Accept -> error clears.

## Should you screen-record? Yes.

It's an interactive IDE flow, so a screen capture is the right format. Produce two cuts from one take:

- **README hero GIF** - short (~15–25s), **silent**, **looping**, cropped to the editor + panel,
  kept **< 10 MB**. Tool: **ScreenToGif** (Windows, free - record, trim, and export GIF in one app)
  or LICEcap. Save it as `docs/demo.gif`.
- **Announcement video** - longer MP4 (optionally with voiceover) for HN/YouTube/Reddit. Tool: **OBS
  Studio**, or **Win+G** (Xbox Game Bar) for a quick grab.

**Capture tips:** record at 1080p; bump the editor font (Ctrl+Mouse-wheel) so it's readable when
scaled down; use the **dark** theme (matches the panel); close clutter (Solution Explorer can stay,
hide the rest); and in editing **trim the model's "thinking" wait** so the loop feels snappy.

## Shot list (≈ 25 seconds)

1. **Open the project** - `File -> Open -> Project/Solution -> demo/CheckoutDemo/CheckoutDemo.csproj`.
   The **Error List** shows `CS0019` on `GrandTotal`. (Have the Claude Code panel docked on the right.)
2. **Launch** - click **Launch Claude Code** in the panel; the pill turns green **Connected**.
3. **Ask** (type one of the prompts below).
4. **Diff opens** - Claude calls `getDiagnostics`, explains the bug, and the fix opens in the **native
   VS diff**. Click **Accept**.
5. **Resolved** - the Error List clears. (Optional: show the **stats panel** ticking up.)

### Prompts that demo well
- `There's a build error in this project - fetch the diagnostics and fix it.`
- `What compiler errors do you see? Fix them.`
- For a second beat showing **reject-with-feedback**: ask for a change, then click
  **Reject with feedback…** and type e.g. `use a named constant instead`.

## What Claude should do

It reads `CS0019` via `getDiagnostics`, then changes:

```diff
- private static readonly string TaxRate = "0.08";
+ private static readonly decimal TaxRate = 0.08m;
```

A clean one-line diff - exactly what reads well in a GIF.

## C++ fixture (`CheckoutDemoCpp`) - for the #15942 audience

Same bug in C++ (a `std::string` tax rate multiplied by a `double`), as a console solution so VS loads
it and the Error List populates. **This is the clip to lead with in #15942** - but two things first.

### Prerequisite: the C++ workload (currently NOT installed here)
A build check on this machine failed with `Microsoft.Cpp.Default.props was not found (…\VC\v180\…)`,
which means **"Desktop development with C++" is not installed in VS 2026**. Until it is, the project
can't build, IntelliSense won't analyze it, and `getDiagnostics` will have nothing to read.

Install it: **Visual Studio Installer -> Modify (VS 2026) -> Desktop development with C++ -> Modify**.
When you first open `CheckoutDemoCpp.sln`, if VS offers to **retarget** to the installed toolset, accept
it (the project pins `v143`; one click upgrades it).

### Verify the path before recording (the important step)
C++ `getDiagnostics` reads the **Error List**, and IntelliSense can lag, so make the error
deterministic by **building first**:

1. Open `demo/CheckoutDemoCpp/CheckoutDemoCpp.sln`; retarget if prompted.
2. **Build** (Ctrl+Shift+B) - it fails; the Error List shows the C++ type error.
3. Launch Claude from the panel, then: `Build is failing - fetch the compiler errors and fix them.`
4. Confirm Claude's `getDiagnostics` call actually returns the C++ error (watch the **Output -> Claude
   Code** pane for `getDiagnostics … -> 1 file(s)` with diagnostics, not `[]`).
5. If it returns the error -> record the clip (same shot list as above). If it returns `[]` -> the C++
   Error List read needs a fix in `ErrorListReader` before this is demo-able; tell me and we'll fix it.

The fix Claude should make:

```diff
- const std::string taxRate = "0.08";
+ const double taxRate = 0.08;
```

> **Recording order:** ship the **C# GIF** now (verified). Only post the **C++ clip** to #15942 after
> step 4 actually returns diagnostics - don't demo an unverified path to the audience that asked for it.
