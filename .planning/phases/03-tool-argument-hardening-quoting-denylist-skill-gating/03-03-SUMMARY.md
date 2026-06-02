---
phase: 03-tool-argument-hardening-quoting-denylist-skill-gating
plan: 03
subsystem: command-execution
tags: [security, sec-06, skill-gating, modal-preview, sha256, always-allow-rekey, npx-hardening]
requires:
  - SEC-06 (REQUIREMENTS.md): skill execution must demand human-in-the-loop approval
  - Plan 01: CommandService.ExecuteWithArgListAsync + SkillService.InterpreterMap
  - Plan 02: CommandConfirmationContext DenylistHit/DenylistReason + BuildEntry content_hash slot
  - Phase 1 D1/D2/D5: modal-hop primitive, AlwaysAllow session lifetime, modal-first invariant
provides:
  - "CommandConfirmationContext.{ScriptBody,Interpreter,ContentHash} init-only properties (D-08)"
  - "CommandConfirmationWindow.ShowAsync(ctx) — static modal-hop helper carrying the lifted _modalLock semaphore (D-08)"
  - "AlwaysAllowSession re-keyed to HashSet<(string Tool, string Cmd, string? ContentHash)> (D-10)"
  - "ExecuteSkillTool.RequiredLevel = 6 + markdown bypass + ReadAndHashSkillFile helper + modal hop (D-09)"
  - "MaterializeSkillTool.RequiredLevel = 8 + LLM-content SHA256 + modal hop surfacing destination path (D-09)"
  - "ModalAuditEntry internal helper class — single source of truth for the audit JSONL shape; content_hash wired to ctx.ContentHash (D-10)"
  - "SkillService.InstallFromOnlineAsync hardened with SEC-06 regex + npx.cmd→npx ExecuteWithArgListAsync fallback (D-11)"
  - "CommandService.ExecuteWithArgListAsync rethrows Win32Exception on NativeErrorCode == 2 (D-11 locked patch — enables the npx fallback)"
affects:
  - AIBWindows/Services/CommandConfirmationContext.cs (extended: +3 init-only props, +13 LOC)
  - AIBWindows/Services/AlwaysAllowSession.cs (re-keyed: HashSet<string> -> HashSet<(string,string,string?)>; net +16/-6 LOC)
  - AIBWindows/Views/CommandConfirmationWindow.xaml (extended: HeaderText x:Name + ScrollViewer row + window root size mode change; +25 LOC)
  - AIBWindows/Views/CommandConfirmationWindow.xaml.cs (extended: ScriptBody ctor block + static ShowAsync helper + _modalLock field; +53 LOC)
  - AIBWindows/Services/NativeTools.cs (rewired RunCommandTool; rewrote ExecuteSkillTool + MaterializeSkillTool; extracted ModalAuditEntry helper; +189/-39 net LOC)
  - AIBWindows/Services/SkillService.cs (InstallFromOnlineAsync hardened: regex + npx.cmd fallback + XML doc; +38/-1 net LOC)
  - AIBWindows/Services/CommandService.cs (ExecuteWithArgListAsync narrow rethrow on ERROR_FILE_NOT_FOUND; +8 LOC)
tech-stack:
  added: []                # no new packages; BCL-only (System.Security.Cryptography, System.Text.RegularExpressions, System.ComponentModel.Win32Exception)
  patterns:
    - "Shared static modal-hop helper pattern: window class owns the semaphore + the Dispatcher.InvokeAsync hop; all modal-bearing tools call it instead of duplicating the hop body"
    - "ValueTuple-keyed in-memory cache with built-in structural equality (no IEqualityComparer needed)"
    - "SHA256-over-full-bytes invalidation key for silent-edit defense (Pattern 4 in 03-PATTERNS.md)"
    - "Narrow Win32Exception rethrow guard (NativeErrorCode == 2) to surface CreateProcess ERROR_FILE_NOT_FOUND for caller-level fallback logic — defends against PATHEXT-not-honored on Windows .cmd shims"
key-files:
  created: []
  modified:
    - AIBWindows/Services/CommandConfirmationContext.cs
    - AIBWindows/Services/AlwaysAllowSession.cs
    - AIBWindows/Views/CommandConfirmationWindow.xaml
    - AIBWindows/Views/CommandConfirmationWindow.xaml.cs
    - AIBWindows/Services/NativeTools.cs
    - AIBWindows/Services/SkillService.cs
    - AIBWindows/Services/CommandService.cs
  deleted: []
