---
phase: 02-key-rotation-env-hardening
milestone: security-remediation-v1
status: draft
created: 2026-05-31
requirements:
  - SEC-03
nyquist_compliant: true
covers:
  - SEC-03.A1
  - SEC-03.A2
  - SEC-03.A3
  - SEC-03.A4
  - D-01
  - D-03
  - D-04
  - D-05
  - D-06
  - D-09
  - D-10
  - D-11
  - D-12
  - NFR-03
  - NFR-04
---

# Phase 02 — VERIFICATION (Manual UAT)

**Phase:** 02-key-rotation-env-hardening
**Milestone:** security-remediation-v1
**Build under test:** \<commit SHA after merge of 02-04b\>
**Tester:** \<name\>
**Date:** \<YYYY-MM-DD\>

> Manual UAT per CONTEXT.md D-07 + VALIDATION.md per-task table. No automated test
> suite exists for AIBWindows (see `.planning/codebase/TESTING.md`). Each scenario
> MUST be executed against a fresh launch of the WPF app unless the scenario explicitly
> chains state. Build precondition: `dotnet build AIBWindows/AIB.csproj` → 0 errors.

**D-12 evidence reference:** `.planning/phases/02-key-rotation-env-hardening/evidence/openai-console-rotation-2026-MM-DD.png` (referenced by S2).

**Vault path:** `~/.AIB/credentials/openai.bin`
**Audit log path:** `~/.AIB/logs/audit.log`
**Settings file:** `~/.AIB/profile.dat` (DPAPI-encrypted; deserialize via debugger or temporary helper script)

---

## Scenarios

### S1 — SEC-03.A1 + D-10: live key removed from disk
- [ ] Pre: clean working tree at the commit under test (post 02-04b merge).
- [ ] Step: in PowerShell run:
  ```
  Test-Path C:\Users\Carlo\CPAPS\AIB\.env
  Test-Path C:\Users\Carlo\CPAPS\AIB\AIBWindows\.env
  Test-Path C:\Users\Carlo\CPAPS\AIB\AIBLinux\.env
  Get-ChildItem -Recurse -Filter .env -ErrorAction SilentlyContinue
  ```
- [ ] Expected: first three return `False`; the recursive scan returns 0 entries.
- [ ] Step: open `AIBWindows/AIB.csproj` and search for `DotNetEnv`.
- [ ] Expected: 0 matches.
- Observed: ________________ (pending tester)
- Result: [ ] Pass  [ ] Fail

### S2 — SEC-03.A2 + D-12: rotation evidence present
- [ ] Pre: Task 3 D-12 manual rotation completed; owner committed redacted screenshot.
- [ ] Step:
  ```
  Get-ChildItem .planning/phases/02-key-rotation-env-hardening/evidence/openai-console-rotation-*.png
  ```
- [ ] Expected: at least one PNG file matching `evidence/openai-console-rotation-YYYY-MM-DD.png`; file size > 0 and < 5MB.
- [ ] Step: open the screenshot visually.
- [ ] Expected: image shows the OLD `sk-svcacct-...` key explicitly marked `Revoked` with revocation timestamp; the NEW key visible only as last-4 in the list view; current date visible (browser bar, system clock, or page header). No pixel contains the full new key value.
- Observed: ________________ (pending tester)
- Result: [ ] Pass  [ ] Fail

### S3 — SEC-03.A3 + D-01 + D-03: fresh vault → FirstRunWindow on first hotkey
- [ ] Pre: app NOT running.
- [ ] Step:
  ```
  Remove-Item ~/.AIB/credentials/openai.bin -ErrorAction SilentlyContinue
  ```
  Then via debugger or temporary helper set `settings.AiProvider = "OpenAI"` and `settings.ApiKey = "use-vault"` in `~/.AIB/profile.dat`. Launch `AIB.exe`. Wait for tray icon. Press `Ctrl+Shift+Space`.
- [ ] Expected: `FirstRunWindow` appears centered on screen (480×520, dark theme matching ChatWindow); `ChatWindow` does NOT appear. The Ollama radio is pre-selected per D-07.
- Observed: ________________ (pending tester)
- Result: [ ] Pass  [ ] Fail

