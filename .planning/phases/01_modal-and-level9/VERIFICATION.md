# Phase 01 — VERIFICATION (Manual UAT)

**Phase:** 01_modal-and-level9
**Milestone:** security-remediation-v1
**Build under test:** <commit SHA of T7>
**Tester:** <name>
**Date:** <YYYY-MM-DD>

> Manual UAT per CONTEXT.md D7. No automated test suite exists for AIBWindows
> (see .planning/codebase/TESTING.md). Each scenario MUST be executed against
> a fresh launch of the WPF app unless the scenario explicitly chains state.

## Scenarios

### S1 — L1 Deny path
- [ ] Pre: level == 1 (default fresh launch).
- [ ] Step: trigger `run_command` with `git status` via a chat turn.
- [ ] Expected: modal appears showing Tool=`run_command`, Command=`git status`, Nível=`1/9`, CWD=`<~/.AIB path>`.
- [ ] Step: click "Recusar".
- [ ] Expected: tool result string returned to the chat is exactly `Comando recusado pelo usuário`.
- Observed: ________________
- Result: [ ] Pass  [ ] Fail

### S2 — L1 Allow path
- [ ] Pre: level == 1.
- [ ] Step: trigger `run_command("git status")` → click "Permitir" (do NOT check AlwaysAllow).
- [ ] Expected: tool returns the output of `git status` (or `[Comando executado, mas não retornou saída]` if outside a git repo).
- Observed: ________________
- Result: [ ] Pass  [ ] Fail

### S3 — AlwaysAllow within a session
- [ ] Pre: level == 1, same app session as S2.
- [ ] Step: trigger `run_command("git status")` → click "Permitir" with the "Sempre permitir" CheckBox checked.
- [ ] Step: trigger `run_command("git status")` again (same exact string).
- [ ] Expected: second invocation does NOT show the modal; tool returns output directly.
- Observed: ________________
- Result: [ ] Pass  [ ] Fail

### S4 — AlwaysAllow cleared on app restart
- [ ] Pre: complete S3 in this session (allowlist now contains "git status").
- [ ] Step: fully close the app (tray menu → Sair) and relaunch.
- [ ] Step: trigger `run_command("git status")` again.
- [ ] Expected: modal appears (AlwaysAllow state did NOT persist).
- Observed: ________________
- Result: [ ] Pass  [ ] Fail

### S5 — L9 still shows modal for destructive verb
- [ ] Pre: elevate to level 9 via `/unlock_level 9` (manual chat command).
- [ ] Step: trigger `run_command("rm -rf /tmp/x")`.
- [ ] Expected: modal appears. The Command field shows `rm -rf /tmp/x` verbatim. Nível shows `9/9`.
- [ ] Step: click "Recusar".
- [ ] Expected: tool returns `Comando recusado pelo usuário`. Command is NOT executed.
- Observed: ________________
- Result: [ ] Pass  [ ] Fail

### S6 — Esc denies
- [ ] Pre: any level.
- [ ] Step: trigger any `run_command`. With the modal focused, press Esc.
- [ ] Expected: modal closes via the Deny path. Tool returns `Comando recusado pelo usuário`. Audit log records outcome `deny`.
- Observed: ________________
- Result: [ ] Pass  [ ] Fail

### S7 — No-UI guard
- [ ] Pre: a debug harness or build configuration where `System.Windows.Application.Current == null` (e.g. a headless console driver of `RunCommandTool`).
- [ ] Step: instantiate `RunCommandTool` and call `ExecuteAsync(argsJson: "{ \"command\": \"git status\" }", userLevel: 5)` directly.
- [ ] Expected: returns `ACESSO NEGADO: interface de confirmação indisponível.` Audit log records outcome `deny_no_ui`. NO crash, no exception bubbles.
- Observed: ________________
- Result: [ ] Pass  [ ] Fail

### S8 — Audit log contents
- [ ] Pre: scenarios S1-S6 (and S7 if exercised) have run in order in a single test session.
- [ ] Step: open `~/.AIB/logs/audit.log` in a text editor.
- [ ] Expected: file exists; contains one JSON object per line (JSONL); each line has fields `ts`, `tool`, `cmd`, `level`, `cwd`, `outcome`, `always_allow`. Outcomes across the run map 1:1 to scenarios as follows:
  - S1 → `deny`
  - S2 → `allow`
  - S3 first call → `always_allow`; S3 second call → `always_allow`
  - S5 → `deny`
  - S6 → `deny`
  - S7 (if run) → `deny_no_ui`
- Observed (line count, paste a couple of lines): ________________
- Result: [ ] Pass  [ ] Fail

## Sign-off

- [ ] All 8 scenarios pass.
- [ ] CONCERNS.md CRITICAL #1 and #3 are listed in "Resolved" with the build SHA above.
- [ ] No regression observed in non-`run_command` tools (memory, vault, read_screen, manage_clipboard) — they MUST NOT show the confirmation modal.

Tester signature: ________________   Date: ____________
