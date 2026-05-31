---
phase: 02-key-rotation-env-hardening
plan: "04a"
subsystem: settings-ui-polish
tags: [security, wpf, ui, settings-ui, sentinel-display, bug-fix]
dependency_graph:
  requires: [02-01, 02-02, 02-03]
  provides: [keytextbox-read-only-label, alterar-chave-button, sentinel-corruption-closed]
  affects: [AIBWindows/Views/SettingsWindow.xaml, AIBWindows/Views/SettingsWindow.xaml.cs]
tech_stack:
  added: []
  patterns:
    - read-only-textbox-display
    - sub-dialog-no-shutdown
    - sentinel-friendly-label-mapping
key_files:
  created: []
  modified:
    - AIBWindows/Views/SettingsWindow.xaml
    - AIBWindows/Views/SettingsWindow.xaml.cs
  deleted: []
decisions:
  - "UI-SPEC Secondary surface: KeyTextBox is IsReadOnly + friendly label per sentinel value"
  - "T-02-15 closed: Save_Click no longer writes KeyTextBox.Text back to settings.ApiKey"
  - "T-02-16 closed: AlterarChave_Click does NOT shut down on Cancel (different lifecycle from first-launch hotkey path)"
metrics:
  duration: "~4 minutes (inline)"
  completed: "2026-05-31"
  tasks_completed: 1
  files_changed: 2
---

# Phase 2 Plan 04a: SettingsWindow Polish Summary

**One-liner:** SettingsWindow KeyTextBox is now a friendly read-only label that maps sentinel values to human-readable strings; new Alterar chave button reopens FirstRunWindow without shutting down on Cancel; the sentinel-corruption bug (KeyTextBox.Text writing back to settings.ApiKey on Save) is closed.

## Tasks Completed

| Task | Name | Commit | Key Files |
|------|------|--------|-----------|
| 1 | SettingsWindow KeyTextBox polish — read-only label + Alterar chave button opening FirstRunWindow | feat(02-04a): SettingsWindow KeyTextBox polish + Alterar chave button | AIBWindows/Views/SettingsWindow.xaml + .xaml.cs |

## Files Modified

### `AIBWindows/Views/SettingsWindow.xaml` (+15 lines)
KeyTextBox (line 113) wrapped in 2-column Grid:
- Col 0 (`Width="*"`): `<TextBox x:Name="KeyTextBox" IsReadOnly="True" Background="#2A2A30" Foreground="#888888"/>` (matches MaxHistoryTextBox read-only treatment at line 161).
- Col 1 (`Width="Auto"`): new `<Button x:Name="AlterarChaveButton" Content="Alterar chave" Width="110" Height="35" Margin="5,12,0,0" Click="AlterarChave_Click">` with inline ControlTemplate (Border background `InputBg`, BorderBrush `#2A2A30`, CornerRadius 4 — matches BrowseButton style intent).

### `AIBWindows/Views/SettingsWindow.xaml.cs` (+44/-3 lines)
- `LoadUiValues` (line 36–61): removed direct `KeyTextBox.Text = _currentSettings.ApiKey;` assignment; added `RefreshKeyTextBoxLabel()` call after `UpdateUiForProvider()`.
- `RefreshKeyTextBoxLabel()` — new private method (lines 64–87). Four-case mapping per UI-SPEC Secondary surface:
  - `use-vault` → `"Configurada (cofre DPAPI)"`
  - `ollama` → `"(não necessário para Ollama)"`
  - `sk-...` → `"Configurada (legado)"` (defensive — should not appear after first boot post-D-11)
  - else → `"(não configurada)"`
- `AlterarChave_Click(object sender, RoutedEventArgs e)` — new handler (lines 89–100). Instantiates `new FirstRunWindow()`, calls `ShowDialog()`. On `DialogResult == true`: reloads `_currentSettings` via `_settingsService.LoadSettings()` and calls `RefreshKeyTextBoxLabel()`. On any other value (Cancel/null): no-op. Comment explicitly notes the Settings path is NOT the first-launch hotkey path so the app does NOT shut down on Cancel (T-02-16 mitigation).
- `Save_Click` (line 130 onwards): DELETED the line `_currentSettings.ApiKey = KeyTextBox.Text;`. Replaced with a 2-line comment documenting that KeyTextBox is display-only and FirstRunWindow now owns settings.ApiKey writes. T-02-15 mitigation.