### S4 — SEC-03.A4 + D-05: placeholder key rejected (regex deny + audit firstrun_invalid_key)
- [ ] Pre: S3 just ran; `FirstRunWindow` open.
- [ ] Step: click the OpenAI radio. In `KeyTextBox` paste `sk-PLACEHOLDER`. Click `Salvar`.
- [ ] Expected: `ErrorLabel` becomes visible with a "Chave inválida" message; `SaveButton` becomes disabled; `FirstRunWindow` stays open.
- [ ] Step: tail audit log:
  ```
  Get-Content ~/.AIB/logs/audit.log -Tail 1
  ```
- [ ] Expected: the last JSONL line contains `"outcome":"firstrun_invalid_key"` and `"provider":"OpenAI"`. The line does NOT contain the literal string `sk-PLACEHOLDER` or any other key value.
- Observed: ________________ (pending tester)
- Result: [ ] Pass  [ ] Fail

### S5 — D-01 + D-03 + D-08: Ollama-branch user never sees FirstRunWindow
- [ ] Pre: app NOT running. Via debugger or helper set `settings.AiProvider = "Ollama"` and `settings.ApiKey = "ollama"` in `profile.dat`. Vault state irrelevant for this scenario.
- [ ] Step: launch app, wait for tray, press `Ctrl+Shift+Space`.
- [ ] Expected: `ChatWindow` opens directly; `FirstRunWindow` does NOT appear at any point.
- Observed: ________________ (pending tester)
- Result: [ ] Pass  [ ] Fail

### S6 — D-04 + Pitfall 6: four cancel paths each emit audit + clean shutdown
- [ ] Pre: vault empty, `settings.AiProvider="OpenAI"`, `settings.ApiKey="use-vault"`.
- [ ] Step (a): launch app → hotkey → `FirstRunWindow` opens → press `Esc`.
- [ ] Expected: window closes; process exits within ~5s (tray icon disappears within ~30s of explorer hover refresh); audit log last line contains `"outcome":"firstrun_cancelled"`. The cancel audit is emitted by `App.ShowFirstRunWindow` (per Pitfall 6 — NOT by the window itself).
- [ ] Step (b): relaunch app → hotkey → click the window `X` close button.
- [ ] Expected: same as (a).
- [ ] Step (c): relaunch app → hotkey → click `Cancelar` button in `FirstRunWindow`.
- [ ] Expected: same as (a).
- [ ] Step (d): relaunch app → hotkey → press `Alt+F4`.
- [ ] Expected: same as (a).
- [ ] Step: tail audit log:
  ```
  Get-Content ~/.AIB/logs/audit.log -Tail 4
  ```
- [ ] Expected: 4 JSONL lines each with `"outcome":"firstrun_cancelled"`.
- Observed: ________________ (pending tester)
- Result: [ ] Pass  [ ] Fail

### S7 — D-05 positive: regex accepts real-shape key
- [ ] Pre: vault empty, `settings.ApiKey="use-vault"`, `settings.AiProvider="OpenAI"`.
- [ ] Step: launch app → hotkey → `FirstRunWindow` opens → click OpenAI radio → paste a real-shape key matching `^sk-[a-zA-Z0-9_-]{20,}$` (any test key value with the right shape) → click `Salvar`.
- [ ] Expected: `ErrorLabel` hidden throughout; `SaveButton` enabled after the field validates; window closes; `ChatWindow` opens (D-08 sequencing — only after Save).
- Observed: ________________ (pending tester)
- Result: [ ] Pass  [ ] Fail

### S8 — D-06: vault write + sentinel persistence + firstrun_saved audit
- [ ] Pre: S7 just completed successfully.
- [ ] Step:
  ```
  Test-Path ~/.AIB/credentials/openai.bin
  Get-Item ~/.AIB/credentials/openai.bin | Select-Object Length
  ```
- [ ] Expected: file exists; size > 0.
- [ ] Step: via debugger or helper load `profile.dat`.
- [ ] Expected: `settings.ApiKey == "use-vault"`.
- [ ] Step:
  ```
  Get-Content ~/.AIB/logs/audit.log -Tail 1
  ```
