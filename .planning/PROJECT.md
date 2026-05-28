# PROJECT: AIB

**Created:** 2026-05-28
**Status:** Active — Milestone "Security Remediation v1"
**Type:** Internal team tool (department-scale, dozens of users)

## What is AIB?

AIB is a local-first AI assistant for power users. Two implementations live in this repo:

- **AIBWindows/** — primary product. .NET 8 WPF + WinForms desktop app. Full ReAct agent with 12 native tools (memory, vault, read_file, read_screen, run_command, search_web, execute_skill, materialize_skill, manage_clipboard, set_reminder, glob, grep, list_dir), Ollama backend via OpenAI-compatible API, Whisper.net voice in, LiteDB memory with `bge-micro-v2` embeddings, DPAPI-protected credential vault, dynamic skill loader (`npx skills`), Shadow Assistant dwell observer that auto-OCRs hovered windows, global hotkey + tray.
- **AIBLinux/** — legacy PyQt6 chat client. Screenshot + message only. Far behind Windows; classified as **lite client** for this milestone.

See `.planning/codebase/` for full map (STACK, ARCHITECTURE, STRUCTURE, INTEGRATIONS, CONVENTIONS, TESTING, CONCERNS).

## Audience

Distributed inside the organization to **dozens of users** (department-scale). Trust boundary:

- Users are not adversarial but are not security-trained either.
- Hosts are corp-managed Windows boxes; admin rights variable.
- Distribution today is manual (shared build). Future: signed installer + central config.

## Threat model (one-liner)

The realistic attack surface is **indirect prompt injection through tool outputs** (`read_screen`, `read_file`, `search_web`, Shadow Assistant dwell extraction) compounded by tool calls that execute without a human-in-the-loop. A hostile webpage, PDF, or hovered window can today instruct the model to call `run_command` silently. Level 9 (`MessageCount >= 1500`) drops the denylist entirely, turning any long-time user into a one-prompt-to-RCE target.

See `.planning/codebase/CONCERNS.md` for full inventory (39 findings).

## Current milestone — Security Remediation v1

**Goal:** Close the 3 in-scope CRITICAL findings + all HIGH security & reliability findings so that the declared `Regras de Identidade/SEGURANCA.MD` posture matches the implementation.

**In scope (this milestone):**

- CRITICAL: Modal confirmation for `run_command` (wire `CommandConfirmationWindow`)
- CRITICAL: Remove Level-9 sandbox bypass (level 9 removes denylist, NOT the modal)
- CRITICAL: Rotate OpenAI service-account key + harden `.env` story
- HIGH (security): denylist replacement, skill execution gating, argument quoting via `ArgumentList`, prompt-injection isolation of tool outputs, GrepTool/GlobTool credential blocklist
- HIGH (reliability): tri-state warmup, retry policy on all network calls

**Out of scope (deferred, recorded for traceability):**

- CRITICAL `/unlock_level` chat backdoor — **accepted risk** for this milestone. Owner intends to keep this as a developer mode for now. Will be gated behind a Settings toggle (off by default) in a follow-up; not removed.
- All MEDIUM and LOW findings unless they share a phase with an in-scope HIGH.
- AIBLinux feature parity (separate milestone).
- Tests + CI (separate milestone — TESTING.md is a known gap).
- Tech-debt refactors (god-files NativeTools.cs / OpenAIService.cs / ChatWindow.xaml.cs) unless required to land a security fix.

## Definition of done (milestone)

- [ ] All in-scope CRITICAL/HIGH findings have a closing commit and a regression note in the relevant Service.
- [ ] `CONCERNS.md` updated: each fixed finding moved to a "Resolved" section with commit SHA.
- [ ] `Regras de Identidade/SEGURANCA.MD` rewritten to match shipped behavior (no aspirational claims).
- [ ] `SECURITY.md` produced by `/gsd-secure-phase` for at least the modal + level-9 phase.
- [ ] User confirms hand-tested: `run_command` triggers modal at all levels; `/unlock_level 9` still works but no longer disables modal; `.env.example` ships; live key rotated.

## Stack snapshot

- **Windows**: .NET 8 (`net8.0-windows10.0.19041.0`), WPF + WinForms, OpenAI 2.10.0, LiteDB 5.0.21, SmartComponents.LocalEmbeddings preview, Whisper.net 1.9.0, NAudio 2.3.0, Markdig.Wpf 0.5.0.1, PdfPig 0.1.14, OpenXml 3.5.1, NHotkey.Wpf 4.0.0, Hardcodet.NotifyIcon.Wpf 2.0.1, Microsoft.ML.Tokenizers 2.0.0, DotNetEnv 3.1.1, DPAPI.
- **Linux**: Python 3.10+, PyQt6 ≥ 6.6, pynput, openai, mss, Pillow, python-dotenv. Build via bash + Docker (bullseye GLIBC).
- **Backend**: Ollama at `localhost:11434` (OpenAI-compat + `/api/generate` keep-alive + `/api/tags`).
- **External integrations**: Ollama, DuckDuckGo Instant Answer, HuggingFace (Whisper model fetch), `npx skills`, ViaCEP via skill.
- **Storage**: `~/.AIB/memory.db` (LiteDB), JSON files for chat/reminders, DPAPI-encrypted `profile.dat` + per-system credential vault.

## Governance docs (authoritative)

These describe the declared posture. Code is graded against them in `CONVENTIONS.md` and `CONCERNS.md`:

- `GRAVITY.MD` — north-star project rules
- `Regras de Identidade/CODIGO_LIMPO.MD` — code style + SRP
- `Regras de Identidade/SEGURANCA.MD` — Zero-Trust modal + level firewall + DPAPI
- `Regras de Identidade/VISUAL.MD` — UI rules
- `Regras de Identidade/funcionalities.md` — declared feature surface

## Non-goals

- Multi-tenant / cloud hosted deployment.
- Public open-source release in this milestone.
- Mobile clients.
- Commercial licensing / billing.