decisions:
  - "D-08 (Phase 3): CommandConfirmationContext gains three init-only props (ScriptBody / Interpreter / ContentHash); CommandConfirmationWindow.xaml grows a ScrollViewer row (MaxHeight=200, read-only Consolas TextBox) Visibility-gated by ScriptBody!=null; the existing header TextBlock is named HeaderText and the ctor flips its text to 'CONFIRMAÇÃO DE SCRIPT ({Interpreter})' when ScriptBody is present; Window root switched Height=380 -> MinHeight=380 SizeToContent=Height so script previews auto-grow. A new static ShowAsync(ctx) helper on the window class lifts the Phase 1 modal-hop body — the _modalLock SemaphoreSlim moved off RunCommandTool onto the window so all three modal-bearing tools share one critical section."
  - "D-09 (Phase 3): ExecuteSkillTool.RequiredLevel raised 1 -> 6; markdown-only skills (Interpreter == 'markdown') bypass the modal entirely; non-markdown skills read scriptPath off disk (cap 50KB; truncate-with-marker for preview) and compute SHA256 over the FULL file bytes (not the truncated body — silent edits past the cap still invalidate the AlwaysAllow tuple). MaterializeSkillTool.RequiredLevel raised 5 -> 8; SHA256 is computed over the LLM-supplied script_content bytes directly (no disk read — file does not exist yet); ctx.Command surfaces destination path as `{interpreter} -> {destPath}` so the modal renders WHERE the write will land (closes RESEARCH Open Question #1)."
  - "D-10 (Phase 3): AlwaysAllowSession re-keyed HashSet<string> -> HashSet<(string Tool, string Cmd, string? ContentHash)>; ValueTuple structural equality covers the match — no custom IEqualityComparer needed. All three call sites in NativeTools.cs pass the tuple (run_command uses (Tool, Cmd, (string?)null) — the cast is required for the compiler to disambiguate the ValueTuple overload). BuildEntry's content_hash field is wired to ctx.ContentHash (replacing Plan 02's null placeholder); audit JSONL now emits `\"content_hash\":null` for run_command and a 64-char hex string for skill tools."
  - "D-11 (Phase 3): SkillService.InstallFromOnlineAsync inserts the SEC-06 regex `^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(@[A-Za-z0-9_.\\-]+)?$` BEFORE any shell-out; replaces the `cmd /c call npx -y skills add {installArg} --yes` interpolation with `ExecuteWithArgListAsync(\"npx.cmd\", [-y, skills, add, installArg, --yes], workPath, 900_000)` and a fallback to `\"npx\"` on `Win32Exception { NativeErrorCode: 2 }` (CreateProcess does not honor PATHEXT — RESEARCH Pitfall 2). XML doc records dead-code-today status + future-caller gating requirement."
  - "D-11 LOCKED PATCH: CommandService.ExecuteWithArgListAsync gains a narrow `catch (Win32Exception ex) when (ex.NativeErrorCode == 2) { throw; }` guard before the existing generic catch. Without this, the generic catch would swallow the exception into the 'ERRO ao executar comando' string and the SkillService npx fallback would never fire. This is the locked path; Option B (string inspection of the returned error message) was explicitly ruled out."
  - "D-11 leading-anchor caveat: the locked regex `^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(@[A-Za-z0-9_.\\-]+)?$` allows `..foo/bar` because `.` is in the leading character class. This plan SHIPPED THE REGEX AS WRITTEN per CONTEXT.md D-11 and surfaced the caveat in plan must_haves.truths. If Plan 04 / UAT decides to tighten to `^[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*(@[A-Za-z0-9_.\\-]+)?$`, that is a gap-closure follow-up. Defense-in-depth note: dead code today (zero call sites) AND the regex already stops every cmd-metacharacter byte class — even with the leading-anchor caveat, no injectable byte can reach the npx.cmd shim parser."
  - "ModalAuditEntry helper placement: extracted BuildEntry into an internal static class (`ModalAuditEntry.Build(ctx, outcome, alwaysAllow)`) co-located in NativeTools.cs so all three modal-bearing tools (RunCommandTool, ExecuteSkillTool, MaterializeSkillTool) emit byte-identical JSONL audit rows from a single source. The lower-friction choice per 03-PATTERNS.md §6 closing note — no separate file, no cross-file churn, but a single symbol so future schema changes touch one place."
metrics:
  duration: ~30min (4 atomic tasks, build × 4, all green after Task 3+4 land together)
  completed: 2026-06-02
  files_created: 0
  files_modified: 7
  files_deleted: 0
  loc_delta: "+13 (Context) +16/-6 (AlwaysAllowSession) +25 (XAML) +53 (xaml.cs) +189/-39 (NativeTools) +37/-1 (SkillService) +8 (CommandService) ≈ +295 net LOC"
  tasks_executed: 4
  tasks_total: 4
  build_result: "0 errors, 24 baseline warnings (unchanged — all in ChatWindow.xaml.cs / ContextSidebar.xaml.cs / OpenAIService.cs, pre-existing across Plans 01 + 02)"