- [ ] Expected: line contains `"outcome":"firstrun_saved"`, `"provider":"OpenAI"`, and `"key_last4":"<4chars>"`. The line does NOT contain the full key value or any prefix starting with `sk-svcacct`.
- Observed: ________________ (pending tester)
- Result: [ ] Pass  [ ] Fail

### S9 — D-06 read: chat request consumes vault-resolved key
- [ ] Pre: S8 just completed successfully; `ChatWindow` is open.
- [ ] Step: send a test chat message (e.g. "Hello, respond with a single word"). Wait for response.
- [ ] Expected: OpenAI returns a normal response (no `[ERROR]:` prefix; no 401). Audit log has NO new entries from this chat path (vault read on every chat request is intentionally silent per T-02-08 disposition).
- Observed: ________________ (pending tester)
- Result: [ ] Pass  [ ] Fail

### S10 — D-09: Ollama branch lists installed models + ShadowModelName mirror
- [ ] Pre: `ollama serve` running locally on port 11434 with at least one model installed.
- [ ] Step: clean vault, set `settings.ApiKey="use-vault"`, set `settings.AiProvider="OpenAI"` (or open Alterar chave from Settings to force `FirstRunWindow`). Launch → hotkey → window opens → click Ollama radio.
- [ ] Expected: `ModelComboBox` populates with installed models within ~2s; `LoadingProgress` hides; `OllamaErrorBlock` not visible.
- [ ] Step: pick a model → click `Salvar`. Via debugger or helper inspect `profile.dat`.
- [ ] Expected: `settings.AiProvider == "Ollama"`; `settings.ApiKey == "ollama"`; `settings.ModelName == settings.ShadowModelName == <picked model>`. Audit log last line contains `"outcome":"firstrun_saved"`, `"provider":"Ollama"`, and `"model":"<picked>"`.
- Observed: ________________ (pending tester)
- Result: [ ] Pass  [ ] Fail

### S11 — D-09 fail: Ollama unavailable → OllamaErrorBlock + retry + fallback link
- [ ] Pre: stop Ollama (`Stop-Service ollama` or `taskkill /F /IM ollama.exe`).
- [ ] Step: clean vault, set `settings.AiProvider="OpenAI"` + `settings.ApiKey="use-vault"`. Launch → hotkey → `FirstRunWindow` opens (default Ollama radio).
- [ ] Expected: `ModelComboBox` hides; `OllamaErrorBlock` becomes visible with the "Ollama não detectado" message; `RetryButton` visible; `FallbackLink` ("usar qwen2.5:7b mesmo assim") visible. No app hang or thrown exception.
- [ ] Step: click `FallbackLink`.
- [ ] Expected: `FallbackModelDisplay` shows `qwen2.5:7b` read-only; `SaveButton` enables.
- Observed: ________________ (pending tester)
- Result: [ ] Pass  [ ] Fail

### S12 — D-10: DotNetEnv removed + build clean
- [ ] Pre: clean working tree at the commit under test.
- [ ] Step:
  ```
  Select-String "DotNetEnv" AIBWindows/AIB.csproj
  dotnet build AIBWindows/AIB.csproj
  ```
- [ ] Expected: `Select-String` returns 0 matches; `dotnet build` exits 0 with `Build succeeded`. Warning count = 24 (Phase 1 baseline; no new warnings).
- Observed: ________________ (pending tester)
- Result: [ ] Pass  [ ] Fail

### S13 — D-11: existing-user migration is once + idempotent (audit migration_clear_apikey)
- [ ] Pre: app NOT running. Via debugger or temporary helper, pre-encrypt `settings.ApiKey="sk-svcacct-test"` (any test value beginning with `sk-`) into `profile.dat`. Audit log file may exist or be fresh.
- [ ] Step: launch app. Wait for tray.
- [ ] Expected: console contains `[MIGRATION] settings.ApiKey replaced with 'use-vault' sentinel (D-11).`; `profile.dat` now has `settings.ApiKey == "use-vault"`; audit log appends one new JSONL line with `"outcome":"migration_clear_apikey"` and `"previous_key_present":true`. The line does NOT contain the test key value `sk-svcacct-test`.
- [ ] Step: close app via tray → `Sair`. Relaunch.
- [ ] Expected: console does NOT print `[MIGRATION]` again; audit log gets NO new `migration_clear_apikey` line on this second boot (idempotent guard skips because `ApiKey == "use-vault"` already).
- Observed: ________________ (pending tester)
- Result: [ ] Pass  [ ] Fail

