# Phase 3: Tool argument hardening (quoting + denylist + skill gating) — Research

**Researched:** 2026-05-31
**Domain:** .NET 8 process invocation safety on Windows + WPF dialog reuse + PowerShell denylist normalization
**Confidence:** HIGH (CONTEXT.md locks 12 decisions; remaining unknowns isolate to UI polish and Portuguese phrasing)

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions

> Verbatim from `03-CONTEXT.md`. Planner MUST honor these; no alternatives may be researched or planned.

- **D-01 — Hybrid floor + modal-only.** Delete `ContainsWord` and the multi-table `ApplyDenylist` (`NativeTools.cs:413-448`). Replace with a single small **floor list** that refuses ONLY at `userLevel < 7`. At `userLevel ≥ 7` the modal is the sole authority (per Phase 1 D5 — modal always fires regardless). The Phase 1 `ConfirmDangerousCommands` setting still gates whether the floor list runs at all (default ON = floor active; OFF = modal-only at every level).
- **D-02 — Floor list contents.** Four categories (recursive deletion, format/partition, shutdown/reboot/logoff, registry destructive ops). Lives in `AIBWindows/Services/CommandFloorList.cs` as a `static readonly` array of `(Regex Pattern, string Category, string Reason)`. Reason strings PT-BR.
- **D-03 — Matching algorithm: normalize-then-regex.** Pipeline: lowercase → strip `"x" + "y"` / `'x' + 'y'` concat → expand `mv → Move-Item`, `ri → Remove-Item`, `ni → New-Item`, `sc → Set-Content`, `ac → Add-Content`, `gci → Get-ChildItem` → detect `-EncodedCommand` (any case) and refuse at L<7 with the dedicated reason → run each floor regex with `\b` word boundaries, first match wins.
- **D-04 — Modal fires first; floor refuses after Allow at L<7.** `CommandConfirmationContext` gains `DenylistHit` (bool, default false) and `DenylistReason` (string?). Modal shows amber `AVISO:` banner under `CommandText` when `DenylistHit == true`. Audit log gains new outcome `"allow_then_floor_deny"`.
- **D-05 — `CommandService.ExecuteWithArgListAsync`.** New signature: `Task<string> ExecuteWithArgListAsync(string fileName, IEnumerable<string> args, string? workDir = null, int timeoutMs = 20000)`. Builds `ProcessStartInfo` with `UseShellExecute = false`, `RedirectStandardOutput/Error = true`, `CreateNoWindow = true`, `StandardOutputEncoding = Encoding.UTF8`, populates `psi.ArgumentList`. Extract shared `RunProcessAsync(Process)` helper from existing `ExecuteAsync`. `ExecuteAsync(string)` stays unchanged for the `run_command` post-modal path.
- **D-06 — LLM `arguments` string passes as a SINGLE argument.** `SkillService.RunSkillAsync` builds `ArgumentList = [scriptPath, argumentsString]` (after interpreter switches). Scripts parse their own `sys.argv[1]` / `$args[0]`. The `safeArgs.Replace("\n", " ").Replace("\r", "")` scrub remains as a defensive `\0`-style trim, documented inline.
- **D-07 — Interpreter mapping table.** `SkillService` exposes a static `Dictionary<string, (string FileName, string[] Switches)> InterpreterMap` (case-insensitive). Keys: `python` → `py.exe` + `[]`; `powershell` → `powershell.exe` + `[-NoProfile, -ExecutionPolicy, Bypass, -File]`; `cmd` → `cmd.exe` + `[/c]`. Unknown interpreter → `"Erro: interpretador '{x}' não suportado."`. Final ArgumentList = `Switches.Concat([scriptPath, argumentsString])`.
- **D-08 — Modal extends in place.** `CommandConfirmationContext` gains `ScriptBody`, `Interpreter`, `ContentHash` (all optional). XAML grows one `<RowDefinition Height="Auto"/>` containing `<ScrollViewer MaxHeight="200" VerticalScrollBarVisibility="Auto">` wrapping a read-only `<TextBox FontFamily="Consolas" FontSize="12" IsReadOnly="True" AcceptsReturn="True" TextWrapping="NoWrap"/>`. Row Visibility binds to `ctx.ScriptBody != null`. Header flips to `"CONFIRMAÇÃO DE SCRIPT ({Interpreter})"` when ScriptBody present. New shared helper `CommandConfirmationWindow.ShowAsync(ctx)` returning `(bool allowed, bool alwaysAllow)`.
- **D-09 — `execute_skill` modal cached by (skillName + content SHA256).** Read file (cap 50KB; tail-truncate with `"\n\n[... SCRIPT TRUNCATED]"`), compute SHA256, populate `ctx.ScriptBody` + `ctx.ContentHash`. Markdown-only skills (`Interpreter == "markdown"`) skip modal. Hash mismatch on next call re-fires the modal.
- **D-10 — `AlwaysAllowSession` re-keyed by tuple.** New: `HashSet<(string Tool, string Cmd, string? ContentHash)>`. Same session-only lifetime, lock-guarded, exact-match. AuditLog gains optional `content_hash` field (null for run_command).
- **D-11 — `InstallFromOnlineAsync` hardened.** Insert at line 154 BEFORE any shell-out: `if (!Regex.IsMatch(installArg, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(@[A-Za-z0-9_.\-]+)?$")) return "Erro: formato de skill inválido (esperado: owner/repo[@version]).";`. Replace `cmd /c call npx ...` with `ExecuteWithArgListAsync("npx", new[] { "-y", "skills", "add", installArg, "--yes" }, workPath, 900000)`. XML doc comment: caller must gate behind `RequiredLevel ≥ 7` + modal flow.
- **D-12 — Delete `AIBWindows/Services/DynamicSkillTool.cs` entirely.** Zero `ToolRegistry` registration (verified — `ToolRegistry.cs:65-81` lists 12 tools; `DynamicSkillTool` is absent; comment at `:9-12` explicitly states "skills dinâmicas não são mais registradas aqui individualmente"). csproj is SDK-style with implicit globbing; `git rm` suffices.

### Claude's Discretion

- Exact XAML margins, padding, gradient choice for the new ScrollViewer row; auto-sizing window height behavior; banner color for `DenylistHit` warning (suggest `WarningAccent` gradient).
- Exact Portuguese phrasing of floor-list reasons + `DenylistReason` banner; follow `"ACESSO NEGADO (SANDBOX): ..."` pattern but switch SANDBOX → FLOOR.
- Whether `CommandConfirmationWindow.ShowAsync(ctx)` helper lives as a static method on the window class, on `CommandConfirmationContext`, or as a new tiny `ModalConfirmationService` — pick whichever minimizes coupling.
- Exact extraction shape of `RunProcessAsync(Process)` helper (split however keeps both methods under ~50 lines each).

### Deferred Ideas (OUT OF SCOPE)

- **"Factory reset" button** (settings UX milestone candidate).
- **Live API validation of `installArg`** (quality milestone — manifest fetch from skills.sh).
- **AlwaysAllow persistence across sessions** (Phase 1 D2 defers; tuple key is serializer-ready).
- **Unit tests for `CommandFloorList.Match`** (NFR-01: no test suite this milestone).
- **Splitting `NativeTools.cs`** (god-file refactor — separate milestone).
- **Re-enabling `DynamicSkillTool`** (deleted in D-12).
- **`/unlock_level` chat backdoor** (accepted risk, PROJECT.md / Phase 1).
- **Settings UI for the floor list** (binary toggle via `ConfirmDangerousCommands` only).

</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| SEC-04 | All tool-internal exec uses `ProcessStartInfo.ArgumentList`. `run_command` keeps post-modal raw cmd string per carve-out. Acceptance: `; rm -rf %USERPROFILE%\Documents` reaches a skill as one literal argv element. | §"Architecture Patterns" Pattern 1 (ArgumentList migration); §"Common Pitfalls" #1 (.cmd shim bypass); §"Code Examples" §RunSkillAsync rewrite |
| SEC-05 | Replace `ContainsWord` denylist with allowlist OR delete entirely (modal-only). Recorded in plan + `SEGURANCA.MD`. Acceptance: trivial bypasses (Remove + -Item concat, -EncodedCommand, alias mv) either reach the modal or no longer exist as a concept. | §"Architecture Patterns" Pattern 2 (hybrid floor); §"Code Examples" §CommandFloorList shape; §"Validation Architecture" REQ-SEC-05 |
| SEC-06 | `ExecuteSkillTool.RequiredLevel ≥ 6`; `MaterializeSkillTool.RequiredLevel = 8` with script-body preview in modal; every skill execution routes through `CommandConfirmationWindow`; `InstallFromOnlineAsync` regex-validates `installArg`. Acceptance: L1 `execute_skill` → "permissão negada"; L6 → modal with script body. | §"Architecture Patterns" Pattern 3 (skill modal hop) + Pattern 4 (script-body cache by SHA256); §"Code Examples" §InstallFromOnlineAsync regex; §"Common Pitfalls" #2 (npx.cmd PATHEXT) |

</phase_requirements>

## Summary

Phase 3 closes the three remaining HIGH security findings (arg injection, porous denylist, unrestricted skill exec) by leveraging two already-shipped Phase 1 primitives: `CommandConfirmationContext` (extended in place) and the `Dispatcher.InvokeAsync + _modalLock` hop pattern. The work decomposes into three architectural concerns:

1. **Argv hardening.** `.NET 8`'s `ProcessStartInfo.ArgumentList` is the canonical mitigation — Microsoft is listed **"Not Affected"** on the CERT/CC BatBadBut advisory (CVE-2024-1874 family) as of 2024-04-17. ArgumentList correctly escapes Windows `CreateProcess` argv (CommandLineToArgvW round-trip) for direct `.exe` invocations. **Critical caveat:** ArgumentList does NOT escape `cmd.exe` metacharacters (`&`, `|`, `^`, `<`, `>`, `%`), so calling `cmd.exe /c <userarg>` or invoking a `.cmd / .bat` shim is **still unsafe**. Phase 3's design correctly side-steps this by invoking `py.exe` and `powershell.exe` directly (D-07 InterpreterMap) — neither is a batch file. The one `.cmd` consumer that remains is `npx` on Windows (which is `npx.cmd`); CONTEXT.md's `<specifics>` block handles this with the `npx.cmd → npx` two-step lookup.

2. **Denylist replacement.** The locked design is hybrid: a tiny PT-BR-reasoned floor list of destructive verbs that refuses ONLY at L<7 ∩ `ConfirmDangerousCommands == ON`, with the modal as the unconditional first gate. This closes CONCERNS.md's "porous substring matching" finding by switching to normalize-then-regex with `\b` boundaries, alias expansion, and PowerShell quote-concat collapse — preserving Phase 1's D5 (modal always fires) and D8 (setting controls only post-modal floor) invariants. The deferred Phase-1-D3 denylist-hit flag closes here via new `DenylistHit`/`DenylistReason` context fields and a new `allow_then_floor_deny` audit outcome.

