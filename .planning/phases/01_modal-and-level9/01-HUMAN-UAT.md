---
status: partial
phase: 01_modal-and-level9
source: [01-VERIFICATION.md, VERIFICATION.md]
started: 2026-05-29T00:00:00Z
updated: 2026-05-29T00:00:00Z
---

## Current Test

[awaiting human testing]

## Tests

The 8 scenarios below are reproduced verbatim from `VERIFICATION.md` (T9 scaffold) and `01-VERIFICATION.md` (verifier report). Run them against the WPF build at HEAD (commit chain ending at the latest `fix(security): serialize modal + bind audit path to current DataDir` plus T7 `feat(security): wire CommandConfirmationWindow into RunCommandTool`). Fill `result:` per scenario, then mark this file `status: passed` (or `status: failed` with gap entries).

### 1. S1 — L1 Deny path
expected: Modal appears with Tool=`run_command`, Command=`git status`, Nível=`1/9`, CWD=`<~/.AIB path>`. Click "Recusar". Tool returns `Comando recusado pelo usuário`.
result: [pending]

### 2. S2 — L1 Allow path
expected: Click "Permitir" (no AlwaysAllow). Tool returns real `git status` output (or `[Comando executado, mas não retornou saída]`).
result: [pending]

### 3. S3 — AlwaysAllow within a session
expected: First call with AlwaysAllow checked → executes. Second `git status` call → no modal, executes directly.
result: [pending]

### 4. S4 — AlwaysAllow cleared on app restart
expected: After S3, close app via tray (Sair) and relaunch. `git status` shows modal again.
result: [pending]

### 5. S5 — L9 still shows modal for destructive verb
expected: `/unlock_level 9`, then `rm -rf /tmp/x`. Modal appears with Nível=`9/9` and the destructive verb visible. Click "Recusar". Tool returns `Comando recusado pelo usuário`.
result: [pending]

### 6. S6 — Esc denies
expected: Trigger any `run_command`, press Esc with modal focused. Modal closes via Deny path. Tool returns `Comando recusado pelo usuário`. Audit log records `deny`.
result: [pending]

### 7. S7 — No-UI guard
expected: With `Application.Current == null` (headless harness), `RunCommandTool.ExecuteAsync` returns `ACESSO NEGADO: interface de confirmação indisponível.` Audit log records `deny_no_ui`. No crash.
result: [pending]

### 8. S8 — Audit log contents
expected: `~/.AIB/logs/audit.log` exists with one JSONL entry per scenario. Fields per line: `ts`, `tool`, `cmd`, `level`, `cwd`, `outcome`, `always_allow`. Outcome map: S1→`deny`, S2→`allow`, S3 first→`always_allow`, S3 second→`always_allow`, S5→`deny`, S6→`deny`, S7→`deny_no_ui`.
result: [pending]

## Summary

total: 8
passed: 0
issues: 0
pending: 8
skipped: 0
blocked: 0

## Gaps
