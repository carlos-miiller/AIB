# Phase 3: Tool argument hardening (quoting + denylist + skill gating) - Context

**Gathered:** 2026-05-31
**Status:** Ready for planning

<domain>
## Phase Boundary

This phase eliminates command-injection through tool arguments and locks
down skill execution behind the Phase-1 modal. Three requirements close:

- **SEC-04** — `ProcessStartInfo.ArgumentList` for all tool-internal exec
  (skills, npx install path). `run_command` keeps its post-modal raw cmd
  string per REQUIREMENTS SEC-04 carve-out.
- **SEC-05** — Replace the porous `ContainsWord` denylist
  (`NativeTools.cs:311-319`, `:413-448`) with a **hybrid**: a tiny
  destructive-verb floor list that refuses at L<7, modal-only authority
  at L≥7. Closes the deferred denylist-flag slot from Phase 1 D3.
- **SEC-06** — `ExecuteSkillTool.RequiredLevel = 6` (was 1),
  `MaterializeSkillTool.RequiredLevel = 8` (was 5); both route through
  the Phase-1 `CommandConfirmationWindow`; `materialize_skill` modal
  shows the full script body; `SkillService.InstallFromOnlineAsync`
  regex-validates `installArg`.

**In scope:** AIBWindows only. `NativeTools.cs` (RunCommandTool denylist
removal/replacement, ExecuteSkillTool, MaterializeSkillTool),
`CommandService.cs` (new `ExecuteWithArgListAsync`),
`SkillService.cs` (RunSkillAsync rewrite, MaterializeSkillAsync, hardened
InstallFromOnlineAsync), `CommandConfirmationContext.cs` +
`CommandConfirmationWindow.xaml(.cs)` (ScriptBody/Interpreter fields +
ScrollViewer row + DenylistHit/DenylistReason fields),
`AlwaysAllowSession.cs` (re-keyed by tuple). Delete `DynamicSkillTool.cs`.

**Out of scope:** AIBLinux (deferred milestone). Tech-debt split of
`NativeTools.cs` god-file. UI for level changes. Settings persistence of
AlwaysAllow (still session-only per Phase 1 D2). `/unlock_level` chat
backdoor (accepted risk, PROJECT.md). Live API key validation (Phase 2
deferred). Retry policy on the new ArgumentList path (REL-02 is Phase 6).
"Factory reset" button (deferred — see Deferred Ideas).

</domain>

<decisions>
## Implementation Decisions

### SEC-05 — Denylist replacement

- **D-01 — Hybrid floor + modal-only.** Delete `ContainsWord` and the
  multi-table `ApplyDenylist` (sysDirs / userLevel<=4 / destructives /
  netCmds tables at `NativeTools.cs:413-448`). Replace with a single
  small **floor list** that refuses ONLY at userLevel < 7. At
  userLevel ≥ 7 the modal is the sole authority (per Phase 1 D5 — modal
  always fires regardless). The Phase 1 `ConfirmDangerousCommands`
  setting still gates whether the floor list runs at all (default ON =
  floor active; OFF = modal-only at every level).

- **D-02 — Floor list contents.** Four categories of destructive verbs:
  - Recursive deletion: `rm -rf`, `rm -r`, `del /s`, `del /f /s`,
    `rmdir /s`, `Remove-Item -Recurse`, `Remove-Item -Force -Recurse`.
  - Format / partition: `format`, `diskpart`, `wmic logicaldisk`,
    `cipher /w`.
  - Shutdown / reboot / logoff: `shutdown`, `Restart-Computer`,
    `Stop-Computer`, `logoff`.
  - Registry destructive ops: `reg delete`,
    `Remove-ItemProperty -Path HK*`, `Remove-Item -Path HK*`.

  Lives as a `static readonly` array of `(Regex Pattern, string
  Category, string Reason)` in a new `CommandFloorList` static helper
  (own file: `AIBWindows/Services/CommandFloorList.cs`) so the table
  is auditable in one place. Reason strings are user-visible Portuguese
  to match existing tool messages (e.g.
  `"Comando de deleção recursiva — requer Nível 7."`).