3. **Skill modal routing.** Both `ExecuteSkillTool` (L1→L6) and `MaterializeSkillTool` (L5→L8) acquire the same `_modalLock` + `Dispatcher.InvokeAsync` hop that `RunCommandTool` uses today (`NativeTools.cs:362-375`). The modal is extended in place — one new ScrollViewer row (Consolas 12pt, MaxHeight=200, VerticalScrollBar=Auto, read-only TextBox) bound to `ctx.ScriptBody != null`. `execute_skill` populates ScriptBody by reading the on-disk file (≤50KB) and computing SHA256; `materialize_skill` populates it from the LLM-supplied `script_content` directly. `AlwaysAllowSession` re-keys to `(Tool, Cmd, ContentHash?)` so a silently-edited skill on disk re-fires the modal automatically.

**Primary recommendation:** Plan three waves matching the phase's natural seams — Wave 1 (mechanical ArgumentList migration: CommandService.ExecuteWithArgListAsync + RunSkillAsync rewrite + DynamicSkillTool delete), Wave 2 (denylist swap + modal extension: CommandFloorList + CommandConfirmationContext fields + XAML row + ShowAsync helper + AlwaysAllowSession re-key), Wave 3 (skill gating + install validation + docs: ExecuteSkillTool/MaterializeSkillTool RequiredLevel + modal hop + InstallFromOnlineAsync regex + SEGURANCA.MD rewrite). Each wave is committable independently; Wave 2 depends on Wave 1's shared helper; Wave 3 depends on Wave 2's modal helper.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Argv quoting (literal user data → child process) | Backend (`CommandService.ExecuteWithArgListAsync`) | — | Pure runtime concern; `ProcessStartInfo` lives in `System.Diagnostics`; no UI surface. |
| Floor-list matching | Backend (`CommandFloorList` static helper) | — | Pure string transform + regex; auditable in one file. |
| Modal display & input capture | Frontend Server (WPF UI thread via `Application.Current.Dispatcher`) | Backend tool (initiates the hop) | UI is WPF-only; Dispatcher hop is the established pattern (Phase 1 D1; `NativeTools.cs:362-375`). |
| Modal context construction | Backend (each `ITool.ExecuteAsync`) | — | Tools own their level/command/script-body data; context is an immutable POCO carrier. |
| Session approval state | Backend (`AlwaysAllowSession` static singleton) | — | Process-singleton in-memory; no UI binding, no persistence. |
| Audit trail | Backend (`AuditLogService` static, JSONL append) | — | Established in Phase 1 D6; fire-and-forget. |
| Skill installation (`npx`) | Backend (`SkillService.InstallFromOnlineAsync`) | — | Pure shell-out; future caller must gate behind modal but no modal exists today. |
| Skill content hashing | Backend (`SHA256` of file bytes) | — | Stateless utility; called from `ExecuteSkillTool.ExecuteAsync` before modal. |

## Standard Stack

> The phase touches no NuGet additions. All work uses BCL primitives already present in the project. The "stack" table below is the BCL surface this phase consumes.

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| `System.Diagnostics.Process` / `ProcessStartInfo` | .NET 8 BCL | Argv-safe child-process invocation via `ArgumentList` | [VERIFIED: docs.microsoft.com/dotnet/api/system.diagnostics.processstartinfo.argumentlist] Microsoft's official escape boundary; "Strings added to the list don't need to be previously escaped." [CITED: learn.microsoft.com] |
| `System.Text.RegularExpressions.Regex` | .NET 8 BCL | Floor-list pattern matching with `\b` boundaries | [VERIFIED: codebase] Already used throughout `NativeTools.cs` for tool arg parsing and the `ContainsWord` helper this phase replaces. |
| `System.Security.Cryptography.SHA256` | .NET 8 BCL | Skill script content hash for AlwaysAllow cache key | [VERIFIED: BCL standard] Hash-stable identifier; D-09 requires it for "silently edited skill" defense. |
| `System.Windows.Application.Current.Dispatcher` (WPF) | .NET 8 BCL | UI-thread marshal for modal `ShowDialog` | [VERIFIED: codebase] Phase 1 D1 pattern at `NativeTools.cs:365-370`; reused verbatim per D-08 helper extraction. |
| `System.Threading.SemaphoreSlim` | .NET 8 BCL | `_modalLock` to serialize concurrent modal hops | [VERIFIED: codebase] Already declared at `NativeTools.cs:288` for `RunCommandTool`; helper hoists it. |
| `System.Text.Json.JsonSerializer` | .NET 8 BCL | AuditLog JSONL serialization (extending Phase 1 schema) | [VERIFIED: codebase] `AuditLogService.AppendAsync` at `AuditLogService.cs:38`. |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| `System.IO.File` (`ReadAllBytes`, `Exists`) | .NET 8 BCL | Read skill file for SHA256 + ScriptBody cap | D-09 modal cache lookup before showing modal. |
| `System.Text.Encoding.UTF8` | .NET 8 BCL | Decode skill file bytes for ScriptBody | Skill files are PT-BR-commented Python/PowerShell — UTF-8 is the safe default. |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `ProcessStartInfo.ArgumentList` (D-05) | Hand-rolled CommandLineToArgvW escape | Microsoft owns the escape semantics in `PasteArguments.EncodeArgumentForCommandLine` [CITED: github.com/dotnet/corert PasteArguments.cs]; hand-rolling re-introduces BatBadBut-class bugs. **Use ArgumentList.** |
| `static readonly Regex[]` (D-02 floor list) | Single concatenated `(a|b|c)` regex | Single regex hides which entry matched; per-entry array preserves the `(Pattern, Category, Reason)` triplet D-02 requires. **Use array.** |
| `SHA256.HashData(bytes)` (D-09) | MD5 / SHA1 | Cache key only — collision resistance not load-bearing; but SHA256 is the BCL default and cheap. **Use SHA256.** |
| Static method on `CommandConfirmationWindow` (D-08 `ShowAsync` helper) | New `ModalConfirmationService` class | Helper has zero state and one job (hop + audit-pass-through wiring is per-caller); static method on the window minimizes new types. **Discretion to planner.** |

**Installation:**
```bash
# No NuGet additions. Phase 3 is BCL-only.
```

**Version verification:** Not applicable — no third-party packages installed. All consumed APIs are BCL surfaces stable since .NET Core 3.x / .NET 5+.

## Package Legitimacy Audit

> Not applicable. Phase 3 installs no external packages. All work uses .NET 8 BCL surfaces already referenced in `AIBWindows/AIB.csproj`. The one external invocation (`npx skills add ...` in `InstallFromOnlineAsync`) is dead code today (zero call sites) and is hardened defensively per D-11 against a future caller; it does not add a NuGet dependency.

| Package | Registry | Disposition |
|---------|----------|-------------|
| *(none)* | — | Phase 3 is a code-only refactor; no `<PackageReference>` additions. |

## Architecture Patterns

### System Architecture Diagram

```
                  ┌──────────────────────────────────┐
                  │  LLM emits tool call (ReAct loop)│
                  │  run_command | execute_skill |   │
                  │  materialize_skill               │
                  └────────────┬─────────────────────┘
                               │
                               ▼
                  ┌──────────────────────────────────┐
                  │ ToolRegistry.ExecuteToolAsync    │
                  │  - check RequiredLevel ≤ user    │  ← L6/L8 gating per D-11/D-12
                  │    (L<6 execute_skill → "permissão│
                  │     negada"; L<8 materialize     │
                  │     → same)                      │
                  └────────────┬─────────────────────┘
                               │
        ┌──────────────────────┼──────────────────────────────────┐
        │                      │                                  │
        ▼                      ▼                                  ▼
┌───────────────┐    ┌────────────────────┐         ┌───────────────────────┐
│ RunCommandTool│    │ ExecuteSkillTool   │         │ MaterializeSkillTool  │
│ ExecuteAsync  │    │ ExecuteAsync       │         │ ExecuteAsync          │
└───────┬───────┘    └─────────┬──────────┘         └──────────┬────────────┘
        │                      │                               │
        │              ┌───────▼──────────┐           ┌────────▼─────────┐
        │              │ Read scriptPath, │           │ Use LLM-supplied │
        │              │ read ≤50KB body, │           │ script_content   │
        │              │ compute SHA256   │           │ directly as body │
        │              │ (skip if md)     │           │ (no disk read)   │
        │              └───────┬──────────┘           └────────┬─────────┘
        │                      │                               │
        ▼                      ▼                               ▼
┌──────────────────────────────────────────────────────────────────┐
│ CommandFloorList.Match(cmd, level)  ← run_command only           │
│   normalize → alias expand → EncodedCmd reject → regex w/ \b     │
│   returns (Hit: bool, Reason: string?)                           │
└────────────┬─────────────────────────────────────────────────────┘
             │
             ▼  (DenylistHit + DenylistReason populated)
┌──────────────────────────────────────────────────────────────────┐
│ Build CommandConfirmationContext                                 │
│   { Tool, Command, Level, Cwd,                                   │
│     DenylistHit, DenylistReason,            ← D-04 new           │
│     ScriptBody, Interpreter, ContentHash }  ← D-08 new           │
└────────────┬─────────────────────────────────────────────────────┘
             │
             ▼
┌──────────────────────────────────────────────────────────────────┐
│ AlwaysAllowSession.Contains((Tool, Cmd, ContentHash?))           │
│   YES → audit "always_allow", skip modal, go to floor gate       │
│   NO  → next                                                     │
└────────────┬─────────────────────────────────────────────────────┘
             │
             ▼
┌──────────────────────────────────────────────────────────────────┐
│ CommandConfirmationWindow.ShowAsync(ctx)  ← shared D-08 helper   │
│   _modalLock.WaitAsync                                           │
│   Dispatcher.InvokeAsync → new Window(ctx).ShowDialog()          │
│   Window renders:                                                │
│     - Tool / Cmd / Nível / Cwd (Phase 1)                         │
│     - AVISO banner if DenylistHit (amber WarningAccent)          │
│     - ScrollViewer + Consolas TextBox if ScriptBody != null      │
│     - Header: "CONFIRMAÇÃO DE COMANDO" or                        │
│                "CONFIRMAÇÃO DE SCRIPT ({Interpreter})"           │
│   Returns (allowed: bool, alwaysAllow: bool)                     │
└────────────┬─────────────────────────────────────────────────────┘
             │
       allowed = false                       allowed = true
             │                                       │
             ▼                                       ▼
   ┌──────────────────┐                 ┌──────────────────────────┐
   │ audit "deny"     │                 │ if alwaysAllow:          │
   │ return refusal   │                 │   AlwaysAllowSession.Add │
   └──────────────────┘                 │   audit "always_allow"   │
                                        │ else: audit "allow"      │
                                        └────────────┬─────────────┘
                                                     │
                                                     ▼
                                ┌─────────────────────────────────┐
                                │ Floor gate (run_command, L<7 ∩  │
                                │  ConfirmDangerousCommands == ON)│
                                │ DenylistHit? → audit            │
                                │   "allow_then_floor_deny",      │
                                │   return DenylistReason         │
                                └────────────┬────────────────────┘
                                             │
                                             ▼
                       ┌──────────────────────────────────────────┐
                       │ run_command:                             │
                       │   CommandService.ExecuteAsync(cmd, cwd)  │
                       │   (cmd.exe /c {cmd} — unchanged)         │
                       │                                          │
                       │ execute_skill / materialize_skill:       │
                       │   CommandService.ExecuteWithArgListAsync │
                       │   (py.exe | powershell.exe + ArgumentList│
                       │    = [switches..., scriptPath,           │
                       │       argumentsString])                  │
                       └──────────────────────────────────────────┘
```

