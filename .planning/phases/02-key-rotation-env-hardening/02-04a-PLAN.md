---
phase: 02-key-rotation-env-hardening
plan: 04a
type: execute
wave: 3
depends_on:
  - 02-01
  - 02-02
  - 02-03
files_modified:
  - AIBWindows/Views/SettingsWindow.xaml
  - AIBWindows/Views/SettingsWindow.xaml.cs
autonomous: true
requirements:
  - SEC-03
must_haves:
  truths:
    - "SettingsWindow KeyTextBox is read-only and displays a friendly label per sentinel value (Configurada cofre DPAPI / nao necessario para Ollama / Configurada legado) (UI-SPEC Secondary surface KeyTextBox polish)"
    - "Alterar chave button on SettingsWindow opens FirstRunWindow as a dialog; on success settings reload and label refreshes; on Cancel SettingsWindow stays open (DOES NOT call Application.Current.Shutdown)"
    - "SettingsWindow Save_Click no longer writes KeyTextBox.Text back to settings.ApiKey — the sentinel-corruption bug is closed"
  artifacts:
    - path: "AIBWindows/Views/SettingsWindow.xaml"
      provides: "KeyTextBox is read-only with friendly label + new Alterar chave button per UI-SPEC Secondary surface"
      contains: "Alterar chave"
    - path: "AIBWindows/Views/SettingsWindow.xaml.cs"
      provides: "Code-behind reads sentinel from settings.ApiKey and displays friendly label; AlterarChave_Click opens new FirstRunWindow().ShowDialog()"
      contains: "Configurada"
  key_links:
    - from: "AIBWindows/Views/SettingsWindow.xaml.cs (AlterarChave_Click)"
      to: "AIBWindows/Views/FirstRunWindow.xaml.cs"
      via: "new FirstRunWindow().ShowDialog() then on success reload settings and refresh KeyTextBox label"
      pattern: "new FirstRunWindow"
---

<objective>
SettingsWindow polish slice of the original plan 04 (split per checker Issue 4 — original plan was over budget at 4 tasks + 6 files + 1 binary). This plan ships ONLY the SettingsWindow KeyTextBox polish + Alterar chave button. Docs + VERIFICATION.md + D-12 rotation checkpoint move to 02-04b.

Purpose: UI-SPEC Secondary surface mandates a read-only friendly label so users do not see the literal sentinel string `use-vault` in the existing TextBox (bad UX). The Alterar chave button is the user's path to re-key without restarting the app. Removing the corrupting Save_Click line that wrote KeyTextBox.Text back to settings.ApiKey closes a sentinel-corruption bug.

Output: 2 file edits in pre-existing files. Wave 3 — depends on plan 01 (build clean), plan 02 (FirstRunWindow exists for AlterarChave_Click to instantiate), plan 03 (sentinel runtime is wired so the read-only label makes sense).

This plan is autonomous=true — pure code edits with automated build verification.
</objective>

<execution_context>
@$HOME/.claude/get-shit-done/workflows/execute-plan.md
@$HOME/.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/STATE.md
@.planning/ROADMAP.md
@.planning/REQUIREMENTS.md
@.planning/phases/02-key-rotation-env-hardening/02-CONTEXT.md
@.planning/phases/02-key-rotation-env-hardening/02-RESEARCH.md
@.planning/phases/02-key-rotation-env-hardening/02-PATTERNS.md
@.planning/phases/02-key-rotation-env-hardening/02-UI-SPEC.md
@.planning/phases/02-key-rotation-env-hardening/02-01-PLAN.md
@.planning/phases/02-key-rotation-env-hardening/02-02-PLAN.md
@.planning/phases/02-key-rotation-env-hardening/02-03-PLAN.md
@AIBWindows/Views/SettingsWindow.xaml
@AIBWindows/Views/SettingsWindow.xaml.cs
</context>

<tasks>