requirements_addressed: [SEC-06]
---

# Phase 03 Plan 03: SEC-06 skill gating — modal preview + tuple AlwaysAllow + npx hardening Summary

**One-liner:** Extended CommandConfirmationContext with 3 D-08 fields (ScriptBody/Interpreter/ContentHash); lifted the Phase 1 modal-hop body into `CommandConfirmationWindow.ShowAsync` carrying a shared `_modalLock` so all three modal-bearing tools (run_command, execute_skill, materialize_skill) use one critical section; raised ExecuteSkillTool to L6 and MaterializeSkillTool to L8 with full script-body preview + SHA256-keyed AlwaysAllow; re-keyed AlwaysAllowSession to `HashSet<(Tool, Cmd, ContentHash?)>`; wired BuildEntry's content_hash to ctx.ContentHash; hardened SkillService.InstallFromOnlineAsync with the D-11 regex + npx.cmd→npx ExecuteWithArgListAsync fallback (CommandService rethrows `Win32Exception { NativeErrorCode: 2 }` so the fallback fires).

## Tasks Executed

| Task | Name                                                                                                                  | Commit  | Files                                                                                                                                  |
| ---- | --------------------------------------------------------------------------------------------------------------------- | ------- | -------------------------------------------------------------------------------------------------------------------------------------- |
| 1    | Extend CommandConfirmationContext + ScriptBody XAML row + static ShowAsync helper (D-08)                              | 099731d | AIBWindows/Services/CommandConfirmationContext.cs, AIBWindows/Views/CommandConfirmationWindow.xaml, .../CommandConfirmationWindow.xaml.cs |
| 2    | Re-key AlwaysAllowSession from HashSet<string> to HashSet<(Tool, Cmd, ContentHash?)> (D-10)                           | 3afe32b | AIBWindows/Services/AlwaysAllowSession.cs                                                                                              |
| 3    | RequiredLevel bumps + skill modal hop + tuple AlwaysAllow + content_hash wiring + _modalLock relocation (D-08, D-09, D-10) | 84acfc5 | AIBWindows/Services/NativeTools.cs                                                                                                     |
| 4    | InstallFromOnlineAsync regex + npx.cmd→npx fallback + CommandService rethrow guard (D-11)                             | 336eba9 | AIBWindows/Services/SkillService.cs, AIBWindows/Services/CommandService.cs                                                             |

## Method / Member Surface Map (post-execution HEAD)

### CommandConfirmationContext.cs — 9 init-only properties

| # | Property        | Type       | Origin   | Purpose                                                                |
| - | --------------- | ---------- | -------- | ---------------------------------------------------------------------- |
| 1 | Tool            | string     | Phase 1  | "run_command" / "execute_skill" / "materialize_skill"                  |
| 2 | Command         | string     | Phase 1  | Modal-displayed command line (or skill display string + dest path)     |
| 3 | Level           | int        | Phase 1  | User's current level for header rendering                              |
| 4 | Cwd             | string     | Phase 1  | Working directory shown in the modal                                   |
| 5 | DenylistHit     | bool       | Plan 02  | True when floor list would refuse post-modal (amber AVISO banner)      |
| 6 | DenylistReason  | string?    | Plan 02  | PT-BR reason for the AVISO banner                                      |
| 7 | ScriptBody      | string?    | Plan 03  | 50KB-capped script preview rendered in ScrollViewer (D-08)             |
| 8 | Interpreter     | string?    | Plan 03  | "python" / "powershell" / "cmd" — flips header to "CONFIRMAÇÃO DE SCRIPT" |
| 9 | ContentHash     | string?    | Plan 03  | SHA256 hex digest; null for run_command; tuple key element for AlwaysAllow (D-10) |

### AlwaysAllowSession.cs (43 lines)

| Member                                                                                | Visibility               | Notes                                                                                  |
| ------------------------------------------------------------------------------------- | ------------------------ | -------------------------------------------------------------------------------------- |
| `HashSet<(string Tool, string Cmd, string? ContentHash)> _allowed`                    | private static readonly  | Tuple key with built-in structural equality (ordinal string compare on each element)   |
| `Contains((string Tool, string Cmd, string? ContentHash) key)`                        | public static            | Lock-guarded; session-only; Phase 1 D2 lifetime preserved                              |
| `Add((string Tool, string Cmd, string? ContentHash) key)`                             | public static            | Same lock contract                                                                     |
| `Clear()`                                                                             | public static            | Cleared on app exit                                                                    |

### CommandConfirmationWindow.xaml.cs (97 lines)