### S14 — NFR-04: profile.dat schema unchanged
- [ ] Pre: a Phase-1-era `profile.dat` (any pre-2026-05-31 backup) available at `~/.AIB/profile.dat`.
- [ ] Step: launch the Phase-2 build.
- [ ] Expected: no `[DESERIALIZATION]` error in console; `SettingsService.LoadSettings()` returns a populated `UserAppSettings` POCO. Any missing field receives the C# default; no field added or removed by Phase 2.
- [ ] Step: open SettingsWindow.
- [ ] Expected: every field renders without exception; provider combo round-trips the loaded `AiProvider` value.
- Observed: ________________ (pending tester)
- Result: [ ] Pass  [ ] Fail

### S15 — Settings polish (02-04a): friendly label + Alterar chave reopens FirstRunWindow without shutdown
- [ ] Pre: app running with a configured vault (S8 already completed in this session OR similar prior setup); `settings.ApiKey == "use-vault"`.
- [ ] Step: open `SettingsWindow` via tray menu `Configurações`.
- [ ] Expected: `KeyTextBox` displays the literal text `Configurada (cofre DPAPI)` in greyed-out style (`#888888` on `#2A2A30`). Field is read-only — typing into it produces no input.
- [ ] Step: click `Alterar chave` button.
- [ ] Expected: `FirstRunWindow` opens as a sub-dialog over `SettingsWindow`.
- [ ] Step: press `Esc` in `FirstRunWindow`.
- [ ] Expected: `FirstRunWindow` closes; `SettingsWindow` stays open; the app does NOT shut down (T-02-16 — Settings path is NOT the first-launch hotkey path). Audit log does NOT receive a `firstrun_cancelled` line emitted by `App.ShowFirstRunWindow` (that handler is bound to the hotkey path; SettingsWindow's `AlterarChave_Click` does not emit it).
- [ ] Step: set `settings.AiProvider="Ollama"` + `settings.ApiKey="ollama"` via debugger; re-open SettingsWindow.
- [ ] Expected: `KeyTextBox` now displays `(não necessário para Ollama)`.
- Observed: ________________ (pending tester)
- Result: [ ] Pass  [ ] Fail

---

## Audit Log Outcome Coverage Map

| Outcome literal | Scenario(s) | Source |
|-----------------|-------------|--------|
| `firstrun_saved` | S8 (OpenAI), S10 (Ollama) | FirstRunWindow.SaveOpenAiBranch / SaveOllamaBranch |
| `firstrun_invalid_key` | S4 | FirstRunWindow.ValidateOpenAiKey / SaveOpenAiBranch |
| `firstrun_cancelled` | S6 (all 4 cancel paths) | App.ShowFirstRunWindow (Pitfall 6 — parent-owned, NOT window) |
| `migration_clear_apikey` | S13 | App.OnStartup D-11 migration |

All four literal strings appear above per checker Issue 7.

---

## D-XX Coverage Map

| Decision | Scenario(s) |
|----------|-------------|
| D-01 | S3, S5 |
| D-03 | S3, S5 |
| D-04 | S6 |
| D-05 | S4, S7 |
| D-06 | S8 (write), S9 (read) |
| D-09 | S10 (success), S11 (fail) |
| D-10 | S1, S12 |
| D-11 | S13 |
| D-12 | S2 |

---

## Sign-Off

- [ ] Tester ran all 15 scenarios in order against a fresh `dotnet build` of the post-02-04b merge commit.
- [ ] All 15 scenarios marked Pass (or all Fail entries have an open bug reference in CONCERNS.md).
- [ ] D-12 rotation evidence committed at the referenced path; visually confirmed (S2).
- [ ] Audit log retained a copy at `.planning/phases/02-key-rotation-env-hardening/evidence/audit-log-uat-YYYY-MM-DD.jsonl` for record.

**Approval:** \<tester-name\> · \<YYYY-MM-DD\>
