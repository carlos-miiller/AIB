---
gsd_state_version: 1.0
milestone: security-remediation-v1
milestone_name: Security Remediation v1
status: in_progress
last_updated: "2026-06-02T11:32:37.101Z"
progress:
  total_phases: 6
  completed_phases: 2
  total_plans: 10
  completed_plans: 6
  percent: 33
---

# STATE: AIB

**Last updated:** 2026-05-31

## Current position

Phase: 03 (tool-argument-hardening-quoting-denylist-skill-gating) — EXECUTING
Plan: 1 of 4
Phase: 3 — context gathered (`03-CONTEXT.md` + `03-DISCUSSION-LOG.md`); 12 decisions locked (D-01..D-12), "Factory reset" deferred. Ready for `/gsd-plan-phase 3`.

- **Project:** AIB (`.planning/PROJECT.md`)
- **Milestone:** security-remediation-v1 (`.planning/ROADMAP.md`)
- **Active phase:** Phase 3 — context complete; planning next.
- **Last action:** `/gsd-discuss-phase 3` (`49ecbdc`) — 12 decisions captured: hybrid floor list (D-01..D-04, closes Phase 1 D3 deferred slot), CommandService.ExecuteWithArgListAsync dual-path (D-05..D-07), CommandConfirmationWindow extended with script body + content-hash cache (D-08..D-10), InstallFromOnlineAsync hardened + DynamicSkillTool deleted (D-11, D-12).
- **Prior actions:** Phase 2 complete (`ddd82ed` marks roadmap, 02-04b executed earlier); `/gsd-discuss-phase 2` (`a7f943a`); Phase 1 execution + UAT outstanding. Commits: `bc4a191` … `66ee9a7` (Phase 1 T1-T9) → Phase 2 waves 1-4 → `ddd82ed`.

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
| 2 | Key rotation + `.env` hardening | complete (`ddd82ed`) | SEC-03 |
| 3 | Tool argument hardening | context gathered (`03-CONTEXT.md`) | SEC-04, SEC-05, SEC-06 |
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
- Phase 03 D-01: Hybrid denylist = tiny floor list refused at L<7, modal-only at L≥7. Preserves Phase 1 D5/D8 invariants.
- Phase 03 D-02: Floor list = recursive deletion + format/partition + shutdown/logoff + registry destructive ops. Lives in new `CommandFloorList.cs`.
- Phase 03 D-03: Matching = normalize-then-regex (lowercase, strip quote-concat, alias map mv→Move-Item etc., reject `-EncodedCommand` at L<7, regex word boundaries).
- Phase 03 D-04: Modal fires first; floor refuses post-Allow at L<7. CommandConfirmationContext gains `DenylistHit` + `DenylistReason`. New audit outcome `allow_then_floor_deny`. Closes Phase 1 D3 deferred slot.
- Phase 03 D-05: `CommandService` grows `ExecuteWithArgListAsync(fileName, IEnumerable<string> args, cwd, timeoutMs)`; shared `RunProcessAsync(Process)` helper with existing `ExecuteAsync(string)`. run_command path unchanged.
- Phase 03 D-06: LLM `arguments` string is a SINGLE ArgumentList element (`[scriptPath, argumentsString]`). Scripts parse their own argv[1].
- Phase 03 D-07: Static `InterpreterMap` Dictionary in `SkillService` is the single source of truth for fileName + switches; consumed by RunSkillAsync + InstallFromOnlineAsync.
- Phase 03 D-08: `CommandConfirmationWindow` extends in place; `CommandConfirmationContext` gains `ScriptBody`, `Interpreter`, `ContentHash`. New ScrollViewer row, Visibility-bound to ScriptBody != null. Shared `ShowAsync(ctx)` helper.
- Phase 03 D-09: `execute_skill` reads scriptPath off disk (≤50KB), computes SHA256, populates ScriptBody. Markdown-only skills skip modal.
- Phase 03 D-10: `AlwaysAllowSession` re-keyed by `(Tool, Cmd, ContentHash?)`. Session-only, lock-guarded, cleared on App.Exit. AuditLog gains optional `content_hash` field.
- Phase 03 D-11: `InstallFromOnlineAsync` keeps method body, adds SEC-06 regex `^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(@[A-Za-z0-9_.\-]+)?$` at :154, rewrites npx invocation to `ExecuteWithArgListAsync("npx", ["-y", "skills", "add", installArg, "--yes"], workPath, 900000)`. Dead today but future-ready.
- Phase 03 D-12: Delete `AIBWindows/Services/DynamicSkillTool.cs` entirely. Zero call sites; ToolRegistry comment already states lazy-loading uses execute_skill instead.

## Open accepted-risks

- CRITICAL `/unlock_level` chat backdoor — explicit owner decision; out of scope this milestone. Tracked for next milestone (dev-mode gating).

## Working tree

Clean after Phase 1 execution. 9 atomic commits landed (`bc4a191` … `66ee9a7`); `dotnet build AIBWindows/AIB.csproj` reports 0 errors, 0 new warnings.

## Next command

```
/gsd-plan-phase 3
```

Phase 3 context locked at `03-CONTEXT.md` (commit `49ecbdc`). 12 decisions ready for researcher + planner. Parallel manual UAT for phase 01 still pending: run the 8 scenarios in `01-HUMAN-UAT.md` against the WPF build (HEAD ≥ `f9c1471` recommended), then `/gsd-verify-work 1` to close the human-needed gate.
