---
phase: 03-tool-argument-hardening-quoting-denylist-skill-gating
plan: 02
subsystem: command-execution
tags: [security, sec-05, denylist, floor-list, modal, audit]
requires:
  - SEC-05 (REQUIREMENTS.md): post-modal denylist must cover the gaps CONCERNS.md flagged (quote-concat / alias / EncodedCommand)
  - Phase 1 D5 / D8: modal-first invariant and ConfirmDangerousCommands carve-out preserved
  - Phase 3 D-01..D-04: hybrid floor list at L<7, modal-only at L>=7
provides:
  - "AIBWindows/Services/CommandFloorList.cs — public static Match(string command, int userLevel) -> (bool Hit, string? Reason)"
  - "CommandConfirmationContext: DenylistHit (bool) + DenylistReason (string?) init-only fields (D-04)"
  - "CommandConfirmationWindow.xaml: DenylistBanner Border + DenylistText TextBlock reusing WarningAccent gradient"
  - "RunCommandTool.ExecuteAsync wired to floor precheck; new 'allow_then_floor_deny' audit outcome"
  - "BuildEntry carries optional content_hash field (null for run_command; Plan 03 populates SHA256 for skill tools)"
affects:
  - AIBWindows/Services/CommandFloorList.cs (NEW, 108 lines)
  - AIBWindows/Services/CommandConfirmationContext.cs (extended: +2 fields, +16 LOC)
  - AIBWindows/Views/CommandConfirmationWindow.xaml (extended: +5 lines for banner)
  - AIBWindows/Views/CommandConfirmationWindow.xaml.cs (extended: +9 LOC for banner toggle)
  - AIBWindows/Services/NativeTools.cs (rewired RunCommandTool, -65/+24 LOC; ApplyDenylist + ContainsWord deleted)
tech-stack:
  added: []                # no new packages; BCL-only (System.Text.RegularExpressions)
  patterns:
    - "Normalize-then-regex pipeline for command floor list (D-03): lowercase -> quote-concat collapse -> alias expansion -> EncodedCommand refuse -> word-boundary regex match"
    - "Init-only POCO extension preserves Phase 1 ctor compatibility (DenylistHit defaults to false)"
    - "WarningAccent LinearGradientBrush reuse — no new palette entries (VISUAL.MD compliance)"
key-files:
  created:
    - AIBWindows/Services/CommandFloorList.cs
  modified:
    - AIBWindows/Services/CommandConfirmationContext.cs
    - AIBWindows/Views/CommandConfirmationWindow.xaml
    - AIBWindows/Views/CommandConfirmationWindow.xaml.cs
    - AIBWindows/Services/NativeTools.cs
  deleted: []                # no file deletions — ContainsWord + ApplyDenylist are method-level removes inside NativeTools.cs
decisions:
  - "D-01 (Phase 3): CommandFloorList.Match short-circuits with (false, null) at userLevel >= 7. ConfirmDangerousCommands setting still gates the post-modal floor call. Phase 1 D5 (modal always fires) untouched."
  - "D-02 (Phase 3): Six regex entries shipped across four categories — recursive-delete (3 entries: rm -rf, del /s + rmdir /s, Remove-Item -Recurse), format/partition (1 entry: format/diskpart/wmic logicaldisk/cipher /w), shutdown (1 entry: shutdown/Restart-Computer/Stop-Computer/logoff), registry-destructive (1 entry: reg delete + Remove-ItemProperty -Path HK + Remove-Item -Path HK)."
  - "D-03 (Phase 3): Pipeline runs in fixed order — ToLowerInvariant -> _concatPattern.Replace(\"\") -> _aliasPattern.Replace via _aliases lookup (mv/ri/ni/sc/ac/gci) -> _encodedCmdPattern outright refuse -> _entries first-match-wins."
  - "D-04 (Phase 3): RunCommandTool.ExecuteAsync calls CommandFloorList.Match BEFORE building the ctx; ctx.DenylistHit / DenylistReason drive the modal AVISO banner; post-modal refuse path emits the new audit outcome 'allow_then_floor_deny'."
  - "BuildEntry content_hash ordering — chose Option A (literal (string?)null placeholder with TODO comment), since CommandConfirmationContext does NOT yet have the ContentHash property (Plan 03 adds it). Verified with grep before commit. Plan 03 first task swaps null for ctx.ContentHash."
  - "_modalLock semaphore at NativeTools.cs:288 retained verbatim. Plan 03 owns the modal-helper extraction (D-08, CommandConfirmationWindow.ShowAsync); lifting the lock now would create cross-plan churn."