- **D-03 — Matching algorithm: normalize-then-regex.**
  Pre-processing pipeline before pattern match:
  1. Lowercase.
  2. Strip quote-concat artifacts: collapse `"x" + "y"` and
     `'x' + 'y'` patterns into `xy` (PowerShell concat bypass).
  3. Expand a small alias map: `mv -> Move-Item`, `ri -> Remove-Item`,
     `ni -> New-Item`, `sc -> Set-Content`, `ac -> Add-Content`,
     `gci -> Get-ChildItem` (only the destructive aliases CONCERNS.md
     flagged).
  4. If the command contains `-EncodedCommand` (any case), refuse at
     L<7 outright with reason
     `"powershell -EncodedCommand não é avaliável — requer Nível 7."`
     This is the only floor entry NOT in the verb table.
  5. Run each floor regex with `\b` word boundaries against the
     normalized string. First match wins.

  Documented in `SEGURANCA.MD` (rewritten by Phase 2 D-12 path) as
  **best-effort floor; the modal is the actual gate**.

- **D-04 — Modal fires first; floor refuses after Allow at L<7.**
  Preserves Phase 1 D5 ("modal ALWAYS fires regardless of
  level/setting") + D8 (`ConfirmDangerousCommands` gates only the
  post-modal denylist). Extend `CommandConfirmationContext` with two
  fields:
  ```csharp
  public bool   DenylistHit    { get; init; } = false;
  public string? DenylistReason { get; init; }   // user-visible PT
  ```
  RunCommandTool runs the floor-match BEFORE building the context,
  sets these two fields, then shows the modal. The modal displays an
  amber `AVISO:` banner under CommandText when `DenylistHit == true`
  (e.g. `"AVISO: este comando será recusado pelo floor list após
  aprovação (nível atual = N). Razão: <DenylistReason>"`). User can
  still click Allow; the post-modal floor check then refuses and
  RunCommandTool returns the `DenylistReason`. New audit-log outcome
  string: `"allow_then_floor_deny"` (joins the existing
  `allow / deny / always_allow / deny_no_ui` set from Phase 1 D6).

### SEC-04 — Argument quoting

