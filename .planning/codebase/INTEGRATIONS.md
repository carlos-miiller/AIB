# External Integrations

**Analysis Date:** 2026-05-28

AIB is designed as a **privacy-first local assistant**: every external surface defaults to localhost (Ollama, Windows OCR, Whisper.net, LiteDB) and the few outbound HTTP calls are explicit (model download, DuckDuckGo lookup, skill marketplace, ViaCEP via skill). There are no telemetry, analytics, error-tracking, or CI/CD integrations.

## APIs & External Services

**LLM providers (OpenAI-compatible chat-completions API):**

- **Ollama (default, local)** — `http://127.0.0.1:11434/v1` for chat-completions, `http://127.0.0.1:11434` for native endpoints.
  - SDK: `OpenAI.Chat.ChatClient` (`AIBWindows/Services/OpenAIService.cs` lines 720-723) configured with `OpenAIClientOptions { Endpoint = new Uri(apiUrl) }` (lines 718-720).
  - Auth: env-like placeholder string `"ollama"` (`SettingsService.cs` line 15, `OpenAIService.cs` line 710).
  - Native usage beyond chat-completions:
    - `POST {apiUrl}/api/generate` with `{ keep_alive: -1, options: { num_ctx: 16384 } }` during warmup to lock model in VRAM (`OpenAIService.cs` lines 79-94). Comment at lines 80-90 explains why 16384 was chosen.
    - `GET {apiUrl}/api/tags` to list installed local models for the Settings dropdown (`SettingsService.cs` lines 97-114).
  - IPv6 workaround: `localhost` is rewritten to `127.0.0.1` to avoid 2-minute resolver timeouts (`OpenAIService.cs` lines 713, 665).
  - Default models: `qwen2.5:7b` for both main agent and Shadow Assistant (`SettingsService.cs` lines 16-18). `GRAVITY.MD` line 7 mentions `gemma4:e2b` and `qwen3:4b` (`AIBWindows/README.md` line 25) as the recommended local model. Comments in `OpenAIService.cs` lines 84, 200-203 reference `gemma4:e2b` tuning.

- **Google Gemini (alternative provider, configured by name only)** — `SettingsService.cs` line 17 lists `"Google Gemini"` as a string value for `AiProvider`, but no Gemini-specific SDK is wired. The OpenAI client is reused; if Gemini support exists, it depends on Gemini's OpenAI-compat endpoint.

- **OpenAI cloud (legacy/optional)** — Same `OpenAI` SDK, default endpoint when `URL` env is empty (`AIBLinux/app/openai_client.py` lines 19-22: "Se URL estiver vazia ou não definida, OpenAI usa o padrão (api.openai.com)").

**Web search:**

- **DuckDuckGo Instant Answer API** (`AIBWindows/Services/WebSearchService.cs` lines 18-43):
  - Endpoint: `https://api.duckduckgo.com/?q={query}&format=json&no_html=1&skip_disambig=1`
  - User-Agent: `AIB-Assistant/1.0`
  - No auth, no API key. Parses `AbstractText`, `AbstractSource`, and top 5 `RelatedTopics`.
- **Google / Bing** — stubs only. The code returns an info message pointing back to DDG (lines 46-58), because scraping is unreliable and no API keys are configured.
- Exposed to the LLM via `SearchWebTool` (`NativeTools.cs` lines 365-389) which requires user level ≥ 3.

**Skill marketplace:**

- **skills.sh** — used for both discovery and install:
  - Search: `GET https://skills.sh/api/search?q={query}` with User-Agent `AIB-Assistant/1.0` (`AIBWindows/Services/SkillsShService.cs` lines 29-33).
  - Install: shells out to `npx -y skills add {owner/name@version} --yes` and copies the resulting `.agents/skills/{name}/` tree into `~/.AIB/skills/` (`AIBWindows/Services/SkillService.cs` lines 147-177). Implies `node` + `npx` must be on PATH at runtime.
  - Fallback: if the HTTP search fails, also shells out to `npx skills search {query}` (`SkillsShService.cs` lines 36-38, 58-61).

**Other outbound HTTP (single-purpose, well-known endpoints):**

- **HuggingFace** — one-time download of Whisper base model on first run: `https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin` saved to `%LocalAppData%\AIB\Models\ggml-base.bin` (`AIBWindows/Services/VoiceService.cs` lines 59-67).
- **ViaCEP** — invoked by the bundled `consultar_cep` Python skill: `https://viacep.com.br/ws/{cep}/json/` (`AIBWindows/Services/DefaultSkills.cs` lines 43-54).

## Data Storage

**Embedded databases:**