### Recommended Project Structure

```
AIBWindows/Services/
├── CommandService.cs                  # MODIFY (D-05: extract RunProcessAsync, add ExecuteWithArgListAsync)
├── CommandFloorList.cs                # NEW (D-02, D-03: normalize-then-regex floor)
├── SkillService.cs                    # MODIFY (D-06, D-07, D-11: InterpreterMap, RunSkillAsync rewrite, install regex)
├── DynamicSkillTool.cs                # DELETE (D-12)
├── NativeTools.cs                     # MODIFY (RunCommandTool D-04 floor gate;
│                                       #         ExecuteSkillTool RequiredLevel=6 + modal hop;
│                                       #         MaterializeSkillTool RequiredLevel=8 + modal hop)
├── CommandConfirmationContext.cs      # MODIFY (D-04 + D-08: 5 new init-only props)
├── AlwaysAllowSession.cs              # MODIFY (D-10: re-key to tuple)
└── AuditLogService.cs                 # NO STRUCTURAL CHANGE (anonymous-object schema absorbs content_hash)

AIBWindows/Views/
├── CommandConfirmationWindow.xaml     # MODIFY (D-08: ScrollViewer row, D-04: amber banner)
└── CommandConfirmationWindow.xaml.cs  # MODIFY (consume new context fields; add ShowAsync helper)

Regras de Identidade/
└── SEGURANCA.MD                       # MODIFY (NFR-03: rewrite denylist/skill/quoting paragraphs)
```

### Pattern 1: ArgumentList migration for tool-internal exec

**What:** Replace `$"py \"{scriptPath}\" {safeArgs}"` (interpolation into a string passed to `cmd.exe /c`) with a typed argv enumerable handed directly to `python.exe` / `powershell.exe`. The OS spawns the interpreter directly (no `cmd.exe` in the chain), so user input never reaches a shell parser.

**When to use:** Every tool-internal exec path where any argument comes from the LLM. SEC-04 carve-out: `run_command` is exempt because the user explicitly typed the command in the modal and the post-modal path preserves the raw string verbatim.

**Example:**
```csharp
// Source: codebase CommandService.cs:10-72 (existing ExecuteAsync) + D-05 design
// New method, sibling to ExecuteAsync(string).
public static async Task<string> ExecuteWithArgListAsync(
    string fileName,
    IEnumerable<string> args,
    string? workDir = null,
    int timeoutMs = 20000)
{
    var psi = new ProcessStartInfo
    {
        FileName = fileName,                      // e.g. "py.exe" or "powershell.exe"
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,                  // REQUIRED for ArgumentList
        CreateNoWindow = true,
        StandardOutputEncoding = Encoding.UTF8,
        WorkingDirectory = string.IsNullOrWhiteSpace(workDir)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : workDir
    };
    foreach (var arg in args) psi.ArgumentList.Add(arg);

    using var process = new Process { StartInfo = psi };
    return await RunProcessAsync(process, timeoutMs);  // shared helper extracted from existing ExecuteAsync
}
```

### Pattern 2: Hybrid floor list (D-02, D-03)

**What:** A small, regex-based, normalize-then-match floor that refuses ONLY at L<7 ∩ `ConfirmDangerousCommands == ON`, applied AFTER the user clicks Allow in the modal. The modal is always the first gate; the floor is a second-layer hard refusal for the four destructive verb categories D-02 enumerates.

**When to use:** Only in `RunCommandTool.ExecuteAsync`. Skill tools do NOT consult the floor list — their content is fully visible to the user in the modal preview (D-08, D-09), and they execute via ArgumentList against an interpreter (not cmd.exe), so cmd-injection vectors don't apply.

**Example:**
```csharp
// Source: D-02 + D-03 design, codebase NativeTools.cs:311-319 (ContainsWord — to be deleted)
public static class CommandFloorList
{
    private static readonly (Regex Pattern, string Category, string Reason)[] _entries = new[]
    {
        // Recursive deletion
        (new Regex(@"\brm\s+(-rf|-r\s+-f|-f\s+-r|-r)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
         "recursive-delete",
         "Comando de deleção recursiva (rm -r/-rf) — requer Nível 7."),
        (new Regex(@"\bdel\s+/s\b|\bdel\s+/f\s+/s\b|\brmdir\s+/s\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
         "recursive-delete",
         "Comando de deleção recursiva (del /s, rmdir /s) — requer Nível 7."),
        (new Regex(@"\bremove-item\s+.*-recurse\b|\bremove-item\s+.*-force\s+.*-recurse\b",
                   RegexOptions.IgnoreCase | RegexOptions.Compiled),
         "recursive-delete",
         "Comando de deleção recursiva (Remove-Item -Recurse) — requer Nível 7."),
        // Format / partition
        (new Regex(@"\bformat\b|\bdiskpart\b|\bwmic\s+logicaldisk\b|\bcipher\s+/w\b",
                   RegexOptions.IgnoreCase | RegexOptions.Compiled),
         "format",
         "Comando de formatação/partição — requer Nível 7."),
        // Shutdown / logoff
        (new Regex(@"\bshutdown\b|\brestart-computer\b|\bstop-computer\b|\blogoff\b",
                   RegexOptions.IgnoreCase | RegexOptions.Compiled),
         "shutdown",
         "Comando de desligamento/logoff — requer Nível 7."),
        // Registry destructive
        (new Regex(@"\breg\s+delete\b|\bremove-itemproperty\s+.*-path\s+hk|\bremove-item\s+.*-path\s+hk",
                   RegexOptions.IgnoreCase | RegexOptions.Compiled),
         "registry-destructive",
         "Operação destrutiva no registro do Windows — requer Nível 7."),
    };

    private static readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mv"]  = "Move-Item",
        ["ri"]  = "Remove-Item",
        ["ni"]  = "New-Item",
        ["sc"]  = "Set-Content",
        ["ac"]  = "Add-Content",
        ["gci"] = "Get-ChildItem",
    };

    /// <summary>Returns (Hit: true, Reason) if the normalized command matches a floor entry. Reason is PT-BR.</summary>
    public static (bool Hit, string? Reason) Match(string command, int userLevel)
    {
        if (userLevel >= 7) return (false, null);  // D-01: floor inactive at L≥7

        string normalized = command.ToLowerInvariant();
        // Step 2: strip "x" + "y" / 'x' + 'y' quote-concat (defense vs PowerShell "Remove" + "-Item" bypass)
        normalized = Regex.Replace(normalized, @"[""']\s*\+\s*[""']", "");
        // Step 3: alias expansion (token-aware so "mv" inside a path doesn't expand)
        normalized = Regex.Replace(normalized, @"\b(mv|ri|ni|sc|ac|gci)\b",
            m => _aliases[m.Value].ToLowerInvariant());
        // Step 4: -EncodedCommand outright at L<7
        if (Regex.IsMatch(normalized, @"-encodedcommand\b"))
            return (true, "powershell -EncodedCommand não é avaliável — requer Nível 7.");
        // Step 5: floor regex match
        foreach (var (pattern, _, reason) in _entries)
            if (pattern.IsMatch(normalized)) return (true, reason);

        return (false, null);
    }
}
```

### Pattern 3: Skill modal hop (D-08 shared helper)

**What:** Lift the Phase-1 modal-hop body from `RunCommandTool.ExecuteAsync` (`NativeTools.cs:362-375`) into a static helper on `CommandConfirmationWindow` so all three modal-bearing tools share one implementation of the dispatcher hop, the `_modalLock`, and the audit-log wrapping.

**When to use:** From `RunCommandTool`, `ExecuteSkillTool`, and `MaterializeSkillTool`. The helper accepts an already-built `CommandConfirmationContext` and returns `(bool allowed, bool alwaysAllow)`. Callers do their own `AlwaysAllowSession` check before calling the helper (the helper does not own session state).

**Example:**
```csharp
// Source: lift from NativeTools.cs:362-375 (RunCommandTool) per D-08
// Place on CommandConfirmationWindow (static method) to minimize new types.
public partial class CommandConfirmationWindow : Window
{
    private static readonly SemaphoreSlim _modalLock = new(1, 1);

    /// <summary>
    /// Hops to the UI thread, shows the confirmation modal for the given context, and returns the
    /// user's decision. Reentrancy-guarded by a single SemaphoreSlim across all tools.
    /// </summary>
    public static async Task<(bool Allowed, bool AlwaysAllow)> ShowAsync(CommandConfirmationContext ctx)
    {
        if (System.Windows.Application.Current == null) return (false, false);  // D-1 no-UI guard

        await _modalLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return await System.Windows.Application.Current.Dispatcher.InvokeAsync<(bool, bool)>(() =>
            {
                var win = new CommandConfirmationWindow(ctx) { Owner = System.Windows.Application.Current.MainWindow };
                bool result = win.ShowDialog() == true;
                return (result && win.IsAllowed, win.AlwaysAllow);
            }).Task;
        }
        finally
        {
            _modalLock.Release();
        }
    }
}
```

### Pattern 4: Script-body cache by (skillName + SHA256)

**What:** `execute_skill` reads the resolved script file (cap 50KB; tail-truncate marker), computes `SHA256(scriptFileBytes)`, populates `ctx.ScriptBody` + `ctx.ContentHash`. `AlwaysAllowSession` is keyed by `(Tool, Cmd, ContentHash?)` so a silently-edited skill file produces a new tuple → cache miss → modal re-fires showing the new body.

**When to use:** Inside `ExecuteSkillTool.ExecuteAsync`, before calling `CommandConfirmationWindow.ShowAsync`. Markdown-only skills (`Interpreter == "markdown"`) skip the entire modal path — they return text per `SkillService.RunSkillAsync:191-192` and never execute a process.