- **D-05 — Extend `CommandService` with `ExecuteWithArgListAsync`.**
  New signature:
  ```csharp
  public static async Task<string> ExecuteWithArgListAsync(
      string fileName,
      IEnumerable<string> args,
      string? workDir = null,
      int timeoutMs = 20000)
  ```
  Builds `ProcessStartInfo` with `UseShellExecute = false`,
  `RedirectStandardOutput/Error = true`, `CreateNoWindow = true`,
  `StandardOutputEncoding = Encoding.UTF8`, `WorkingDirectory` mirror
  of existing `ExecuteAsync`, and populates `psi.ArgumentList` from
  the enumerable. Reuses the same output-capture / timeout-kill /
  50KB truncation block as `ExecuteAsync` (extract the shared tail
  into a private `RunProcessAsync(Process)` helper so both methods
  share the read pump and truncation logic; no copy-paste).
  `ExecuteAsync(string)` stays unchanged for the `run_command`
  post-modal path (REQUIREMENTS SEC-04: "after approval, executed via
  cmd as the user typed it").

- **D-06 — LLM `arguments` string passes as a SINGLE argument.**
  `SkillService.RunSkillAsync` builds
  `ArgumentList = [scriptPath, argumentsString]` (after standard
  interpreter switches from D-07). Scripts that need multi-arg parse
  their own `sys.argv[1]` / `$args[0]`. Satisfies REQUIREMENTS SEC-04
  acceptance: `; rm -rf %USERPROFILE%\Documents` reaches the script as
  one literal argv element, never seen by cmd.exe.
  `safeArgs.Replace("\n", " ").Replace("\r", "")` whitespace scrub
  goes away — ArgumentList serializes literal bytes, no shell
  interpretation. (The replace is kept ONLY as a defensive trim
  against `\0`-style edge cases inside the single argument; document
  inline why.)

- **D-07 — Interpreter mapping table (single source of truth).**
  ```csharp
  // SkillService.cs
  private static readonly Dictionary<string, (string FileName, string[] Switches)>
      InterpreterMap = new(StringComparer.OrdinalIgnoreCase)
  {
      ["python"]     = ("py.exe",         Array.Empty<string>()),
      ["powershell"] = ("powershell.exe", new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File" }),
      ["cmd"]        = ("cmd.exe",        new[] { "/c" }),
  };
  ```
  Both `RunSkillAsync` and `InstallFromOnlineAsync` consume this table.
  Unknown interpreter → `"Erro: interpretador '{x}' não suportado."`
  (matches existing wording at `SkillService.cs:201`).
  Final ArgumentList = `Switches.Concat(new[] { scriptPath,
  argumentsString })`. `DynamicSkillTool` is deleted (D-12) so it is
  not a consumer.

### SEC-06 — Skill gating + modal

- **D-08 — Modal extends in place; no second window.** Add three
  optional fields to `CommandConfirmationContext`:
  ```csharp
  public string? ScriptBody   { get; init; }  // null = command-line only
  public string? Interpreter  { get; init; }  // for header label
  public string? ContentHash  { get; init; }  // SHA256 of ScriptBody, for D-10 cache
  ```
  `CommandConfirmationWindow.xaml` grows one new `<RowDefinition
  Height="Auto"/>` containing a `<ScrollViewer MaxHeight="200"
  VerticalScrollBarVisibility="Auto">` wrapping a read-only
  `<TextBox FontFamily="Consolas" FontSize="12" IsReadOnly="True"
  AcceptsReturn="True" TextWrapping="NoWrap"/>`. The whole row's
  Visibility binds to `ctx.ScriptBody == null ? Collapsed : Visible`.
  Window height becomes auto-sized; existing 380×450 stays as the
  minimum for the command-only case. Header label flips from
  `"CONFIRMAÇÃO DE COMANDO"` to
  `"CONFIRMAÇÃO DE SCRIPT ({Interpreter})"` when ScriptBody is
  present. Reuses Phase 1 lock, IsAllowed/AlwaysAllow output, and
  audit pipeline. `execute_skill` and `materialize_skill` both
  construct their own `CommandConfirmationContext` and call the same
  dispatcher hop pattern from `RunCommandTool` (lift the Phase 1
  modal-hop body into a small private helper —
  `CommandConfirmationWindow.ShowAsync(ctx)` returning
  `(bool allowed, bool alwaysAllow)` — so the three tools share it).

- **D-09 — `execute_skill` shows the resolved script body, cached
  by (skill name + content SHA256).** First call per session for a
  given `(skillName, sha256(scriptFileBytes))` pair: read the file
  (cap at 50KB; tail-truncate with `"\n\n[... SCRIPT TRUNCATED]"`),
  compute SHA256, populate `ctx.ScriptBody` + `ctx.ContentHash`,
  show the modal. Subsequent calls with a HIT in the AlwaysAllow set
  (per D-10) skip the modal entirely. If the disk content changes
  between calls (hash differs), the cache miss re-fires the modal
  with the new body — defends the "silently edited skill" attack.
  Markdown-only skills (`Interpreter == "markdown"` in
  `SkillMetadata`) skip the modal entirely; they return text per
  existing `RunSkillAsync:191-192` and never execute a process.

- **D-10 — `AlwaysAllowSession` re-keyed by tuple.** Today:
  `HashSet<string>` keyed by raw command. New:
  `HashSet<(string Tool, string Cmd, string? ContentHash)>`. Same
  session-only lifetime (cleared on `App.Exit`), same lock-guarded
  semantics, exact-match comparison. For `run_command`, ContentHash
  is null and the tuple effectively degrades to (Tool, Cmd). For
  `execute_skill` / `materialize_skill`, ContentHash carries the
  SHA256 from D-09. Audit-log schema gains an optional
  `content_hash` field (null for run_command); JSONL line stays
  one-per-outcome.

- **D-11 — `InstallFromOnlineAsync` hardened + future-ready.**
  Method stays in `SkillService.cs` (zero call sites today, but
  REQUIREMENTS SEC-06 mandates the regex). Add at line 154 BEFORE
  any shell-out:
  ```csharp
  if (!Regex.IsMatch(installArg, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(@[A-Za-z0-9_.\-]+)?$"))
      return "Erro: formato de skill inválido (esperado: owner/repo[@version]).";
  ```
  Replace the `cmd /c call npx -y skills add ...` shell-out with
  `CommandService.ExecuteWithArgListAsync("npx",
      new[] { "-y", "skills", "add", installArg, "--yes" },
      workPath, 900000)`. `npx` resolves via PATH (no `cmd /c`
  intermediary). XML doc comment on the method body warns: "Future
  callers MUST gate this behind a RequiredLevel ≥ 7 and the
  CommandConfirmationWindow flow (see D-08); currently dead code."
  Closes SEC-06 acceptance criterion `npx skills add ..badrepo
  rejected; owner/repo@1.0.0 accepted` without depending on a
  caller existing.

- **D-12 — Delete `AIBWindows/Services/DynamicSkillTool.cs`
  entirely.** Class has zero registration in `ToolRegistry`
  (verified — `ToolRegistry.cs:65-81` lists 12 tools; DynamicSkillTool
  is absent and the comment at `:9-12` explicitly states "skills
  dinâmicas não são mais registradas aqui individualmente"). The
  class duplicates the same unsafe `py "script" {args}` / `powershell
  ... {args}` interpolation pattern SEC-04 is eliminating. Removing
  it: shrinks the binary; eliminates a parallel attack surface; and
  removes the only other consumer that D-07's interpreter table
  would have needed to serve. Audit existing csproj `<Compile>`
  globs to confirm no manual include line needs editing.

### Claude's Discretion

- Exact XAML margins, padding, gradient choice for the new
  ScrollViewer row; auto-sizing window height behavior; banner color
  for the `DenylistHit` warning (suggest the existing
  `WarningAccent` gradient). All within D-08's "extend
  CommandConfirmationWindow in place" frame and Phase 1 D7's visual
  language.
- Exact Portuguese phrasing of floor-list reasons + DenylistReason
  banner; should follow the `"ACESSO NEGADO (SANDBOX): ..."` pattern
  already in NativeTools but switch SANDBOX → FLOOR.
- Whether `CommandConfirmationWindow.ShowAsync(ctx)` helper lives as
  a static method on the window class, on `CommandConfirmationContext`,
  or as a new tiny `ModalConfirmationService` — pick whichever
  minimizes coupling. Same Dispatcher.InvokeAsync + _modalLock
  pattern as `NativeTools.cs:362-375`.
- Exact extraction shape of `RunProcessAsync(Process)` helper in
  `CommandService` (D-05) — split however keeps both
  `ExecuteAsync` and `ExecuteWithArgListAsync` under ~50 lines each.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Requirements + governance
- `.planning/REQUIREMENTS.md` §SEC-04, §SEC-05, §SEC-06 — authoritative
  requirements; acceptance criteria are quoted in the success_criteria
  block of Phase 3 in ROADMAP.md and re-stated here in D-01..D-11.
- `.planning/ROADMAP.md` "Phase 3" section — scope + success criteria;
  "Files touched" list aligns with D-01..D-12.
- `Regras de Identidade/SEGURANCA.MD` — declared posture. Phase 3 MUST
  update the "denylist" / "skill execution" / "argument quoting"
  paragraphs to describe the hybrid floor + ArgumentList + L6/L8 skill
  levels per NFR-03. The Phase 2 rewrite (key path) already sets the
  present-tense pattern.
- `.planning/codebase/CONCERNS.md` HIGH findings —
  "RunCommandTool denylist uses naive substring/word matching",
  "ExecuteSkill / MaterializeSkill allow arbitrary script execution",
  "Skill execution and command execution argument quoting are unsafe".
  All three move to a "Resolved" section with Phase 3's last commit
  SHA at milestone close.

### Prior phase context
- `.planning/phases/01_modal-and-level9/CONTEXT.md` §D1 — modal
  threading pattern (Dispatcher.InvokeAsync + _modalLock). The skill
  modal-hop helper in D-08 reuses this verbatim.
- `.planning/phases/01_modal-and-level9/CONTEXT.md` §D2 — AlwaysAllow
  scope (session-only HashSet, lock-guarded). D-10 re-keys but keeps
  the lifetime + lock contract.
- `.planning/phases/01_modal-and-level9/CONTEXT.md` §D3 — modal text
  schema (Tool/Command/Level/Cwd). D-08 extends with ScriptBody /
  Interpreter / ContentHash; D-04 extends with DenylistHit /
  DenylistReason. The "denylist hit flag deferred to Phase 3" note in
  Phase 1 D3 is closed here by D-04.
- `.planning/phases/01_modal-and-level9/CONTEXT.md` §D5 — modal
  always fires; L9 only changes denylist. D-01 preserves this; the
  hybrid floor still runs post-modal at L<7.
- `.planning/phases/01_modal-and-level9/CONTEXT.md` §D6 — AuditLog
  JSONL schema. D-04 adds `allow_then_floor_deny` outcome; D-10 adds
  optional `content_hash` field.
- `.planning/phases/01_modal-and-level9/CONTEXT.md` §D8 —
  `ConfirmDangerousCommands` setting gates ONLY post-modal denylist
  at L<9. D-01 inherits this exactly; the new floor list is the new
  "post-modal denylist".
- `.planning/phases/02-key-rotation-env-hardening/02-CONTEXT.md` §D6 —
  `OpenAIService.cs:657, 705` use-vault sentinel. Reference only;
  Phase 3 does not touch OpenAIService.

### Code references (read before editing)
- `AIBWindows/Services/NativeTools.cs:282-461` (`RunCommandTool`) —
  Phase 1 modal flow lives here. D-04 inserts the floor-match BEFORE
  the context build at `:332-338`. D-01 deletes `ContainsWord` at
  `:311-319` and `ApplyDenylist` at `:413-448` (replaced by the new
  `CommandFloorList` helper).
- `AIBWindows/Services/NativeTools.cs:549-576` (`ExecuteSkillTool`)
  and `:582-612` (`MaterializeSkillTool`) — `RequiredLevel` raises to
  6 and 8 respectively; both grow a modal-hop block before calling
  `SkillService.RunSkillAsync` / `MaterializeSkillAsync` (D-08, D-09).
- `AIBWindows/Services/SkillService.cs:147-177`
  (`InstallFromOnlineAsync`) — regex check inserted at `:154` per
  D-11; npx invocation rewritten to ExecuteWithArgListAsync.
- `AIBWindows/Services/SkillService.cs:186-205` (`RunSkillAsync`) —
  switch expression at `:196-202` deleted; replaced by D-07 mapping
  table + ExecuteWithArgListAsync call per D-05/D-06.
- `AIBWindows/Services/SkillService.cs:207-242`
  (`MaterializeSkillAsync`) — body unchanged; the modal flow lives in
  `MaterializeSkillTool.ExecuteAsync` (NativeTools.cs:602-611). The
  modal reads `script_content` argument and passes it as `ScriptBody`
  before MaterializeSkillAsync ever runs.
- `AIBWindows/Services/CommandService.cs` — entire file rewritten
  per D-05; existing `ExecuteAsync(string)` body extracts the shared
  `RunProcessAsync(Process)` helper.
- `AIBWindows/Services/CommandConfirmationContext.cs` — three new
  init-only properties per D-04 and D-08 (DenylistHit, DenylistReason,
  ScriptBody, Interpreter, ContentHash).
- `AIBWindows/Views/CommandConfirmationWindow.xaml` /
  `.xaml.cs` — new RowDefinition + ScrollViewer + TextBox per D-08;
  amber banner control bound to DenylistHit per D-04; constructor
  reads the new context fields.
- `AIBWindows/Services/AlwaysAllowSession.cs` — re-keyed from
  `HashSet<string>` to `HashSet<(string Tool, string Cmd, string?
  ContentHash)>` per D-10. Public API (`Contains` / `Add` / `Clear`)
  takes the tuple instead of bare string; RunCommandTool /
  ExecuteSkillTool / MaterializeSkillTool call sites updated.
- `AIBWindows/Services/AuditLogService.cs` (Phase 1) — entry object
  extends with optional `content_hash` field (null for run_command);
  the JSONL emitter already handles null via System.Text.Json default.
- `AIBWindows/Services/ToolRegistry.cs:65-81` — registration list
  unchanged (already includes ExecuteSkillTool + MaterializeSkillTool).
- `AIBWindows/Services/DynamicSkillTool.cs` — entire file DELETED
  per D-12. Verify no `<Compile Remove="..."/>` or other csproj entry
  references it (currently included via SDK-style globbing).

### Linux (out of scope, listed for traceability)
- `AIBLinux/main.py`, `AIBLinux/app/openai_client.py` — unchanged;
  Linux skill exec is not a concept in the lite client.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets

- **`CommandConfirmationWindow` + `CommandConfirmationContext`** — the
  Phase-1 modal infrastructure is already in place and was explicitly
  designed (per Phase 1 D3) to accept tool names beyond `run_command`.
  Phase 3 only adds optional fields; no rewrite.
- **`_modalLock` SemaphoreSlim + Dispatcher.InvokeAsync pattern**
  (`NativeTools.cs:284-288`, `:362-375`) — handles serialization of
  parallel tool calls. The new modal-hop helper for skills (D-08)
  reuses this verbatim; no new concurrency primitive.
- **`AuditLogService.AppendAsync` + `BuildEntry`**
  (`NativeTools.cs:451-460`) — JSONL append-only, fire-and-forget,
  SemaphoreSlim-guarded. D-04's `allow_then_floor_deny` outcome and
  D-10's `content_hash` field slot in by extending the anonymous
  object passed to AppendAsync. No new file or service.
- **`AlwaysAllowSession`** (Phase 1 D2) — the static singleton; D-10
  re-keys without changing the lifetime semantics.
- **`DirectoryService.DataDir`** — already the source of truth for
  `~/.AIB`; SkillService.SkillsDir derives from it; nothing to add.
- **`SettingsService.LoadSettings` / `ConfirmDangerousCommands`** —
  the Phase 1 D8 field that the new floor list reads at the same call
  site as today's denylist (`NativeTools.cs:399`).

### Established Patterns

- **Modal first, side effect second.** Every Phase 1 / Phase 3
  destructive tool runs: modal hop → audit log → execution. D-04 and
  D-08 keep this order intact.
- **Audit log first on each decision boundary.** Phase 1 D6 emits a
  JSONL line on every modal outcome; Phase 3 emits one per new
  outcome (`allow_then_floor_deny`) and one per skill modal outcome
  (with `content_hash`).
- **DPAPI / session-only for secrets and approvals.** Phase 1 D2
  established that approvals do not survive process exit. D-10
  re-keys but does not change the lifetime; AlwaysAllow remains
  in-memory only.
- **Single-source-of-truth interpreter mapping.** D-07 mirrors the
  pattern Phase 2 used for the provider-aware FirstRunWindow detector
  (single static method, both call sites consume) — same goal: avoid
  duplicate switch expressions drifting apart.
- **Settings field gating only the post-modal denylist.** Phase 1 D8
  explicitly carves the modal out of `ConfirmDangerousCommands`
  control. D-01 + D-04 follow the carve-out: floor list is gated by
  the setting at L<9 ∩ L<7; the modal is unconditional.

### Integration Points

- **`RunCommandTool.ExecuteAsync` (NativeTools.cs:322-407)** —
  Phase 3 inserts (a) floor-list precheck → fills DenylistHit /
  DenylistReason on the context, (b) post-modal floor-refuse branch
  at the existing denylist gate (`:399-403`).
- **`ExecuteSkillTool.ExecuteAsync` (:568-575)** — grow a modal-hop
  block: read scriptPath via `SkillService.ListLocalSkills`, compute
  SHA256 + read body (skip if markdown interpreter), check
  AlwaysAllowSession by tuple, hop modal, then call
  `SkillService.RunSkillAsync`.
- **`MaterializeSkillTool.ExecuteAsync` (:602-611)** — same modal-hop
  block; ScriptBody = the LLM-supplied `script_content` argument
  itself (no disk read needed; the file does not exist yet).
- **`SkillService.RunSkillAsync` (:186-205)** — switch expression
  replaced by `InterpreterMap` lookup + `ExecuteWithArgListAsync`.
- **`SkillService.InstallFromOnlineAsync` (:147-177)** — regex check
  added; `cmd /c call npx ...` becomes
  `ExecuteWithArgListAsync("npx", new[] { "-y", "skills", "add",
  installArg, "--yes" }, ...)`.
- **`CommandService.ExecuteAsync` (:10-72)** — body refactored into
  `RunProcessAsync(Process)`; the new
  `ExecuteWithArgListAsync` calls the same helper after building
  ProcessStartInfo with ArgumentList.
- **`AlwaysAllowSession`** — public API changes; all three callers
  (RunCommandTool, ExecuteSkillTool, MaterializeSkillTool) updated.

</code_context>

<specifics>
## Specific Ideas

- Floor list table lives in `AIBWindows/Services/CommandFloorList.cs`
  as `public static class CommandFloorList { public static
  (bool Hit, string? Reason) Match(string command, int userLevel)
  { ... } }`. Single entry point keeps the regex + alias map + concat
  strip in one auditable file. Future-easy to unit-test once a test
  milestone lands.
- `CommandConfirmationWindow` script-body row uses Consolas 12pt
  matching the existing `CommandText` font choice
  (`CommandConfirmationWindow.xaml:46`).
- The DenylistHit banner uses the existing `WarningAccent`
  LinearGradientBrush (`:18-22`) for visual consistency with the ⚠
  header.
- `npx` invocation in `InstallFromOnlineAsync` resolves the
  executable via PATH. On Windows, `.cmd` shims exist for npm-based
  tools; `Process.Start("npx", args)` may not find the .cmd shim
  without `UseShellExecute = true`, which is exactly what
  ArgumentList forbids. Mitigation: try `"npx.cmd"` first; fall back
  to `"npx"` if that throws ERROR_FILE_NOT_FOUND. Document inline.
- DynamicSkillTool deletion: csproj is SDK-style with implicit
  `<Compile Include="**/*.cs"/>`; no explicit entry to remove.
  `git rm` is sufficient. Confirm `dotnet build` is 0/0 after.

</specifics>

<deferred>
## Deferred Ideas

- **"Factory reset" button** — owner raised this during gray-area
  selection. New capability: a UI button (likely in SettingsWindow)
  that wipes `~/.AIB/{memory.db,profile.dat,logs/,credentials/,
  skills/,chat/,reminders/}` and re-triggers FirstRunWindow. Outside
  Phase 3 scope (tool argument hardening). Belongs in its own phase
  in a future milestone — candidate Trigger: "Settings UX milestone"
  or a dedicated "Data hygiene" phase. Captured here so it does not
  fall off the radar.
- **Live API validation of `installArg`** — D-11 only does regex.
  A future quality phase could fetch the manifest from skills.sh
  before shelling out.
- **AlwaysAllow persistence across sessions** — Phase 1 D2 already
  defers this; D-10 keeps the same posture (session-only). If a
  future milestone adds persistence, the tuple key in D-10 is
  serializer-ready.
- **Unit tests for `CommandFloorList.Match`** — no test suite exists
  this milestone (NFR-01); a tests milestone is planned separately.
  Floor-list regex is the canonical "porous-vs-tight" check that
  would benefit most from a regression suite.
- **Splitting `NativeTools.cs`** — 946-line god-file, HIGH tech-debt
  finding. Phase 3 touches three tools inside it but does not
  refactor; refactor milestone covers this.
- **Re-enabling `DynamicSkillTool`** — deleted in D-12. If a future
  feature wants per-skill first-class tool entries (instead of the
  lazy `execute_skill` indirection), it would re-implement with
  ArgumentList + modal-hop from day one.
- **`/unlock_level` chat backdoor** — accepted risk per PROJECT.md
  / Phase 1; not touched by Phase 3.
- **Settings UI for the floor list** — toggling individual floor
  entries from the UI is out of scope; the setting is binary
  (`ConfirmDangerousCommands` on/off, inherited from Phase 1 D8).

</deferred>

---

*Phase: 3-tool-argument-hardening-quoting-denylist-skill-gating*
*Context gathered: 2026-05-31*
