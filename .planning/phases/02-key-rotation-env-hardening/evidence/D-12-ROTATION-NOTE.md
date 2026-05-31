---
phase: 02-key-rotation-env-hardening
artifact: D-12 rotation note (text substitute for binary PNG)
created: 2026-05-31
status: revoke-confirmed; new-key-not-provisioned
provider: OpenRouter (OpenAI-compatible)
---

# D-12 Rotation — Text Evidence

## Outcome

OLD service-account-style key (`sk-...`) was REVOKED on **2026-05-31** in the **OpenRouter** dashboard (Default Workspace → API Keys page). The API Keys list now reads "No API keys yet" — every key on the account was removed, not just rotated.

## Provider Deviation vs Plan

- Plan 02-04b Task 3 named "OpenAI console" (platform.openai.com). Actual provider was **OpenRouter** (`openrouter.ai`), which exposes an OpenAI-compatible Chat Completions surface consumed by the same `OpenAI` SDK in `OpenAIService.cs`. From the AIB code perspective the wiring is identical — both providers accept `ApiKeyCredential(apiKey)` against a `ChatClient(...)` constructed with `OpenAIClientOptions.Endpoint`.
- The leaked key was OpenRouter-issued, not OpenAI-issued. Revoke surface differs (OpenRouter has no separate "Service Accounts" section — all keys live in one flat list).

## New-Key Decision

Owner elected NOT to provision a new OpenRouter key at rotation time. AIB will operate in **Ollama-only mode** until a new key is provisioned via `FirstRunWindow` on a future hotkey press.

Consequences:
- S9 in `02-VERIFICATION.md` (OpenAI vault-read chat round-trip) — DEFERRED. Cannot be exercised until a new key exists.
- S7/S8 (D-05 regex + D-06 vault write) — DEFERRED for OpenAI branch; the wire-up itself is still verified by S3/S4/S6 cancel/invalid paths.
- The vault file `~/.AIB/credentials/openai.bin` is currently empty (deleted by the rotation flow). `NeedsFirstRun` returns `true` if the owner ever switches `settings.AiProvider` back to `"OpenAI"`.
- Daily operation runs `settings.AiProvider = "Ollama"` + `settings.ApiKey = "ollama"`; first hotkey opens ChatWindow directly per D-03 (verified by S5).

## Screenshot Provenance

A screenshot of the empty OpenRouter API Keys page (showing "No API keys yet") was provided in the orchestrating chat session on 2026-05-31. The binary PNG was NOT committed to `evidence/` because the orchestrator did not have a tool surface to persist inline conversation images as binary files on disk. This text note is the audit-trail substitute.

If the owner wants the binary committed later: save the PNG file at this path:
`C:\Users\Carlo\CPAPS\AIB\.planning\phases\02-key-rotation-env-hardening\evidence\openai-console-rotation-2026-05-31.png`
then `git add` + `git commit -m "docs(02-04b): D-12 rotation screenshot"`.

## SEC-03 Closure Assessment

| Acceptance | Status | Notes |
|------------|--------|-------|
| SEC-03.A1 — live key removed from disk | ✓ shipped | Plans 02-01 + 02-04b doc updates |
| SEC-03.A2 — rotation evidence | ✓ behavioral / ✗ binary | Revoke confirmed via in-chat screenshot; no committed PNG (owner choice) |
| SEC-03.A3 — fresh clone prompts | ✓ shipped | Plans 02-02 + 02-03 (NeedsFirstRun detector + FirstRunWindow) |
| SEC-03.A4 — placeholder rejected | ✓ shipped | Plan 02-02 regex (D-05) |

The leak window is now CLOSED: the OLD key returns 401 from OpenRouter regardless of where the leaked copy may still exist (disk/backup/USB/cloud-sync). The repository contains no live key in any tracked file (verified by S1 + S12 acceptance checks).
