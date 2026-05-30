# Phase 2: Key rotation + `.env` hardening - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-05-30
**Phase:** 02-key-rotation-env-hardening
**Areas discussed:** First-run UX (rounds 1+2), `.env` file role, key rotation, settings migration

---

## Gray area selection

Presented four phase-specific gray areas. User selected one initially and
expanded scope twice during the round.

| Area | Description | Selected initially |
|---|---|---|
| Windows key resolution precedence | Vault vs settings.ApiKey ordering | |
| Linux key path — keyring vs lite-client manual | python-keyring vs deferred | |
| First-run UX when no key found | SettingsWindow vs FirstRunWindow vs banner vs Ollama fallback | ✓ |
| `.env` file role going forward | Delete vs DEBUG-only vs partial strip | |

Linux scope later resolved as "keep as it is for now" → deferred.
Windows precedence later resolved implicitly via D-06 (Save target).
`.env` role + rotation discussed in extension rounds.

---

## First-run UX

### Q1 — Trigger condition

| Option | Description | Selected |
|--------|-------------|----------|
| Open SettingsWindow modally on startup (Recommended) | Reuses existing SettingsWindow + KeyTextBox at SettingsWindow.xaml.cs:40,124 | |
| Show banner in ChatWindow; AI disabled until key set | Non-blocking; banner-with-action pattern | |
| Refuse to start — error dialog pointing to README | Hard failure; cleanest "no AI without key" | |
| Auto-fallback to local Ollama | Silent Ollama fallback | |
| Other (free text) | "Open SettingsWindow modally on startup but only when we input the shortcut key to display the program" | ✓ |

**User's choice:** Custom — trigger LAZILY on hotkey, not on app launch.
App stays silent in tray; detector runs when hotkey is pressed.
**Notes:** This shapes D-01. Avoids a popup on every boot for an app
that lives in the tray most of the time.

### Q2 — Key entry surface

| Option | Description | Selected |
|--------|-------------|----------|
| Open existing Settings to Conexão tab, focus KeyTextBox, yellow banner above (Recommended) | Minimal new code; reuses SettingsWindow.xaml.cs:40 | |
| Dedicated FirstRunWindow (separate XAML, single-purpose key input) | Cleaner separation; extra file | ✓ |
| Inline overlay inside ChatWindow (no SettingsWindow) | Adds state machine to god-file ChatWindow.xaml.cs | |

**User's choice:** Dedicated `FirstRunWindow`. Diverges from recommended
minimal-code path — accepted because separation of concerns matters
more than file count here.
**Notes:** Locks D-02.

### Q3 — Detector logic

| Option | Description | Selected |
|--------|-------------|----------|
| Vault empty AND AiProvider ≠ Ollama (provider-aware) | Skip prompt entirely for Ollama users | ✓ |
| Vault empty AND (ApiKey null OR 'sk-PLACEHOLDER' OR 'placeholder') (Recommended) | Strict, provider-agnostic | |
| Vault empty AND no ApiKey present (no placeholder pattern) | Loosest; relies on 401 errors | |

**User's choice:** Provider-aware. Ollama users never see FirstRunWindow.
**Notes:** Locks D-03.

### Q4 — Cancel path

| Option | Description | Selected |
|--------|-------------|----------|
| ChatWindow opens; AI calls return friendly error until key set (Recommended) | Non-destructive; FirstRunWindow re-triggers next hotkey | |
| Auto-switch AiProvider to 'Ollama', open ChatWindow | Assumes cancel means "no cloud" | |
| App quits cleanly; tray icon stays for next hotkey invocation | Forces decision; audit-log `firstrun_cancelled` | ✓ |

**User's choice:** Quit cleanly on cancel.
**Notes:** Locks D-04. Stricter than recommended — matches the spirit of
SEC-03 acceptance "app refuses to start with placeholder key".

### Q5 — Save validation

| Option | Description | Selected |
|--------|-------------|----------|
| Regex pattern only `^sk-[a-zA-Z0-9_-]{20,}$` — no network (Recommended) | Fast, offline; rejects placeholders | ✓ |
| Regex + live API ping (models.list before save) | Catches revoked keys; ~1 round-trip + spinner | |
| No validation — trust input, fail at first chat send | Simplest; awkward recovery | |

**User's choice:** Regex only.
**Notes:** Locks D-05. Live API ping deferred (see Deferred Ideas).

### Q6 — Save target

| Option | Description | Selected |
|--------|-------------|----------|
| CredentialService vault ONLY; settings.ApiKey = 'use-vault' sentinel (Recommended) | Single source of truth; OpenAIService rewrites for sentinel | ✓ |
| settings.ApiKey ONLY (skip CredentialService for OPENAI) | Minimal change; leaves CredentialService unused for most-sensitive key | |
| BOTH (vault primary, settings.ApiKey mirror) | Belt-and-suspenders; sync bugs possible | |

**User's choice:** Vault only with sentinel.
**Notes:** Locks D-06. Also implicitly resolves the "Windows key
resolution precedence" gray area from the original selection round.

### Q7 — Visual style