- **LiteDB 5.0.21** — single-file BSON store. Path: `~/.AIB/memory.db` (`AIBWindows/Services/MemoryService.cs` line 14, resolved via `DirectoryService.DataDir`).
  - Collection: `memories`
  - Schema (`MemoryService.cs` lines 30-37): `Id int`, `Title string`, `Content string`, `Vector float[]`, `CreatedAt DateTime`.
  - Operations: `RememberAsync` (`lines 39-63`), `Recall` with cosine top-3 ≥ 0.2 threshold (`lines 78-123`), `ListMemories` (`lines 125-141`), `GetRecentMemories(int count)` (`lines 143-156`), `DeleteMemory(int id)` (`lines 158-171`).

**Vector / embeddings store:**

- **SmartComponents.LocalEmbeddings** (in-process `bge-micro-v2` ONNX, ~80 MB RAM) — vectors stored as `float[]` inline on each `MemoryRecord` in LiteDB. Similarity computed in-process via manual cosine (`MemoryService.cs` lines 65-76).

**JSON file stores (no DB):**

- `~/.AIB/chat_history.json` — capped at 50 most-recent sessions (`AIBWindows/Services/ChatHistoryService.cs` lines 20, 44-47).
- `%AppData%\AIB\reminders.json` — pending reminders, re-scheduled on startup (`AIBWindows/Services/ReminderService.cs` lines 36, 40-66). (Note: `ReminderService` uses `Environment.SpecialFolder.ApplicationData` directly instead of `DirectoryService.DataDir`, so reminders land in `%AppData%\AIB\` not `~/.AIB/` — a small inconsistency.)

**Encrypted blobs (DPAPI CurrentUser scope):**

- `~/.AIB/profile.dat` — `UserAppSettings` serialized JSON, then `ProtectedData.Protect` (`AIBWindows/Services/SettingsService.cs` lines 60-88). Includes a fallback path that reads cleartext JSON and re-encrypts on next save (lines 67-78) — a legacy migration.
- `~/.AIB/credentials/{system}.bin` — credential vault. Each file holds a JSON `Dictionary<string,string>` encrypted with DPAPI (`AIBWindows/Services/CredentialService.cs` lines 41-44, 100-109). `system` is normalized to lowercase (line 26).

**File storage:**

- **Local filesystem only.** No cloud storage SDK (no AWS S3, GCS, Azure Blob, Dropbox, Drive).
- Paths managed centrally by `AIBWindows/Services/DirectoryService.cs`:
  - Data root: `~/.AIB/` (line 8) with migration from legacy `%AppData%\AIB\` (lines 32-37).
  - Temp root: `%Temp%\AIB\` (line 9) with subfolders `screenshots/`, `ocr_cache/`, `cmd_output/`.
- File reading is multi-format: PDF (`PdfPig`), DOCX (`OpenXml.Wordprocessing`), XLSX (`OpenXml.Spreadsheet`), plain text — all via `ReadFileTool` (`AIBWindows/Services/NativeTools.cs` lines 163-275).

**Caching:**

- No HTTP cache, no Redis, no Memcached.
- In-memory: `OpenAIService._history` (list of `ChatMessage`), `ShadowHistoryService.Suggestions` (capped at 5, ephemeral — `AIBWindows/Services/ShadowHistoryService.cs` lines 26, 32), `ContextService.RecentFiles` (capped at 5, ephemeral — `AIBWindows/Services/ContextService.cs` lines 38-53).

## Authentication & Identity

**No application-level auth.** AIB is single-user, runs as the logged-in Windows user, and inherits OS identity.

**Identity surfaces:**

- **Windows DPAPI** (Data Protection API) — used to bind secrets to the current Windows user account. Scope: `DataProtectionScope.CurrentUser` (`SettingsService.cs` line 61, `CredentialService.cs` lines 42, 105). Decryption only works on the same machine + same user profile.
- **Self-managed credential vault** — accessed by the LLM through the `manage_vault` tool, gated at user level ≥ 7 (`NativeTools.cs` lines 115-156). Stores 3rd-party credentials (system name + key/value pairs) on behalf of the user.

## Monitoring & Observability

**Error tracking:** None. No Sentry, Application Insights, Datadog, etc.

**Logs:**
- `Console.WriteLine` only. Examples: `[WARMUP] ...` (`OpenAIService.cs` line 78, 95), `[REGISTRY] ...` (`ToolRegistry.cs` lines 39, 47, 51, 58, 87), `[SHADOW] ...` (`ShadowAssistantService.cs` lines 98, 124, 138, 192, 207, 234, 236), `[UIA] ...` (`WindowTextExtractor.cs` lines 44, 49), `[STREAM-DBG] / [STREAM-END] ...` (`OpenAIService.cs` lines 257-298), `[OCR] ...` (`OcrService.cs` lines 45, 78, 149, 165), `[SKILL] ...` (`DynamicSkillTool.cs` line 55), `[DEBUG-COFRE] ...` (`CredentialService.cs` lines 59-78), `[HOTKEY] ...` (`App.xaml.cs` line 69), `[AI] ...` (`OpenAIService.cs` line 723).
- Verbose mode toggle: `UserAppSettings.VerboseConsoleLogging` (`SettingsService.cs` line 27) enables per-update stream diagnostics in `OpenAIService.StreamResponseAsync`.
- A `LogsDir` is reserved (`DirectoryService.cs` line 17, `~/.AIB/logs/`) but **no file logger writes to it** — only the directory is created. Nothing on disk; everything goes to `stdout` (which is hidden when launched as a WPF GUI app).

**Metrics:**
- In-app: token counter via `OpenAIService.OnTokenCountChanged` event (`OpenAIService.cs` lines 23, 834-839), driven by `Microsoft.ML.Tokenizers` BPE count against `gpt-4o`. Surfaced in the chat UI with color thresholds (`GRAVITY.MD` §2.2).
- Gamification: `UserAppSettings.MessageCount` drives the level (`LevelService.cs` lines 8-18), which in turn gates tools (`ToolRegistry.GetActiveTools(int userLevel)`).

## CI/CD & Deployment

**Hosting:** None — desktop app distribution.

**CI pipeline:** None detected. No `.github/`, no `.gitlab-ci.yml`, no `azure-pipelines.yml`.

**Build scripts (manual):**
- AIBWindows: `dotnet build` / `dotnet publish` against `AIBWindows/AIB.csproj` (`PublishSingleFile=true`, `SelfContained=true`, `RuntimeIdentifier=win-x64`).
- AIBLinux: bash scripts in `AIBLinux/` (`build.sh`, `build_universal.sh`, `docker_build.sh`) — see `STACK.md` "Build / Dev Tooling".

**Distribution:** Manual binary handoff. `AIBLinux/build.sh` line 21 reminds the user to copy `.env` next to `dist/AIB` after building.

## Environment Configuration

**Required env vars** (per `AIBWindows/README.md` lines 22-26; AIBLinux reads them via `python-dotenv`):
- `OPENAI_API_KEY` — placeholder `ollama` for local, real key for cloud.
- `URL` — OpenAI-compatible base URL (e.g., `http://localhost:11434/v1`).
- `MODEL` — model name (e.g., `qwen3:4b`).

**Note on AIBWindows:** The C# code does not call `DotNetEnv.Env.Load()` despite the package being a declared dependency. All runtime config is sourced from the DPAPI-encrypted `profile.dat`. The `AIBWindows/.env` file appears to be vestigial.

**Secrets location:**
- `.env` files at repo root, in `AIBWindows/`, and in `AIBLinux/`. All three are `.gitignored`-equivalent (their existence is noted but not their contents).
- Encrypted runtime secrets in `~/.AIB/profile.dat` and `~/.AIB/credentials/*.bin` (DPAPI CurrentUser).

## Webhooks & Callbacks

**Incoming:** None. No HTTP server, no socket listener.

**Outgoing:**
- Outbound HTTP only (see "APIs & External Services" above) — Ollama localhost, DuckDuckGo, HuggingFace (one-time), skills.sh, ViaCEP (via skill).

## Process / IPC integrations

**Subprocess spawning (`cmd.exe /c …`):**
- All shell execution is funneled through `AIBWindows/Services/CommandService.cs` (`Process` with `RedirectStandardOutput/Error`, UTF-8, default 20 s timeout, 50 KB output cap — lines 14-65).
- Triggered by:
  - `RunCommandTool` (LLM-driven, level ≥ 2, with per-level sandboxing for system dirs, destructive ops, and network commands — `NativeTools.cs` lines 281-358).
  - `SkillService.RunSkillAsync` and `DynamicSkillTool.ExecuteAsync` (Python `py`, PowerShell `powershell.exe -NoProfile -ExecutionPolicy Bypass -File`, or `cmd.exe /c`).
  - `SkillService.InstallFromOnlineAsync` via `npx -y skills add … --yes` (`SkillService.cs` line 161).
  - `SkillsShService` fallback search via `npx skills search` (`SkillsShService.cs` lines 36-38, 60).

**Clipboard (Windows):**
- `System.Windows.Clipboard.SetText` / `GetText` via `ManageClipboardTool` (`NativeTools.cs` lines 516-573). Write requires level ≥ 6.
- Smart paste of file paths from Windows Explorer in chat input (`GRAVITY.MD` §1.2).

**Win32 P/Invoke (`user32.dll`):**
- `GetForegroundWindow`, `GetWindowText` — active window detection in `ReadScreenTool` (`NativeTools.cs` lines 405-441) and `ShadowAssistantService` (`AIBWindows/Services/ShadowAssistantService.cs` lines 296-306).
- `WindowFromPoint`, `GetAncestor`, `GetClassName` — cursor → top-level window resolution for Shadow Assistant (`ShadowAssistantService.cs` lines 266-307).
- `GetWindowRect` — window bounds for OCR window capture (`OcrService.cs` lines 64, 170-175).

**WinRT (`Windows.Media.Ocr`):**
- Built-in Windows 10/11 OCR engine — language-aware (`pt-BR` preferred; falls back to user profile languages — `OcrService.cs` lines 22-27). Requires the user to install the OCR language pack via "Settings → Apps → Optional features".
- Inputs: `BitmapDecoder` → `SoftwareBitmap` → `OcrEngine.RecognizeAsync`. Multiple capture variants: active screen (`ExtractTextFromActiveScreenAsync`), specific HWND (`ExtractTextFromWindowAsync`), all monitors (`ExtractTextFromAllScreensAsync`), arbitrary base64 (`ExtractTextFromBase64Async`).

**UI Automation (`System.Windows.Automation`):**
- Text extraction from focused window via UIA tree walk (`AIBWindows/Services/WindowTextExtractor.cs`). Tries `TextPattern` first, then `ControlType.Document`, then full tree walk capped at depth 8 / 4000 chars. 800 ms hard timeout. Used by `ShadowAssistantService` as a 10× cheaper replacement for OCR (~50-300 ms / <15 MB instead of 1-2 s / 200-400 MB — comment at `WindowTextExtractor.cs` line 17).

**Audio (NAudio):**
- `WaveInEvent` capture at 16 kHz / 16-bit / mono → in-memory buffer → manually framed WAV bytes → `Whisper.net` processor (`AIBWindows/Services/VoiceService.cs` lines 73-92, 189-222). Auto-commits transcription on 1200 ms silence threshold when wake word `"AIB"` is detected (lines 32-36, 152-163).

**System tray / notifications:**
- WPF tray icon (`Hardcodet.NotifyIcon.Wpf`) with context menu, left-click toggles main window (`App.xaml.cs` lines 38-57).
- Balloon notifications: `_notifyIcon.ShowBalloonTip(...)` + `SystemSounds.Beep.Play()` (`App.xaml.cs` lines 17-24). Used by `ReminderService` to fire reminders (`ReminderService.cs` lines 114-118).

**Global hotkey:**
- `NHotkey.Wpf` `Ctrl+Shift+Space` toggles the chat window (`App.xaml.cs` lines 61-66, 83-87). Failure is logged, not fatal.

**Shadow Assistant (cursor-driven RPA):**
- `DispatcherTimer` at 400 ms polls cursor position. After 3 s dwell on a window, extracts text via UIA, sends it to LLM with a dedicated stateless prompt (`ShadowAssistantService.cs` lines 25-27, 62-68, 235). Per-screen widget activation via `OnActiveScreenChanged` event (lines 59-60, 130-139). Opt-in via `UserAppSettings.ShadowAssistantEnabled` (default `false`).

**Inter-component (in-process):**
- `OpenAIService` ↔ `ToolRegistry` ↔ `ITool` implementations. The registry dispatches by tool name; tools are level-gated (`ToolRegistry.cs` lines 32-53).
- Two execution modes for tool calls:
  1. Native OpenAI function-calling via `ChatTool.CreateFunctionTool` schema (each tool exposes one in `ITool.ChatToolDefinition`).
  2. Regex fallback that scrapes `Action: name({...})` patterns out of free text when the LLM can't produce valid JSON (`OpenAIService.cs` lines 34-36). Mentioned in `GRAVITY.MD` §2.4.

## MCP / Agent Frameworks

**Model Context Protocol (MCP):** Not detected. No MCP server/client SDK referenced.

**Skills protocol:**
- The project uses **skills.sh** as a marketplace (see above). Skills are simple Python/PowerShell scripts with a `skill.json` manifest (`{Name, Description, ScriptFile, Interpreter, Dependencies}` — `AIBWindows/Services/SkillService.cs` lines 10-17) or a Markdown front-matter form (lines 106-128). Lazy loading — they are NOT registered as top-level tools to avoid prompt bloat; instead the LLM calls them via the `execute_skill` tool (`NativeTools.cs` lines 447-474). The `materialize_skill` tool (level ≥ 5) lets the agent author new skills on the fly (`NativeTools.cs` lines 480-510).

---

*Integration audit: 2026-05-28*
