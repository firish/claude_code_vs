# WebQuote — attach-to-a-running-app fixture

A minimal ASP.NET Core API that exists to test the **real-app** debugger path: **attach to a live
process** (not F5), arm **break-on-thrown**, then drive a request to the bug. It's the web-ified twin of
the [`NullOrigin`](../NullOrigin) console fixture.

**The bug:** `GET /quote/{id}` prices an order. Order **103** has a `null` Customer, so
[`Pricing.QuoteFor`](Program.cs) throws a `NullReferenceException` deep in the handler — which a generic
`catch` turns into a bland **500**. The status code tells you `/quote/103` is broken; it does *not* tell
you it's a null Customer in `Pricing.QuoteFor`. Break-on-thrown lands you at the throw site, with the
offending order in scope.

## Why a web app (and not another console)

Console fixtures exit immediately — there's nothing to attach to. A web app **stays running**, so it's a
real live process you attach to, exactly like a hosted ASP.NET app or a service. This is the case you
*can't* do with F5.

## 1. Run it (outside VS)

```powershell
dotnet run --project demo/WebQuote
# -> now serving http://localhost:5179   (Ctrl+C to stop)
```

Sanity check in another terminal: `curl http://localhost:5179/quote/101` → `{"order":101,"price":80}`.
`curl http://localhost:5179/quote/103` → a 500 (`could not price order 103: NullReferenceException`).

> **Attach gotcha:** `dotnet run` launches the app as a **child** `dotnet` process, so you may see two
> `dotnet` entries. Attach to the one **hosting the app** (bound to 5179 — usually the higher PID). To
> avoid the ambiguity entirely, run the built output instead:
> `dotnet demo/WebQuote/bin/Debug/net8.0/WebQuote.dll` — one clean process. Use a **Debug** build so the
> PDBs are present and VS can break with source.

## 2. Set up VS

1. Open **`demo/WebQuote/WebQuote.sln`** in VS 2026 (so VS has the source + symbols for the break).
2. Claude Code panel → tick **Allow Claude to drive debugger** → **Launch Claude Code**.

## 3a. Let Claude drive the whole loop (the headline)

Because Claude Code can run `curl`, it can close the entire loop itself — attach, arm, trigger, inspect:

> A WebQuote API is running on http://localhost:5179 and returns a 500 for `/quote/103`. Attach to its
> process, turn on break-on-thrown for `System.NullReferenceException`, then trigger `GET /quote/103` and
> tell me exactly where and why it throws.

What it should do:
1. `vs_list_processes` (filter `WebQuote` or `dotnet`) → find the PID.
2. `vs_attach` to it → `vs_debug_state` shows `mode: run` + `debuggedProcesses: [WebQuote…]` (the new
   multi-process field).
3. `vs_break_on_thrown` `System.NullReferenceException`.
4. **Trigger the request in the background** — `curl http://localhost:5179/quote/103`. This call *hangs*,
   because the request thread is now paused at the break; that's expected.
5. `vs_debug_state` → `mode: break` at `Pricing.QuoteFor`, with `order.Customer == null`.
6. `vs_exception` → the `NullReferenceException` (type + message).
7. Diagnose (null Customer on order 103), then `vs_continue` / `vs_stop_debugging` to release the request.

**PASS** = it breaks inside `Pricing.QuoteFor` (the *origin*), not the `catch`, and reports the null
Customer — a cause the 500 alone never revealed.

## 3b. Or you drive the request (desktop/UI-style)

If you'd rather trigger it yourself (mirrors a UI action Claude can't perform): attach + arm first via
Claude, then in a terminal `curl http://localhost:5179/quote/103` (it will hang at the break). The
prompt-submit hook injects the live break state, so just ask Claude **"it's paused now — what's wrong?"**

## 4. Bonus checks (cover the rest of the new surface)

- **Function breakpoint + managed-engine check:** *"Set a breakpoint on `Pricing.QuoteFor` by function
  name, then trigger `/quote/101` and read the locals."* If the locals read correctly, plain `Attach()`
  selected the managed engine (no `Attach2` needed) — and it confirms `vs_set_breakpoint`'s `function`
  mode binds.
- **Conditional breakpoint at scale:** *"Break in `Pricing.QuoteFor` only when `order.Id == 103`,"* then
  hit a few endpoints — it should stop only on 103.

## The fix (don't apply unless you're testing the diff)

```diff
- decimal discount = order.Customer.Tier == "Gold" ? 0.20m : 0m;
+ decimal discount = order.Customer?.Tier == "Gold" ? 0.20m : 0m;
```

(Or reject orders with no Customer up front. The fixture stays buggy so it's reusable.)
