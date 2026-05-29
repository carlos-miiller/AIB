---
phase: 01_modal-and-level9
plan: 01
subsystem: security/run-command
tags: [security, modal, level-9, audit-log, hitl, sec-01, sec-02]
requirements: [SEC-01, SEC-02]
concerns_closed: [CRITICAL-1, CRITICAL-3]
dependency_graph:
  requires:
    - AIBWindows/Services/DirectoryService.cs (DataDir, LogsDir)
    - AIBWindows/Services/SettingsService.cs (ConfirmDangerousCommands)
    - AIBWindows/Services/CommandService.cs (ExecuteAsync)
    - System.Windows.Application.Current.Dispatcher
  provides:
    - AIB.Services.CommandConfirmationContext (POCO)
    - AIB.Services.AlwaysAllowSession (static)
    - AIB.Services.AuditLogService (static)
    - CommandConfirmationWindow(CommandConfirmationContext) ctor
    - Per-call HITL modal for run_command at every level
    - JSONL audit trail at ~/.AIB/logs/audit.log
  affects:
    - AIBWindows/Services/NativeTools.cs (RunCommandTool.ExecuteAsync restructured; +ApplyDenylist; +BuildEntry)
    - AIBWindows/Views/CommandConfirmationWindow.xaml (+Tool/Level/CWD rows; +IsCancel; height 300 to 380)
    - AIBWindows/Views/CommandConfirmationWindow.xaml.cs (legacy string ctor dropped; new ctx ctor)
    - AIBWindows/Services/SettingsService.cs (XML doc on ConfirmDangerousCommands)
    - .planning/codebase/CONCERNS.md (CRITICAL #1 + #3 to Resolved)
tech_stack:
  added: []
  patterns:
    - Background-thread tool to Application.Current.Dispatcher.InvokeAsync<T>() to ShowDialog to resume on threadpool
    - Static-class + lock-guarded HashSet for process-lifetime state
    - SemaphoreSlim(1,1) around append-only File.AppendAllTextAsync JSONL writes
    - Fire-and-forget _ = AppendAsync(...) for non-critical audit writes
    - Portuguese error-string return idiom for tool deny paths
key_files:
  created:
    - AIBWindows/Services/CommandConfirmationContext.cs
    - AIBWindows/Services/AlwaysAllowSession.cs
    - AIBWindows/Services/AuditLogService.cs
    - .planning/phases/01_modal-and-level9/VERIFICATION.md
  modified:
    - AIBWindows/Views/CommandConfirmationWindow.xaml
    - AIBWindows/Views/CommandConfirmationWindow.xaml.cs
    - AIBWindows/Services/SettingsService.cs
    - AIBWindows/Services/NativeTools.cs
    - .planning/codebase/CONCERNS.md
decisions:
  - D1 Dispatcher.InvokeAsync hop + Application.Current null-guard, implemented in RunCommandTool.ExecuteAsync
  - D2 Session-only HashSet, exact-string match, no normalization, no persistence, implemented in AlwaysAllowSession
  - D3 Modal text content tool/command/level/cwd, implemented across T1+T4+T5+T7
  - D4 Esc denies via IsCancel=True on Deny button, implemented in CommandConfirmationWindow.xaml
  - D5 L9 disables denylist never the modal, implemented at T7 gate userLevel less than 9 AND settings.ConfirmDangerousCommands
  - D6 JSONL audit log at ~/.AIB/logs/audit.log via DirectoryService.DataDir, implemented in AuditLogService + 5 call sites in RunCommandTool
  - D7 Manual UAT 8 scenarios, codified in VERIFICATION.md template
  - D8 ConfirmDangerousCommands gates ONLY the post-modal denylist never the modal, implemented at T7 gate + documented at SettingsService.cs:36
metrics:
  duration_seconds: 392
  tasks_completed: 9
  files_created: 4
  files_modified: 5
  commits: 9
  completed_date: 2026-05-29
---

# Phase 1 Plan 1: Modal confirmation + Level-9 alignment Summary

**One-liner:** RunCommandTool.ExecuteAsync now gates every shell call through CommandConfirmationWindow via Application.Current.Dispatcher.InvokeAsync at every level, with a session-only AlwaysAllow allowlist and a JSONL audit trail at ~/.AIB/logs/audit.log; Level 9 only disables the denylist, never the modal.


## What changed

The orphan CommandConfirmationWindow is now wired into the single tool that needed it. The modal:

- Always fires for run_command regardless of userLevel (closing CONCERNS.md CRITICAL #3, the L9 bypass).
- Always fires regardless of settings.ConfirmDangerousCommands. That flag now ONLY controls whether the denylist runs as a second-layer check AFTER the modal at levels < 9 (D8 revised).
- Renders four context fields per the locked D3 schema: Tool=run_command, the command text itself, Nivel=N/9, and the cwd that cmd.exe will run in (DirectoryService.DataDir).
- Honors a session-only AlwaysAllow allowlist (HashSet<string> in AlwaysAllowSession, exact-string match, lock-guarded, cleared on app exit per D2).
- Esc / X / Alt+F4 all route to Deny via IsCancel=True on the Deny button (D4).
- Returns a Portuguese ACESSO NEGADO interface de confirmacao indisponivel if System.Windows.Application.Current is null (D1 reentrancy guard).
- Returns the Portuguese Comando recusado pelo usuario on a Deny click (matches D7 UAT scenario 1).

Every outcome fire-and-forgets one entry to ~/.AIB/logs/audit.log (JSONL, single line, LF only, UTF-8) with schema ts/tool/cmd/level/cwd/outcome/always_allow. outcome is one of allow / deny / always_allow / deny_no_ui. Concurrent writes serialize through a single SemaphoreSlim(1,1) in AuditLogService; write failures log [AUDIT] Falha ao gravar audit.log to console and never propagate (D6).

The previous denylist body - verbatim - moved into a private RunCommandTool.ApplyDenylist(cmdLower, userLevel). Its call site is now: if (userLevel less than 9 AND settings.ConfirmDangerousCommands) ApplyDenylist(...). The implementing executor preserved every regex, every threshold, every Portuguese error string from the pre-phase body; only the outer if (userLevel < 9) wrapper was hoisted to the call site.

## Decisions Made

See frontmatter decisions block. All eight decisions (D1-D8) from CONTEXT.md landed verbatim. No new decisions arose during execution.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Missing using AIB.Views directive in NativeTools.cs**

- Found during: Task 7 (initial build).
- Issue: First build of T7 failed with CS0246: The type or namespace name CommandConfirmationWindow could not be found. The Services namespace does not implicitly see the Views namespace.
- Fix: Added using AIB.Views; to the top of AIBWindows/Services/NativeTools.cs.
- Files modified: AIBWindows/Services/NativeTools.cs.
- Commit: folded into the T7 commit (6c078c2); no separate commit.

**2. [Rule 1 - Bug] Tuple deconstruction inference error on Dispatcher.InvokeAsync return**

- Found during: Task 7 (initial build).
- Issue: var (allowed, alwaysAllow) = await ...InvokeAsync(() => return (bool, bool)).Task failed with CS8130: Cannot infer the type of implicitly-typed deconstruction variable allowed. The compiler could not flow the inner tuple shape through the DispatcherOperation<T>.Task projection without an explicit <T>.
- Fix: Replaced var (allowed, alwaysAllow) = await ...InvokeAsync(...) with explicit typing: (bool allowed, bool alwaysAllow) = await ...InvokeAsync<(bool, bool)>(...). Both the deconstruction targets and the generic parameter are spelled out so the compiler has no inference to do.
- Files modified: AIBWindows/Services/NativeTools.cs (the modal-hop block inside the new ExecuteAsync).
- Commit: folded into the T7 commit (6c078c2); no separate commit.

Both are textbook Rule 1 auto-fixes (broken behavior caused by my own task code; no architectural change; no user input needed; build passes after the fix). Documented here so the next reader knows the T7 body shipped with explicit type annotations.

### Architectural changes

None. The plan executed exactly as written; no Rule 4 (architectural ask) was triggered.

## Build verification

dotnet build AIBWindows/AIB.csproj after T7: 0 errors, 24 warnings - 0 new warnings. All 24 warnings preexisted in ChatWindow.xaml.cs / ContextSidebar.xaml.cs (CS8618/CS8625/CS8602/CS4014, all related to legacy nullable-reference initialization in unrelated code-behind). None reference any file modified by this phase.

## Authentication gates

None. This phase modified only local code paths; no external service auth was needed.

## Commits

| # | Task | Commit | Files |
|---|------|--------|-------|
| 1 | T1 - CommandConfirmationContext POCO | bc4a191 | AIBWindows/Services/CommandConfirmationContext.cs |
| 2 | T2 - AlwaysAllowSession allowlist | 8b9f62e | AIBWindows/Services/AlwaysAllowSession.cs |
| 3 | T3 - AuditLogService JSONL writer | f777314 | AIBWindows/Services/AuditLogService.cs |
| 4 | T4 - Modal XAML rows + IsCancel | 39607d2 | AIBWindows/Views/CommandConfirmationWindow.xaml |
| 5 | T5 - Modal code-behind ctor swap | 2e1568d | AIBWindows/Views/CommandConfirmationWindow.xaml.cs |
| 6 | T6 - XML doc on ConfirmDangerousCommands | f86a753 | AIBWindows/Services/SettingsService.cs |
| 7 | T7 - RunCommandTool wiring (the central change) | 6c078c2 | AIBWindows/Services/NativeTools.cs |
| 8 | T8 - CONCERNS.md Resolved annotations | 8168f30 | .planning/codebase/CONCERNS.md |
| 9 | T9 - VERIFICATION.md UAT template | 66ee9a7 | .planning/phases/01_modal-and-level9/VERIFICATION.md |

## Verification

Phase-level definition of done (CONTEXT.md):

- [x] dotnet build AIBWindows/AIB.csproj succeeds with no new warnings (built after T7; 0 new).
- [x] CommandConfirmationWindow is invoked from exactly one call site: RunCommandTool.ExecuteAsync (verified via Grep new CommandConfirmationWindow in AIBWindows/ returning exactly one source-code hit in NativeTools.cs).
- [x] CONCERNS.md CRITICAL #1 and #3 are in ## Resolved with the T7 SHA (T8).
- [ ] All 8 UAT scenarios pass and are recorded in VERIFICATION.md - DEFERRED to tester. T9 scaffolds the template; the human owner runs the WPF app and fills in observed/pass-fail for S1-S8 against build 6c078c2. The verifier pass will gate on these.
- [ ] No regression to existing tools (memory, vault, read_screen, etc.) - DEFERRED to UAT S2/S5/S6/S7 runs.

## Known Stubs

None. Every code path introduced is wired end-to-end. The XAML rows are populated by the new ctor; the audit log is written by all five outcome paths in T7; the AlwaysAllow set is both read and written; the denylist gate is fully resolved per D5+D8.

## Threat Flags

None. The phase REDUCES attack surface (every run_command invocation now requires a physical human click, with a tamper-evident audit trail). It introduces no new network endpoint, no new auth path, no new file-system access pattern beyond a single append-only log under ~/.AIB/logs/, and no schema change at any trust boundary.

## Follow-ups

1. graphify update: CLAUDE.md governance asks for graphify update . after code changes. The graphify CLI is not on the shell PATH in the execution environment - flagged for the owner to run manually before the next architecture query.
2. Run UAT against build 6c078c2 per VERIFICATION.md. The 8 scenarios (S1-S8) are the phase exit criteria. Sign-off block expects the tester name + date.
3. Phase 2 (key rotation + .env hardening) is unblocked and queued per ROADMAP.md.

## Self-Check: PASSED

Verified after writing this SUMMARY.md:

- FOUND: AIBWindows/Services/CommandConfirmationContext.cs
- FOUND: AIBWindows/Services/AlwaysAllowSession.cs
- FOUND: AIBWindows/Services/AuditLogService.cs
- FOUND: AIBWindows/Views/CommandConfirmationWindow.xaml (modified)
- FOUND: AIBWindows/Views/CommandConfirmationWindow.xaml.cs (modified)
- FOUND: AIBWindows/Services/SettingsService.cs (modified)
- FOUND: AIBWindows/Services/NativeTools.cs (modified)
- FOUND: .planning/codebase/CONCERNS.md (modified)
- FOUND: .planning/phases/01_modal-and-level9/VERIFICATION.md (new)
- FOUND commit bc4a191 (T1)
- FOUND commit 8b9f62e (T2)
- FOUND commit f777314 (T3)
- FOUND commit 39607d2 (T4)
- FOUND commit 2e1568d (T5)
- FOUND commit f86a753 (T6)
- FOUND commit 6c078c2 (T7)
- FOUND commit 8168f30 (T8)
- FOUND commit 66ee9a7 (T9)
