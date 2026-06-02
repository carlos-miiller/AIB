---
phase: 03-tool-argument-hardening-quoting-denylist-skill-gating
plan: 01
subsystem: command-execution
tags: [security, sec-04, argv-hardening, refactor, dead-code-removal]
requires:
  - SEC-04 (REQUIREMENTS.md): tool args must round-trip through ProcessStartInfo.ArgumentList for tool-internal execs
  - Phase 1 D5 / D8: post-modal carve-out for run_command preserves cmd.exe /c shape
provides:
  - "CommandService.ExecuteWithArgListAsync(string, IEnumerable<string>, string?, int) — ArgumentList primitive"
  - "CommandService.RunProcessAsync(Process, int) shared read-pump / timeout / truncation helper (private)"
  - "SkillService.InterpreterMap static dictionary — single source of truth for interpreter dispatch (D-07)"
  - "SkillService.RunSkillAsync hardened: LLM-supplied arguments reach the script as a single literal argv element (D-06)"
affects:
  - AIBWindows/Services/CommandService.cs (refactored: extract helper + add new public method)
  - AIBWindows/Services/SkillService.cs (refactored: add InterpreterMap, rewrite RunSkillAsync)
  - AIBWindows/Services/DynamicSkillTool.cs (deleted)
tech-stack:
  added: []                # no new packages; BCL-only
  patterns:
    - "ProcessStartInfo.ArgumentList for argv hardening (CERT VU#123335 / BatBadBut — Microsoft 'Not Affected' for direct-exe invocations via PasteArguments.EncodeArgumentForCommandLine)"
    - "Static Dictionary as dispatch table replacing switch expression (D-07)"
key-files:
  created: []
  modified:
    - AIBWindows/Services/CommandService.cs
    - AIBWindows/Services/SkillService.cs
  deleted:
    - AIBWindows/Services/DynamicSkillTool.cs   # blob 8693e6cec49599830e5b06c31b76fd84bb023357, -114 LOC
decisions:
  - D-05 (Phase 3): CommandService.ExecuteWithArgListAsync(fileName, IEnumerable<string> args, workDir?, timeoutMs=20000) populates ProcessStartInfo.ArgumentList; UseShellExecute=false, CreateNoWindow=true, StandardOutputEncoding=UTF8. Shared private RunProcessAsync helper holds the read-pump + timeout-kill + 50KB truncation. ExecuteAsync(string) still uses cmd.exe /c {command} (SEC-04 carve-out).
  - D-06 (Phase 3): SkillService.RunSkillAsync passes the LLM-supplied arguments string as a SINGLE argv element after interpreter switches; no string interpolation into a shell command. Defensive \n / \r / \0 trim documented inline as cosmetic.
  - D-07 (Phase 3): SkillService exposes static readonly Dictionary<string,(string FileName,string[] Switches)> InterpreterMap, case-insensitive (StringComparer.OrdinalIgnoreCase), keys python / powershell / cmd. RunSkillAsync looks up via TryGetValue; markdown skills still bypass execution; unknown interpreter returns the locked PT-BR error string.
  - D-12 (Phase 3): AIBWindows/Services/DynamicSkillTool.cs is deleted from the repo. Zero source-tree references; ToolRegistry already lazy-loads via execute_skill.
metrics:
  duration: ~15min (3 atomic tasks, build × 3, clean + rebuild × 1)
  completed: 2026-06-02
  files_modified: 2
  files_deleted: 1
  loc_delta: +84 / -34 (CommandService) + +24 / -10 (SkillService) + -114 (DynamicSkillTool) = ≈ -50 net LOC
  tasks_executed: 3
  tasks_total: 3
  build_result: 0 errors, 0 new warnings (24 baseline warnings unchanged — all in ChatWindow.xaml.cs, pre-existing)
requirements_addressed: [SEC-04]
---

# Phase 03 Plan 01: Tool argument hardening — ArgumentList foundation Summary

**One-liner:** Extracted CommandService.RunProcessAsync as a shared helper, added ExecuteWithArgListAsync (ProcessStartInfo.ArgumentList path), rewrote SkillService.RunSkillAsync to consume a static InterpreterMap + the new ArgumentList method, deleted the dead DynamicSkillTool.cs — every tool-internal skill exec path now passes LLM-supplied arguments as a single literal argv element (no cmd.exe metacharacter parsing).

## Tasks Executed

