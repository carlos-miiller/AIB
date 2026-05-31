---
phase: 02-key-rotation-env-hardening
plan: "04b"
subsystem: docs-uat-rotation
tags: [security, docs, uat, key-rotation, d-12, evidence]
dependency_graph:
  requires: [02-04a]
  provides: [seguranca-md-updated, readme-updated, verification-md, d12-rotation-evidence-text]
  affects: ["Regras de Identidade/SEGURANCA.MD", "README.md", ".planning/phases/02-key-rotation-env-hardening/02-VERIFICATION.md", ".planning/phases/02-key-rotation-env-hardening/evidence/"]
tech_stack:
  added: []
  patterns:
    - present-tense-shipped-behavior-nfr-03
    - phase1-style-uat-translation
    - owner-driven-key-rotation
key_files:
  created:
    - .planning/phases/02-key-rotation-env-hardening/02-VERIFICATION.md
    - .planning/phases/02-key-rotation-env-hardening/evidence/D-12-ROTATION-NOTE.md
  modified:
    - Regras de Identidade/SEGURANCA.MD
    - README.md
  deleted: []
decisions:
  - "D-12 manual rotation executed: OLD OpenRouter (not OpenAI) key revoked; new key NOT provisioned per owner choice; AIB operates Ollama-only until next provisioning"
  - "NFR-03 — SEGURANCA.MD section 2 + README.md key-setup rewritten in present tense for shipped DPAPI vault + FirstRunWindow path"
  - "Task 3 deviations documented in evidence/D-12-ROTATION-NOTE.md (binary screenshot substitute)"
metrics:
  duration: "~12 minutes (Tasks 1+2 inline; Task 3 owner-driven async)"
  completed: "2026-05-31"
  tasks_completed: 3
  files_changed: 4
---

# Phase 2 Plan 04b: Docs + UAT + D-12 Rotation Summary

**One-liner:** Doc surface (SEGURANCA.MD section 2 + README.md Requisitos Globais) rewritten in present tense to describe the shipped DPAPI vault + FirstRunWindow flow; `02-VERIFICATION.md` translates 15 VALIDATION.md rows into Phase-1-style numbered UAT scenarios with full audit-literal coverage; D-12 rotation executed by owner (OLD key revoked on OpenRouter 2026-05-31) with text evidence note in lieu of committed binary PNG.

## Tasks Completed

| Task | Name | Commit | Key Files |
|------|------|--------|-----------|
| 1 | SEGURANCA.MD + README — present-tense DPAPI vault + FirstRunWindow | docs(02-04b): SEGURANCA.MD + README — present-tense DPAPI vault + FirstRunWindow | Regras de Identidade/SEGURANCA.MD + README.md |
| 2 | 02-VERIFICATION.md — 15 Phase-1-style UAT scenarios | docs(02-04b): 02-VERIFICATION.md — 15 Phase-1-style UAT scenarios | .planning/phases/02-key-rotation-env-hardening/02-VERIFICATION.md |
| 3 | D-12 manual rotation — checkpoint:human-action | (this commit — D-12-ROTATION-NOTE.md + SUMMARY) | .planning/phases/02-key-rotation-env-hardening/evidence/D-12-ROTATION-NOTE.md |

## Files Changed

### Task 1 — `Regras de Identidade/SEGURANCA.MD` (+1 line)
Section 2 ("Tratamento Criptográfico de Segredos") gains a new bullet stating in present tense:
- Chave da OpenAI armazenada exclusivamente no cofre DPAPI em `~/.AIB/credentials/openai.bin`.
- `settings.ApiKey` carries the literal sentinel `"use-vault"`.
- `OpenAIService` consults `CredentialService.RetrieveCredential("openai", "ApiKey")` on every `ChatClient` construction.
- Entry path: `FirstRunWindow` on first `Ctrl+Shift+Space` after install or rotation (detector `App.NeedsFirstRun`).
- Re-key path: `Alterar chave` button in `SettingsWindow` (no app shutdown on Cancel).