**Example:**
```csharp
// Source: D-09 design
private static (string? Body, string? Hash) ReadAndHash(string scriptPath)
{
    if (!File.Exists(scriptPath)) return (null, null);
    var bytes = File.ReadAllBytes(scriptPath);
    var hash = Convert.ToHexString(SHA256.HashData(bytes));
    var body = Encoding.UTF8.GetString(bytes);
    const int Cap = 50_000;
    if (body.Length > Cap) body = body.Substring(0, Cap) + "\n\n[... SCRIPT TRUNCATED]";
    return (body, hash);
}
```

### Anti-Patterns to Avoid

- **Replacing the `cmd /c` shell with `ArgumentList.Add("/c", userCmd)`.** This is the BatBadBut trap (CVE-2024-1874 family). `cmd.exe` re-parses metacharacters from its second argv; ArgumentList does NOT escape `& | ^ < > %`. Phase 3 sidesteps this by invoking `py.exe` / `powershell.exe` directly — never `cmd.exe` for tool-internal exec. The `run_command` path keeps `cmd.exe /c {raw_string}` precisely because the user typed it and approved it in the modal; SEC-04 explicitly carves this case out.
- **Calling `Process.Start("npx", ...)` directly on Windows.** With `UseShellExecute = false`, `CreateProcess` does NOT honor `PATHEXT`. `npx` is `npx.cmd` (npm cmd-shim); the call returns `ERROR_FILE_NOT_FOUND`. Mitigation per CONTEXT.md `<specifics>`: try `npx.cmd` first, fall back to `npx`. Both `.cmd` shim invocations route through `cmd.exe`, so the `installArg` regex (D-11) is the load-bearing defense, not ArgumentList escaping.
- **Adding the deferred MaxFiles=500 grep limits to Phase 3 scope.** Phase 5 covers SEC-08; Phase 3 must not touch `GrepTool` / `GlobTool`.
- **Adding "Always allow this skill" to a per-skill UI checkbox.** AlwaysAllow remains a single session checkbox per D-10 / Phase 1 D2; the tuple key just gives finer eviction granularity.
- **Reading floor-list reasons from a resource file.** D-02 explicitly keeps reasons inline as the third tuple element so a code review sees the user-facing text next to the pattern.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Escape user data into a Windows command line | A custom regex / `.Replace("\"", "\\\"")` quote-and-pray | `ProcessStartInfo.ArgumentList` | Microsoft owns the escape semantics in `PasteArguments.EncodeArgumentForCommandLine` [CITED: github.com/dotnet/corert PasteArguments.cs]; hand-rolling re-introduces CommandLineToArgvW round-trip bugs. |
| Compute a content hash for the AlwaysAllow cache key | An ad-hoc string hash / `string.GetHashCode()` | `SHA256.HashData(byte[])` | `GetHashCode()` is not stable across runtimes/processes; SHA256 from BCL is one line and deterministic. [VERIFIED: BCL stable since .NET Core 2.1] |
| Marshal a `ShowDialog` call from a background thread | A new threading primitive / `SynchronizationContext.Post` | `Application.Current.Dispatcher.InvokeAsync<T>` (already used at `NativeTools.cs:362-375`) | The hop pattern is established; reuse the exact shape Phase 1 verified to avoid re-deriving correctness. |
| Serialize modal calls across parallel tool invocations | A new lock object | The existing `_modalLock` SemaphoreSlim, hoisted into the `ShowAsync` helper | One semaphore instance covers all three modal-bearing tools; multiple locks would let two modals open simultaneously when ReAct dispatches tools in parallel. |
| Append-only JSONL audit writer | A new logging library | The existing `AuditLogService.AppendAsync(object)` | Phase 1 shipped this; just extend the anonymous-object passed in with `content_hash` (null for run_command). |
| Detect a `.cmd` shim on PATH | A new PATH-walker | The two-step try `npx.cmd` then `npx` per `<specifics>` block | npm's cmd-shim convention guarantees `.cmd` for Windows; one fallback covers the common case. |

**Key insight:** Phase 3 is almost entirely a **deletion + reuse** phase. The denylist replacement deletes 50+ lines (`ContainsWord` + `ApplyDenylist` + 4 hardcoded tables in `NativeTools.cs:311-319, :413-448`). The skill tools each gain ~15 lines of modal-hop boilerplate that calls the new `ShowAsync` helper. The interpreter map collapses 4 switch-expression arms into one lookup. The new code surface is the `CommandFloorList` helper (~70 lines) + the `ExecuteWithArgListAsync` method (~30 lines) + 5 new init-only context properties + 1 new XAML row. Nothing else is invented; everything else leverages Phase 1.

## Runtime State Inventory

> Phase 3 is a code+config refactor with NO data migration. The five categories are answered explicitly per the canonical question.

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| **Stored data** | None — verified by `Grep "denylist|ContainsWord|safeArgs"` returning only code matches (no DB rows, no `~/.AIB/*.db` content references the floor list). AlwaysAllowSession is in-memory only (Phase 1 D2; verified `AlwaysAllowSession.cs:7-32` — `HashSet<string>`, no persistence). | None. The re-keying to tuple (D-10) breaks no on-disk schema because there is no on-disk store for approvals. |
| **Live service config** | None. Phase 3 modifies no external service config (no n8n, no Datadog, no Tailscale). The only external invocation is `npx skills add ...` in dead-code `InstallFromOnlineAsync`. | None. |
| **OS-registered state** | None. No Windows Task Scheduler entries, no pm2 processes, no systemd units reference any string Phase 3 changes. The tray-app HKey / autostart registration (if any) is untouched. | None. |
| **Secrets and env vars** | None. The floor list is hardcoded in source. `installArg` regex is hardcoded. No `.env` keys changed; no `CredentialService` system/key tuple renamed. | None. |
| **Build artifacts / installed packages** | `DynamicSkillTool.cs` deletion (D-12) is a source-only `git rm`. csproj is SDK-style with implicit `<Compile>` globbing — no explicit `<Compile Include="...DynamicSkillTool.cs"/>` exists [VERIFIED: codebase — `AIB.csproj` reviewed in STACK.md §"Package Manager"]. After delete, run `dotnet clean && dotnet build AIBWindows/AIB.csproj` to flush `obj/` references to the old type. | One step: `git rm AIBWindows/Services/DynamicSkillTool.cs`; verify `dotnet build` is 0/0. No NuGet `<PackageReference>` removal needed. |

**Canonical question — answered:** After every file in the repo is updated, what runtime systems still have the old behavior cached, stored, or registered? **Nothing.** Phase 3 has no runtime-state surface beyond source code and the build's `obj/`/`bin/` artifacts.

## Common Pitfalls

### Pitfall 1: ArgumentList does NOT escape cmd.exe metacharacters for batch shims

**What goes wrong:** Developer assumes `psi.ArgumentList.Add(userInput)` is universally safe and migrates `cmd /c npx ...` to `psi.FileName = "cmd.exe"; psi.ArgumentList.Add("/c"); psi.ArgumentList.Add($"npx skills add {installArg}");`. The `installArg` is now embedded inside a single argv element that `cmd.exe` re-parses, restoring full BatBadBut vulnerability. CVE-2024-1874 (BatBadBut family) [CITED: kb.cert.org/vuls/id/123335].

**Why it happens:** .NET's `ArgumentList` calls `PasteArguments.EncodeArgumentForCommandLine` which implements the `CommandLineToArgvW` round-trip — quote/backslash escaping only. It does NOT escape `& | ^ < > %` because those are not argv parsing chars; they are `cmd.exe`-parser chars. When you hand the result to `cmd.exe /c`, cmd re-parses the second argv element and the metacharacters fire. [CITED: github.com/dotnet/corert PasteArguments.cs]

**How to avoid:** Phase 3's D-07 InterpreterMap invokes `py.exe` and `powershell.exe` directly — neither is a batch file. The `cmd` interpreter entry (`/c`) is preserved because `cmd.exe` is the user-facing path; tool-internal exec MUST NOT route through it. The `npx` path in `InstallFromOnlineAsync` is the one remaining cmd shim (npm cmd-shim convention), so D-11's `installArg` regex is the load-bearing defense — `^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(@[A-Za-z0-9_.\-]+)?$` rejects every cmd-metacharacter byte class before the spawn happens.

**Warning signs:** Any `FileName = "cmd.exe"` paired with an `ArgumentList` that contains LLM-supplied data. Any `.cmd` or `.bat` suffix in FileName. Any `npx | npm | yarn | pnpm` invocation (all are cmd shims on Windows).

### Pitfall 2: `Process.Start("npx", ...)` returns ERROR_FILE_NOT_FOUND on Windows when UseShellExecute = false

**What goes wrong:** `psi.FileName = "npx"` with `UseShellExecute = false` fails because `CreateProcess` does NOT honor `PATHEXT`. The actual file is `npx.cmd` (or `npx.ps1` per npm version). [VERIFIED: docs.microsoft.com/dotnet/fundamentals/runtime-libraries/system-diagnostics-processstartinfo-useshellexecute — "the system will attempt to find within folders specified by the PATH environment variable" but extension resolution is OS-level CreateProcess]

**Why it happens:** When `UseShellExecute = true`, Windows ShellExecute resolves via the registry's file-association table and PATHEXT. When `UseShellExecute = false` (REQUIRED for `ArgumentList`), .NET calls `CreateProcessW` directly, which only matches the literal filename + `.exe`.

**How to avoid:** CONTEXT.md `<specifics>` block prescribes "try `npx.cmd` first; fall back to `npx`". Concretely:
```csharp
try
{
    return await CommandService.ExecuteWithArgListAsync("npx.cmd", args, workPath, 900_000);
}
catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)  // ERROR_FILE_NOT_FOUND
{
    return await CommandService.ExecuteWithArgListAsync("npx", args, workPath, 900_000);
}
```
The catch is narrow (`Win32Exception` with `NativeErrorCode == 2`), not bare `catch { }`, so genuine spawn failures still surface.

**Warning signs:** Spawn exceptions of type `Win32Exception` with `NativeErrorCode == 2`. The `[REGISTRY]` log line in `ToolRegistry.cs:39` will print the tool call but the inner spawn fails silently if the catch is too broad.

### Pitfall 3: PowerShell `-EncodedCommand` bypasses regex floor

**What goes wrong:** Floor regex catches `Remove-Item -Recurse` but not `powershell -EncodedCommand UmVtb3ZlLUl0ZW0gLVJlY3Vyc2U=` (base64 of `Remove-Item -Recurse`). The CONCERNS.md HIGH finding explicitly lists this as an existing bypass.