| Task | Name                                                                 | Commit  | Files                                    |
| ---- | -------------------------------------------------------------------- | ------- | ---------------------------------------- |
| 1    | Extract RunProcessAsync + add ExecuteWithArgListAsync (D-05)         | 634f4a8 | AIBWindows/Services/CommandService.cs    |
| 2    | InterpreterMap + RunSkillAsync rewrite (D-06, D-07)                  | 4b6f804 | AIBWindows/Services/SkillService.cs      |
| 3    | Delete DynamicSkillTool.cs — zero call sites (D-12)                  | f2b7b93 | AIBWindows/Services/DynamicSkillTool.cs  |

## Method Surface — Exact Line Ranges (post-execution HEAD)

### AIBWindows/Services/CommandService.cs (122 lines)

| Method                                                               | Visibility | Line Range | Behavior                                                                                              |
| -------------------------------------------------------------------- | ---------- | ---------- | ----------------------------------------------------------------------------------------------------- |
| `ExecuteAsync(string command, string? workDir, int timeoutMs)`       | public     | **17-40**  | cmd.exe /c carve-out preserved (SEC-04). Builds ProcessStartInfo, delegates to RunProcessAsync.       |
| `ExecuteWithArgListAsync(string fileName, IEnumerable<string> args, string? workDir, int timeoutMs)` | public | **49-73** | NEW. ProcessStartInfo.ArgumentList path. No cmd.exe in chain. UseShellExecute=false (required).        |
| `RunProcessAsync(Process process, int timeoutMs)`                    | private    | **80-122** | NEW shared helper. Read-pump + timeout-kill + 50KB truncation. Caller owns `using var process`.       |

Both public methods stay under ~50 lines (24 + 25). The read-pump body is not duplicated.

### AIBWindows/Services/SkillService.cs (242 lines)

| Member                  | Visibility    | Line Range | Notes                                                                                                  |
| ----------------------- | ------------- | ---------- | ------------------------------------------------------------------------------------------------------ |
| `InterpreterMap`        | private static readonly | **30-35** | Case-insensitive dictionary. python → (py.exe, []), powershell → (powershell.exe, [-NoProfile, -ExecutionPolicy, Bypass, -File]), cmd → (cmd.exe, [/c]). |
| `RunSkillAsync(string name, string arguments)` | public static async | **199-219** | TryGetValue lookup; defensive \n/\r/\0 trim (cosmetic per inline comment); calls ExecuteWithArgListAsync with [...Switches, scriptPath, safeArgs]. |
| `InstallFromOnlineAsync` | public static async | 147-177 | **Intentionally untouched**. D-11 hardening owned by Plan 03 (cross-plan churn avoidance — depends on CommandConfirmationContext additions). |

### AIBWindows/Services/DynamicSkillTool.cs (deleted)

- Pre-delete blob SHA: `8693e6cec49599830e5b06c31b76fd84bb023357`
- Diff stat: `1 file changed, 114 deletions(-)`
- Reason: zero source-tree call sites (verified pre-delete via `grep -rn DynamicSkillTool AIBWindows/`); the class duplicated the unsafe `py "{scriptPath}" {args}` / `powershell ... {args}` interpolation pattern Task 2 eliminated. Leaving it on disk would have left a future caller a footgun.
- AIB.csproj: SDK-style with implicit `<Compile Include="**/*.cs"/>` globbing; no project-file edit required (verified pre-delete: 0 explicit `<Compile Include="...DynamicSkillTool...">` entries).

## Build Result

| Step                                                                 | Result                                                  |
| -------------------------------------------------------------------- | ------------------------------------------------------- |
| `dotnet build AIBWindows/AIB.csproj` after Task 1                    | 0 errors, 24 warnings (baseline — all in ChatWindow)    |
| `dotnet build AIBWindows/AIB.csproj` after Task 2                    | 0 errors, 24 warnings (no new warnings introduced)      |
| `dotnet clean` + `dotnet build AIBWindows/AIB.csproj` after Task 3   | 0 errors, 24 warnings (no new warnings introduced)      |

All 24 baseline warnings live in `AIBWindows/ChatWindow.xaml.cs` (pre-existing, unrelated to this plan).

## SEC-04 Contract Status

