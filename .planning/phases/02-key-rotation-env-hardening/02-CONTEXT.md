# Phase 2: Key rotation + `.env` hardening - Context

**Gathered:** 2026-05-30
**Status:** Ready for planning

<domain>
## Phase Boundary

No live secret-account keys live on developer disk. The current
`sk-svcacct-…` key is rotated in the OpenAI console; runtime keys flow
through DPAPI via `CredentialService` on Windows. `.env` on disk is
deleted, replaced by `.env.example` with placeholders. A new
`FirstRunWindow` walks the user through provider + key selection on the
first hotkey invocation after a fresh install.

**In scope:** AIBWindows (.NET 8 WPF), `.env` deletion, `.env.example`
creation, `DotNetEnv` package removal, `CredentialService`-backed key
resolution in `OpenAIService`, `FirstRunWindow` (new XAML + code-behind),
provider-aware first-run detection, manual key rotation in OpenAI console
with screenshot evidence.

**Out of scope:** AIBLinux key path (deferred — see Deferred Ideas).
Settings UI rewrites beyond the minimum to point users at FirstRunWindow.
Other systems' credentials in `CredentialService` (unchanged).

</domain>

<decisions>
## Implementation Decisions

### First-run UX (FirstRunWindow)

- **D-01 — Trigger lazily on hotkey, not app launch.** AIB is a tray app
  (`AIBWindows` parallels `AIBLinux/main.py:39` system-tray pattern). App
  boot stays silent. When the global hotkey fires and the first-run
  detector (D-03) returns true, `FirstRunWindow` opens *instead of*
  `ChatWindow`. On a normal launch (key already in vault), the hotkey
  opens `ChatWindow` directly as today.

- **D-02 — Dedicated `FirstRunWindow` XAML + code-behind.** New files:
  `AIBWindows/Views/FirstRunWindow.xaml` and
  `AIBWindows/Views/FirstRunWindow.xaml.cs`. Not reusing
  `SettingsWindow` — single-purpose welcome + provider picker + key
  input. Separation justifies the extra file because the flow is
  meaningfully different from "edit existing settings".

- **D-03 — Detector is provider-aware.** Trigger condition:
  `CredentialService.RetrieveCredential("openai", "ApiKey")` returns an
  error sentinel (no entry) **AND** `settings.AiProvider != "Ollama"`.
  Users on Ollama see no FirstRunWindow at any point — Ollama needs no
  real key. The check runs on every hotkey invocation until the vault
  contains a key OR the provider is `"Ollama"`.

- **D-04 — Cancel = app quits cleanly.** Cancel button, window-X,
  Alt+F4, Esc all map to `DialogResult = false` and `Application.Current
  .Shutdown()`. The tray icon remains for the next hotkey. Audit-log
  line emitted: `outcome=firstrun_cancelled`. Rationale: matches
  REQUIREMENTS.md SEC-03 "app refuses to start with placeholder key" —
  without a key the app simply does not provide a UI.

- **D-05 — Key validation is regex-only.** Pattern
  `^sk-[a-zA-Z0-9_-]{20,}$`. Rejects empty input, `sk-PLACEHOLDER`,
  `placeholder`, accidental whitespace. Inline error label in
  FirstRunWindow ("Chave inválida — deve começar com `sk-`"). No
  network call on Save. Live API validation deferred (could land in a
  later quality phase).

- **D-06 — Save target: `CredentialService` vault ONLY.** Save writes to
  `CredentialService.StoreCredentialAsync("openai", "ApiKey", <value>)`.
  `settings.ApiKey` is overwritten with the literal string `"use-vault"`
  as a sentinel so `OpenAIService` knows to consult the vault.
  `OpenAIService.cs:657` and `OpenAIService.cs:705` are rewritten:

  ```csharp
  string apiKey = settings.ApiKey;
  if (apiKey == "use-vault") {
      apiKey = CredentialService.RetrieveCredential("openai", "ApiKey");
      if (apiKey.StartsWith("ERRO")) {
          // Vault missing — should never reach here if D-03 detector
          // ran. Treat as deny: return error string upstream so the
          // ChatWindow can show "Configure sua chave em Configurações".
          apiKey = "placeholder";
      }
  }
  ```

  No dual-write. The settings field becomes a sentinel only when
  `AiProvider != "Ollama"`. Ollama path keeps `settings.ApiKey =
  "ollama"` literal.