**Why it happens:** `-EncodedCommand` accepts arbitrary base64; the floor list cannot decode + re-match at runtime without an O(N) per-call cost AND without the risk of false positives on legitimate base64 (e.g. a hash being printed).

**How to avoid:** D-03 step 4 mandates an OUTRIGHT refusal at L<7 for any command containing `-EncodedCommand` (any case). The reason string is dedicated: `"powershell -EncodedCommand não é avaliável — requer Nível 7."` At L≥7 the modal shows the full encoded command verbatim and the user decides — preserving Phase 1 D5 (modal is the unconditional gate at every level).

**Warning signs:** Any chat trace where a model emits PowerShell with `-Enc`, `-EncodedCommand`, or its short aliases (`-e`, `-en`, `-encod...`). The regex MUST match the prefix, not just the full keyword, because PowerShell does prefix-match on parameter names.

### Pitfall 4: PowerShell quote-concat bypass — `"Remove" + "-Item"`

**What goes wrong:** A model writes `powershell -Command "& { \"Remove\" + \"-Item\" -Recurse C:\foo }"` to evade the literal `Remove-Item` token in the floor regex. The CONCERNS.md HIGH finding explicitly lists this.

**Why it happens:** PowerShell's parser concatenates string literals at runtime; a substring search for `Remove-Item` sees `"Remove" + "-Item"` and misses.

**How to avoid:** D-03 step 2 collapses `"x" + "y"` and `'x' + 'y'` patterns to `xy` BEFORE the regex pass. The collapse uses a permissive regex `[""']\s*\+\s*[""']` that handles whitespace and either quote style.

**Warning signs:** Floor list returns `(false, null)` for a command that obviously contains `Remove-Item` after manual inspection. The normalized form should be loggable at `[FLOOR]` tag for diagnosis.

### Pitfall 5: ScriptBody preview leaks secrets to audit log via screen recording

**What goes wrong:** A user is recording their screen, modal renders a skill body that prints `$env:OPENAI_API_KEY` literally (some scripts log secrets). Now the secret is in the screen recording AND the audit log if a future change adds ScriptBody to JSONL.

**Why it happens:** D-09 caches by ContentHash but does not sanitize ScriptBody. The audit log gains `content_hash` (D-10) — NOT `script_body` — by design.