| Member                                                                                                          | Visibility            | Notes                                                                                                                                          |
| --------------------------------------------------------------------------------------------------------------- | --------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------- |
| `IsAllowed`, `AlwaysAllow`                                                                                      | public                | Phase 1 result properties                                                                                                                      |
| `_modalLock` (SemaphoreSlim(1,1))                                                                               | private static readonly | D-08: lifted off RunCommandTool; one critical section across all three modal-bearing tools                                                     |
| `CommandConfirmationWindow(CommandConfirmationContext ctx)` ctor                                                | public                | Phase 1 4-field population + Plan 02 DenylistHit handler + Plan 03 ScriptBody+HeaderText handler                                               |
| `ShowAsync(CommandConfirmationContext ctx) -> Task<(bool Allowed, bool AlwaysAllow)>`                           | public static         | D-08: WaitAsync().ConfigureAwait(false) + Dispatcher.InvokeAsync hop + ShowDialog                                                              |
| `Allow_Click`, `Deny_Click`                                                                                     | private               | Phase 1 button handlers                                                                                                                        |

### NativeTools.cs RunCommandTool — Diff Summary

| Concern                                  | Pre-plan (Plan 02 exit)                                                                                                   | Post-plan                                                                                                                                                |
| ---------------------------------------- | ------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `_modalLock` field                       | `private static readonly SemaphoreSlim _modalLock = new(1, 1);` on RunCommandTool                                         | **Deleted.** Lives on CommandConfirmationWindow now; shared across the three modal-bearing tools.                                                       |
| Modal hop block                          | Inline `await _modalLock.WaitAsync` + `Dispatcher.InvokeAsync<(bool,bool)>(...)` body                                     | `var (allowed, alwaysAllow) = await CommandConfirmationWindow.ShowAsync(ctx);` (single line — semaphore + dispatcher hidden inside helper)              |
| AlwaysAllow key                          | `AlwaysAllowSession.Contains(command)` / `Add(command)` (string)                                                          | `var allowKey = (ctx.Tool, ctx.Command, (string?)null);` then `Contains(allowKey)` / `Add(allowKey)`                                                    |
| BuildEntry                               | Local `private static object BuildEntry(...)` with `content_hash = (string?)null` placeholder + TODO                      | Delegates to `ModalAuditEntry.Build(ctx, outcome, alwaysAllow)`; `content_hash = ctx.ContentHash` (null for run_command, hex for skill tools)           |

### NativeTools.cs ExecuteSkillTool — Diff Summary

| Concern                  | Pre-plan                                                                                                  | Post-plan                                                                                                                                                                                          |
| ------------------------ | --------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| RequiredLevel            | `1`                                                                                                       | `6` (D-09)                                                                                                                                                                                          |
| Body                     | Pass-through to `SkillService.RunSkillAsync(skillName, args)`                                             | Skill lookup → markdown bypass → ReadAndHashSkillFile → ctx build → tuple AlwaysAllow → No-UI guard → CommandConfirmationWindow.ShowAsync → outcome routing → `SkillService.RunSkillAsync(...)` on Allow |
| Helper                   | None                                                                                                      | `private static (string? Body, string? Hash) ReadAndHashSkillFile(string scriptPath)` — SHA256 over full bytes, 50KB-capped body                                                                    |
| Markdown handling        | Indirect (RunSkillAsync returns instructions string)                                                      | Explicit early return — bypasses the modal entirely (D-09)                                                                                                                                          |

### NativeTools.cs MaterializeSkillTool — Diff Summary