- **D-07 — Visual style: match `ChatWindow` theme, centered.** Reuse
  ChatWindow's dark theme, gradient accents, and resource dictionary.
  Window opens centered (`WindowStartupLocation =
  CenterScreen`). Layout: welcome title ("Bem-vindo ao AIB"), short
  message ("Para começar, escolha seu provedor de IA"), provider radio
  group ("Ollama (local, sem chave)" / "OpenAI (requer chave)"),
  conditional key input + Ollama model dropdown below.

- **D-08 — Lifecycle: FirstRunWindow → ChatWindow strictly
  sequential.** `ChatWindow` is never shown while `FirstRunWindow` is
  open. On Save success, `FirstRunWindow.Close()` and
  `chatWindow.Show()` from the hotkey handler. ChatWindow's warmup +
  `/api/tags` fetches do NOT run until FirstRunWindow closes
  successfully. Prevents premature OpenAI/Ollama calls with stale
  config.

- **D-09 — Ollama branch: model picker via `/api/tags`.** When the user
  selects "Ollama", `FirstRunWindow` calls the existing
  `SettingsService.GetOllamaModelsAsync(apiUrl)` at
  `SettingsService.cs:102` and populates a `ComboBox` with installed
  models. On Save: `settings.ModelName = selected` AND
  `settings.ShadowModelName = selected` (mirror by default). If
  `/api/tags` returns empty or throws: show "Ollama não detectado em
  `localhost:11434`" with a `Tentar novamente` button and a `Usar
  qwen2.5:7b mesmo assim` fallback link.

### `.env` hardening

- **D-10 — Delete `.env` on disk AND remove `DotNetEnv` package.**
  `DotNetEnv` is referenced at `AIBWindows/AIB.csproj:17` but has ZERO
  call sites anywhere in `AIBWindows/`. The package is dead weight.
  Removal:
  - `rm .env` (live key on disk eliminated)
  - Edit `AIBWindows/AIB.csproj` line 17: remove `DotNetEnv` reference
  - Add `.env.example` at repo root with placeholders:
    ```
    # Optional — only used if you bypass FirstRunWindow and set
    # environment variables manually before launching AIB.
    # AIB Windows does NOT read this file; key lives in DPAPI vault
    # (~/.AIB/credentials/openai.bin).
    OPENAI_API_KEY=sk-PLACEHOLDER
    URL=http://localhost:11434/v1
    MODEL=qwen2.5:7b
    ```
  - `.env.example` is checked in. `.gitignore` already ignores `.env`.

### Migration (existing users with real key in `settings.ApiKey`)

- **D-11 — Force re-entry; do NOT auto-migrate.** Existing installs have
  `settings.ApiKey = "sk-svcacct-…"` inside DPAPI-encrypted
  `profile.dat`. On first launch after this phase ships, the boot path
  overwrites `settings.ApiKey = "use-vault"` unconditionally (regardless
  of current value) and clears the local DPAPI blob's old key field.
  The D-03 detector then triggers `FirstRunWindow` on next hotkey, and
  the user re-pastes the (rotated) key. The old key value is NEVER
  copied into the vault — rotation must happen first.
  Implementation: a one-shot migration step in `App.OnStartup` reads
  settings, overwrites `ApiKey` field to `"use-vault"`, re-encrypts,
  saves. Audit-log line: `outcome=migration_clear_apikey`.

### Rotation (manual action by owner)

- **D-12 — Rotation evidence: screenshot in
  `.planning/phases/02-key-rotation-env-hardening/evidence/`.** Owner
  rotates the `sk-svcacct-…` key in the OpenAI console, captures a
  screenshot showing the OLD key revoked + NEW key visible (last4 only,
  redact full key), saves as
  `evidence/openai-console-rotation-2026-MM-DD.png`. VERIFICATION.md
  references the file path. Manual step; not automatable.

### Claude's Discretion

- FirstRunWindow XAML layout (margins, spacing, control sizing) within
  D-07's "match ChatWindow theme" constraint. Welcome-message copy can
  be polished by Claude. Esc/X handling beyond D-04 (no animation
  required).
- Audit-log entry schema for the new outcomes (`firstrun_cancelled`,
  `firstrun_saved`, `migration_clear_apikey`) — should follow the
  existing `AuditLogService` JSONL pattern from Phase 1 D6.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Requirements + governance
- `.planning/REQUIREMENTS.md` §SEC-03 — authoritative requirement for
  this phase (key rotation + `.env` hardening).
- `.planning/ROADMAP.md` "Phase 2" section — phase scope + success
  criteria.
- `Regras de Identidade/SEGURANCA.MD` — declared posture; must be
  updated by this phase to reflect the DPAPI vault as the canonical key
  store. Lines covering the OpenAI key path are the edit target.
- `.planning/codebase/CONCERNS.md` — "CRITICAL — Live OpenAI
  service-account key on disk in two `.env` files" finding; must be
  moved to "Resolved" with this phase's last commit SHA.

### Prior phase context
- `.planning/phases/01_modal-and-level9/CONTEXT.md` §D6 — audit-log
  pattern (`AuditLogService` writes JSONL to
  `DirectoryService.DataDir/logs/audit.log`); reuse the same writer for
  the new first-run outcomes.
- `.planning/phases/01_modal-and-level9/CONTEXT.md` §D8 — Settings
  migration pattern (missing field → C# default via
  `System.Text.Json.JsonSerializer`). The same migration channel is used
  in D-11 to overwrite `settings.ApiKey` to `"use-vault"`.

### Code references (read before editing)
- `AIBWindows/Services/CredentialService.cs` — full file. Reuses
  `DirectoryService.DataDir/credentials/{system}.bin`, DPAPI-encrypted,
  global key fallback. Do NOT modify; only call.
- `AIBWindows/Services/SettingsService.cs:12-54` (`UserAppSettings`
  POCO) — fields: `ApiKey`, `ApiUrl`, `ModelName`, `ShadowModelName`,
  `AiProvider`. Lines 56-100 (`LoadSettings` / `SaveSettings`) — DPAPI
  encrypt/decrypt path; reuse for the D-11 migration step. Lines
  102-132 (`GetOllamaModelsAsync`) — reuse from FirstRunWindow Ollama
  branch.
- `AIBWindows/Services/OpenAIService.cs:651-690` (`AskStatelessAsync`)
  and `AIBWindows/Services/OpenAIService.cs:696-725` (`EnsureClient`) —
  TWO call sites that read `settings.ApiKey` and instantiate
  `ChatClient`. Both must be rewritten per D-06.
- `AIBWindows/Views/SettingsWindow.xaml.cs:40` and `:124` — existing
  `KeyTextBox` bound to `settings.ApiKey`. After this phase the field
  should become read-only and show the literal `"use-vault"`, with a
  "Alterar chave" button that opens FirstRunWindow.
- `AIBWindows/AIB.csproj:17` — `<PackageReference Include="DotNetEnv"
  Version="3.1.1" />`; deleted per D-10.
- `AIBWindows/Services/DirectoryService.cs` — defines `DataDir`. The
  vault path `~/.AIB/credentials/openai.bin` derives from this.

### Linux (out of scope but referenced for context)
- `AIBLinux/main.py:6` — `load_dotenv()` call site (unchanged this
  phase).
- `AIBLinux/app/openai_client.py:15` — `os.getenv("OPENAI_API_KEY")`
  (unchanged this phase).

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets

- **`CredentialService` (Windows DPAPI vault)** — already provides the
  full encrypt/decrypt/lookup path. `StoreCredentialAsync` and
  `RetrieveCredential` are the only entry points needed. No new vault
  code required.
- **`SettingsService.GetOllamaModelsAsync`** — already implements
  `/api/tags` fetch with graceful empty-list fallback. FirstRunWindow's
  Ollama model picker calls it directly.
- **`AuditLogService` (Phase 1 D6)** — already at `~/.AIB/logs/audit.log`,
  JSONL append-only, `SemaphoreSlim`-guarded, fire-and-forget. New
  first-run outcomes log through this same writer.
- **`DirectoryService.DataDir`** — single source of truth for AIB data
  root; vault path derives from it.
- **`ChatWindow` resource dictionary / theme** — FirstRunWindow uses the
  same `Styles.xaml` (or whatever ChatWindow imports) so the look is
  consistent.

### Established Patterns

- **DPAPI for all at-rest secrets** — `SettingsService.SaveSettings`
  (profile.dat) and `CredentialService.StoreCredentialAsync` both use
  `ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser)`.
  No new crypto primitives introduced this phase.
- **Settings migration via JSON deserializer default** — Phase 1 D8
  established that missing fields → C# defaults. D-11's `ApiKey =
  "use-vault"` overwrite uses the same `LoadSettings` → mutate →
  `SaveSettings` round-trip.
- **Audit-log first, side-effect second** — every security decision
  (modal allow/deny in Phase 1, first-run save/cancel here) emits an
  audit line. Fire-and-forget per Phase 1 D6.

### Integration Points

- **Hotkey handler (Windows equivalent of `AIBLinux/main.py:83`)** —
  must consult D-03 detector before deciding whether to show
  `FirstRunWindow` or `ChatWindow`. New branch logic in the existing
  toggle-window handler.
- **`OpenAIService` constructor / `EnsureClient`** — two read sites for
  `settings.ApiKey` become four lines longer each (vault lookup +
  fallback).
- **`SettingsWindow`** — `KeyTextBox` becomes a read-only field
  displaying `"use-vault"` (or "Configurada" friendly label) plus an
  "Alterar chave" button that opens FirstRunWindow. Optional polish;
  not strictly required for SEC-03 acceptance.
- **`AIB.csproj`** — remove `DotNetEnv` package reference per D-10.

</code_context>

<specifics>
## Specific Ideas

- FirstRunWindow welcome copy (Claude's discretion, draft below):
  - Title: "Bem-vindo ao AIB"
  - Subtitle: "Para começar, escolha seu provedor de IA"
  - Provider radio: "Ollama — local, sem chave" (default) / "OpenAI —
    requer chave da API"
  - OpenAI branch text: "Cole sua chave da OpenAI (começa com `sk-…`).
    Será armazenada com criptografia DPAPI no seu perfil."
  - Ollama branch text: "Selecione o modelo instalado no seu Ollama."
- Ollama model picker shows the same dropdown style as `SettingsWindow`
  model selector for consistency.
- The `.env.example` should explicitly state that AIB Windows does NOT
  read it — the file documents URL/MODEL for users who would otherwise
  guess, not as an active config source.

</specifics>

<deferred>
## Deferred Ideas

- **AIBLinux key path migration** — `AIBLinux/app/openai_client.py:15`
  still uses `os.getenv("OPENAI_API_KEY")` and `main.py:6` still calls
  `load_dotenv()`. Per owner: "keep Linux as it is for now". Deferred
  to the AIBLinux feature-parity milestone (per `.planning/PROJECT.md`
  "Out of scope" — AIBLinux feature parity is a separate milestone).
  `AIBLinux/.env` is left in place this phase; live-key rotation in D-12
  invalidates the literal in both files anyway.
- **Live API validation of key on Save** — D-05 chose regex-only. A
  follow-up phase could add a 1-shot models.list ping with spinner
  during FirstRunWindow Save.
- **Polishing `SettingsWindow.KeyTextBox` into "Alterar chave"
  button** — outlined in Integration Points; not strictly required for
  SEC-03 acceptance. Could ship in this phase if time permits or in a
  small follow-up UX phase.
- **Audit-log retention / rotation** — already deferred in Phase 1 D6;
  same posture here.
- **`/unlock_level` chat backdoor** — accepted risk per PROJECT.md;
  unchanged.

</deferred>

---

*Phase: 2-key-rotation-env-hardening*
*Context gathered: 2026-05-30*
