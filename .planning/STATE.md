---
gsd_state_version: 1.0
milestone: security-remediation-v1
milestone_name: Security Remediation v1
status: in_progress
last_updated: "2026-05-31T03:50:46.348Z"
progress:
  total_phases: 6
  completed_phases: 1
  total_plans: 6
  completed_plans: 1
  percent: 17
---

# STATE: AIB

**Last updated:** 2026-05-30

## Current position

Phase: 1 — complete (code + verifier; 8-scenario UAT persisted at `01-HUMAN-UAT.md`, surfaces in /gsd-progress)
Phase: 2 — context gathered (`02-CONTEXT.md` + `02-DISCUSSION-LOG.md`); 12 decisions locked, Linux deferred. Ready for `/gsd-plan-phase 2`.

- **Project:** AIB (`.planning/PROJECT.md`)
- **Milestone:** security-remediation-v1 (`.planning/ROADMAP.md`)
- **Active phase:** Phase 2 — context complete; planning next.
- **Last action:** `/gsd-discuss-phase 2` (`a7f943a`) — 12 decisions captured (D-01..D-12); fixed ROADMAP.md `## Phase N — ...` → `## Phase N: ...` so parser recognizes phases; AIBLinux key path deferred to feature-parity milestone.
- **Prior actions:** `/gsd-execute-phase 1` → code review (2 criticals fixed) → verifier `human_needed`. Commits: `bc4a191` (T1) → `8b9f62e` (T2) → `f777314` (T3) → `39607d2` (T4) → `2e1568d` (T5) → `f86a753` (T6) → `6c078c2` (T7) → `8168f30` (T8) → `66ee9a7` (T9) → `644fc80` (exec docs) → `f9c1471` (CR-01 + CR-02) → Phase 1 artifacts. Earlier: `/gsd-plan-phase 1` (`aeba91b`); baseline (`6d744c7`); `/gsd-new-project` (`105a59c`); `/gsd-map-codebase` (`93f4d8a`).

## Quick links

- Codebase map: `.planning/codebase/`
- Security findings: `.planning/codebase/CONCERNS.md` (39 findings — 4 CRITICAL, 9 HIGH, 14 MEDIUM, 12 LOW)
- Requirements: `.planning/REQUIREMENTS.md` (10 in-scope requirements: SEC-01..08, REL-01..02)
- Roadmap: `.planning/ROADMAP.md` (6 phases)
- Governance: `GRAVITY.MD`, `Regras de Identidade/{CODIGO_LIMPO,SEGURANCA,VISUAL,funcionalities}.md`

## Phase progress

| # | Phase | Status | Requirements |
|---|---|---|---|
| 1 | Modal confirmation + Level-9 alignment | complete (code + verifier; UAT persists in `01-HUMAN-UAT.md`) | SEC-01, SEC-02 |
| 2 | Key rotation + `.env` hardening | context gathered (`02-CONTEXT.md`) | SEC-03 |
| 3 | Tool argument hardening | not started | SEC-04, SEC-05, SEC-06 |
| 4 | Prompt-injection isolation | not started | SEC-07 |
| 5 | Filesystem exfiltration controls | not started | SEC-08 |
| 6 | Reliability: warmup + retries | not started | REL-01, REL-02 |

## Decisions made (this milestone)

- Phase 01 D1: modal hops UI thread via `Application.Current.Dispatcher.InvokeAsync<(bool,bool)>(...).Task`.
- Phase 01 D2: AlwaysAllow is a session-only `HashSet<string>` with exact-string match, lock-guarded, cleared on app exit.
- Phase 01 D3: modal carries `CommandConfirmationContext` with `Tool`, `Command`, `Level`, `Cwd`; legacy string ctor dropped (zero call sites).
- Phase 01 D4: Esc / X / Alt+F4 deny via `IsCancel="True"` on the Deny button.
- Phase 01 D5: Level 9 disables only the denylist — never the human-in-the-loop modal.
- Phase 01 D6: audit log JSONL at `~/.AIB/logs/audit.log` via `DirectoryService.DataDir`; `SemaphoreSlim(1,1)`; fire-and-forget; `[AUDIT]` console tag on failure.
- Phase 01 D7: manual UAT, 8 scenarios codified in `VERIFICATION.md`.
- Phase 01 D8: `settings.ConfirmDangerousCommands` gates ONLY the post-modal denylist at levels < 9; never the modal itself. Migration: missing field → C# default `true`.
- Phase 02 D-01: First-run UX triggers lazily on hotkey, not app launch (tray-app pattern).
- Phase 02 D-02: Dedicated `FirstRunWindow` (new XAML + code-behind); separate from `SettingsWindow`.
- Phase 02 D-03: Detector provider-aware — skip FirstRunWindow when `AiProvider == "Ollama"`.
- Phase 02 D-04: Cancel = `Application.Current.Shutdown()`; tray icon stays.
- Phase 02 D-05: Save validation = regex only `^sk-[a-zA-Z0-9_-]{20,}$`; no network.
- Phase 02 D-06: Save target = `CredentialService` vault only (`system="openai"`, `key="ApiKey"`); `settings.ApiKey = "use-vault"` sentinel; `OpenAIService.cs:657,705` rewritten.
- Phase 02 D-07: Visual style = match ChatWindow theme, centered, welcome title/msg, provider picker first.
- Phase 02 D-08: Lifecycle = FirstRunWindow → ChatWindow strictly sequential; ChatWindow hidden until Save.
- Phase 02 D-09: Ollama branch = `/api/tags` via `SettingsService.GetOllamaModelsAsync`; dropdown of installed models; `ShadowModelName` mirrors `ModelName`.
- Phase 02 D-10: Delete `.env` from disk + remove `DotNetEnv` from `AIB.csproj:17` (zero call sites); ship `.env.example` with placeholders.
- Phase 02 D-11: Migration = force re-entry; on first launch post-deploy, overwrite `settings.ApiKey = "use-vault"` unconditionally; do NOT copy old key into vault (rotation must happen first).
- Phase 02 D-12: Rotation evidence = screenshot in `.planning/phases/02-key-rotation-env-hardening/evidence/` showing old key revoked + new key last4 + date.

## Open accepted-risks

- CRITICAL `/unlock_level` chat backdoor — explicit owner decision; out of scope this milestone. Tracked for next milestone (dev-mode gating).

## Working tree

Clean after Phase 1 execution. 9 atomic commits landed (`bc4a191` … `66ee9a7`); `dotnet build AIBWindows/AIB.csproj` reports 0 errors, 0 new warnings.

## Next command

```
/gsd-plan-phase 2
```

Phase 2 context locked at `02-CONTEXT.md` (commit `a7f943a`). 12 decisions ready for researcher + planner. Parallel manual UAT for phase 01 still pending: run the 8 scenarios in `01-HUMAN-UAT.md` against the WPF build (HEAD ≥ `f9c1471` recommended), then `/gsd-verify-work 1` to close the human-needed gate.
