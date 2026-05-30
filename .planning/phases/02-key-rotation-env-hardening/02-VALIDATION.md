---
phase: 2
slug: key-rotation-env-hardening
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-05-30
---

# Phase 2 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | None (per NFR-01; no xUnit/NUnit/MSTest). Phase ships manual UAT + compile-as-test. |
| **Config file** | none |
| **Quick run command** | `dotnet build AIBWindows/AIB.csproj` |
| **Full suite command** | Manual UAT via 02-VERIFICATION.md + `dotnet build AIBWindows/AIB.csproj` clean |
| **Estimated runtime** | ~19 seconds (build only); UAT scenarios ~15 min manual |

---

## Sampling Rate

- **After every task commit:** Run `dotnet build AIBWindows/AIB.csproj`
- **After every plan wave:** Re-run full UAT scenario set + audit-log inspect
- **Before `/gsd-verify-work`:** Full suite green + screenshot evidence committed + SEGURANCA.MD updated + CONCERNS.md "Resolved" section updated
- **Max feedback latency:** ~25 seconds (build) per task; full UAT ~15 min

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| TBD-by-planner | TBD | TBD | SEC-03.A1 | T-02-01 / V8 | Live key removed from disk | smoke (FS) | `Test-Path .env, AIBWindows/.env, AIBLinux/.env` → all False | ❌ W0 | ⬜ pending |
| TBD-by-planner | TBD | TBD | SEC-03.A2 | T-02-02 / — | Rotation evidence committed | manual + FS | `Test-Path .planning/phases/02-key-rotation-env-hardening/evidence/openai-console-rotation-*.png` → True | ❌ W0 | ⬜ pending |
| TBD-by-planner | TBD | TBD | SEC-03.A3 | T-02-03 / V1 | Fresh clone → FirstRunWindow on first hotkey | manual smoke (UI) | Delete vault + set `AiProvider=OpenAI`; press hotkey → FirstRunWindow opens | ❌ W0 | ⬜ pending |
| TBD-by-planner | TBD | TBD | SEC-03.A4 | T-02-04 / V5 | App refuses placeholder key | manual smoke | Paste `sk-PLACEHOLDER` → regex rejects + inline error + audit `firstrun_invalid_key` | ❌ W0 | ⬜ pending |
| TBD-by-planner | TBD | TBD | D-01/D-08 | T-02-05 / V1 | Ollama branch skips FirstRunWindow | manual smoke (UI) | `AiProvider=Ollama` → hotkey opens ChatWindow directly | ❌ W0 | ⬜ pending |
| TBD-by-planner | TBD | TBD | D-04 | T-02-06 / V7 | Cancel/X/Esc/Alt+F4 → audit + clean shutdown | manual smoke + audit | Each cancel path emits `outcome:firstrun_cancelled` then `Application.Shutdown` | ❌ W0 | ⬜ pending |
| TBD-by-planner | TBD | TBD | D-05 | T-02-07 / V5 | Regex validation `^sk-[a-zA-Z0-9_-]{20,}$` | manual smoke (UI) | Reject short/placeholder; accept real-shape key | ❌ W0 | ⬜ pending |
| TBD-by-planner | TBD | TBD | D-06 | T-02-08 / V6,V8 | Save writes vault + sentinel + audit | manual smoke + FS | `~/.AIB/credentials/openai.bin` exists, `settings.ApiKey == "use-vault"`, audit `firstrun_saved` | ❌ W0 | ⬜ pending |
| TBD-by-planner | TBD | TBD | D-06 (read) | T-02-09 / V6 | `OpenAIService` reads from vault | manual smoke (chat) | After Save, chat request succeeds; no audit on read path | ❌ W0 | ⬜ pending |
| TBD-by-planner | TBD | TBD | D-09 | T-02-10 / V1 | Ollama branch lists installed models | manual smoke (UI) | Dropdown populated; `ModelName == ShadowModelName == picked` | ❌ W0 | ⬜ pending |
| TBD-by-planner | TBD | TBD | D-09 (fail) | T-02-11 / V7 | Ollama unavailable → fallback link | manual smoke (UI) | Stop Ollama → "Ollama não detectado" + retry + `qwen2.5:7b` link | ❌ W0 | ⬜ pending |
| TBD-by-planner | TBD | TBD | D-10 | T-02-12 / V14 | `DotNetEnv` removed; build clean | automated | `Select-String "DotNetEnv" AIBWindows/AIB.csproj` empty; `dotnet build` → 0 errors | ❌ W0 | ⬜ pending |
| TBD-by-planner | TBD | TBD | D-11 | T-02-13 / V8 | Existing-user migration runs once + idempotent | manual smoke + audit | Pre-set `ApiKey=sk-svcacct-test` → boot → `ApiKey=use-vault` + audit `migration_clear_apikey`; re-boot → no new line | ❌ W0 | ⬜ pending |
| TBD-by-planner | TBD | TBD | D-12 | T-02-14 / — | Rotation evidence captured | manual + FS | Owner-saved PNG in `evidence/`; referenced from VERIFICATION.md | ❌ W0 | ⬜ pending |
| TBD-by-planner | TBD | TBD | NFR-04 | T-02-15 / — | `profile.dat` schema unchanged | smoke (deserialize) | Phase-2 build reads Phase-1-era `profile.dat`; POCO unchanged | ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] `.planning/phases/02-key-rotation-env-hardening/02-VERIFICATION.md` — translate the table above into Phase-1-style numbered scenarios (S1, S2, …) with pre / step / expected / observed fields
- [ ] `.planning/phases/02-key-rotation-env-hardening/evidence/.gitkeep` — directory marker so D-12 screenshot has a tracked home
- [ ] No automated test infrastructure to add — NFR-01 explicit waiver applies

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| FirstRunWindow XAML rendering | D-07 | Visual verification requires desktop runtime | Launch app on fresh vault, press hotkey, confirm dark-theme match with ChatWindow |
| OpenAI key rotation in upstream console | SEC-03.A2 / D-12 | Manual action in OpenAI dashboard | Owner rotates `sk-svcacct-…`; captures redacted screenshot (OLD revoked + NEW last4); commits to `evidence/` |
| Cancel-paths shutdown (no tray ghost) | D-04 | Requires actual WPF event loop + tray notify-icon | Press Esc / X / Cancel / Alt+F4 each from FirstRunWindow; observe clean process exit + audit-log line |
| Ollama-unavailable fallback UX | D-09 (fail) | Requires stopping local Ollama service | Stop `ollama serve`; trigger FirstRunWindow → Ollama branch; observe fallback link |
| Existing-user migration once + idempotent | D-11 | Requires pre-seeded `profile.dat` + two boot cycles | Pre-encrypt `ApiKey=sk-svcacct-test` into `profile.dat`; boot → re-encrypt to `use-vault`; second boot → no duplicate audit |

---

## Validation Sign-Off

- [ ] All tasks have automated build smoke OR Wave 0 dependencies wired
- [ ] Sampling continuity: build runs after every commit; no 3 consecutive task commits without `dotnet build`
- [ ] Wave 0 covers all MISSING references (02-VERIFICATION.md, evidence/.gitkeep)
- [ ] No watch-mode flags
- [ ] Feedback latency < 30s (build) per task
- [ ] `nyquist_compliant: true` set in frontmatter after planner ties Task IDs → table rows

**Approval:** pending
