---
phase: 02-key-rotation-env-hardening
plan: "02"
subsystem: first-run-ux
tags: [security, wpf, ui, vault, credentials, ollama, key-rotation]
dependency_graph:
  requires: []
  provides: [first-run-window, key-vault-write-ux, ollama-picker-ux]
  affects: [AIBWindows/Views/]
tech_stack:
  added: []
  patterns:
    - wpf-dialogresult-lifecycle
    - dpapi-vault-write
    - regex-key-validation
    - ollama-models-fetch
    - audit-log-anonymous-object
key_files:
  created:
    - AIBWindows/Views/FirstRunWindow.xaml
    - AIBWindows/Views/FirstRunWindow.xaml.cs
  modified: []
  deleted: []
decisions:
  - "D-02: dedicated FirstRunWindow.xaml + .xaml.cs (separate from SettingsWindow)"
  - "D-04: Cancel = DialogResult=false + Close; parent owns Application.Current.Shutdown()"
  - "D-05: regex-only validation ^sk-[a-zA-Z0-9_-]{20,}$; no network"
  - "D-06: CredentialService.StoreCredentialAsync vault write; settings.ApiKey='use-vault' sentinel"
  - "D-07: ChatWindow theme tokens reused; centered 480x520; provider radio first"
  - "D-09: Ollama branch uses GetOllamaModelsAsync (/api/tags); ShadowModelName mirrors ModelName"
  - "Pitfall 6: firstrun_cancelled audit + Application.Current.Shutdown() are App.xaml.cs concerns (plan 03), NOT window"
metrics:
  duration: "~5 minutes (after orchestrator rescue from agent Bash-deny halt)"
  completed: "2026-05-31"
  tasks_completed: 2
  files_changed: 2
---

# Phase 2 Plan 02: FirstRunWindow Summary

**One-liner:** New WPF dialog (XAML + code-behind) for first-launch provider selection. OpenAI branch persists key to DPAPI vault via CredentialService with `use-vault` sentinel in profile; Ollama branch picks model from `/api/tags` with offline fallback; Cancel returns `DialogResult=false` only (parent owns shutdown per Pitfall 6).

## Tasks Completed

| Task | Name | Commit | Key Files |
|------|------|--------|-----------|
| 1 | Create FirstRunWindow.xaml — WPF dialog layout matching UI-SPEC | (committed by orchestrator after agent quota/permission halt) | AIBWindows/Views/FirstRunWindow.xaml |
| 2 | Create FirstRunWindow.xaml.cs — code-behind with vault write, regex validation, Ollama fetch, DialogResult lifecycle, audit lines | (committed by orchestrator after agent quota/permission halt) | AIBWindows/Views/FirstRunWindow.xaml.cs |

## Files Created

- `AIBWindows/Views/FirstRunWindow.xaml` — 223 lines. Centered 480×520 window. Header + provider radio group + Ollama branch (model ComboBox + LoadingProgress + OllamaErrorBlock with retry + fallback link + FallbackModelDisplay) + OpenAI branch (KeyTextBox + inline ErrorLabel) + Cancel/Save buttons.
- `AIBWindows/Views/FirstRunWindow.xaml.cs` — 285 lines. Code-behind per UI-SPEC §States + decision matrix.

## Behavioral Contract

### Save → OpenAI branch
1. Regex validate `^sk-[a-zA-Z0-9_-]{20,}$` per D-05.
2. On fail: show `ErrorLabel`; disable SaveButton; emit `firstrun_invalid_key` audit (provider=OpenAI).
3. On pass: `CredentialService.StoreCredentialAsync("openai", "ApiKey", key)` (DPAPI scope=CurrentUser).
4. `settings.AiProvider = "OpenAI"`; `settings.ApiKey = "use-vault"` sentinel; `settings.ApiUrl = ""` (SDK default).
5. Emit `firstrun_saved` audit with `key_last4` only — never the full key (T-02-07 mitigation).
6. `DialogResult = true`; `Close()`.

### Save → Ollama branch
1. Pick model from `ModelComboBox` or apply fallback (`qwen2.5:7b`).
2. `settings.AiProvider = "Ollama"`; `settings.ApiKey = "ollama"`; `settings.ModelName = selectedModel`; `settings.ShadowModelName = selectedModel` (D-09 mirror).
3. Emit `firstrun_saved` audit with `model` field.
4. `DialogResult = true`; `Close()`.

### Cancel
- `DialogResult = false`; `Close()` only.
- Parent (`App.xaml.cs` `ShowFirstRunWindow`, plan 03) inspects `DialogResult`, emits `firstrun_cancelled` audit, calls `Application.Current.Shutdown()`.
- This window does NOT emit `firstrun_cancelled` and does NOT call `Application.Current.Shutdown()` (Pitfall 6 — prevents double-fire + tray-icon ghost race).

### Ollama model fetch
- Queued on UI thread via `Dispatcher.BeginInvoke` so constructor returns immediately.
- `GetOllamaModelsAsync` returns empty list on any error → `OllamaErrorBlock.Visibility = Visible` (UI-SPEC S3); no throw, no hang.
- Retry button hides error + re-fetches.
- Fallback link applies `qwen2.5:7b` read-only display + enables SaveButton (UI-SPEC S5).