| Concern         | Pre-plan                                                                                       | Post-plan                                                                                                                                                                                         |
| --------------- | ---------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| RequiredLevel   | `5`                                                                                            | `8` (D-09)                                                                                                                                                                                         |
| Body            | Pass-through to `SkillService.MaterializeSkillAsync(name, content, interp)`                    | destPath compute → SHA256 over UTF-8 bytes of script_content → 50KB-capped body → ctx build (Command = `"{interp} -> {destPath}"` per RESEARCH Open Q#1) → tuple AlwaysAllow → No-UI guard → ShowAsync → outcome routing → `MaterializeSkillAsync` on Allow |

### NativeTools.cs Shared Helper (new)

| Member                                                                                                | Visibility               | Notes                                                                                  |
| ----------------------------------------------------------------------------------------------------- | ------------------------ | -------------------------------------------------------------------------------------- |
| `ModalAuditEntry.Build(CommandConfirmationContext ctx, string outcome, bool alwaysAllow) -> object`   | internal static          | Single source of truth for the JSONL shape (8 fields incl. `content_hash`). Used by all three modal-bearing tools. |

### SkillService.cs InstallFromOnlineAsync — Diff Summary

| Concern              | Pre-plan                                                                                              | Post-plan                                                                                                                                                                |
| -------------------- | ----------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Imports              | `System.Collections.Generic`, `System.IO`, `System.Linq`, `System.Text.Json`, `System.Threading.Tasks` | + `System.Text.RegularExpressions`                                                                                                                                       |
| installArg gate      | None — installArg flowed directly into the cmd /c interpolation                                       | `Regex.IsMatch(installArg, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(@[A-Za-z0-9_.\-]+)?$")` BEFORE any shell-out; returns PT-BR error on mismatch                              |
| npx invocation       | `await CommandService.ExecuteAsync($"cmd /c call npx -y skills add {installArg} --yes", ...)`         | `ExecuteWithArgListAsync("npx.cmd", [-y, skills, add, installArg, --yes], workPath, 900_000)` with `catch (Win32Exception { NativeErrorCode: 2 })` → fallback to `"npx"` |
| XML doc              | None                                                                                                  | Documents dead-code-today status + the requirement that future callers gate behind RequiredLevel >= 7 + ShowAsync                                                       |

### CommandService.cs ExecuteWithArgListAsync — Diff Summary

| Concern             | Pre-plan                                                                                              | Post-plan                                                                                                                                                                |
| ------------------- | ----------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Catch block         | Single `catch (Exception ex) { return $"ERRO ao executar comando: {ex.Message}"; }`                   | Adds a narrow `catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2) { throw; }` BEFORE the generic catch; every other error path is unchanged |
| Behaviour           | All exceptions converted to PT-BR string                                                              | `Win32Exception { NativeErrorCode: 2 }` (ERROR_FILE_NOT_FOUND) propagates so SkillService.InstallFromOnlineAsync can do npx.cmd → npx fallback                          |

## Build Result

| Step                                                                                  | Result                                                                              |
| ------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------- |
| `dotnet build AIBWindows/AIB.csproj` after Task 1                                     | 0 errors, 0 warnings (incremental snapshot — same baseline cleared post Plan 02)    |
| `dotnet build AIBWindows/AIB.csproj` between Task 2 and Task 3                        | **Intentionally broken** by the AlwaysAllowSession tuple migration — locked by design (Task 2 acceptance note) |
| `dotnet build AIBWindows/AIB.csproj` after Task 3                                     | 0 errors, 24 baseline warnings (all pre-existing — ChatWindow / ContextSidebar / OpenAIService) |
| `dotnet build AIBWindows/AIB.csproj` after Task 4                                     | 0 errors, 24 baseline warnings (unchanged)                                          |

The 24 baseline warnings are exclusively in `ChatWindow.xaml.cs`, `ContextSidebar.xaml.cs`, and `OpenAIService.cs` — same set Plan 01 and Plan 02 observed at their exit. No new warnings introduced by this plan.

## SEC-06 Contract Status

| Surface / Threat                                                | Pre-plan                                                            | Post-plan                                                                                                                                                                                          |
| --------------------------------------------------------------- | ------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **ExecuteSkillTool elevation**                                  | RequiredLevel = 1; any prompt-injection could trigger silently      | RequiredLevel = 6 (T-3-Skill-EoP **mitigate**); ToolRegistry emits PT-BR "permissão negada" string at L<6 — execute_skill body never invoked.                                                       |
| **MaterializeSkillTool elevation**                              | RequiredLevel = 5                                                   | RequiredLevel = 8 (T-3-Skill-EoP **mitigate**); writing arbitrary LLM-supplied script to disk demands explicit human approval.                                                                     |
| **Materialize tampering (LLM-controlled script body to disk)**  | No preview; user could not inspect what would be written            | T-3-Materialize-Tampering **mitigate**: modal renders the 50KB-capped script_content as ScriptBody; ctx.Command surfaces destination path so the user sees BOTH the content AND the write target. |
| **Silent skill edit on disk between executions**                | AlwaysAllow keyed by raw command string — disk edits were invisible | T-3-Silent-Edit **mitigate**: SHA256 over FULL file bytes feeds the tuple AlwaysAllow key; any byte change invalidates the cache and re-fires the modal with the new body.                          |
| **npm/npx installArg path traversal / shell injection**         | Raw interpolation into cmd /c call — every metacharacter parseable  | T-3-InstallArg-Traversal **mitigate**: D-11 regex `^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(@[A-Za-z0-9_.\-]+)?$` runs BEFORE the shell-out; leading-anchor `..foo` caveat surfaced in must_haves.truths (see Decision §). |
| **npx shim PATHEXT not honored by CreateProcess**               | N/A (used cmd /c which does honor PATHEXT, but that was the vector) | T-3-NpxShim-PATHEXT **mitigate**: ArgumentList path tries `npx.cmd` first; `Win32Exception { NativeErrorCode: 2 }` triggers fallback to `npx`. CommandService rethrows that exception unwrapped for the SkillService catch. |
| **Audit content_hash field populated**                          | Always `null` (Plan 02 placeholder)                                 | run_command emits `"content_hash":null`; execute_skill / materialize_skill emit 64-char hex; ModalAuditEntry helper is the single source of truth.                                                  |
| **Modal-first invariant + Phase 1 D2 AlwaysAllow lifetime**     | Both preserved by Plan 02                                           | Both preserved here. AlwaysAllowSession is still lock-guarded, session-only, cleared on app exit (re-keying did not change lifetime semantics).                                                    |

UAT-level behavior verification (RESEARCH §"Validation Architecture" rows 6-12: execute_skill L1 → "permissão negada"; execute_skill L6 → modal with script body; materialize_skill L7 → denial / L8 → modal; InstallFromOnlineAsync rejects `..badrepo` and accepts `owner/repo@1.0.0`; edit-in-place skill detection re-fires modal; audit `content_hash` field shape) is codified in Plan 04's `03-VERIFICATION.md`. This plan ships the contract.

## Decisions Made

- **D-08 implementation:** All three new init-only props on `CommandConfirmationContext` are nullable string-or-int — null defaults preserve the Phase 1 ctor compatibility (RunCommandTool builds the context with the Plan 02 6 fields populated and the Plan 03 3 fields omitted, which evaluate to null/false and keep the existing render path). The XAML `ScriptBodyLabel` + `ScriptBodyScroller` are both `Visibility="Collapsed"` in the XAML; only the new ctor handler toggles them to Visible. `HeaderText` x:Name added to the existing TextBlock — text rewrite only fires when ScriptBody is non-empty. Window root `Height="380"` → `MinHeight="380" SizeToContent="Height"` (Width="450" unchanged) so script previews auto-grow. The static `ShowAsync` helper carries the lifted `_modalLock` SemaphoreSlim — single critical section for all three modal-bearing tools.
- **D-09 implementation:** `ExecuteSkillTool.RequiredLevel = 6`; markdown skills (interpreter == "markdown") bypass the modal via early return (`SkillService.RunSkillAsync` returns the SKILL.md content as instructions — no process spawn, no script preview needed). Non-markdown skills route through the new `ReadAndHashSkillFile` private helper that reads the script bytes, hashes the FULL file via `SHA256.HashData(bytes)`, decodes a 50KB-capped UTF-8 preview with the `[... SCRIPT TRUNCATED]` marker. `MaterializeSkillTool.RequiredLevel = 8`; SHA256 is computed over UTF-8 bytes of the LLM-supplied script_content directly (no disk read — the file is about to be created). ctx.Command for materialize_skill surfaces `"{interpreter} -> {destPath}"` so the modal's "A AIB solicitou a execução do seguinte comando" block shows the destination path (closes RESEARCH Open Question #1). Both skill tools share the same ctx-build → AlwaysAllow → No-UI guard → ShowAsync → outcome routing pattern as RunCommandTool.
- **D-10 implementation:** Tuple shape `(string Tool, string Cmd, string? ContentHash)` — Tool is the tool name string ("run_command" / "execute_skill" / "materialize_skill"), Cmd is the exact command text or skill display string shown in the modal, ContentHash is null for run_command and the SHA256 hex digest for skill tools. C# ValueTuple inference required the explicit `(string?)null` cast in RunCommandTool's `allowKey` (null literal alone is ambiguous on the overload resolution path). All three NativeTools.cs call sites updated atomically in Task 3 (the compile-time break between Task 2 and Task 3 was the locked path per the plan's Task 2/3 sequencing).
- **D-11 implementation:** Regex `^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(@[A-Za-z0-9_.\-]+)?$` runs BEFORE any shell-out via `Regex.IsMatch(installArg, ...)`. The npx invocation switched from `cmd /c call npx -y skills add {installArg} --yes` to `ExecuteWithArgListAsync("npx.cmd", new[] { "-y", "skills", "add", installArg, "--yes" }, workPath, 900_000)`; on `Win32Exception { NativeErrorCode: 2 }` (ERROR_FILE_NOT_FOUND — CreateProcess does not honor PATHEXT for the bare "npx" name on Windows) the fallback re-invokes with `"npx"`. The CommandService rethrow guard was added inside `ExecuteWithArgListAsync` to surface the exception unwrapped — without it, the generic catch would translate the exception to the "ERRO ao executar comando" return string and the fallback would never fire.
- **D-11 LOCKED PATCH (CommandService rethrow):** The narrow `catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2) { throw; }` is placed BEFORE the existing generic `catch (Exception ex) { return ... }` so it wins on type+filter match. Every other Win32Exception (any NativeErrorCode != 2) AND every non-Win32Exception still hits the generic catch and returns the locked PT-BR error string — the public contract is preserved for all other error paths. Locked Option A; Option B (string inspection of the returned error) was explicitly ruled out.
- **D-11 leading-anchor caveat (shipped as written):** Locked regex allows `..foo/bar` because `.` is in the leading character class. Surfaced in must_haves.truths for plan-checker; CONTEXT.md D-11 quoted the regex verbatim; RESEARCH.md §"Security Domain" flagged it as a residual not a blocker. Defense-in-depth note: dead code today (zero call sites) AND the regex already stops every cmd-metacharacter byte class — even with the leading-anchor caveat, no injectable byte can reach the npx.cmd shim parser. If Plan 04 / UAT decides to tighten to `^[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*(@[A-Za-z0-9_.\-]+)?$`, that's a gap-closure follow-up.
- **ModalAuditEntry placement (lower-friction option chosen per 03-PATTERNS.md §6 closing note):** Extracted BuildEntry into an `internal static class ModalAuditEntry` co-located in `NativeTools.cs` rather than a separate file. All three modal-bearing tools call `ModalAuditEntry.Build(ctx, outcome, alwaysAllow)` and emit byte-identical JSONL audit rows. RunCommandTool's local `BuildEntry(...)` wrapper still exists as a thin delegating method (preserves the prior callers' indentation; trivial single-line `=> ModalAuditEntry.Build(ctx, outcome, alwaysAllow);`).