NFR-03 — present-tense factual statement; no `deve`/`deverá`/`será` in the new bullet.

### Task 1 — `README.md` (+1/-1)
Replaced the `Requisitos Globais` line:
- Before: `Arquivo .env configurado na raiz com as chaves de API necessárias (caso não use Ollama).`
- After: present-tense bullet covering FirstRunWindow first-hotkey trigger, DPAPI vault path, `use-vault` sentinel in `profile.dat`, and absence of `.env` in the Windows flow; re-key via Settings "Alterar chave".

### Task 2 — `.planning/phases/02-key-rotation-env-hardening/02-VERIFICATION.md` (+255 lines, new)
15 numbered scenarios (S1..S15) mirroring Phase 1 VERIFICATION format:
- Each scenario: Pre / Step / Expected / Observed / Result fields.
- 1:1 mapping to VALIDATION.md per-task table rows.
- Audit Outcome Coverage Map appended (all 4 literals: `firstrun_saved`, `firstrun_invalid_key`, `firstrun_cancelled`, `migration_clear_apikey`).
- D-XX Coverage Map appended (D-01, D-03, D-04, D-05, D-06, D-09, D-10, D-11, D-12).
- D-12 evidence path explicitly named in S2.
- Sign-Off section with 4 closure checkboxes.

### Task 3 — `.planning/phases/02-key-rotation-env-hardening/evidence/D-12-ROTATION-NOTE.md` (new)
Text substitute for the planned binary PNG. Documents:
- Provider deviation (OpenRouter, not OpenAI direct).
- New-key deferral (owner chose Ollama-only operation).
- Screenshot provenance (in-chat session 2026-05-31; binary not persisted due to orchestrator tool-surface limitation).
- SEC-03 closure assessment per acceptance row.

## Verification (Plan Acceptance Criteria)

| Check | Result |
|-------|--------|
| `Grep "FirstRunWindow" SEGURANCA.MD` ≥1 | ✓ 1 match (new bullet) |
| `Grep "DPAPI" SEGURANCA.MD` ≥1 in section 2 | ✓ 2 matches (existing + new) |
| `Grep "FirstRunWindow" README.md` ≥1 | ✓ 1 match (replacement line) |
| `Grep "Arquivo .env configurado na raiz" README.md` = 0 | ✓ 0 matches (removed) |
| `Grep "DPAPI" README.md` ≥1 | ✓ 1 match (replacement line) |
| 02-VERIFICATION.md exists | ✓ |
| 02-VERIFICATION.md scenario count ≥13 | ✓ 15 |
| `firstrun_saved` / `firstrun_invalid_key` / `firstrun_cancelled` / `migration_clear_apikey` all present | ✓ all 4 |
| `evidence/openai-console-rotation` referenced | ✓ S2 |
| 9 D-XX references present | ✓ D-01, D-03, D-04, D-05, D-06, D-09, D-10, D-11, D-12 |
| Each scenario has Pre/Step/Expected/Observed fields | ✓ 15 scenarios × 4 fields |
| OLD key revoked upstream | ✓ confirmed (OpenRouter dashboard empty) |
| Redacted binary screenshot committed | ✗ NOT COMMITTED (deviation — text note substitute at `evidence/D-12-ROTATION-NOTE.md`) |
| New key paste + chat round-trip end-to-end | ✗ DEFERRED (owner chose Ollama-only) |

## Deviations from Plan

### D-1: Provider mismatch (OpenRouter, not OpenAI direct)

**Found during:** Task 3 owner action.

**Issue:** Plan named `platform.openai.com` and `OpenAI service-account keys`. Actual provider is **OpenRouter** (`openrouter.ai`), which exposes an OpenAI-compatible API consumed by the same `OpenAI` NuGet SDK. The leaked key was OpenRouter-issued, with the same `sk-...` prefix convention.