### SaveButton state machine
- Starts disabled.
- Ollama: enabled when `ModelComboBox.SelectedItem != null` OR fallback applied.
- OpenAI: enabled when regex validation passes.

## Build Status

- `dotnet build AIBWindows/AIB.csproj` — **0 errors, 24 warnings** (Phase 1 baseline; no new warnings introduced).
- One compile fix during commit: `KeyEventArgs` disambiguated to `System.Windows.Input.KeyEventArgs` (line 135) to avoid `System.Windows.Forms.KeyEventArgs` collision (the project references both WPF and WinForms via WPF-default + design-time).

## Verification Checklist

- [x] `AIBWindows/Views/FirstRunWindow.xaml` exists at expected path
- [x] `AIBWindows/Views/FirstRunWindow.xaml.cs` exists at expected path
- [x] Build: 0 errors confirmed (`dotnet build AIBWindows/AIB.csproj`)
- [x] No new warnings vs Phase 1 baseline (24 warnings = baseline)
- [x] `Application.Current.Shutdown` does NOT appear in FirstRunWindow.xaml.cs (Pitfall 6)
- [x] `firstrun_cancelled` string does NOT appear in FirstRunWindow.xaml.cs (Pitfall 6)
- [x] Regex `^sk-[a-zA-Z0-9_-]{20,}$` literal present (D-05)
- [x] `CredentialService.StoreCredentialAsync("openai", "ApiKey", key)` call present (D-06)
- [x] `settings.ApiKey = "use-vault"` sentinel assignment present (D-06)
- [x] `GetOllamaModelsAsync` consumed for model fetch (D-09)
- [x] `ShadowModelName` mirrors `ModelName` on Ollama save (D-09)
- [x] Audit lines use `key_last4` only — full key literal `sk-svcacct` absent (T-02-07, T-02-99)

## Deviations from Plan

### Orchestrator Rescue After Agent Halt

**Found during:** Agent execution

**Issue:** Plan was attempted twice by subagent.
- First attempt (`a14ef68fd2f0ad2ba`... wait — that was 02-01): the 02-02 first attempt (`a6c79f777cd344d30`) hit provider quota mid-flight with zero commits; worktree discarded.
- Second attempt (`ad8a496f3a6b92b8a`): worktree was incorrectly based on `68d7c5b` (an older shadow-assistant branch ancestor) instead of `6323d3c` (post-02-01 merge); agent could not run `git reset --hard` or `dotnet build` or `git commit` due to Bash permission denials, halted with help request.

**Fix:** Orchestrator inline rescue:
1. `git reset --hard 6323d3c` in worktree (carried untracked XAML/cs forward).
2. `dotnet build` → revealed `KeyEventArgs` ambiguity → orchestrator inline-fixed via fully-qualified `System.Windows.Input.KeyEventArgs`.
3. Re-build → 0 errors / 24 warnings (baseline parity).
4. Atomic commits per plan task split.
5. SUMMARY.md written + committed.

**Files modified:** Source files unchanged from agent output except 1-line `KeyEventArgs` qualification.

## SEC-03 Progress

This plan addresses:
- SEC-03 acceptance #1 "User can save key on first launch" — vault write path implemented.
- SEC-03 acceptance #4 "Cancel does not leak window state" — DialogResult contract owns lifecycle.

Pending downstream (plan 02-03): wiring `App.OnHotkeyDetected` → `ShowFirstRunWindow` + parent-owned shutdown + `OpenAIService` vault consumption.

## Threat Mitigations Applied

| Threat ID | Status | Notes |
|-----------|--------|-------|
| T-02-04 | Mitigated | Regex `^sk-[a-zA-Z0-9_-]{20,}$` per D-05; SaveButton disabled until valid; `firstrun_invalid_key` audit on bad input. |
| T-02-06 | Mitigated | Cancel = DialogResult=false + Close only; `Application.Current.Shutdown()` absent from window; `firstrun_cancelled` audit absent (parent owns both per Pitfall 6). |
| T-02-07 | Mitigated | Audit schema uses `key_last4` only; no full-key literal anywhere in file. |
| T-02-08 | Mitigated | Delegates to existing `CredentialService.StoreCredentialAsync` (DPAPI `ProtectedData.Protect`, `DataProtectionScope.CurrentUser`); no new crypto. |
| T-02-10 | Mitigated | SaveButton starts disabled; state machine enables only after valid input. |
| T-02-11 | Mitigated | `GetOllamaModelsAsync` catches all and returns empty → UI shows `OllamaErrorBlock` + retry + fallback; no throw, no hang. |
| T-02-99 | Mitigated | No literal beginning with `sk-svcacct` in file; key never echoed to chat. |

## Known Stubs

- `App.xaml.cs ShowFirstRunWindow` — referenced in Cancel handler comment as the parent that owns shutdown + `firstrun_cancelled` audit. Implemented in plan 02-03.
- SettingsWindow provider combo entry for `OpenAI` — referenced in RESEARCH Q1 resolution. Implemented in plan 02-03.

## Self-Check: PASSED

- `AIBWindows/Views/FirstRunWindow.xaml` exists at expected path: FOUND
- `AIBWindows/Views/FirstRunWindow.xaml.cs` exists at expected path: FOUND
- Commits for Task 1 + Task 2 + SUMMARY in worktree: FOUND
- Build: 0 errors confirmed
