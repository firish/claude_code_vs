# Launch checklist — Claude Code for Visual Studio

From "it builds" to "people are using it." Work top-to-bottom; the order matters.

**Highest-leverage move:** post in [anthropics/claude-code#15942](https://github.com/anthropics/claude-code/issues/15942) — the people who asked for exactly this. But do Phases 0–4 first so they land on something installable with a clear README.

---

## Phase 0 — Decisions (gate everything)
- [x] License: **MIT**
- [ ] Open-source the repo (recommended — it auto-installs hooks + binds a socket; people will want to read it)
- [ ] Free vs paid (recommend **free** for v1)
- [x] Names: display name "Claude Code for Visual Studio", publisher "US Calibration"

## Phase 1 — Repo hygiene & docs
- [x] `README.md`
- [x] `LICENSE` (MIT)
- [ ] `CHANGELOG.md` (start at 1.0.0)
- [ ] Add a hero **GIF** at `docs/demo.gif` and screenshots
- [ ] Create a **public GitHub repo** and push (no remote yet) — update the placeholder URLs in `README.md` and `source.extension.vsixmanifest`
- [ ] Confirm no secrets in git history (auth token is redacted in logs — verify)

## Phase 2 — Package the VSIX
- [ ] Add an **icon** (`Resources/icon.png`, 90×90) and a **preview image**, then uncomment the `<Icon>` / `<PreviewImage>` / `<License>` lines in `source.extension.vsixmanifest`
- [x] Version set to **1.0.0**; metadata/tags filled
- [ ] Build **Release**: `msbuild src/ClaudeCodeVS/ClaudeCodeVS.csproj /t:Rebuild /p:Configuration=Release`
- [ ] Confirm install target stays `[18.0,19.0)` (VS 2026)
- [ ] **Test the Release `.vsix` on a clean VS 2026** — not your dev box, not the Experimental instance
- [ ] (Optional) code-sign the VSIX

## Phase 3 — Publish to VS Marketplace
- [ ] Create a publisher at **marketplace.visualstudio.com/manage** (Microsoft account) — the *Visual Studio* Marketplace, not the VS Code one
- [ ] Upload the `.vsix`; fill the listing (overview from README, screenshots, price = free, enable Q&A)
- [ ] Attach the `.vsix` to a **GitHub Release** too, for sideloading

## Phase 4 — Demo assets
- [ ] 60–90s capture: launch → Claude fixes a **C++** build error → native diff → accept → green; then reject-with-feedback + the stats panel
- [ ] Tools: OBS (video), ScreenToGif/LICEcap (README GIF, keep it < 10 MB)

## Phase 5 — Announce (see `docs/announcement.md` for ready-to-paste drafts)
1. [ ] **GitHub #15942** comment
2. [ ] Anthropic **Claude Developers Discord** / community forum
3. [ ] **r/cpp**, **r/visualstudio**, **r/ClaudeAI**
4. [ ] **Show HN**
5. [ ] **X / LinkedIn** with the GIF
6. [ ] (Optional) a **blog post** on reverse-engineering the IDE protocol — good content marketing
7. [ ] (Optional) ping **Anthropic DevRel**

## Phase 6 — Keep it alive
- [ ] **Protocol-bump smoke test on every `claude` update:** run the spike, confirm handshake + openDiff + getDiagnostics + `/permission`, then bump the pinned version.
- [ ] Triage Marketplace Q&A + GitHub issues fast in week one; ask reporters for the **Output → Claude Code** pane.
- [ ] Backfill **VS 2022** support if there's demand.
- [ ] Semver; ship patches quickly given the fragility.
