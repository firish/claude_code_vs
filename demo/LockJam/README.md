# LockJam — deadlock-triage fixture (the `vs_threads` wait/lock heuristic)

A console app that **compiles, starts, then hangs**. Five background workers run against three accounts;
only **three** of them form the actual deadlock. The test: can the debugger surface flag the stuck threads
(`vs_threads` marks threads parked on a lock/wait), and can Claude pick the **cycle** out of the noise —
without mistaking the merely-idle thread or the busy thread for a culprit?

This is the one capability you **can't** confirm by reading source: which thread holds which lock is a
runtime fact, and the wait/lock flag depends on whether the BCL frame (`Monitor.Enter`) is actually
visible in EnvDTE's thread stacks (see the **caveat** below — it's the thing this fixture really probes).

## The five threads

| Thread name | What it does | Expected verdict |
|---|---|---|
| `xfer A->B` | holds account **A**, blocks acquiring **B** | **deadlock cycle** |
| `xfer B->C` | holds account **B**, blocks acquiring **C** | **deadlock cycle** |
| `xfer C->A` | holds account **C**, blocks acquiring **A** | **deadlock cycle** |
| `audit` | pure CPU spin, never blocks | **not** waiting (healthy) |
| `dispatch` | parked on an empty work queue (`SemaphoreSlim.Wait`) | waiting, but **not** the cycle |

The three `xfer` workers lock two accounts in a rotated order, so each holds exactly what the next one is
blocked on: `A → B → C → A`. A `Barrier` guarantees all three grab their first lock before any reaches for
the second, so the cycle forms on **every** run (no flaky interleaving).

## Run it

1. Open **`demo/LockJam/LockJam.sln`** in VS 2026 (Debug build, so symbols are present).
2. Claude Code panel → tick **Allow Claude to drive debugger** (needed so Claude can pause it with
   `vs_break_all`) → **Launch Claude Code**.
3. Press **F5** (or have Claude `vs_start_debugging`). The app wedges almost immediately and **hangs** — it
   does **not** self-break, so pausing it is the test.

> Why you pause it: a deadlocked thread never *hits* a breakpoint, so **Break All** is the only way in —
> that's exactly what `vs_break_all` does (or *Debug ▸ Break All* by hand). The fixture used to self-break;
> it now hangs so the run actually exercises `vs_break_all`. (Prefer auto-break? Uncomment the two lines in
> `Main`.)

## Ask Claude

> The app is hung (deadlocked). Pause it, look at all the threads, and tell me which ones are deadlocked
> and on what — and which ones are *not* part of the deadlock.

**What it should do:** call `vs_break_all` to freeze the hung process, then `vs_threads`, then reason over
the result (the thread names encode the route; each `xfer` worker's stack tops out in `Monitor.Enter`
inside `Transfer`).

## PASS / FAIL

**PASS (core — the heuristic spots the cycle):**
- `vs_threads` flags **`xfer A->B`, `xfer B->C`, `xfer C->A`** with `waiting: true, waitOn: "Monitor.Enter"`.
- Claude names those three as the deadlock and **excludes** `dispatch` (waiting, but on `SemaphoreSlim` —
  an empty queue, not a contended lock) and `audit` (running, not flagged at all).
- The `dispatch` thread is flagged `waiting: true, waitOn: "SemaphoreSlim"` — proving the heuristic
  distinguishes *kinds* of waits, and that "waiting" alone ≠ "deadlocked."

**PASS (stretch — exact cycle):** Claude reconstructs the `A → B → C → A` ordering by reading each stuck
worker's `from`/`to` locals — call `vs_get_frame_locals` with the `threadId` of each `xfer` thread (from
`vs_threads`) to see the account it holds vs. the one it's blocked acquiring.

**FAIL — and the most important thing to check:** if the `waiting` flags are **absent** and the raw
`stack` arrays show `[External Code]` instead of `System.Threading.Monitor.Enter`, then **Just My Code is
hiding the BCL frame** the heuristic matches on. The threads are still clearly stuck (three workers in
`Transfer`), but `WaitHint` never sees a marker. Fix to verify against: **Tools ▸ Options ▸ Debugging ▸
General → uncheck "Enable Just My Code"** (and ensure "Show external code" is on), re-run, and confirm
the `Monitor.Enter` frame — and therefore the flag — appears. If the flag only fires with JMC off, that's
a real finding: the heuristic should fall back to the *user* frame (e.g. `Transfer`) or we should widen
the markers.

## Notes

- **Per-thread locals are supported.** `vs_get_frame_locals` takes an optional `threadId` (from
  `vs_threads`): it switches `Debugger.CurrentThread` to that thread, reads the frame, and restores the
  context — so a non-current stuck thread's `from`/`to` are readable and the exact `A→B→C→A` ordering is
  reachable from the tools. (This closed a gap an earlier draft of this fixture flagged — pure EnvDTE, no AD7.)
- **No true lock ownership.** EnvDTE can't *report* "account B's gate is held by `xfer A->B`"; the cycle is
  inferred from names + per-thread locals + source, not handed to you. True ownership needs AD7/Concord or
  out-of-proc tooling (SOS `!syncblk`, ClrMD). Documented limitation (`docs/DEBUGGER.md`).

## The fix (don't apply — the fixture is meant to stay deadlocked)

Acquire the accounts in a **global order** (e.g. always lock the lower `Id` first) so no cycle can form.
