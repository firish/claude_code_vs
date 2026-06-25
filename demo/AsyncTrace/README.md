# AsyncTrace — async-stack & cross-await inspection fixture

A console app with a three-level **async** pipeline. The point isn't a hard bug — it's to confirm what the
debugger surface (`vs_debug_state`, `vs_get_frame_locals`, `vs_evaluate`) actually sees when you're paused
on an **async continuation**, where the physical thread stack no longer holds the logical callers.

## The chain

```
Main → await RunAsync(items)
            └ foreach item: running += await ComputeAsync(n, running)
                                          └ await InnerAsync(n, soFar)
                                                └ await Task.Yield(); … ; return blended;   ← breakpoint
```

`ComputeAsync` does `Task.Delay(15).ConfigureAwait(false)` and `InnerAsync` does `await Task.Yield()`, so by
the time you stop on the marked line in `InnerAsync` you've **resumed on a threadpool thread** — a real
continuation, not the original `Main` thread. That's exactly the case where async-aware stacks matter.

## Run it

1. Open **`demo/AsyncTrace/AsyncTrace.sln`** in VS 2026 (Debug build).
2. Set a breakpoint on the `return blended;` line in `InnerAsync` (marked in [Program.cs](Program.cs)).
3. Claude Code panel → **Launch Claude Code**. Reads are ungated; no drive toggle needed (unless you want
   Claude to `vs_continue` between iterations).
4. Press **F5**. The breakpoint hits once per item (4 times).

## Expected values (so PASS is checkable)

Input `{5, 3, 8, 2}`, `running` accumulates across iterations. `scaled = n*2`, `blended = scaled + soFar/2`
(integer division):

| hit | n | soFar | scaled | blended |
|---|---|---|---|---|
| 1st | 5 | 0  | 10 | 10 |
| 2nd | 3 | 10 | 6  | 11 |
| 3rd | 8 | 21 | 16 | 26 |
| 4th | 2 | 47 | 4  | 27 |

Final output: `total = 74`. The 3rd hit (`n=8, soFar=21`) is the most useful to inspect — non-trivial
`soFar`, so you can confirm the accumulator carried correctly across the awaits.

## Ask Claude

> I'm paused inside `InnerAsync`. Read the locals here, then walk up the call stack and show me the async
> callers (`ComputeAsync`, `RunAsync`) and their locals. What thread are we on?

## PASS / characterize

**PASS — current async frame (this must work):**
- `vs_debug_state` / the push hook reports `stoppedAt` in `InnerAsync`, with `args` `n` + `soFar` and
  locals `scaled` + `blended` reading the **correct post-await values** from the table above (e.g. on the
  3rd hit: `n=8, soFar=21, scaled=16, blended=26`). This proves the debugger maps the state machine's
  hoisted fields back to source names across the await.
- `vs_evaluate("scaled + soFar")` in frame 0 returns the right number (e.g. `37` on the 3rd hit).
- The thread is a **threadpool** thread, not the `Main` thread — confirming we're genuinely on a
  continuation (otherwise the test isn't exercising the async boundary).

**Characterize — the logical async stack (the open question):** record which of these you get:
- **(best)** `callStack` / `vs_get_frame_locals(1+)` reaches the *logical* async callers — `ComputeAsync`
  (with its `n`, `soFar`) and `RunAsync` (with `running`, `items`). This means EnvDTE surfaces VS's
  stitched async call stack. ✅ a genuine capability to document.
- **(limited)** the stack shows only the *physical* resumed frames — `InnerAsync.MoveNext`, async-builder
  internals, `ThreadPool…`, `[External Code]` — and the async callers are absent. Then cross-await
  *caller* inspection isn't available through the current tools; note it as a known limitation (and a
  motivation for the per-frame-source / `IDebugStackFrame2` follow-up in `docs/DEBUGGER.md`).

**RESULT (live, 1.3.0):** **(limited)** — physical-only. At the break the stack is `InnerAsync → [Resuming Async Method] → MoveNext → ThreadPool.Dispatch → …`; `ComputeAsync`/`RunAsync`/`Main` are absent (suspended state machines). Current-frame post-await locals + `vs_evaluate` read correctly across iterations, and `threadId` targeting works — but the logical async callers and their suspended hoisted locals aren't reachable, and it's **not** a JMC artifact. Logged as a `docs/DEBUGGER.md` limitation + a ROADMAP item (a logical async-stack reader, the high-value follow-up).

Either outcome is a useful result — the goal is to **know** which one is true, since it can't be inferred
from source. (If the stack is physical-only, try toggling **Tools ▸ Options ▸ Debugging → "Enable Just My
Code"** and re-checking — JMC affects how async frames are presented.)