metrics:
  duration: ~10min (3 atomic tasks, build x 3, all green on first attempt)
  completed: 2026-06-02
  files_created: 1
  files_modified: 4
  files_deleted: 0
  loc_delta: "+108 (CommandFloorList) +16 (Context) +5 (XAML) +9 (xaml.cs) -41 net (NativeTools: +24 / -65) = +97 net LOC overall"
  tasks_executed: 3
  tasks_total: 3
  build_result: "0 errors, 24 warnings (baseline — all in ChatWindow.xaml.cs, unchanged)"
requirements_addressed: [SEC-05]
---

# Phase 03 Plan 02: Hybrid floor list — CommandFloorList + AVISO banner + audit-schema content_hash slot Summary

**One-liner:** Replaced the porous ContainsWord/ApplyDenylist machinery with the new CommandFloorList helper (D-03 normalize-then-regex pipeline closing quote-concat / alias / -EncodedCommand bypasses); extended the modal context + WPF view to render an amber AVISO banner when the floor would refuse post-Allow; rewired RunCommandTool.ExecuteAsync to call the floor before context build and emit the new `allow_then_floor_deny` audit outcome; extended BuildEntry with a `content_hash` slot for Plan 03's skill-modal SHA256 entries.

## Tasks Executed

| Task | Name                                                                                            | Commit  | Files                                                                                                                                       |
| ---- | ----------------------------------------------------------------------------------------------- | ------- | ------------------------------------------------------------------------------------------------------------------------------------------- |
| 1    | Create CommandFloorList.cs + extend CommandConfirmationContext (D-01, D-02, D-03, D-04)         | d42eb51 | AIBWindows/Services/CommandFloorList.cs (NEW), AIBWindows/Services/CommandConfirmationContext.cs                                            |
| 2    | Extend CommandConfirmationWindow XAML + code-behind to render AVISO banner (D-04)               | fa2c69b | AIBWindows/Views/CommandConfirmationWindow.xaml, AIBWindows/Views/CommandConfirmationWindow.xaml.cs                                         |
| 3    | Rewire RunCommandTool + delete ContainsWord/ApplyDenylist + extend BuildEntry content_hash (D-01, D-04) | e480a97 | AIBWindows/Services/NativeTools.cs                                                                                                          |

## CommandFloorList.cs — Surface Map (post-execution HEAD)

108 lines total. File-scoped namespace `AIB.Services;`.

| Member               | Visibility               | Line Range | Notes                                                                                                                          |
| -------------------- | ------------------------ | ---------- | ------------------------------------------------------------------------------------------------------------------------------ |
| `_entries`           | private static readonly  | 28-58      | Tuple array of (Regex Pattern, string Category, string Reason) — 6 entries covering 4 D-02 categories. All `IgnoreCase|Compiled`. |
| `_aliases`           | private static readonly  | 63-71      | Dictionary<string,string> with `StringComparer.OrdinalIgnoreCase`. mv/ri/ni/sc/ac/gci → canonical cmdlet (lowercase).           |
| `_concatPattern`     | private static readonly  | 73-74      | `[""']\s*\+\s*[""']` — strips PowerShell quote-concat.                                                                          |
| `_aliasPattern`      | private static readonly  | 75-76      | `\b(mv|ri|ni|sc|ac|gci)\b` IgnoreCase — alias detection.                                                                       |
| `_encodedCmdPattern` | private static readonly  | 77-78      | `-encodedcommand\b` IgnoreCase — D-03 step 4.                                                                                  |
| `Match`              | public static            | 84-106     | (bool Hit, string? Reason) return; D-03 pipeline in fixed order; first-match-wins.                                             |

Regex categories shipped (D-02):
1. **Recursive deletion** — 3 entries: `rm -rf`/`rm -r`, `del /s`/`del /f /s`/`rmdir /s`, `Remove-Item -Recurse`.
2. **Format / partition** — 1 entry: `format`/`diskpart`/`wmic logicaldisk`/`cipher /w`.
3. **Shutdown / reboot / logoff** — 1 entry: `shutdown`/`Restart-Computer`/`Stop-Computer`/`logoff`.
4. **Registry destructive** — 1 entry: `reg delete`/`Remove-ItemProperty -Path HK*`/`Remove-Item -Path HK*`.