<task type="auto" tdd="false">
  <name>Task 1: SettingsWindow KeyTextBox polish — read-only label + Alterar chave button opening FirstRunWindow</name>
  <files>AIBWindows/Views/SettingsWindow.xaml, AIBWindows/Views/SettingsWindow.xaml.cs</files>
  <read_first>
    - AIBWindows/Views/SettingsWindow.xaml (lines 100-160 — current KeyTextBox + ProviderComboBox context, BrowseButton style reference at 43-62, MaxHistoryTextBox read-only treatment at line 160)
    - AIBWindows/Views/SettingsWindow.xaml.cs (current LoadUiValues / Save_Click — confirm where KeyTextBox is read from/written to settings.ApiKey today)
    - AIBWindows/Views/FirstRunWindow.xaml.cs (instantiation contract for AlterarChave_Click — new FirstRunWindow().ShowDialog() pattern, returns bool? DialogResult)
    - .planning/phases/02-key-rotation-env-hardening/02-UI-SPEC.md Secondary surface SettingsWindow.KeyTextBox polish (lines 422-443 — concrete property values for IsReadOnly, Text, Foreground, Background, Button positioning)
  </read_first>
  <behavior>
    - KeyTextBox is IsReadOnly=True; user cannot edit the field directly.
    - When settings.ApiKey equals "use-vault": KeyTextBox.Text is "Configurada (cofre DPAPI)".
    - When settings.ApiKey equals "ollama": KeyTextBox.Text is "(não necessário para Ollama)".
    - When settings.ApiKey starts with "sk-" (legacy/pre-migration defensive case — should not occur after first boot post-Phase-2 thanks to D-11): KeyTextBox.Text is "Configurada (legado)".
    - KeyTextBox.Foreground is text.secondary (e.g. #888888 or #888899), Background is #2A2A30 (matches MaxHistoryTextBox read-only treatment).
    - A new Button labelled Alterar chave sits to the right of KeyTextBox in a 2-column Grid (column 0 is star, column 1 is Auto; KeyTextBox in column 0, Button in column 1).
    - Button Width=110, Height=35, Margin=5,12,0,0 styled the same as the existing BrowseButton resource (SettingsWindow.xaml:43-62).
    - Button click handler AlterarChave_Click: instantiates new FirstRunWindow() and calls ShowDialog(). On DialogResult==true: reloads _currentSettings via _settingsService.LoadSettings() and re-runs the KeyTextBox label-update logic to reflect the new sentinel value. On Cancel: do nothing (user stayed in SettingsWindow; DialogResult=false is a no-op here — DO NOT call Application.Current.Shutdown() from this code path, because the user explicitly invoked Alterar chave from Settings, not from the first-launch hotkey path, and shutting down would surprise them).
    - SettingsWindow's existing Save_Click does NOT write KeyTextBox.Text back to settings.ApiKey (KeyTextBox is now read-only — its value is just a friendly label, not a settable value). Remove or guard the line in Save_Click that currently does _currentSettings.ApiKey = KeyTextBox.Text; so the sentinel is preserved across SettingsWindow saves.
  </behavior>
  <action>
    Two edits to SettingsWindow.

    Edit A — AIBWindows/Views/SettingsWindow.xaml: Locate the current KeyTextBox element (around lines 111-112). Wrap it in a 2-column Grid. Add the new Button x:Name=AlterarChaveButton Content=Alterar chave Width=110 Height=35 Margin=5,12,0,0 Grid.Column=1 Click=AlterarChave_Click. Style attribute should reference the existing BrowseButton style if present (or copy its template inline if not). Mark KeyTextBox with IsReadOnly=True and Background=#2A2A30 and Foreground=#888888 per UI-SPEC Secondary surface KeyTextBox polish.

    Edit B — AIBWindows/Views/SettingsWindow.xaml.cs:
    1. Add a new method RefreshKeyTextBoxLabel() (private void) that reads _currentSettings.ApiKey and sets KeyTextBox.Text per the four-case mapping in the behavior block above. Call this method from the end of LoadUiValues() (currently called from the constructor) so the label is correct on every SettingsWindow open.
    2. Add a new handler AlterarChave_Click(object sender, RoutedEventArgs e) that does: instantiate new FirstRunWindow(), call ShowDialog(), and on DialogResult==true reload _currentSettings via _settingsService.LoadSettings() and call RefreshKeyTextBoxLabel(). Do NOT call Application.Current.Shutdown() in this handler (it is NOT the first-launch path).
    3. Locate the existing Save_Click handler (around lines 120-141). Remove the line that does _currentSettings.ApiKey = KeyTextBox.Text; (currently around lines 125-128) — KeyTextBox is now display-only, so writing its display text back to settings would corrupt the sentinel (e.g. settings.ApiKey would become the literal string "Configurada (cofre DPAPI)" which is not a valid sentinel value). Simply DELETE that line, since KeyTextBox is no longer the source of truth for ApiKey — FirstRunWindow now owns that write path.

    DO NOT modify ProviderComboBox or any other Settings UI element (plan 03 already added the OpenAI item; KeyTextBox polish is the only Settings change in this plan).
  </action>
  <verify>
    <automated>dotnet build C:\Users\Carlo\CPAPS\AIB\AIBWindows\AIB.csproj</automated>
  </verify>
  <acceptance_criteria>
    - Select-String -Path AIBWindows/Views/SettingsWindow.xaml -Pattern "Alterar chave" returns 1 match (Edit A)
    - Select-String -Path AIBWindows/Views/SettingsWindow.xaml -Pattern "IsReadOnly" returns at least 1 match (Edit A — KeyTextBox)
    - Select-String -Path AIBWindows/Views/SettingsWindow.xaml -Pattern "AlterarChave_Click" returns 1 match (Edit A — event binding)
    - Select-String -Path AIBWindows/Views/SettingsWindow.xaml.cs -Pattern "private void AlterarChave_Click" returns 1 match (Edit B handler)
    - Select-String -Path AIBWindows/Views/SettingsWindow.xaml.cs -Pattern "new FirstRunWindow" returns 1 match (Edit B — dialog instantiation)
    - Select-String -Path AIBWindows/Views/SettingsWindow.xaml.cs -Pattern "Configurada" returns at least 1 match (label for use-vault sentinel)
    - Select-String -Path AIBWindows/Views/SettingsWindow.xaml.cs -Pattern "não necessário para Ollama" returns 1 match (label for ollama sentinel)
    - Select-String -Path AIBWindows/Views/SettingsWindow.xaml.cs -Pattern "RefreshKeyTextBoxLabel" returns at least 2 matches (method definition + at least 1 call site)
    - Select-String -Path AIBWindows/Views/SettingsWindow.xaml.cs -Pattern "_currentSettings.ApiKey = KeyTextBox.Text" returns 0 matches (the corrupting line is removed)
    - Select-String -Path AIBWindows/Views/SettingsWindow.xaml.cs -Pattern "Application.Current.Shutdown" returns 0 matches (this is NOT the first-launch path)
    - dotnet build AIBWindows/AIB.csproj exits 0 with Build succeeded
  </acceptance_criteria>
  <done>SettingsWindow shows friendly read-only label for the key field + Alterar chave button that reopens FirstRunWindow without shutting down on cancel; the sentinel-corruption bug (KeyTextBox.Text writing back to settings.ApiKey) is closed; build is green.</done>
</task>

</tasks>

<threat_model>
## Trust Boundaries

| Boundary | Description |
|----------|-------------|
| SettingsWindow KeyTextBox ↔ settings file | Plan 04a Task 1 removes a corrupting code path where KeyTextBox display text was being written back to settings.ApiKey on Save. |
| SettingsWindow AlterarChave_Click ↔ FirstRunWindow | The Settings path opens FirstRunWindow as a sub-dialog. Cancel here must NOT trigger app shutdown (different lifecycle from the first-launch hotkey path). |

## STRIDE Threat Register

| Threat ID | Category | Component | Severity | Disposition | Mitigation Plan |
|-----------|----------|-----------|----------|-------------|-----------------|
| T-02-15 | Tampering (ASVS V14) | SettingsWindow Save_Click corrupting the sentinel by writing display label back to settings.ApiKey | high | mitigate | Task 1 Edit B step 3: DELETE the line _currentSettings.ApiKey = KeyTextBox.Text; in Save_Click. Acceptance check: Select-String returns 0 matches for that exact line in the post-edit file. |
| T-02-16 | Spoofing / UX (ASVS V1) | SettingsWindow Alterar chave button accidentally shutting down the app on Cancel (Pitfall 6 misapplication) | medium | mitigate | Task 1 Edit B step 2: AlterarChave_Click does NOT call Application.Current.Shutdown(). Acceptance check: Select-String returns 0 matches for Application.Current.Shutdown in SettingsWindow.xaml.cs. |
| T-02-SC | Tampering / Supply Chain | npm/pip/cargo installs in this plan | low | accept | No package installs — two pre-existing-file edits only. RESEARCH Package Legitimacy Audit confirms N/A for the entire phase. |
</threat_model>

<verification>
- dotnet build AIBWindows/AIB.csproj exits 0 with Build succeeded after Task 1.
- Select-String -Path AIBWindows/Views/SettingsWindow.xaml.cs -Pattern "_currentSettings.ApiKey = KeyTextBox.Text" returns 0 matches (corrupting line removed).
- Select-String -Path AIBWindows/Views/SettingsWindow.xaml.cs -Pattern "Application.Current.Shutdown" returns 0 matches (Pitfall 6 mitigated in Settings path).
</verification>

<success_criteria>
- SettingsWindow displays the vault sentinel as a friendly label; Alterar chave button safely reopens FirstRunWindow.
- The sentinel-corruption bug (KeyTextBox.Text → settings.ApiKey) is closed.
- Build is green after the edits.
</success_criteria>

<output>
Create .planning/phases/02-key-rotation-env-hardening/02-04a-SUMMARY.md when done, listing: SettingsWindow edits (line deltas), confirmation of corrupting-line removal, build status.
</output>
