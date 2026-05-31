---
phase: 02-key-rotation-env-hardening
plan: "03"
subsystem: runtime-wiring
tags: [security, openai, vault, hotkey, migration, settings-ui]
dependency_graph:
  requires: [02-01, 02-02]
  provides: [vault-runtime-wired, d11-migration, hotkey-first-run-branch, settings-openai-roundtrip]
  affects: [AIBWindows/Services/OpenAIService.cs, AIBWindows/App.xaml.cs, AIBWindows/Views/SettingsWindow.xaml, AIBWindows/Views/SettingsWindow.xaml.cs]
tech_stack:
  added: []
  patterns:
    - sentinel-string-d06
    - one-shot-boot-migration-d11
    - provider-aware-first-run-detector-d03
    - parent-owned-shutdown-pitfall6
key_files:
  created: []
  modified:
    - AIBWindows/Services/OpenAIService.cs
    - AIBWindows/App.xaml.cs
    - AIBWindows/Views/SettingsWindow.xaml
    - AIBWindows/Views/SettingsWindow.xaml.cs
  deleted: []
decisions:
  - "D-01: lazy first-run trigger on hotkey (tray-app pattern)"
  - "D-03: provider-aware NeedsFirstRun detector — skip FirstRunWindow when AiProvider == 'Ollama'"
  - "D-04: parent-owned Application.Current.Shutdown() on cancel path"
  - "D-06: use-vault sentinel branch at both OpenAIService call sites"
  - "D-08: ChatWindow hidden until FirstRunWindow.Save (DialogResult=true)"
  - "D-11: one-shot OnStartup migration overwrites settings.ApiKey to 'use-vault' (does NOT copy leaked key)"
  - "Pitfall 6: window only sets DialogResult; parent owns Application.Current.Shutdown() + firstrun_cancelled audit"
metrics:
  duration: "~10 minutes (inline orchestrator execution after subagent Bash deny)"
  completed: "2026-05-31"
  tasks_completed: 3
  files_changed: 4
---

# Phase 2 Plan 03: Runtime Wiring Summary

**One-liner:** Wired Phase 2 runtime: OpenAIService honors `use-vault` sentinel at both ChatClient construction sites; App.OnStartup runs one-shot D-11 migration forcing re-entry; App.OnHotkeyDetected branches on D-03 provider-aware detector to show FirstRunWindow before ChatWindow; SettingsWindow combo extended for OpenAI round-trip.

## Tasks Completed

| Task | Name | Commit | Key Files |
|------|------|--------|-----------|
| 1 | Insert use-vault sentinel at OpenAIService AskStatelessAsync + EnsureClient | feat(02-03): wire use-vault sentinel at both OpenAIService call sites | AIBWindows/Services/OpenAIService.cs |
| 2 | App D-11 migration + D-01/D-03/D-08 hotkey branch + parent-owned shutdown | feat(02-03): App D-11 migration + D-01/D-03/D-08 hotkey branch + parent-owned shutdown | AIBWindows/App.xaml.cs |
| 3 | Extend SettingsWindow provider combo for OpenAI | feat(02-03): extend SettingsWindow provider combo for OpenAI | AIBWindows/Views/SettingsWindow.xaml + .xaml.cs |

## Files Modified

### `AIBWindows/Services/OpenAIService.cs` (+22 lines)
Identical 11-line vault-sentinel block inserted at:
- `AskStatelessAsync` (line ~668): between Ollama branch and placeholder fallback.
- `EnsureClient` (line ~727): between Ollama branch and placeholder fallback (12-space indent, nested in `if (_client == null || _lastModel != modelName)`).

Both sites: `if (apiKey == "use-vault")` → `CredentialService.RetrieveCredential("openai", "ApiKey")` → fall back to `"placeholder"` on `ERRO`-prefix (auth failure surfaces to user via SDK error → user re-runs FirstRunWindow via hotkey).