## Build Status

- `dotnet build AIBWindows/AIB.csproj` after edits → **0 errors, 24 warnings** (Phase 1 baseline).
- One round-trip rebuild after comment tweak (replaced literal `Application.Current.Shutdown()` in comment with the same string spelled in prose to satisfy the plan's "0 matches" acceptance criterion).

## Verification Checklist (Plan Acceptance Criteria)

- [x] `Grep "Alterar chave" SettingsWindow.xaml` → ≥1 match (Edit A — XAML Content + handler binding)
- [x] `Grep "IsReadOnly" SettingsWindow.xaml` → ≥1 match (KeyTextBox + existing read-only siblings)
- [x] `Grep "AlterarChave_Click" SettingsWindow.xaml` → 1 match (event binding)
- [x] `Grep "private void AlterarChave_Click" SettingsWindow.xaml.cs` → 1 match
- [x] `Grep "new FirstRunWindow" SettingsWindow.xaml.cs` → 1 match
- [x] `Grep "Configurada" SettingsWindow.xaml.cs` → ≥1 match (two labels: cofre DPAPI + legado)
- [x] `Grep "não necessário para Ollama" SettingsWindow.xaml.cs` → 1 match
- [x] `Grep "RefreshKeyTextBoxLabel" SettingsWindow.xaml.cs` → ≥2 matches (declaration + call sites in LoadUiValues + AlterarChave_Click)
- [x] `Grep "_currentSettings.ApiKey = KeyTextBox.Text" SettingsWindow.xaml.cs` → **0 matches** (corrupting line removed — T-02-15 mitigation)
- [x] `Grep "Application.Current.Shutdown" SettingsWindow.xaml.cs` → **0 matches** (T-02-16 mitigation — no shutdown in Settings path)
- [x] `dotnet build` exits 0

## Sentinel-Corruption Bug Closed

Before this plan:
```csharp
// SettingsWindow.Save_Click (BEFORE)
_currentSettings.ApiKey = KeyTextBox.Text;  // could write "Configurada (cofre DPAPI)" into settings.ApiKey
```
Without the polish, KeyTextBox.Text could carry the literal sentinel string `use-vault` (if loaded post-migration) OR the friendly label after this polish, both of which would corrupt the real sentinel. Now: KeyTextBox is display-only; FirstRunWindow.SaveOpenAiBranch + App.OnStartup migration own the only two writes to `settings.ApiKey`.

## Threat Mitigations Applied

| Threat ID | Status | Notes |
|-----------|--------|-------|
| T-02-15 | Mitigated | Save_Click DELETED the corrupting `_currentSettings.ApiKey = KeyTextBox.Text;` line. Grep confirms 0 matches in post-edit file. |
| T-02-16 | Mitigated | AlterarChave_Click does not call `Application.Current.Shutdown()` (the Cancel/null branch is a no-op). Grep confirms 0 matches for the literal string in SettingsWindow.xaml.cs. |
| T-02-SC | N/A | No package installs. |

## Deviations from Plan

### Comment Rewording for Literal-Match Acceptance

**Found during:** Acceptance check.

**Issue:** Initial code-behind had a comment containing the literal `Application.Current.Shutdown()` to document the intentional absence; this tripped the strict 0-match acceptance criterion.

**Fix:** Reworded the comment to use the prose phrase "shut down the app" instead of the literal call syntax. Behavioral contract unchanged.

## Known Stubs

None. Plan ships cleanly.

## SEC-03 Progress

- SEC-03 acceptance #1 (UX polish for vault-backed key) — closed.
- SEC-03 acceptance #2 + #3 — closed by upstream plans (02-02 FirstRunWindow + 02-03 runtime wiring).
- D-12 rotation evidence still owner-action (plan 02-04b).

## Self-Check: PASSED

- Both files modified at expected paths: FOUND
- Build: 0 errors confirmed
- Acceptance Grep matches all confirmed
