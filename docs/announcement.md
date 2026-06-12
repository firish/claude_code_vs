# Announcement drafts

Replace `<MARKETPLACE>`, `<REPO>`, and `<DEMO>` with your real links before posting. Lead with the GIF
everywhere. Be upfront that it's an unofficial community project.

---

## 1. GitHub issue #15942 (primary — highest-intent audience)

> **Subject:** none (it's a comment on the existing issue)

I built a native Visual Studio extension that implements Claude Code's IDE-integration protocol — so the
`claude` CLI drives a real VS diff window and sees your build errors, the same way the VS Code and
JetBrains integrations do.

What works today (Visual Studio 2026):

- **Native diff** with Accept / Reject / **Reject with feedback** — and it's the *single* approval gate
  (no duplicate y/n prompt in the terminal)
- **getDiagnostics** over the Error List, so Claude reads C#/C++ compiler errors and fixes them
- **Automatic selection + active-file context**
- **"Run wild"** auto-accept toggle, and a panel with **live token usage + estimated cost**

It's a protocol bridge — the CLI does all the agent work; the extension is just the IDE half.
Localhost-only, auth-token gated, no model calls of its own.

- Marketplace: `<MARKETPLACE>`
- Source + README: `<REPO>`
- Demo: `<DEMO>`

Unofficial / community project, not affiliated with Anthropic. Feedback very welcome — especially from
the C++ folks in this thread, since that's who I built it for.

---

## 2. Show HN

> **Title:** Show HN: Claude Code for Visual Studio – native diff and IDE integration

I implemented Claude Code's (undocumented) IDE-integration protocol as a native Visual Studio 2026
extension. The `claude` CLI does all the agent work; my extension is the IDE half — it speaks MCP/JSON-RPC
over a localhost WebSocket and drives a real VS diff window, shares compiler diagnostics, and tracks the
active selection.

The fun part was reverse-engineering the contract: the WebSocket upgrade has to echo
`Sec-WebSocket-Protocol: mcp` or the CLI silently drops the connection; making the VS diff the *single*
approval gate required a PreToolUse hook (the permission-prompt flag is headless-only); and non-ASCII
content broke in four separate encoding spots before it round-tripped.

Marketplace: `<MARKETPLACE>` · Source: `<REPO>` · Demo: `<DEMO>`

Unofficial, MIT, VS 2026 for now. Happy to answer protocol questions.

---

## 3. Reddit — r/cpp / r/visualstudio / r/ClaudeAI

> **Title:** I built a native Visual Studio extension for Claude Code (diff + diagnostics + IDE integration)

Claude Code shipped IDE integration for VS Code and JetBrains but not Visual Studio, so I built it. The
CLI drives a real VS diff window (accept/reject, or reject-with-feedback), reads your C#/C++ compiler
errors via getDiagnostics, and there's a panel with live token/cost stats. It's a thin protocol bridge —
no model calls of its own, localhost-only, auth-token gated.

VS 2026 for now, MIT, unofficial. Demo + install: `<REPO>`

(For r/cpp, lead with the C++ diagnostics angle — that's the original feature request.)

---

## 4. X / LinkedIn (short)

Claude Code now works *inside* Visual Studio 🎉

Native diff with accept/reject, reject-with-feedback, C#/C++ diagnostics sharing, and a live token/cost
panel. The CLI does the agent work; my extension is the IDE half of its integration protocol.

VS 2026 · MIT · unofficial. Demo 👇 `<DEMO>`