| Option | Description | Selected |
|--------|-------------|----------|
| Match CommandConfirmationWindow (dark, AIB-branded) (Recommended) | Visual consistency with Phase-1 modal | |
| Match SettingsWindow (light/system theme, plain controls) | Consistent with where the key normally lives | |
| Plain unstyled WPF window | Fastest; ugly | |
| Other (free text) | "Match the chat window's design and theme, centered on screen, welcome title and message, user has to first select the provider (ollama/other), if ollama, skip apikey necessity." | ✓ |

**User's choice:** Custom — match ChatWindow theme, centered, welcome
title/message, provider picker FIRST, Ollama branch skips key input.
**Notes:** Reshapes the entire FirstRunWindow into a provider-picker
wizard. Locks D-07 and introduces D-09 (Ollama model picker).

### Q8 — Lifecycle

| Option | Description | Selected |
|--------|-------------|----------|
| Hotkey → FirstRunWindow opens, ChatWindow stays hidden → on Save, FirstRunWindow closes and ChatWindow opens (Recommended) | Strict sequencing; no broken-UI flash | ✓ |
| Hotkey → ChatWindow + FirstRunWindow on top | ChatWindow visible behind modal; risk of premature warmup | |
| App launch → FirstRunWindow once, then tray | Contradicts D-01 | |

**User's choice:** Strict sequential.
**Notes:** Locks D-08.

### Q9 — Ollama model picker

| Option | Description | Selected |
|--------|-------------|----------|
| Fetch /api/tags live (uses SettingsService.GetOllamaModelsAsync); dropdown of installed models; ShadowModelName mirrors ModelName (Recommended) | Reuses existing helper; graceful empty-list fallback | ✓ |
| Free-text input for model name | No network; user can typo | |
| Hardcoded curated list with 'Other' free-text | Curated; drifts | |
| Skip model picker; default to qwen2.5:7b | Fastest path | |

**User's choice:** Live `/api/tags` fetch with dropdown.
**Notes:** Locks D-09. Reuses `SettingsService.cs:102`.

---

## `.env` file role

### Q10 — `.env` going forward (Windows)

| Option | Description | Selected |
|--------|-------------|----------|
| Delete .env from disk + remove DotNetEnv from AIB.csproj + ship .env.example with placeholders (Recommended) | Removes dead code + live secret; smallest surface | ✓ |
| Keep .env reading as DEBUG-only fallback | Useful for dev override; adds call site that doesn't exist today | |
| Delete .env but KEEP DotNetEnv package | Cosmetic difference vs option 1 | |

**User's choice:** Full deletion + package removal.
**Notes:** Locks D-10. Scout confirmed `DotNetEnv` has zero call sites
in `AIBWindows/`, so removal is a no-op for runtime behavior.

---

## Rotation evidence

### Q11 — Evidence captured in VERIFICATION.md

| Option | Description | Selected |
|--------|-------------|----------|
| Screenshot of OpenAI console (old key revoked + new key last4 + date) saved under `.planning/phases/02-key-rotation-env-hardening/evidence/` (Recommended) | Unambiguous artifact; redact full new key | ✓ |
| Console line: copy dashboard text into VERIFICATION.md as quoted text | Text-only; grep-able | |
| Both: screenshot + text quote | Belt-and-suspenders | |
| Just a 'rotated on YYYY-MM-DD' line | Lowest friction; lowest auditability | |

**User's choice:** Screenshot under `evidence/`.
**Notes:** Locks D-12. Owner does this manually after PR merge.

---

## Migration of existing installs

### Q12 — Existing settings.ApiKey containing real key

| Option | Description | Selected |
|--------|-------------|----------|
| Auto-migrate: if matches `^sk-…`, copy to vault, replace settings.ApiKey with 'use-vault' (Recommended) | Zero friction; silent migration | |
| Force re-entry: ignore existing settings.ApiKey, trigger FirstRunWindow on next hotkey | Forces user through new flow; annoying if key not at hand | ✓ |
| Prompt: 'Move existing key to vault?' Yes/No | Most explicit; extra dialog | |
| Defer to researcher/planner | Skip decision | |

**User's choice:** Force re-entry; do NOT auto-migrate.
**Notes:** Locks D-11. Old `settings.ApiKey` value is overwritten to
`"use-vault"` on first boot post-deploy; user re-pastes the rotated
key on next hotkey. The pre-rotation key value is never copied
forward.

---

## Claude's Discretion

- FirstRunWindow XAML layout details (margins, spacing, control sizing)
  within D-07's "match ChatWindow theme" constraint.
- Welcome-message copy (draft in CONTEXT.md `<specifics>`; Claude can
  polish).
- Audit-log entry schemas for the new outcomes (`firstrun_cancelled`,
  `firstrun_saved`, `migration_clear_apikey`) — follow Phase 1 D6
  JSONL pattern.
- Polishing `SettingsWindow.KeyTextBox` into a read-only "Configurada
  via cofre" label + "Alterar chave" button — listed in CONTEXT.md
  Integration Points; ship in this phase if time permits or follow-up.

## Deferred Ideas

- AIBLinux key path migration → AIBLinux feature-parity milestone.
- Live API validation of key on Save → optional follow-up phase.
- Settings UI rewrite around the now-sentinel `ApiKey` field → small
  follow-up if time-pressed.
- Audit-log retention/rotation → already deferred in Phase 1.
- `/unlock_level` chat backdoor → accepted risk per PROJECT.md.