### `AIBWindows/App.xaml.cs` (+59/-2 lines)
Three sub-edits:
- **Field added** (line 16): `private readonly SettingsService _settingsService = new();`
- **OnStartup refactor + D-11 migration** (lines 34–52): existing `var settings = new SettingsService().LoadSettings();` rewritten to use shared `_settingsService` instance. New idempotent migration block: guard `if (settings.ApiKey != "use-vault" && settings.ApiKey != "ollama")` → capture `bool hadKey`, set `settings.ApiKey = "use-vault"`, save, fire-and-forget audit `migration_clear_apikey` with `previous_key_present` (bool only — never the key value), loud console `[MIGRATION]` log.
- **OnHotkeyDetected replaced + 2 helpers** (lines 84–135):
  - `OnHotkeyDetected`: sets `e.Handled = true`; loads settings; calls `NeedsFirstRun(settings)`; if true, `ShowFirstRunWindow()` and early-return (D-08 sequencing); else `_chatWindow?.ToggleWindow()`.
  - `NeedsFirstRun(UserAppSettings s)` — `private static`: returns false for Ollama provider (D-03); else `CredentialService.RetrieveCredential("openai", "ApiKey").StartsWith("ERRO")`.
  - `ShowFirstRunWindow()` — `private`: `new FirstRunWindow().ShowDialog()`; on `DialogResult=true` toggles ChatWindow (D-08); on Cancel/null emits `firstrun_cancelled` audit and calls `Current.Shutdown()` (D-04 + Pitfall 6 — window itself does NOT call Shutdown or emit cancel audit; verified in plan 02-02).

### `AIBWindows/Views/SettingsWindow.xaml` (+1 line)
Inserted `<ComboBoxItem Content="OpenAI"/>` between existing Ollama and Google Gemini items in ProviderComboBox (line 104).

### `AIBWindows/Views/SettingsWindow.xaml.cs` (+10 lines)
Added `else if (selected == "OpenAI")` branch in `UpdateUiForProvider` before the final fallback. Keeps `AdvancedConnectionPanel.Visibility = Visibility.Visible`; clears localhost Ollama default URL (`http://localhost:11434/v1` OR `http://127.0.0.1:11434/v1`) to empty string so OpenAI SDK uses default endpoint `https://api.openai.com/v1` (RESEARCH Q3). Does NOT touch `KeyTextBox` (deferred to plan 02-04a polish slice).

## Use-Vault Sentinel Audit (cross-file count)

| File | Occurrences | Role |
|------|-------------|------|
| `AIBWindows/Services/OpenAIService.cs` | 2 | read at AskStatelessAsync (~line 668) + EnsureClient (~line 727) |
| `AIBWindows/App.xaml.cs` | 3 | comment (line 38) + idempotency guard (line 40) + migration write (line 43) |
| `AIBWindows/Views/FirstRunWindow.xaml.cs` | 1 | write on OpenAI Save (line 232, from plan 02-02) |
| **Total** | **6** | architecture map: 2 read + 2 write + 2 guard/comment |

## Build Status

- After Task 1: `dotnet build AIBWindows/AIB.csproj` → **0 errors, 24 warnings** (Phase 1 baseline).
- After Task 2: `dotnet build` → **0 errors, 24 warnings**.
- After Task 3: `dotnet build` → **0 errors, 24 warnings**.

No new warnings introduced; no regression vs Phase 1 baseline.

## Audit Outcomes Wired

| Outcome | File | Trigger | Payload | Mitigation |
|---------|------|---------|---------|------------|
| `migration_clear_apikey` | App.xaml.cs | OnStartup D-11 migration runs | `{ ts, outcome, previous_key_present: bool }` — NEVER the key value | T-02-99 (no key in audit) |
| `firstrun_cancelled` | App.xaml.cs | FirstRunWindow returns DialogResult=false or null | `{ ts, outcome }` | D-04 + Pitfall 6 — single audit emission point |

## Verification Checklist