| Surface                                  | Pre-plan                                                      | Post-plan                                                                                              |
| ---------------------------------------- | ------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------ |
| **execute_skill → RunSkillAsync**        | `cmd.exe /c py "{scriptPath}" {LLM-args}` — metachar exposed  | `py.exe` + ArgumentList=[scriptPath, LLM-args] — single literal argv element. **HARDENED (D-06).**     |
| **DynamicSkillTool (parallel surface)**  | Same unsafe interpolation duplicated                          | **Deleted (D-12).**                                                                                    |
| **run_command (post-modal)**             | `cmd.exe /c {command}` — human-approved                       | Unchanged byte-for-byte. SEC-04 carve-out (Phase 1 D5 / D8 invariant). **PRESERVED.**                  |
| **InstallFromOnlineAsync (npx)**         | `cmd /c call npx -y skills add {installArg} --yes`            | Unchanged. **Plan 03 owns D-11 hardening.**                                                            |

UAT-level argv injection verification (`"; rm -rf %USERPROFILE%\Documents"` reaches the script as one literal argv element) is recorded against Plan 04's `_test_echo_args` skill provisioning. This plan ships the contract; UAT closes the loop.

## Decisions Made

- **D-05 implementation:** ExecuteAsync(string) keeps `FileName = "cmd.exe"`, `Arguments = "/c {command}"` (verified by grep — 1 hit each, only inside ExecuteAsync). ExecuteWithArgListAsync uses the fileName parameter directly (no /c, no cmd.exe). Both public methods set `UseShellExecute = false` (verified — 2 hits). Read-pump body exists exactly once in the file (verified — `[TIMEOUT] O comando demorou mais de` appears 1×; `[AVISO: Saída muito longa, truncada...]` appears 1×).
- **D-06 implementation:** RunSkillAsync now builds `args = [...pair.Switches, skill.ScriptFile, safeArgs]` and hands them to `ExecuteWithArgListAsync`. `safeArgs` strips `\n`, `\r`, AND `\0` (added per D-06; previous code only stripped `\n` / `\r`). Old switch expression deleted (verified — 0 `skill.Interpreter.ToLower() switch` hits; 0 `py "{scriptPath}"` interpolation hits).
- **D-07 implementation:** `InterpreterMap` dictionary uses `StringComparer.OrdinalIgnoreCase` for case-insensitive lookup. Unknown-interpreter path returns the locked PT-BR string `"Erro: interpretador '{x}' não suportado."` (verified — exactly 1 hit) without spawning a process. Markdown skills still short-circuit before InterpreterMap lookup (verbatim preservation of the existing branch).
- **D-12 implementation:** `git rm` (no csproj edit needed per SDK-style globbing). Post-delete: source-tree text grep for `DynamicSkillTool` returns 0 hits; `dotnet clean` + `dotnet build` is 0/0.

## Deviations from Plan

None — the plan executed exactly as written. All three tasks landed at their first attempt with all grep acceptance criteria green and build 0/0 after each.

## Known Stubs

None introduced.

## Threat Flags

None — no new security-relevant surface introduced beyond what the threat model already disposes (T-3-Argv-Injection mitigated, T-3-Parallel-Argv-Surface mitigated, T-3-CmdExe-Carveout-Regression accept preserved, T-3-SC accept — BCL-only).

## TDD Gate Compliance

Plan frontmatter does not declare `type: tdd`; no RED/GREEN/REFACTOR gate sequence required. Each task carries its own automated `<verify>` (dotnet build) which passed at the first commit attempt.

## Downstream Hooks

- **Plan 02** depends on `CommandService.ExecuteWithArgListAsync` being callable — surface is now present at line 49 of `AIBWindows/Services/CommandService.cs`.
- **Plan 03** depends on `CommandService.ExecuteWithArgListAsync` AND `SkillService.InterpreterMap` — both present; Plan 03 will additionally rewrite `InstallFromOnlineAsync` (D-11 regex + npx ArgumentList) and harden the modal context.

## Self-Check: PASSED

- `AIBWindows/Services/CommandService.cs` — FOUND (modified, 122 lines, 3 methods)
- `AIBWindows/Services/SkillService.cs` — FOUND (modified, 242 lines, InterpreterMap at 30-35, RunSkillAsync at 199-219)
- `AIBWindows/Services/DynamicSkillTool.cs` — INTENTIONALLY ABSENT (git rm in commit f2b7b93)
- Commit `634f4a8` — FOUND in git log (Task 1, D-05)
- Commit `4b6f804` — FOUND in git log (Task 2, D-06 + D-07)
- Commit `f2b7b93` — FOUND in git log (Task 3, D-12)
- Build gate — 0 errors / 0 new warnings after each task