**How to avoid:** D-09 + D-10 design is already safe: only the SHA256 enters AuditLog. The visible-on-screen leak is unavoidable when the user is reviewing the body (that's the whole point of D-08), but it never persists to disk beyond the user's screen recording. Plan must NOT add `script_body` to the audit log.

**Warning signs:** Any planner edit that adds `script_body` to the anonymous object passed to `AuditLogService.AppendAsync`.

### Pitfall 6: Modal helper deadlock on re-entrant Dispatcher hop

**What goes wrong:** A tool calling `CommandConfirmationWindow.ShowAsync` is itself running on the UI thread (e.g. tested from a button click). `Dispatcher.InvokeAsync` posts the work to the same thread's queue but the awaiting code holds the thread — deadlock.

**Why it happens:** `Dispatcher.InvokeAsync<T>(...).Task` does not synchronously pump messages; the awaiting code expects the UI thread to be free.

**How to avoid:** All three modal-bearing tools (`RunCommandTool`, `ExecuteSkillTool`, `MaterializeSkillTool`) are invoked from `ToolRegistry.ExecuteToolAsync` on a background thread (originated by `OpenAIService.StreamResponseAsync` which dispatches tools via `Task.WhenAll` per CONCERNS.md). No UI-thread entry point exists. Phase 1 verified this; Phase 3 inherits the same safety. The `_modalLock.WaitAsync().ConfigureAwait(false)` in the helper preserves this — DO NOT change it to `ConfigureAwait(true)`.

**Warning signs:** Any future unit-test harness that calls `ShowAsync` directly from `[Test]` code on a `[STAThread]` may deadlock. The fix is to skip the modal entirely under tests (`Application.Current == null` guard returns `(false, false)` — D1).

## Code Examples

Verified patterns from the existing codebase + the locked CONTEXT.md design:

### Example 1: D-05 RunProcessAsync extraction (preserve existing semantics)

```csharp
// Source: CommandService.cs:10-72 (extract shared pump from existing ExecuteAsync)
private static async Task<string> RunProcessAsync(Process process, int timeoutMs)
{
    var output = new StringBuilder();
    var error  = new StringBuilder();
    process.OutputDataReceived += (s, e) => { if (e.Data != null) output.AppendLine(e.Data); };
    process.ErrorDataReceived  += (s, e) => { if (e.Data != null) error.AppendLine(e.Data); };

    process.Start();
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();

    var finished = await Task.Run(() => process.WaitForExit(timeoutMs));
    if (finished)
    {
        process.WaitForExit();  // drain buffers
    }
    else
    {
        try { process.Kill(true); } catch { /* swallow per CONCERNS.md LOW finding */ }
        return $"[TIMEOUT] O comando demorou mais de {timeoutMs/1000}s.\nOutput parcial:\n{output}";
    }

    string result = output.ToString();
    string err = error.ToString();
    if (!string.IsNullOrWhiteSpace(err)) result += $"\n[ERRO]:\n{err}";
    if (string.IsNullOrWhiteSpace(result)) return "[Comando executado, mas não retornou saída]";
    if (result.Length > 50_000) result = result.Substring(0, 50_000) + "\n\n[AVISO: Saída muito longa, truncada...]";
    return result;
}
```

### Example 2: D-06 + D-07 RunSkillAsync rewrite

```csharp
// Source: SkillService.cs:186-205 (replace switch expression with InterpreterMap + ExecuteWithArgListAsync)
private static readonly Dictionary<string, (string FileName, string[] Switches)> InterpreterMap
    = new(StringComparer.OrdinalIgnoreCase)
{
    ["python"]     = ("py.exe",         Array.Empty<string>()),
    ["powershell"] = ("powershell.exe", new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File" }),
    ["cmd"]        = ("cmd.exe",        new[] { "/c" }),
};

public static async Task<string> RunSkillAsync(string name, string arguments)
{
    var skill = ListLocalSkills().FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    if (skill == null) return $"Erro: Skill '{name}' não encontrada.";

    if (skill.Interpreter == "markdown")
        return $"INSTRUÇÕES DA SKILL '{name}':\n\n{File.ReadAllText(skill.ScriptFile)}\n\nSugestão: Use 'materialize_skill' para criar um script para esta skill.";

    if (!InterpreterMap.TryGetValue(skill.Interpreter, out var pair))
        return $"Erro: interpretador '{skill.Interpreter}' não suportado.";

    // D-06: argumentsString is ONE argv element. Defense-in-depth \0/\n trim documented inline.
    // ArgumentList serializes literal bytes — no shell interpretation — so the strip is paranoia,
    // not security: it just prevents weird argv display in logs if the LLM emits a stray NUL.
    string safeArgs = (arguments ?? "").Replace("\n", " ").Replace("\r", "").Replace("\0", "");

    var args = new List<string>(pair.Switches) { skill.ScriptFile, safeArgs };
    return await CommandService.ExecuteWithArgListAsync(pair.FileName, args,
        Path.GetDirectoryName(skill.ScriptFile));
}
```

### Example 3: D-11 InstallFromOnlineAsync hardening

```csharp
// Source: SkillService.cs:147-177 (insert regex at :154, rewrite npx call)
public static async Task<string> InstallFromOnlineAsync(string url)
{
    string workPath = Path.Combine(Path.GetTempPath(), "AIB_Skills_Work");
    try
    {
        EnsureDir();
        if (!Directory.Exists(workPath)) Directory.CreateDirectory(workPath);
        string installArg = url;
        if (url.Contains("skills.sh/"))
        {
            var urlParts = url.Split("skills.sh/", StringSplitOptions.RemoveEmptyEntries)[1]
                              .Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (urlParts.Length >= 3) installArg = $"{urlParts[0]}/{urlParts[1]}@{urlParts[2]}";
        }

        // D-11: regex BEFORE any shell-out
        if (!Regex.IsMatch(installArg, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(@[A-Za-z0-9_.\-]+)?$"))
            return "Erro: formato de skill inválido (esperado: owner/repo[@version]).";

        // D-11: replace cmd /c call npx ... with explicit argv. Try npx.cmd first (Win shim), fall back to npx.
        string result;
        try
        {
            result = await CommandService.ExecuteWithArgListAsync(
                "npx.cmd", new[] { "-y", "skills", "add", installArg, "--yes" }, workPath, 900_000);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)  // ERROR_FILE_NOT_FOUND
        {
            result = await CommandService.ExecuteWithArgListAsync(
                "npx", new[] { "-y", "skills", "add", installArg, "--yes" }, workPath, 900_000);
        }

        string agentsSkillsPath = Path.Combine(workPath, ".agents", "skills");
        if (Directory.Exists(agentsSkillsPath))
        {
            foreach (var skillDir in Directory.GetDirectories(agentsSkillsPath))
            {
                string skillName = Path.GetFileName(skillDir);
                string targetPath = Path.Combine(SkillsDir, skillName);
                if (Directory.Exists(targetPath)) Directory.Delete(targetPath, true);
                CopyDirectory(skillDir, targetPath);
            }
            return $"SUCESSO: A habilidade '{installArg}' foi instalada.";
        }
        return $"FALHA na instalação: {result}";
    }
    catch (Exception ex) { return $"Erro: {ex.Message}"; }
}
```

### Example 4: ExecuteSkillTool modal hop (D-08, D-09, D-10)

```csharp
// Source: NativeTools.cs:549-576 (raise RequiredLevel to 6 + add modal-hop block)
public class ExecuteSkillTool : ITool
{
    public string Name => "execute_skill";
    public string Description => "Executa uma habilidade dinâmica local (script python/powershell).";
    public int RequiredLevel => 6;  // was 1 — D-11 / SEC-06

    // ChatToolDefinition unchanged

    public async Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        string skillName = ToolArgParser.Get(argumentsJson, "skill_name");
        string args      = ToolArgParser.Get(argumentsJson, "arguments");
        if (string.IsNullOrWhiteSpace(skillName)) return "ERRO: 'skill_name' é obrigatório.";

        var skill = SkillService.ListLocalSkills()
            .FirstOrDefault(s => s.Name.Equals(skillName, StringComparison.OrdinalIgnoreCase));
        if (skill == null) return $"Erro: Skill '{skillName}' não encontrada.";

        // Markdown-only skills bypass the modal — they don't execute a process. D-09.
        if (skill.Interpreter == "markdown") return await SkillService.RunSkillAsync(skillName, args);

        // D-09: read body + SHA256 (cap 50KB)
        var (body, hash) = ReadAndHash(skill.ScriptFile);

        var ctx = new CommandConfirmationContext
        {
            Tool = "execute_skill",
            Command = $"{skill.Interpreter} {skill.Name} {args}",
            Level = userLevel,
            Cwd = Path.GetDirectoryName(skill.ScriptFile) ?? "",
            ScriptBody = body,
            Interpreter = skill.Interpreter,
            ContentHash = hash,
        };

        // D-10: tuple key
        var key = (ctx.Tool, ctx.Command, ctx.ContentHash);
        if (!AlwaysAllowSession.Contains(key))
        {
            var (allowed, alwaysAllow) = await CommandConfirmationWindow.ShowAsync(ctx);
            if (!allowed)
            {
                _ = AuditLogService.AppendAsync(BuildEntry(ctx, "deny", false));
                return "Comando recusado pelo usuário";
            }
            if (alwaysAllow)
            {
                AlwaysAllowSession.Add(key);
                _ = AuditLogService.AppendAsync(BuildEntry(ctx, "always_allow", true));
            }
            else
            {
                _ = AuditLogService.AppendAsync(BuildEntry(ctx, "allow", false));
            }
        }
        else
        {
            _ = AuditLogService.AppendAsync(BuildEntry(ctx, "always_allow", true));
        }

        return await SkillService.RunSkillAsync(skillName, args);
    }
}
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `ProcessStartInfo.Arguments = $"..."` with manual quoting | `ProcessStartInfo.ArgumentList.Add(arg)` | .NET Core 2.1 (2018); flagged as security-preferred Apr 2024 (BatBadBut) | Eliminates argv-injection class entirely for direct `.exe` invocations; does NOT cover `.cmd` / `.bat` shims. |
| `cmd.exe /c {string}` with raw user input | Direct interpreter invocation (`py.exe`, `powershell.exe`) + ArgumentList | This phase (Phase 3) | Removes the cmd.exe metacharacter parser from the path; cmd.exe stays in scope ONLY for `run_command` post-modal (carve-out). |
| Substring denylist (`ContainsWord`) | Normalize → alias-expand → quote-collapse → `\b` regex (CommandFloorList) | This phase (Phase 3) | Closes PowerShell quote-concat, alias, `-EncodedCommand` bypasses CONCERNS.md flagged. |
| Pre-`_modalLock` parallel `ShowDialog` races | `SemaphoreSlim(1,1)` + shared `ShowAsync` helper | Phase 1 D1 (review CR-01) + Phase 3 D-08 (lift helper) | All three modal-bearing tools share one critical section; never two modals at once even when ReAct calls tools in parallel. |
| `AlwaysAllow` keyed by raw command string | Keyed by `(Tool, Cmd, ContentHash?)` | This phase (Phase 3 D-10) | Cache invalidates automatically when a skill file is edited on disk; defense vs. "silently swapped skill" attack. |

**Deprecated/outdated:**
- `DynamicSkillTool.cs` (Phase 3 D-12 deletes): the lazy-loaded `execute_skill` indirection (`ToolRegistry.cs:9-12` comment) supersedes per-skill first-class tools. Zero call sites today.
- `safeArgs.Replace("\n", " ").Replace("\r", "")` as a security control (Phase 3 D-06 demotes to defensive `\0`-style trim): ArgumentList already preserves literal bytes including newlines; the strip is cosmetic for log readability.
- `ContainsWord` whole-word matcher at `NativeTools.cs:311-319`: superseded by `CommandFloorList.Match`. Delete entirely.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | `Process.Start("npx.cmd", ...)` invokes the npm cmd-shim path resolution correctly on a typical Windows dev box | Pitfall #2 + Example 3 | Spawn fails. Mitigated by the `npx` fallback in the same try/catch. Failure mode is the existing CONCERNS.md "InstallFromOnlineAsync has zero call sites today" — dead code, so the regression risk is academic until a future caller appears. |
| A2 | `SHA256.HashData(byte[])` (static, .NET 5+ API) is available in the project's `net8.0-windows10.0.19041.0` target | Pattern 4 + Example 4 | Compile error. Mitigation: trivial to swap for `using var sha = SHA256.Create(); sha.ComputeHash(bytes);` (.NET Standard 2.0 surface). |
| A3 | The `-EncodedCommand` outright refusal does NOT cause false positives on legitimate user commands | D-03 step 4 + Pitfall #3 | A user typing a script that contains the literal substring `-EncodedCommand` (e.g. documenting it) hits the floor. Mitigation: the modal still fires first; the user sees the command verbatim and can either retype it or elevate to L≥7 where the floor is inactive. |
| A4 | `py.exe` is on PATH on the user's box (the Python Launcher for Windows ships with the official Python installer) | D-07 InterpreterMap | If the user installed Python via Microsoft Store or chocolatey without the launcher, `py.exe` is missing and spawn fails. Mitigation: same as existing behavior at `SkillService.cs:198` which already uses `py "scriptPath"`. No regression. |
| A5 | The new ScrollViewer row's `MaxHeight=200` is acceptable for the longest reasonable skill body (rendered behavior, not security) | D-08 + XAML | Visual-only; long bodies scroll. No security impact. |
| A6 | `CommandFloorList`'s regex catalog is sized correctly for Phase 3's stated scope (the four destructive-verb categories from D-02), not exhaustive | D-02 + Pattern 2 | The "floor" is best-effort by design (CONTEXT.md D-03: "documented as best-effort floor; the modal is the actual gate"). The modal at L<9 ∩ ON is the unconditional defense. |
| A7 | The model class `SkillMetadata` (`SkillService.cs:10-17`) is the authoritative source for `Interpreter`, and its values are limited to `python`/`powershell`/`cmd`/`markdown` | D-07 + Example 2 + Example 4 | A skill with an arbitrary interpreter string falls into "interpretador não suportado" — error returned, no spawn. Safe-by-default. |

**If a planner reads this table and any row's risk is unacceptable for the milestone, surface it to discuss-phase before locking the plan.** All seven assumptions are low-risk given the design's defense-in-depth posture (modal first, then floor, then ArgumentList), but they are documented honestly per the research protocol.

## Open Questions (RESOLVED)

1. **Should `materialize_skill`'s modal display also gate WHERE on disk the script will land?**
   - What we know: D-08 displays `script_content` as ScriptBody; D-09 mentions `Cwd` is the directory the script is being written to. The current modal shows `Cwd` already (Phase 1 D3).
   - What's unclear: whether the modal should explicitly call out "será gravado em ~/.AIB/skills/{name}/{name}.{ext}" as a separate row, or whether the `Cwd` row is sufficient.
   - Recommendation: planner should include the resolved destination path in `Cwd` (or in a small caption above ScriptBody) so the user sees the write-target. This is a UI discretion item per CONTEXT.md.
   - **RESOLVED:** Surface the resolved destination path in the Command field of `CommandConfirmationContext` (e.g. `python -> C:\Users\...\AIB\skills\foo\foo.py`) so it appears in the existing modal "A AIB solicitou..." block. No new XAML row required — Cwd row already renders the parent directory; Command field renders the full destination. UI discretion item closed per CONTEXT.md.

2. **Does the `_modalLock` need to be re-entrant for an edge case where `materialize_skill` is followed by `execute_skill` in the same ReAct turn?**
   - What we know: ReAct loop calls tools sequentially within a single iteration boundary (`OpenAIService.cs:205` caps at 18 iterations); two modals back-to-back is the worst case.
   - What's unclear: whether the OpenAI tool-call schema allows truly parallel `materialize_skill` + `execute_skill` invocations.
   - Recommendation: keep the non-reentrant `SemaphoreSlim(1,1)`. Sequential modal display is the desired UX. If a future scenario requires interleaved modals, a `SemaphoreSlim(int.MaxValue, int.MaxValue)` swap is the patch.
   - **RESOLVED:** Keep the non-reentrant `SemaphoreSlim(1, 1)`. Sequential modal display is the desired UX for back-to-back `materialize_skill` + `execute_skill`. If a future scenario requires interleaved modals, the patch is a one-line swap to `SemaphoreSlim(int.MaxValue, int.MaxValue)`. No reentrancy logic added in Phase 3.

3. **At L9, should `materialize_skill` still require the modal?**
   - What we know: Phase 1 D5 says modal always fires regardless of level. SEC-06 raises `MaterializeSkillTool.RequiredLevel = 8` (modal still fires at L≥8 because RequiredLevel passed ≠ modal skipped).
   - What's unclear: whether the user at L9 truly wants confirmation for every skill materialize, or whether the AlwaysAllow checkbox is sufficient.
   - Recommendation: keep the modal at all levels (consistent with Phase 1 D5). AlwaysAllow per-skill-content-hash gives the L9 user an opt-out.
   - **RESOLVED:** Modal fires at all levels (Phase 1 D5 preserved). L9 users opt out via the per-content-hash AlwaysAllow checkbox — same UX as run_command. No level-conditional modal skip.

4. **`InstallFromOnlineAsync` is dead code today; should the regex live behind a `RequiredLevel ≥ 7` modal even though no caller exists?**
   - What we know: D-11 explicitly notes "Future callers MUST gate this behind a RequiredLevel ≥ 7 and the CommandConfirmationWindow flow". The XML doc comment in the implementation will warn about this.
   - What's unclear: whether the planner should add a `RequiredLevel ≥ 7` runtime check inside `InstallFromOnlineAsync` itself (defense-in-depth), even though no `ITool` exposes it today.
   - Recommendation: do NOT add the runtime level check. CONTEXT.md D-11 specifies "XML doc comment warns"; adding a runtime guard would require passing a `userLevel` parameter that has no caller. The regex + ArgumentList is the locked closure for SEC-06.
   - **RESOLVED:** No runtime `RequiredLevel >= 7` check inside `InstallFromOnlineAsync`. The XML doc comment on the method records the future-caller gating requirement (RequiredLevel >= 7 + ShowAsync hop). The regex + ArgumentList is the locked closure for SEC-06 per CONTEXT.md D-11. Adding a userLevel parameter is churn for zero call sites today.

## Environment Availability

> Phase 3 has no external dependencies beyond the existing `.NET 8` toolchain and Windows runtime APIs already used by the project. The audit below confirms the platforms / tools the implementation will exercise.

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET 8 SDK | All compile/test steps | ✓ (assumed — already builds in Phase 1 + Phase 2) | 8.x | — |
| `dotnet build AIBWindows/AIB.csproj` | T-N verification after D-12 delete | ✓ | — | — |
| `py.exe` (Python Launcher) | Skill exec smoke at UAT | ✓/? on dev box | — | `python.exe` if launcher missing (NO change to existing behavior; matches `SkillService.cs:198` semantics) |
| `powershell.exe` (Windows PowerShell 5.x) | Skill exec smoke at UAT | ✓ (Windows built-in) | 5.x | `pwsh.exe` not required |
| `npx.cmd` (npm v7+ cmd-shim) | `InstallFromOnlineAsync` smoke | ✓/? on dev box | — | `npx` fallback in same try/catch (Pitfall #2) — but path is dead code today anyway |
| WPF runtime (`Application.Current.Dispatcher`) | All modal hops | ✓ | .NET 8 | — |
| `~/.AIB/skills/` writable | `materialize_skill` UAT | ✓ (existing `DirectoryService` invariant) | — | — |
| `~/.AIB/logs/audit.log` writable | Audit JSONL append (Phase 1 invariant) | ✓ (existing) | — | — |

**Missing dependencies with no fallback:** None. All required surfaces are either BCL or established Phase 1 invariants.

**Missing dependencies with fallback:** `npx.cmd` → `npx` is the only fallback path, isolated to dead code (`InstallFromOnlineAsync`).

## Validation Architecture

> `workflow.nyquist_validation` is absent in `.planning/config.json` — treated as enabled per protocol. However, `NFR-01` (REQUIREMENTS.md) explicitly states "No new tests required to ship this milestone. Project has no test suite. Acceptance for each requirement is manual UAT (recorded in phase VERIFICATION.md)." This section therefore documents the **manual UAT validation strategy** with explicit per-requirement evidence, not an automated test plan. The planner MUST include a `VERIFICATION.md` scaffolding task following Phase 1's T9 pattern.

### Test Framework
| Property | Value |
|----------|-------|
| Framework | **None** — manual UAT only (NFR-01). |
| Config file | none |
| Quick run command | `dotnet build AIBWindows/AIB.csproj` (must report 0 errors, 0 new warnings) |
| Full suite command | Manual UAT scenarios codified in `03-VERIFICATION.md` (per planner task, mirroring `01-VERIFICATION.md` shape) |
| Phase gate | All UAT scenarios pass + tester sign-off + Phase 1 modal regression check |

### Phase Requirements → Test Map

| Req ID | Behavior | Test Type | Validation Approach | Evidence |
|--------|----------|-----------|---------------------|----------|
| SEC-04 | LLM-supplied arg `; rm -rf %USERPROFILE%\Documents` to a skill is a single literal argv element | manual UAT | Author a temporary "echo argv" skill (Python `print(sys.argv)` or PowerShell `$args | ForEach-Object { Write-Host $_ }`) registered as a default skill. Invoke `execute_skill` with `arguments` containing `; rm -rf C:\NotAReal\Path`. Observe modal showing the command + accept. Observe skill output containing the literal string `; rm -rf C:\NotAReal\Path` on a single line. Verify the path was NOT deleted by checking the parent directory before and after. | Skill output stdout copied into VERIFICATION.md scenario observed field. |
| SEC-04 | `cmd.exe /c` remains the post-modal path for `run_command` (carve-out) | manual UAT | Run `run_command` with `dir C:\`, observe modal, accept, verify directory listing returns. Confirm `CommandService.ExecuteAsync(string)` is unchanged via diff. | UAT pass + diff of `CommandService.cs:10-72` showing the original body extracted into `RunProcessAsync` but the public signature + behavior preserved. |
| SEC-05 | L<7 trivial bypass `"Remove" + "-Item" -Recurse C:\foo` is rejected by the floor after modal Allow | manual UAT | At L6: invoke `run_command` with `powershell -Command "& {'Remove' + '-Item' -Recurse C:\\TempTestDir}"`. Observe modal with amber AVISO banner. Click Allow. Verify tool returns the floor rejection string. Verify audit.log line has `outcome: "allow_then_floor_deny"` and `cmd` is the verbatim input. | audit.log line + UAT scenario observed field. |
| SEC-05 | L<7 `-EncodedCommand` is rejected by the floor | manual UAT | At L6: invoke `run_command` with `powershell -EncodedCommand R2V0LURhdGU=` (= `Get-Date`). Observe modal AVISO banner. Click Allow. Verify floor rejection. | audit.log + UAT field. |
| SEC-05 | L≥7 reaches the modal verbatim; floor inactive | manual UAT | At L9 (via `/unlock_level 9`): invoke `run_command` with `rm -rf C:\TempTestDir`. Observe modal WITHOUT amber banner. Click Deny. Verify tool returns "Comando recusado pelo usuário". | UAT field. |
| SEC-06 | `execute_skill` at L1 returns the standard "permissão negada" string (via existing `ToolRegistry.cs:37` level check) | manual UAT | At L1 (fresh launch): invoke `execute_skill` with `consultar_cep` skill. Verify tool returns the existing `ToolRegistry.cs:37` format string `"ACESSO NEGADO: A ferramenta 'execute_skill' exige Nível 6, mas o seu nível atual é 1."`. No modal appears. | UAT field + grep `ToolRegistry.cs:36-38` confirming string shape. |
| SEC-06 | `execute_skill` at L6 shows the modal with the script body | manual UAT | At L6: invoke `execute_skill` with `consultar_cep` (Python skill). Observe modal with `CONFIRMAÇÃO DE SCRIPT (python)` header, ScrollViewer rendering the Python source, Tool/Nível/Cwd rows intact. Click Allow. Verify skill output. | UAT field + screenshot. |
| SEC-06 | `materialize_skill` at L7 returns "permissão negada"; at L8 shows modal with script body | manual UAT | At L7: attempt `materialize_skill` with a one-line PowerShell. Verify denial. At L8: same call. Observe modal with `script_content` as ScriptBody. Verify file written to `~/.AIB/skills/{name}/` after Allow. | UAT field + filesystem check. |
| SEC-06 | `InstallFromOnlineAsync` rejects `..badrepo` and accepts `owner/repo@1.0.0` | manual UAT (smoke harness) | Open a debug harness or run `csc -run`-style scratch that calls `SkillService.InstallFromOnlineAsync(...)` with both inputs. Verify the first returns `"Erro: formato de skill inválido..."` immediately (no shell-out). Verify the second proceeds to the npx call (which will likely fail in the sandbox; observe the spawn attempt in console). | UAT field + console output. |
| Floor regression | Edit-in-place skill detection — modal re-fires after content change | manual UAT | At L6: `execute_skill consultar_cep` → AlwaysAllow checked. Edit `~/.AIB/.default_skills/consultar_cep/consultar_cep.py` (add a `print("modified")` line). Re-invoke `execute_skill consultar_cep`. Verify modal re-fires (hash mismatch invalidated AlwaysAllow). | UAT field. |
| Audit log | `allow_then_floor_deny` outcome appears with correct schema | manual UAT | After the SEC-05 L<7 scenario above, read `~/.AIB/logs/audit.log` final line. Verify it contains `"outcome":"allow_then_floor_deny"` and the schema otherwise matches Phase 1 D6. | audit.log line copy. |
| Audit log | `content_hash` field is null for run_command, populated for skill tools | manual UAT | After running both `run_command` and `execute_skill` in the same session, inspect the two audit lines. Verify `run_command` line has `"content_hash":null` (or omitted via JSON default) and `execute_skill` line has a 64-char hex string. | audit.log lines. |
| Build | `dotnet build AIBWindows/AIB.csproj` is 0/0 after `DynamicSkillTool` delete | build | Run `dotnet build`. Verify exit code 0, no new warnings. | CI-style log paste in VERIFICATION.md. |
| Build | `grep -rn DynamicSkillTool AIBWindows/` returns zero hits after delete | build | Grep the source tree post-delete. | Grep output. |

### Sampling Rate
- **Per task commit:** none (no unit tests).
- **Per wave merge:** `dotnet build AIBWindows/AIB.csproj` MUST be 0/0.
- **Phase gate:** All UAT scenarios in `03-VERIFICATION.md` pass + tester sign-off, before `/gsd-verify-work 3` is invoked.

### Wave 0 Gaps
- [ ] No test infrastructure to bootstrap — NFR-01 explicitly defers this to a future "Tests + CI" milestone.
- [ ] Planner MUST include a task to author `.planning/phases/03-tool-argument-hardening-quoting-denylist-skill-gating/03-VERIFICATION.md` following the Phase 1 `01-VERIFICATION.md` shape (header + scenarios with checkbox observed/pass-fail + sign-off). The scenarios above are the canonical set.
- [ ] Planner MUST include a task to provision a temporary "echo argv" skill in `~/.AIB/skills/_test_echo_args/` as part of the SEC-04 UAT preparation. This skill is deleted at the end of UAT.

## Security Domain

> Security enforcement is ON for this project (CLAUDE.md governance + `Regras de Identidade/SEGURANCA.MD` enforced + REQUIREMENTS.md NFR-03). This section documents the ASVS categories and STRIDE-style threats the phase mitigates, for the plan-checker's `<threat_model>` block.

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V1 Architecture, Design, Threat Modeling | yes | Documented in this file's threat model + CONTEXT.md decisions D-01..D-12 |
| V2 Authentication | no | No auth surface in this phase |
| V3 Session Management | partial | AlwaysAllow session state (D-10 tuple key); lifetime = process exit; no cross-session persistence |
| V4 Access Control | yes | `RequiredLevel` per-tool gating (`ToolRegistry.cs:36-38`); raised to 6/8 for skill tools (D-11 / SEC-06) |
| V5 Input Validation, Encoding, Output Encoding | yes | `installArg` regex (D-11); floor list normalize-then-regex (D-03); ArgumentList for argv quoting (D-05) |
| V6 Cryptography | partial | SHA256 for skill content hash (D-09) — used as a cache invalidation key, not as a security boundary |
| V7 Error Handling and Logging | yes | Audit log JSONL (D-04 + D-10); fire-and-forget; existing Phase 1 infrastructure |
| V8 Data Protection | no | No new persistent data in this phase |
| V9 Communication | no | No new network surface |
| V10 Malicious Code | yes | Script-body preview in modal (D-08, D-09) — the user reviews exact script before write/exec |
| V11 Business Logic | yes | Modal-first-then-floor invariant (D-04) preserves Phase 1 D5/D8 contracts |
| V12 File and Resources | yes | `npx.cmd` shim handling (Pitfall #2); skill file read cap 50KB (D-09) |
| V13 API and Web Service | no | No new API surface |
| V14 Configuration | partial | `ConfirmDangerousCommands` controls floor activation per D-01 (inherits Phase 1 D8) |

### Known Threat Patterns for AIBWindows (.NET 8 / WPF / OpenAI ReAct)

| Pattern | STRIDE | Standard Mitigation | Phase 3 Closure |
|---------|--------|---------------------|-----------------|
| **Argv injection via `;`, `&`, `|`, backtick, `$()`** in skill arguments | Tampering | `ProcessStartInfo.ArgumentList` (BCL) — Microsoft listed "Not Affected" on CERT BatBadBut advisory | Closed by D-05 + D-06 + D-07. Skill args reach `py.exe` / `powershell.exe` directly; cmd.exe never sees them. |
| **Skill repo path traversal** in `InstallFromOnlineAsync` (`..badrepo`, `../../etc/passwd`, leading-dot, double-slash) | Tampering | Strict allowlist regex (D-11) | Closed by D-11 regex `^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(@[A-Za-z0-9_.\-]+)?$` BEFORE shell-out. Caveat: `..foo` matches the regex because `.` is in the class; planner should consider if this needs an explicit `[A-Za-z0-9]` leading-char anchor. **Surface this to plan-checker.** |
| **Level-1 skill execution** (LLM hallucinates a destructive skill and chains `materialize_skill → execute_skill` at L1) | Elevation of Privilege | RequiredLevel gating + modal-with-preview | Closed by D-11 raising `ExecuteSkillTool.RequiredLevel = 6` and `MaterializeSkillTool.RequiredLevel = 8`; both route through `CommandConfirmationWindow.ShowAsync`. |
| **Materialize-without-preview overwrites** (LLM silently swaps a trusted skill with a hostile body) | Tampering | Modal shows full script body before write (D-08) | Closed by D-08 — modal renders LLM-supplied `script_content` as ScriptBody before any disk write. |
| **Silent skill edit** (skill file edited on disk between AlwaysAllow approval and next exec) | Tampering | AlwaysAllow tuple key includes ContentHash (D-10) | Closed by D-10 — hash mismatch invalidates AlwaysAllow cache; modal re-fires showing new body. |
| **PowerShell `-EncodedCommand` denylist bypass** | Tampering | Outright refusal at L<7 (D-03 step 4) | Closed by D-03 step 4 — dedicated reject before the floor regex pass. |
| **PowerShell quote-concat bypass** (`"Remove" + "-Item"`) | Tampering | Normalize-then-regex with concat strip (D-03 step 2) | Closed by D-03 step 2 — regex `[""']\s*\+\s*[""']` collapses both quote styles before pattern match. |
| **PowerShell alias bypass** (`mv` for `Move-Item`, `ri` for `Remove-Item`) | Tampering | Alias expansion in normalize pipeline (D-03 step 3) | Closed by D-03 step 3 — destructive aliases per CONCERNS.md flagged set are expanded to canonical cmdlet names before regex match. |

### Residual Risks (post-Phase-3)

| Risk | Why Residual | Mitigation Path |
|------|--------------|------------------|
| `/unlock_level 9` chat backdoor | Owner-accepted (PROJECT.md / Phase 1 deferred) | Future "dev-mode gating" milestone |
| LLM-driven indirect injection via `read_screen` / `read_file` / `search_web` | Phase 4 scope (SEC-07) | Phase 4 wraps tool outputs in `UNTRUSTED EXTERNAL CONTENT` markers |
| `npx skills add` is dead code today but hardened defensively | Future caller could re-enable without modal hop | D-11 XML doc comment + plan-checker review of any future PR re-adding `InstallFromOnlineAsync` calls |
| Floor list is "best-effort by design" (CONTEXT.md D-03) | Modal is the unconditional authority; floor is second-layer hard floor | Acceptable per D-01 + Phase 1 D5; documented in SEGURANCA.MD rewrite |
| `Settings → ConfirmDangerousCommands OFF` makes the modal the only gate at all levels | Owner-controlled toggle inherited from Phase 1 D8 | Acceptable — modal is HITL by design |

## Project Constraints (from CLAUDE.md / governance)

- **CLAUDE.md (project root):** This project has a graphify knowledge graph at `graphify-out/`. **Verified absent** by `Glob graphify-out/**/*.md` returning zero files. The CLAUDE.md directive to "read graphify-out/GRAPH_REPORT.md for god nodes" is non-applicable for Phase 3; do not block on it. Planner may surface this as a stale CLAUDE.md instruction to discuss-phase if desired, but it does not affect Phase 3 scope.
- **`Regras de Identidade/CODIGO_LIMPO.MD`:**
  - No DI container; `ChatWindow.xaml.cs` is Composition Root. — Phase 3 adds NO new wiring in `ChatWindow`; all new types are static or instantiated by existing tools.
  - One service per system integration. — `CommandFloorList` (new file) is the one new service; `DynamicSkillTool` deletion (net negative file count).
  - Mandatory `ITool` contract. — `ExecuteSkillTool` + `MaterializeSkillTool` already implement `ITool`; this phase modifies their `ExecuteAsync` only.
  - Async + UI Dispatcher discipline. — All UI hops use `Application.Current.Dispatcher.InvokeAsync<T>`, matching Phase 1 D1.
  - Namespace aliases for ambiguity. — N/A for this phase.
- **`Regras de Identidade/SEGURANCA.MD`:**
  - "No silent shell" / modal always fires. — Phase 1 D5; Phase 3 D-04 preserves invariant.
  - DPAPI for secrets. — N/A.
  - `RequiredLevel` as access control. — Phase 3 D-11 raises levels per SEC-06.
  - Embeddings stay local. — N/A.
- **NFR-01:** No new tests this milestone. — Phase 3's validation is manual UAT (codified above).
- **NFR-03:** `SEGURANCA.MD` rewrite required at milestone end to describe shipped behavior. — Planner MUST include a task to update the "denylist" / "skill execution" / "argument quoting" paragraphs of `Regras de Identidade/SEGURANCA.MD` per Phase 3 outcomes (Phase 2's D-12 path established the present-tense pattern).
- **NFR-04:** No `profile.dat` or `memory.db` schema change. — Verified: Phase 3 touches neither.
- **NFR-05:** Modal must not add > 100ms to non-shell tool calls. — N/A for Phase 3 (only shell + skill tools route through modal; memory/vault/read_screen still skip per Phase 1 invariant).
- **`atomic_commits: true`** in `config.json`. — Each task commit MUST be one logical change; planner must split waves accordingly.
- **`commit_style: conventional`**. — Use `feat(security):`, `refactor(skill):`, `docs(security):` per phase 1 + phase 2 commit history.

## Sources

### Primary (HIGH confidence)
- **Microsoft Learn — `ProcessStartInfo.ArgumentList` Property:** [VERIFIED via WebFetch] — "Strings added to the list don't need to be previously escaped." Confirmed `ArgumentList` and `Arguments` are mutually exclusive. URL: https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.argumentlist?view=net-8.0
- **Microsoft Learn — `ProcessStartInfo.UseShellExecute` conceptual:** [VERIFIED via WebFetch] — Confirmed that with `UseShellExecute = false`, `FileName` must be a fully qualified path OR a simple executable name resolved via PATH; OS-level `CreateProcess` does NOT honor PATHEXT. URL: https://learn.microsoft.com/en-us/dotnet/fundamentals/runtime-libraries/system-diagnostics-processstartinfo-useshellexecute
- **CERT/CC BatBadBut Advisory (Vulnerability Note VU#123335):** [VERIFIED via WebFetch] — Microsoft listed "Not Affected" as of 2024-04-17. URL: https://www.kb.cert.org/vuls/id/123335
- **dotnet/corert `PasteArguments.cs` source:** [VERIFIED via WebFetch] — Confirmed escape mechanism is CommandLineToArgvW round-trip only (quote/backslash); no cmd.exe metacharacter escaping. URL: https://github.com/dotnet/corert/blob/master/src/System.Private.CoreLib/shared/System/PasteArguments.cs
- **Codebase: `AIBWindows/Services/NativeTools.cs`** — full read; line numbers cited throughout.
- **Codebase: `AIBWindows/Services/CommandService.cs`** — full read.
- **Codebase: `AIBWindows/Services/SkillService.cs`** — full read.
- **Codebase: `AIBWindows/Services/DynamicSkillTool.cs`** — full read; deletion target.
- **Codebase: `AIBWindows/Services/ToolRegistry.cs`** — full read; confirmed D-12's "zero registration" claim.
- **Codebase: `AIBWindows/Views/CommandConfirmationWindow.xaml(.cs)`** — full read.
- **Codebase: `AIBWindows/Services/CommandConfirmationContext.cs`, `AlwaysAllowSession.cs`, `AuditLogService.cs`, `SettingsService.cs`** — full read.
- **`.planning/phases/03-tool-argument-hardening-quoting-denylist-skill-gating/03-CONTEXT.md`** — locked decisions D-01..D-12 + canonical_refs + code_context.
- **`.planning/phases/01_modal-and-level9/PLAN.md` + `CONTEXT.md` + `PATTERNS.md`** — Phase 1 invariants D1..D8.
- **`.planning/REQUIREMENTS.md`** §SEC-04, §SEC-05, §SEC-06, §NFR-01, §NFR-03, §NFR-05.
- **`.planning/codebase/CONCERNS.md`** — the three HIGH findings being closed + the resolved entries pattern from Phase 1.

### Secondary (MEDIUM confidence)
- **Flatt Security BatBadBut research article:** [CITED — affected-language list does NOT include .NET; Microsoft "Not Affected" cross-checked via CERT.] URL: https://flatt.tech/research/posts/batbadbut-you-cant-securely-execute-commands-on-windows/
- **GitHub dotnet/runtime issue #67950:** [CITED — confirms `ArgumentList` is the .NET 6+ recommendation for `Process.Start` security.] URL: https://github.com/dotnet/runtime/issues/67950
- **npm cmd-shim README:** [CITED — confirms `.cmd` shim convention for npm-based Windows tools.] URL: https://github.com/npm/cmd-shim

### Tertiary (LOW confidence — flagged for validation if any locked decision depended on them; none did)
- **Generic WebSearch results for PowerShell `-EncodedCommand` detection regex** — used only to confirm the existence of the bypass pattern; the floor's outright-refusal approach is independent of any specific regex shape.
- **GitHub `cmd-shim` issue #45 on shell-metachar paths** — used only as supporting evidence for the `npx.cmd` PATHEXT issue.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — All BCL surfaces; no third-party additions; CONTEXT.md locks design.
- Architecture: HIGH — Three patterns (ArgumentList, hybrid floor, modal hop) all extend Phase 1 invariants with no new threading primitives.
- Pitfalls: HIGH — BatBadBut, `.cmd` PATHEXT, quote-concat, `-EncodedCommand`, alias bypass all verified against external sources (CERT, Microsoft Learn, CONCERNS.md flagged set).
- Security threat model: HIGH — Eight tampering/EoP threats enumerated with explicit closure refs to D-01..D-11.
- Validation strategy: MEDIUM — NFR-01 explicitly defers automation; manual UAT scenarios are the binding gate per project convention; planner must include VERIFICATION.md scaffolding task.

**Research date:** 2026-05-31
**Valid until:** 2026-07-01 (60 days — stable design, BCL surfaces only; revisit if .NET 9 ships a `cmd.exe` shim handler or if CONCERNS.md gains new HIGH findings in the tool argv space).

## RESEARCH COMPLETE
