---
phase: 2
slug: key-rotation-env-hardening
status: draft
nyquist_compliant: true
wave_0_complete: true
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
| 02-01-T1 | 02-01 | 1 | SEC-03.A1 | T-02-01 / V8 | Live key removed from disk | smoke (FS) | `Test-Path .env, AIBWindows/.env, AIBLinux/.env` → all False | ✅ | ⬜ pending |
| 02-04b-T4 | 02-04b | 4 | SEC-03.A2 | T-02-13 / — | Rotation evidence committed | manual + FS | `Test-Path .planning/phases/02-key-rotation-env-hardening/evidence/openai-console-rotation-*.png` → True | ✅ (.gitkeep via 02-01-T2) | ⬜ pending |
| 02-03-T2 | 02-03 | 2 | SEC-03.A3 | T-02-03 / V1 | Fresh clone → FirstRunWindow on first hotkey | manual smoke (UI) | Delete vault + set `AiProvider=OpenAI`; press hotkey → FirstRunWindow opens | ✅ | ⬜ pending |
| 02-02-T2 | 02-02 | 1 | SEC-03.A4 | T-02-04 / V5 | App refuses placeholder key | manual smoke | Paste `sk-PLACEHOLDER` → regex rejects + inline error + audit `firstrun_invalid_key` | ✅ | ⬜ pending |
| 02-03-T2 | 02-03 | 2 | D-01/D-08 | T-02-03 / V1 | Ollama branch skips FirstRunWindow | manual smoke (UI) | `AiProvider=Ollama` → hotkey opens ChatWindow directly | ✅ | ⬜ pending |
| 02-03-T2 | 02-03 | 2 | D-04 | T-02-06 / V7 | Cancel/X/Esc/Alt+F4 → audit + clean shutdown (parent-owned per Pitfall 6) | manual smoke + audit | Each cancel path: window sets DialogResult=false; `App.ShowFirstRunWindow` emits `outcome:firstrun_cancelled` then `Application.Current.Shutdown()` | ✅ | ⬜ pending |
| 02-02-T2 | 02-02 | 1 | D-05 | T-02-04 / V5 | Regex validation `^sk-[a-zA-Z0-9_-]{20,}$` | manual smoke (UI) | Reject short/placeholder; accept real-shape key | ✅ | ⬜ pending |
| 02-02-T2 | 02-02 | 1 | D-06 | T-02-07,T-02-08 / V6,V8 | Save writes vault + sentinel + audit | manual smoke + FS | `~/.AIB/credentials/openai.bin` exists, `settings.ApiKey == "use-vault"`, audit `firstrun_saved` | ✅ | ⬜ pending |
| 02-03-T1 | 02-03 | 2 | D-06 (read) | T-02-08 / V6 | `OpenAIService` reads from vault | manual smoke (chat) | After Save, chat request succeeds; no audit on read path | ✅ | ⬜ pending |
| 02-02-T2 | 02-02 | 1 | D-09 | T-02-11 / V1 | Ollama branch lists installed models | manual smoke (UI) | Dropdown populated; `ModelName == ShadowModelName == picked` | ✅ | ⬜ pending |
| 02-02-T2 | 02-02 | 1 | D-09 (fail) | T-02-11 / V7 | Ollama unavailable → fallback link | manual smoke (UI) | Stop Ollama → "Ollama não detectado" + retry + `qwen2.5:7b` link | ✅ | ⬜ pending |
| 02-01-T2 | 02-01 | 1 | D-10 | T-02-12 / V14 | `DotNetEnv` removed; build clean | automated | `Select-String "DotNetEnv" AIBWindows/AIB.csproj` empty; `dotnet build` → 0 errors | ✅ | ⬜ pending |
| 02-03-T2 | 02-03 | 2 | D-11 | T-02-02 / V8 | Existing-user migration runs once + idempotent | manual smoke + audit | Pre-set `ApiKey=sk-svcacct-test` → boot → `ApiKey=use-vault` + audit `migration_clear_apikey`; re-boot → no new line | ✅ | ⬜ pending |
| 02-04b-T4 | 02-04b | 4 | D-12 | T-02-13 / — | Rotation evidence captured | manual + FS | Owner-saved PNG in `evidence/`; referenced from VERIFICATION.md | ✅ | ⬜ pending |
| 02-03-T2 | 02-03 | 2 | NFR-04 | — | `profile.dat` schema unchanged | smoke (deserialize) | Phase-2 build reads Phase-1-era `profile.dat`; POCO unchanged (no `UserAppSettings` fields added in plan 03) | ✅ | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

**Mapping note (post Issue-4 plan split):** Plan 04 was split into `02-04a` (SettingsWindow polish, wave 3) and `02-04b` (docs + VERIFICATION.md + D-12 rotation checkpoint, wave 4 depends_on 02-04a). Task IDs above use the post-split form. Plan 01 Task 2 ships `.gitkeep` (wave 0 receptacle); Plan 04b Task 3 ships `02-VERIFICATION.md` (wave 0 UAT translation).

---

## Wave 0 Requirements

- [x] `.planning/phases/02-key-rotation-env-hardening/02-VERIFICATION.md` — translated from this per-task table into Phase-1-style numbered scenarios (S1, S2, …) with pre / step / expected / observed fields **by 02-04b task 3** (wave 4 deliverable; planned in 02-04b-PLAN.md after the Issue-4 split).
- [x] `.planning/phases/02-key-rotation-env-hardening/evidence/.gitkeep` — directory marker so D-12 screenshot has a tracked home **by 02-01 task 2** (wave 1 deliverable; planned in 02-01-PLAN.md task 2).
- [x] No automated test infrastructure to add — NFR-01 explicit waiver applies.

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| FirstRunWindow XAML rendering | D-07 | Visual verification requires desktop runtime | Launch app on fresh vault, press hotkey, confirm dark-theme match with ChatWindow |
| OpenAI key rotation in upstream console | SEC-03.A2 / D-12 | Manual action in OpenAI dashboard | Owner rotates `sk-svcacct-…`; captures redacted screenshot (OLD revoked + NEW last4); commits to `evidence/` |
| Cancel-paths shutdown (no tray ghost) | D-04 | Requires actual WPF event loop + tray notify-icon | Press Esc / X / Cancel / Alt+F4 each from FirstRunWindow; observe clean process exit + audit-log line (emitted by `App.ShowFirstRunWindow`, not by the window itself — Pitfall 6) |
| Ollama-unavailable fallback UX | D-09 (fail) | Requires stopping local Ollama service | Stop `ollama serve`; trigger FirstRunWindow → Ollama branch; observe fallback link |
| Existing-user migration once + idempotent | D-11 | Requires pre-seeded `profile.dat` + two boot cycles | Pre-encrypt `ApiKey=sk-svcacct-test` into `profile.dat`; boot → re-encrypt to `use-vault`; second boot → no duplicate audit |

---

## Validation Sign-Off

- [x] All tasks have automated build smoke OR Wave 0 dependencies wired
- [x] Sampling continuity: build runs after every commit; no 3 consecutive task commits without `dotnet build`
- [x] Wave 0 covers all MISSING references (02-VERIFICATION.md via 02-04b-T3, evidence/.gitkeep via 02-01-T2)
- [x] No watch-mode flags
- [x] Feedback latency < 30s (build) per task
- [x] `nyquist_compliant: true` set in frontmatter — every row tied to a concrete `02-XX-TY` plan/task ID

**Approval:** approved 2026-05-31
