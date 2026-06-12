# Demo fixture & recording guide

A tiny, reliable project for recording the launch GIF/video. `CheckoutDemo` has **one deliberate,
realistic compile error** (a tax rate typed as `string`), so the demo loop is clean:

> fetch the errors → Claude reads diagnostics → native diff opens → Accept → error clears.

## Should you screen-record? Yes.

It's an interactive IDE flow, so a screen capture is the right format. Produce two cuts from one take:

- **README hero GIF** — short (~15–25s), **silent**, **looping**, cropped to the editor + panel,
  kept **< 10 MB**. Tool: **ScreenToGif** (Windows, free — record, trim, and export GIF in one app)
  or LICEcap. Save it as `docs/demo.gif`.
- **Announcement video** — longer MP4 (optionally with voiceover) for HN/YouTube/Reddit. Tool: **OBS
  Studio**, or **Win+G** (Xbox Game Bar) for a quick grab.

**Capture tips:** record at 1080p; bump the editor font (Ctrl+Mouse-wheel) so it's readable when
scaled down; use the **dark** theme (matches the panel); close clutter (Solution Explorer can stay,
hide the rest); and in editing **trim the model's "thinking" wait** so the loop feels snappy.

## Shot list (≈ 25 seconds)

1. **Open the project** — `File → Open → Project/Solution → demo/CheckoutDemo/CheckoutDemo.csproj`.
   The **Error List** shows `CS0019` on `GrandTotal`. (Have the Claude Code panel docked on the right.)
2. **Launch** — click **Launch Claude Code** in the panel; the pill turns green **Connected**.
3. **Ask** (type one of the prompts below).
4. **Diff opens** — Claude calls `getDiagnostics`, explains the bug, and the fix opens in the **native
   VS diff**. Click **Accept**.
5. **Resolved** — the Error List clears. (Optional: show the **stats panel** ticking up.)

### Prompts that demo well
- `There's a build error in this project — fetch the diagnostics and fix it.`
- `What compiler errors do you see? Fix them.`
- For a second beat showing **reject-with-feedback**: ask for a change, then click
  **Reject with feedback…** and type e.g. `use a named constant instead`.

## What Claude should do

It reads `CS0019` via `getDiagnostics`, then changes:

```diff
- private static readonly string TaxRate = "0.08";
+ private static readonly decimal TaxRate = 0.08m;
```

A clean one-line diff — exactly what reads well in a GIF.

## Note on C++ (the #15942 audience)

This fixture is **C#** on purpose: the `getDiagnostics` path is *verified* for C#. The C++ path is
designed (Error List backend) but **not yet tested end-to-end**. Since the #15942 thread is C++
developers, you'll want a C++ clip too — but **verify C++ `getDiagnostics` works before recording it**,
so the demo doesn't fall flat on the exact audience it's for. Ask and we'll build + test a C++ fixture.