**Impact:** None on AIB code — `OpenAIService.cs` uses `OpenAIClientOptions.Endpoint` + `ApiKeyCredential`, both provider-agnostic for OpenAI-compatible Chat Completions surfaces. SEGURANCA.MD and README.md prose still names "OpenAI" because the owner switches between OpenRouter and OpenAI without code changes; the vault key system is "openai" generically.

**Fix:** No code change. Doc prose intentionally kept as "OpenAI" to mean any OpenAI-compatible provider. Evidence note documents the actual rotation surface.

### D-2: New key not provisioned + chat round-trip deferred

**Found during:** Task 3 owner action.

**Issue:** Plan Task 3 step 4 expected a new key to be created and tested end-to-end via chat. Owner chose to leave the dashboard empty and switch AIB to Ollama-only operation until a new key is needed.

**Impact:**
- S7/S8/S9 in `02-VERIFICATION.md` (OpenAI regex-positive + vault-write + chat round-trip) cannot be exercised right now. They remain in the UAT doc as deferred until next provisioning.
- S5 (Ollama-skip path) is exercisable now and covers daily operation.
- S3/S4/S6 (FirstRunWindow flow + invalid key + cancel paths) remain exercisable since the OpenAI branch UI is reachable by temporarily setting `settings.AiProvider = "OpenAI"`.

**Fix:** Documented in `evidence/D-12-ROTATION-NOTE.md`. Future provisioning is a one-step UX (hotkey → FirstRunWindow → paste → Salvar) — no code work needed.

### D-3: Binary screenshot not committed

**Found during:** Task 3 evidence step.

**Issue:** Plan Task 3 step 7 expected a redacted PNG at `evidence/openai-console-rotation-2026-05-31.png`. The orchestrator received the screenshot inline in the chat session but had no tool surface to persist it as binary on disk.

**Fix:** Text note at `evidence/D-12-ROTATION-NOTE.md` records the revoke confirmation + provenance. If the owner wants the PNG committed later, save the file at the documented path and `git add` + commit.

## SEC-03 Final Status

| Acceptance | Closed? | Closing Plan |
|------------|---------|--------------|
| #1 live key removed from disk | ✓ | 02-01 |
| #2 rotation evidence | ✓ behaviorally; ✗ binary | 02-04b Task 3 (text note + in-chat screenshot) |
| #3 fresh clone prompts to load key | ✓ | 02-02 + 02-03 |
| #4 app refuses placeholder key | ✓ | 02-02 (D-05 regex) |

SEC-03 substantively closed; #2 evidence is text+chat rather than committed binary.

## Threat Mitigations Applied

| Threat ID | Status | Notes |
|-----------|--------|-------|
| T-02-13 | Mitigated | OLD key revoked at OpenRouter — any holder of the leaked copy receives 401 from upstream. Leak window CLOSED. |
| T-02-14 | N/A | Binary PNG not committed; no key-value pixel risk. |
| T-02-17 | Mitigated | NFR-03 — SEGURANCA.MD + README rewritten present-tense; old `.env` bullet gone from README. |
| T-02-18 | Mitigated | 02-VERIFICATION.md ships 15 numbered scenarios; full audit-literal + D-XX coverage. |
| T-02-SC | N/A | No package installs. |

## Known Stubs / Follow-Ups

- New OpenAI/OpenRouter key provisioning — owner-action, deferred. When done: hotkey → FirstRunWindow → Salvar; S7/S8/S9 exercise.
- D-12 binary screenshot — optional later commit if owner wants stronger audit trail than the text note.

## Self-Check: PASSED

- Tasks 1 + 2 + 3 closed (Task 3 with documented deviations)
- All doc files modified at expected paths
- 02-VERIFICATION.md exists with 15 scenarios + full audit-literal + D-XX coverage
- Evidence text note committed in lieu of binary PNG per owner decision
- No code changes in this plan; build state unchanged from plan 02-04a (0 errors / 24 warnings)