- [x] `Grep "use-vault" AIBWindows/Services/OpenAIService.cs` → 2 matches
- [x] `Grep "use-vault" AIBWindows/App.xaml.cs` → 3 matches (1 write + 1 guard + 1 comment)
- [x] `Grep "use-vault" AIBWindows/Views/FirstRunWindow.xaml.cs` → 1 match (plan 02-02 write)
- [x] `Grep migration_clear_apikey AIBWindows/App.xaml.cs` → 1 match
- [x] `Grep firstrun_cancelled AIBWindows/App.xaml.cs` → 1 match
- [x] `Grep "\[MIGRATION\] settings.ApiKey replaced" AIBWindows/App.xaml.cs` → 1 match
- [x] `Grep NeedsFirstRun AIBWindows/App.xaml.cs` → 2 matches (declaration + call site)
- [x] `Grep ShowFirstRunWindow AIBWindows/App.xaml.cs` → 2 matches (declaration + call site)
- [x] `Grep "new FirstRunWindow\(\)" AIBWindows/App.xaml.cs` → 1 match
- [x] `Grep '<ComboBoxItem Content="OpenAI"/>' AIBWindows/Views/SettingsWindow.xaml` → 1 match
- [x] `Grep 'else if (selected == "OpenAI")' AIBWindows/Views/SettingsWindow.xaml.cs` → 1 match
- [x] Build green (0 errors, 24 baseline warnings) after every commit

## SEC-03 Progress

This plan addresses:
- SEC-03 acceptance #2 "fresh clone → app prompts to load key" — OnStartup migration + NeedsFirstRun detector + hotkey branch deliver this end-to-end.
- SEC-03 acceptance #3 "app refuses to start with placeholder key" — `placeholder` falls through to OpenAI SDK auth error, which surfaces in chat; user re-runs FirstRunWindow via hotkey.

Pending downstream:
- Plan 02-04a: SettingsWindow KeyTextBox polish (read-only sentinel display + Alterar chave button).
- Plan 02-04b: SEGURANCA.md rewrite + README update + VERIFICATION scenarios + D-12 rotation evidence (owner-performed in OpenAI console).

## Threat Mitigations Applied

| Threat ID | Status | Notes |
|-----------|--------|-------|
| T-02-02 | Mitigated | D-11 migration writes `use-vault` UNCONDITIONALLY; code path does NOT call `CredentialService.StoreCredentialAsync` from OnStartup; leaked key forgotten. |
| T-02-03 | Mitigated | OnHotkeyDetected guards on `NeedsFirstRun`; early-return prevents `_chatWindow.ToggleWindow()` when vault is empty (D-08). |
| T-02-06 | Mitigated | ShowFirstRunWindow owns `Current.Shutdown()` on UI thread; FirstRunWindow itself has no Shutdown call (verified in plan 02-02 acceptance). |
| T-02-08 | Accepted | DPAPI decrypt <1ms per RESEARCH §Assumptions A7; cache out of scope. |
| T-02-09 | Mitigated | `[MIGRATION]` console log contains only the sentinel string, never the previous key value. |
| T-02-13 | Mitigated | ProviderComboBox extended with OpenAI item; UpdateUiForProvider OpenAI branch handles URL clear per Q3. |
| T-02-99 | Mitigated | Audit payload contains only `previous_key_present: bool`, never the key value or any prefix/suffix. |

## Deviations from Plan

### Orchestrator Inline Execution After Subagent Bash Deny

**Found during:** Wave 2 dispatch.

**Issue:** Spawned `gsd-executor` subagent for plan 02-03 halted before any file edits — Bash tool was denied for the worktree HEAD assertion (`git rev-parse --abbrev-ref HEAD`), the build verification (`dotnet build`), and the atomic commit operations. No worktree was created, so no partial state to recover.

**Fix:** Orchestrator executed all three tasks inline on the main working tree (no worktree isolation), per workflow `<runtime_compatibility>` fallback rule. Each task verified with `dotnet build` (0 errors / 24 baseline warnings) before atomic commit. SUMMARY written + committed inline.

**Files modified:** No deviation from plan's `files_modified` list.

## Known Stubs

None. All four files modified per plan acceptance criteria; build green.

## Self-Check: PASSED

- All 4 files modified at expected paths: FOUND
- Acceptance Grep matches all confirmed: FOUND
- Build: 0 errors confirmed at each commit
- Use-vault sentinel sources: 3 files (read + write + write), 6 total occurrences