All 6 reasons carry the PT-BR prefix `ACESSO NEGADO (FLOOR):` (SANDBOX → FLOOR substitution per CONTEXT.md Claude's Discretion); one additional reason string is emitted by the `-EncodedCommand` early refuse (total 7 PT-BR strings in file — grep counted 8 because the doc-comment also references the prefix).

## RunCommandTool — Diff Summary

| Concern                                       | Pre-plan (Phase 1)                                                                                                       | Post-plan                                                                                                                              |
| --------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------ | -------------------------------------------------------------------------------------------------------------------------------------- |
| Floor verdict computation                     | None (denylist ran post-modal, blind to UI)                                                                              | `var (floorHit, floorReason) = CommandFloorList.Match(command, userLevel);` BEFORE ctx build (line 321)                                |
| Modal context shape                           | 4 init-only fields (Tool, Command, Level, Cwd)                                                                            | +2 init-only fields (DenylistHit, DenylistReason) — populated from floor verdict (lines 330-331)                                       |
| Modal UI                                      | No floor warning                                                                                                          | Amber AVISO banner renders when ctx.DenylistHit is true (xaml.cs line 23-27)                                                           |
| Post-modal gate                               | `ApplyDenylist(command.ToLowerInvariant(), userLevel)` — 4 hardcoded tables (sysDirs/level4/destructives/netCmds)         | `if (floorHit && settings.ConfirmDangerousCommands) { audit('allow_then_floor_deny'); return floorReason!; }` (lines 394-399)          |
| Audit outcomes                                | 4 strings: allow, always_allow, deny, deny_no_ui                                                                          | 5 strings: + `allow_then_floor_deny` (strict superset, no breakage)                                                                    |
| BuildEntry schema                             | 7 fields: ts, tool, cmd, level, cwd, outcome, always_allow                                                                | 8 fields: + `content_hash` (literal `(string?)null` placeholder; Plan 03 swaps in `ctx.ContentHash`)                                   |
| ContainsWord helper                           | private static at :311-319 (\b word boundary matcher)                                                                     | **DELETED.** No call sites remain anywhere in AIBWindows/.                                                                              |
| ApplyDenylist method                          | private static at :413-448 (4 hardcoded denylist tables)                                                                  | **DELETED.** Replaced by CommandFloorList.Match — closes CONCERNS.md HIGH "naive substring/word matching, bypassable".                  |
| `_modalLock` semaphore                        | `private static readonly SemaphoreSlim _modalLock = new(1, 1);` at :288                                                   | **Unchanged.** Plan 03 lifts it into `CommandConfirmationWindow.ShowAsync` as part of D-08.                                            |

## Build Result

| Step                                                       | Result                                                  |
| ---------------------------------------------------------- | ------------------------------------------------------- |
| `dotnet build AIBWindows/AIB.csproj` after Task 1          | 0 errors, 24 warnings (baseline — all in ChatWindow)    |
| `dotnet build AIBWindows/AIB.csproj` after Task 2          | 0 errors, 24 warnings (no new warnings introduced)      |
| `dotnet build AIBWindows/AIB.csproj` after Task 3          | 0 errors, 24 warnings (no new warnings introduced)      |

All 24 baseline warnings live in `AIBWindows/ChatWindow.xaml.cs` (pre-existing, unrelated to this plan — same baseline Plan 01 observed).

## SEC-05 Contract Status

| Threat                                              | Pre-plan                                                                          | Post-plan                                                                                                                                                                              |
| --------------------------------------------------- | --------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Quote-concat bypass (`"Remove" + "-Item" -Recurse`) | Bypassed: ContainsWord matched literal substring; quoted PowerShell tokens evaded | **Closed.** D-03 step 2 strips `["']\s*\+\s*["']` BEFORE regex match. The Remove-Item -Recurse entry catches the collapsed form.                                                       |
| Alias bypass (`mv`/`ri`/`ni`/`sc`/`ac`/`gci`)       | Bypassed: ApplyDenylist matched `remove-item` literally, missed `ri`              | **Closed.** D-03 step 3 expands the six aliases to canonical cmdlets via `_aliases` lookup. `ri -recurse` becomes `remove-item -recurse` and hits the floor regex.                     |
| `-EncodedCommand` bypass                            | Bypassed: base64 payload was opaque to regex                                      | **Closed.** D-03 step 4 outright-refuses at L<7 with a dedicated reason. At L>=7 the modal shows the encoded command verbatim — human decides (Phase 1 D5 preserved).                  |
| Modal-first invariant regression                    | N/A (modal-first was Phase 1's baseline)                                          | **Preserved.** CommandFloorList.Match runs as a precheck for UI display; the post-modal floor refuse runs ONLY after the modal returns Allow. Same Phase 1 modal hop, untouched.       |
| Audit log schema break                              | N/A                                                                               | **Strict superset.** Existing 4 outcome strings still produced; new `allow_then_floor_deny` added; new `content_hash` field is JSON-null today (System.Text.Json emits naturally).     |

UAT-level behavior verification (RESEARCH.md §"Validation Architecture" SEC-05 rows 3-5: quote-concat / EncodedCommand / alias rejected at L<7 with the AVISO banner; L>=7 reaches the modal verbatim) is codified in Plan 04's `03-VERIFICATION.md`. This plan ships the contract.

## Decisions Made

- **D-01 implementation:** `Match` returns `(false, null)` at userLevel >= 7 as the first statement (line 87 of CommandFloorList.cs). The `floorHit` flag in RunCommandTool is therefore false-by-construction at L>=7, so the post-modal floor refuse block (lines 394-399 of NativeTools.cs) is unreachable at high levels even when ConfirmDangerousCommands is ON.
- **D-02 implementation:** Six regex entries shipped (3 + 1 + 1 + 1) covering the four locked categories. All use `RegexOptions.IgnoreCase | RegexOptions.Compiled` — 9 hits total in the file (6 from `_entries` + 3 from the helper regexes; the helper `_concatPattern` uses `Compiled` only, matching PATTERNS guidance for non-IgnoreCase ASCII matchers).
- **D-03 implementation:** Pipeline order is locked verbatim — step 1 (ToLowerInvariant) at line 90, step 2 (concat strip) at line 93, step 3 (alias expansion via dictionary lookup) at line 96, step 4 (EncodedCommand refuse) at lines 99-100, step 5 (first-match-wins regex loop) at lines 103-104.
- **D-04 implementation:** ctx is built with `DenylistHit = floorHit, DenylistReason = floorReason` (lines 330-331 of NativeTools.cs). The modal ctor reads `ctx.DenylistHit && !string.IsNullOrEmpty(ctx.DenylistReason)` to gate the AVISO banner (xaml.cs line 23). The post-modal floor refuse path emits `allow_then_floor_deny` outcome (line 397) before returning the PT-BR reason.
- **BuildEntry content_hash ordering — Option A chosen.** Verified with `grep -n ContentHash AIBWindows/Services/CommandConfirmationContext.cs` before commit — 0 hits, confirming Plan 03's property has NOT yet landed. Picked the `(string?)null` literal placeholder with the inline `TODO(Plan 03 / D-10)` comment so the BuildEntry initializer compiles standalone. Plan 03 will swap the literal `null` for `ctx.ContentHash` when it adds the property to the context POCO. System.Text.Json emits `"content_hash":null` naturally; no AuditLogService.cs edit required.
- **_modalLock retention:** Confirmed at NativeTools.cs:288 post-commit (`grep -nE 'private static readonly SemaphoreSlim _modalLock' AIBWindows/Services/NativeTools.cs` returns 1 hit). Plan 03 lifts it into the new `CommandConfirmationWindow.ShowAsync` static helper as part of D-08 modal-helper extraction; this plan deliberately scoped to floor + audit + denylist deletion.
- **Sandbox vs FLOOR prefix:** All six PT-BR reason strings use `ACESSO NEGADO (FLOOR):` not `(SANDBOX)` — substitution explicitly authorized by CONTEXT.md ("Claude's Discretion") and matches the new helper's identity. The four `(SANDBOX)` prefixes that survived in NativeTools.cs (GlobTool, GrepTool, ListDirTool) are unrelated to run_command and stay byte-for-byte (intentional — Plan 02 scope is run_command + skill-context fields only).

## Deviations from Plan

None — the plan executed exactly as written. All three tasks landed at their first attempt with every grep acceptance criterion green and `dotnet build` reporting 0 errors / 24 baseline warnings (unchanged from Plan 01's exit state) after each commit.

## Known Stubs

None introduced. The `content_hash = (string?)null` placeholder in BuildEntry is documented behavior (Plan 02 / Phase 3 D-04: null is the correct value for run_command; Plan 03 D-10 swaps to `ctx.ContentHash` for skill tools). Not a stub — it is the locked contract for this plan's surface.

## Threat Flags

None — no new security-relevant surface introduced beyond what the threat model already disposes. T-3-Floor-Bypass-Quote-Concat / T-3-Floor-Bypass-Alias / T-3-Floor-Bypass-EncodedCommand all `mitigate` via the D-03 pipeline; T-3-Modal-First-Invariant-Regression `mitigate` (verified by inspection — `_modalLock` block unchanged); T-3-ScreenRecording-Leak / T-3-Floor-False-Positive / T-3-SC remain `accept` per the plan's locked dispositions.

## TDD Gate Compliance

Plan frontmatter declares `type: execute` (not `tdd`); no RED/GREEN/REFACTOR gate sequence required. Each task carries its own automated `<verify>` (dotnet build) which passed at the first commit attempt.

## Downstream Hooks (Plan 03 — Wave 3)

- **CommandConfirmationContext.DenylistHit / DenylistReason** — already present (init-only, additive). Plan 03 extends with ScriptBody / Interpreter / ContentHash without conflict.
- **BuildEntry content_hash slot** — present with `(string?)null` placeholder + TODO marker. Plan 03's first task replaces the literal with `ctx.ContentHash` once the property exists. JsonSerializer schema is forward-compatible.
- **`allow_then_floor_deny` outcome string** — established in the audit log schema. Plan 03's skill audit entries share the same schema and can introduce additional outcomes (e.g., `skill_deny`, `skill_allow_with_hash`) following the same pattern.
- **DenylistBanner XAML element** — `x:Name="DenylistBanner"` present at line 51 of CommandConfirmationWindow.xaml. Plan 03 can re-render the banner for skill tools by setting the same fields on the new skill context (no XAML change required).
- **`_modalLock` semaphore** — still at NativeTools.cs:288. Plan 03 lifts it into `CommandConfirmationWindow.ShowAsync` as part of D-08 modal-helper extraction; this plan's RunCommandTool deliberately did not touch the modal-hop block.

## Self-Check: PASSED

- `AIBWindows/Services/CommandFloorList.cs` — FOUND (NEW, 108 lines, file-scoped namespace `AIB.Services;`)
- `AIBWindows/Services/CommandConfirmationContext.cs` — FOUND (extended, +DenylistHit/DenylistReason init-only props)
- `AIBWindows/Views/CommandConfirmationWindow.xaml` — FOUND (DenylistBanner Border at line 51, DenylistText TextBlock at line 52)
- `AIBWindows/Views/CommandConfirmationWindow.xaml.cs` — FOUND (DenylistBanner.Visibility toggle at line 26)
- `AIBWindows/Services/NativeTools.cs` — FOUND (RunCommandTool rewired; ContainsWord + ApplyDenylist deleted; BuildEntry +content_hash)
- Commit `d42eb51` — FOUND in `git log --oneline -5` (Task 1, D-01..D-04 context + floor helper)
- Commit `fa2c69b` — FOUND in `git log --oneline -5` (Task 2, D-04 AVISO banner)
- Commit `e480a97` — FOUND in `git log --oneline -5` (Task 3, D-01 + D-04 rewire + dead-code delete)
- Build gate — 0 errors / 24 baseline warnings (unchanged from Plan 01's exit state) after each of the three commits
- Source assertion — `grep -rn ApplyDenylist AIBWindows/` returns 0 hits, `grep -rn ContainsWord AIBWindows/` returns 0 hits (full removal verified post-commit)
- Audit outcome set — `grep -oE '"(allow|always_allow|deny|deny_no_ui|allow_then_floor_deny)"' AIBWindows/Services/NativeTools.cs | sort -u` returns all 5 outcomes (strict superset of Phase 1)
