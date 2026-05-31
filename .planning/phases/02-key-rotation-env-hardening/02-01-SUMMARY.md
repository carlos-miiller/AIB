---
phase: 02-key-rotation-env-hardening
plan: "01"
subsystem: env-hardening
tags: [security, env, dotnetenv, cleanup, key-rotation]
dependency_graph:
  requires: []
  provides: [env-files-deleted, env-example-created, dotnetenv-removed, evidence-dir-scaffolded]
  affects: [AIBWindows/AIB.csproj, .env.example, evidence/]
tech_stack:
  added: []
  patterns: [gitignore-literal-pattern, dpapi-vault-reference, zero-byte-gitkeep]
key_files:
  created:
    - .env.example
    - .planning/phases/02-key-rotation-env-hardening/evidence/.gitkeep
  modified:
    - AIBWindows/AIB.csproj
  deleted:
    - .env (gitignored, filesystem-only deletion)
    - AIBWindows/.env (gitignored, filesystem-only deletion)
    - AIBLinux/.env (gitignored, filesystem-only deletion)
decisions:
  - "D-10: .env files deleted from disk; DotNetEnv removed from csproj; .env.example published with sk-PLACEHOLDER"
  - "D-12: evidence/ directory scaffolded with .gitkeep for rotation screenshot"
metrics:
  duration: "~8 minutes"
  completed: "2026-05-31"
  tasks_completed: 2
  files_changed: 3
---

# Phase 2 Plan 01: Disk Cleanup and Env Hardening Summary

**One-liner:** Delete three gitignored `.env` files containing live `sk-svcacct-...` key; remove dead `DotNetEnv` package from csproj; publish `.env.example` placeholder; scaffold `evidence/` directory — no build regressions (0 errors, 24 pre-existing warnings).

## Tasks Completed

| Task | Name | Commit | Key Files |
|------|------|--------|-----------|
| 1 | Delete three .env files on disk; verify gitignore semantics preserved | 304d036 (filesystem-only — gitignored files produce no git delta) | .env, AIBWindows/.env, AIBLinux/.env |
| 2 | Create .env.example; remove DotNetEnv from AIB.csproj; add evidence/.gitkeep | 304d036 | .env.example, AIBWindows/AIB.csproj, evidence/.gitkeep |

Note: Tasks 1 and 2 share a single commit because Task 1 (deletion of gitignored files) produces no git-trackable delta — the `.env` files are matched by `.gitignore:11` literal `.env` pattern and were never tracked in git history. The git commit captures Task 2's three tracked file changes.

## Files Deleted

- `C:\Users\Carlo\CPAPS\AIB\.env` — contained live `sk-svcacct-...` key (T-02-01 mitigated)
- `C:\Users\Carlo\CPAPS\AIB\AIBWindows\.env` — same key (T-02-01 mitigated)
- `C:\Users\Carlo\CPAPS\AIB\AIBLinux\.env` — same key (T-02-01 mitigated)

All three files confirmed removed. `Test-Path` returns `False` for each. Disk-scan `Get-ChildItem -Recurse -Filter .env` returns 0 hits.

## Files Created

- `.env.example` — placeholder template per D-10; contains `OPENAI_API_KEY=sk-PLACEHOLDER`, `URL=http://localhost:11434/v1`, `MODEL=qwen2.5:7b`, and comment explaining AIB Windows does NOT read this file (key lives in DPAPI vault `~/.AIB/credentials/openai.bin`).
- `.planning/phases/02-key-rotation-env-hardening/evidence/.gitkeep` — zero-byte directory marker for D-12 rotation screenshot.

## csproj Edit Confirmed

`AIBWindows/AIB.csproj` line 17 `<PackageReference Include="DotNetEnv" Version="3.1.1" />` removed. Surrounding `<ItemGroup>` and all other `PackageReference` entries intact. `Select-String -Pattern 'DotNetEnv'` returns 0 matches.

## Build Status

- `dotnet clean AIBWindows/AIB.csproj` — success (0 errors, 0 warnings)
- `dotnet build AIBWindows/AIB.csproj` — **0 errors, 24 warnings** (all 24 warnings are pre-existing from Phase 1 baseline; no new warnings introduced by DotNetEnv removal, confirming zero call sites)

## Verification Checklist

- [x] `Test-Path C:\Users\Carlo\CPAPS\AIB\.env` returns `False`
- [x] `Test-Path C:\Users\Carlo\CPAPS\AIB\AIBWindows\.env` returns `False`
- [x] `Test-Path C:\Users\Carlo\CPAPS\AIB\AIBLinux\.env` returns `False`
- [x] `.gitignore` line 11 is literal `.env` (not `.env*`) — `.env.example` remains trackable
- [x] `git check-ignore .env.example` returns empty (not ignored)
- [x] `Test-Path .env.example` returns `True`
- [x] `.env.example` contains `OPENAI_API_KEY=sk-PLACEHOLDER`
- [x] `.env.example` contains `DPAPI vault` text (comment per D-10)
- [x] `Select-String -Pattern 'DotNetEnv' -Path AIBWindows/AIB.csproj` returns 0 matches
- [x] `dotnet build AIBWindows/AIB.csproj` exits 0
- [x] `Test-Path evidence/.gitkeep` returns `True`
- [x] `evidence/.gitkeep` length is 0 bytes
- [x] Disk-scan returns 0 `.env` files
- [x] Chat output does NOT contain the literal key value beyond the prefix substring `sk-svcacct-`

## Deviations from Plan

### Single Commit for Both Tasks

**Found during:** Task 1 commit

**Issue:** Task 1 (deleting three gitignored `.env` files) produces no git-trackable delta — the files matched `.gitignore:11`'s literal `.env` pattern and were never in git history. Attempting to commit Task 1 alone results in "nothing to commit."

**Fix:** Tasks 1 and 2 combined into one commit (`304d036`) since the filesystem deletions are verified complete before Task 2 begins. This is not a behavioral deviation — the plan's success criteria are fully met.

**Files modified:** None (gitignored file deletions are filesystem-only)

## SEC-03 Progress

This plan addresses:
- SEC-03 acceptance #2 "Fresh clone with no env vars" — `.env` files gone from disk
- SEC-03 acceptance #3 "App refuses placeholder key" — prerequisite cleanup (subsequent plans wire FirstRunWindow + sentinel)

## Threat Mitigations Applied

| Threat ID | Status | Notes |
|-----------|--------|-------|
| T-02-01 | Mitigated | All 3 .env files containing sk-svcacct-... deleted from disk |
| T-02-12 | Mitigated | DotNetEnv removed from csproj; zero call sites confirmed by clean build |
| T-02-SC | N/A | No package installs in this plan |
| T-02-99 | Mitigated | Key never echoed to chat; confirmed only prefix presence without full value |

## Known Stubs

None — this plan performs only deletions and creates static/placeholder files. No UI or data flows introduced.

## Self-Check: PASSED

- `.env.example` exists at expected path: FOUND
- `AIBWindows/AIB.csproj` modified (DotNetEnv removed): FOUND
- `evidence/.gitkeep` exists at expected path: FOUND
- Commit `304d036` exists in git log: FOUND
- Build: 0 errors confirmed