## Deviations from Plan

None — the plan executed exactly as written. All four tasks landed at their first attempt with every source acceptance criterion green and `dotnet build` reporting 0 errors / 24 baseline warnings (unchanged) after Tasks 3 + 4 land together (Task 2 was intentionally compile-broken between commits per the plan's locked sequencing).

One micro-correction during Task 1: `Application` was ambiguous between `System.Windows.Application` (WPF) and `System.Windows.Forms.Application` (UseWindowsForms is enabled in `AIB.csproj`). Fully-qualified `System.Windows.Application.Current` inside `ShowAsync` (mirrors how RunCommandTool's original modal-hop block referenced it). Not a deviation — disambiguation required by the project's csproj configuration.

## Known Stubs

None introduced. The Plan 02 `content_hash = (string?)null` TODO placeholder is now resolved (wired to `ctx.ContentHash` via `ModalAuditEntry.Build`); audit JSONL emits `"content_hash":null` for run_command (correct value — run_command has no script body) and the hex digest for skill tools.

## Threat Flags

None — no new security-relevant surface introduced beyond what the plan's `<threat_model>` already disposes. All seven threats in the register have a `mitigate` or explicitly-justified `accept` disposition that matches the shipped code:

- T-3-Skill-EoP **mitigate** — RequiredLevel bumps verified (grep counts).
- T-3-Materialize-Tampering **mitigate** — modal renders ScriptBody + Command surfaces destPath.
- T-3-Silent-Edit **mitigate** — SHA256 over FULL bytes (Pattern 4); tuple key invalidates AlwaysAllow on edit.
- T-3-Modal-Helper-Deadlock **accept** — `WaitAsync().ConfigureAwait(false)` preserved (Pitfall 6); No-UI guard short-circuits when `Application.Current == null`.
- T-3-InstallArg-Traversal **mitigate** — D-11 regex BEFORE any shell-out; leading-anchor caveat surfaced.
- T-3-NpxShim-PATHEXT **mitigate** — npx.cmd → npx fallback via narrow Win32Exception rethrow.
- T-3-ScreenRecording-Leak **accept** — script body on screen is the entire UX point; audit log never stores body, only the SHA256.
- T-3-AlwaysAllow-Persistence-Leak **accept** — session-only HashSet (Phase 1 D2 lifetime preserved).
- T-3-SC **accept** — no package installs (BCL-only: System.Security.Cryptography, System.Text.RegularExpressions, System.ComponentModel.Win32Exception).

## TDD Gate Compliance

Plan frontmatter declares `type: execute` (not `tdd`); no RED/GREEN/REFACTOR gate sequence required. Each task carries its own automated `<verify>` (`dotnet build`) which passed at the first commit attempt for Tasks 1 + 3 + 4. Task 2 source assertions (positive tuple shape + negative old-string API) passed at commit; the build was intentionally compile-broken between Task 2 and Task 3 by design (locked sequencing per the plan).

## Downstream Hooks (Plan 04 — Wave 4)

- **RequiredLevel = 6 / 8 for skill tools** — Plan 04 UAT scenarios validate the level firewall: a run at L<6 against execute_skill returns the standard `"ACESSO NEGADO: A ferramenta 'execute_skill' exige Nível 6, mas o seu nível atual é {x}."` string from ToolRegistry; a run at L>=6 displays the modal with script body preview.
- **`CommandConfirmationWindow.ShowAsync(ctx)` helper** — UAT scenarios for execute_skill / materialize_skill assert that the modal renders the ScrollViewer row with Consolas font and the SCRIPT header label flip.
- **AlwaysAllowSession tuple key** — UAT scenario for silent-edit defense: approve a skill with body X, edit body X → Y on disk, re-invoke the skill; the modal must re-fire (tuple miss on ContentHash).
- **Audit `content_hash` field shape** — UAT scenario `head -1 ~/.AIB/logs/audit.log | jq .content_hash`: emits `null` for run_command rows and a 64-char hex string for execute_skill / materialize_skill rows.
- **`_test_echo_args` skill provisioning** — uses the new SkillService.RunSkillAsync (Plan 01 ArgumentList path) + the new RequiredLevel = 6 (Plan 03 D-09); UAT scenario validates that `"; rm -rf %USERPROFILE%\Documents"` reaches the script as one literal argv element AND the modal fires with the script body visible.
- **`SkillService.InstallFromOnlineAsync` regex acceptance** — UAT scenario for SEC-06: provision a skill via the regex-validated path with `owner/repo@1.0.0` (should pass the regex; npx.cmd → npx fallback exercised); attempt with `..badrepo` (should hit the PT-BR "formato de skill inválido" return). The leading-anchor caveat (regex allows `..foo/bar`) is documented for plan-checker; tightening is a Plan 04 follow-up if desired.

## Self-Check: PASSED

- `AIBWindows/Services/CommandConfirmationContext.cs` — FOUND (45 lines, 9 init-only properties: 4 Phase 1 + 2 Plan 02 + 3 Plan 03)
- `AIBWindows/Services/AlwaysAllowSession.cs` — FOUND (43 lines, tuple-keyed; HashSet<string> shape: 0 hits; tuple shape: 1 hit; lock-guarded Contains/Add/Clear: 3 hits)
- `AIBWindows/Views/CommandConfirmationWindow.xaml` — FOUND (100 lines; ScriptBodyScroller / ScriptBodyText / ScriptBodyLabel / HeaderText all named; `MinHeight="380" SizeToContent="Height"` set)
- `AIBWindows/Views/CommandConfirmationWindow.xaml.cs` — FOUND (97 lines; static `_modalLock` field; static `ShowAsync(ctx)` helper; `WaitAsync().ConfigureAwait(false)` preserved; "CONFIRMAÇÃO DE SCRIPT" header flip; ctor handles ScriptBody)
- `AIBWindows/Services/NativeTools.cs` — FOUND (1156 lines; RunCommandTool has no local `_modalLock`; ShowAsync(ctx) called 3 times; RequiredLevel = 6 once + 8 once; markdown bypass; SHA256.HashData × 2; Convert.ToHexString × 2; `[... SCRIPT TRUNCATED]` × 2; `content_hash = ctx.ContentHash` × 1; allowKey usage × 3 for Contains + × 3 for Add; old string-keyed AlwaysAllow callers: 0 hits; `ModalAuditEntry` helper class present)
- `AIBWindows/Services/SkillService.cs` — FOUND (292 lines; `using System.Text.RegularExpressions;`; `Regex.IsMatch(installArg, ...)` × 1; D-11 regex verbatim × 1; `Erro: formato de skill inválido` × 1; `ExecuteWithArgListAsync("npx.cmd", ...)` + `ExecuteWithArgListAsync("npx", ...)` both present; `Win32Exception ex) when (ex.NativeErrorCode == 2)` × 1; `cmd /c call npx` × 0 — old shell-out gone)
- `AIBWindows/Services/CommandService.cs` — FOUND (131 lines; `Win32Exception ex) when (ex.NativeErrorCode == 2)` × 1 with `throw;` body — locked Option A patch in place)
- Commit `099731d` — FOUND in `git log` (Task 1, D-08 context + XAML + ShowAsync helper)
- Commit `3afe32b` — FOUND in `git log` (Task 2, D-10 tuple AlwaysAllow re-key)
- Commit `84acfc5` — FOUND in `git log` (Task 3, D-08 + D-09 + D-10 NativeTools surgery)
- Commit `336eba9` — FOUND in `git log` (Task 4, D-11 SkillService + CommandService hardening)
- Build gate — 0 errors / 24 baseline warnings (unchanged from Plans 01 + 02 exit state) after Task 4 commit
- Audit outcome set — 5 distinct strings (`allow`, `always_allow`, `deny`, `deny_no_ui`, `allow_then_floor_deny`) — strict superset of Phase 1; skill tools emit the same Plan 02 set, no skill-specific outcome strings added (per Decision §)
