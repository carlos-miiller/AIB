<!-- refreshed: 2026-05-28 -->
# Architecture

**Analysis Date:** 2026-05-28

AIB is a desktop AI assistant shipped in two parallel implementations that share a product concept (tray-resident floating chat triggered by `Ctrl+Shift+Space`, talking to a local LLM via Ollama / OpenAI-compatible API) but diverge sharply in scope:

- **`AIBWindows/`** — production target. C# / .NET 8 / WPF. Full ReAct agent with native tools, RAG memory, OCR, voice input, credential vault, dynamic skills, and a "Shadow Assistant" that watches the active window.
- **`AIBLinux/`** — legacy / experimental. Python 3.10+ / PyQt6. Plain streaming chat with one optional input modality (screenshot of the primary monitor). No tools, no memory, no agent loop.

The `documentação/01_Architecture_Overview.md` set describes the Windows variant. The Linux variant is essentially the seed prototype the Windows code grew out of and is documented here separately.

---

## System Overview — AIBWindows

```text
┌─────────────────────────────────────────────────────────────────────────┐
│                       WPF UI Layer (Views/)                              │
├─────────────────┬─────────────────┬──────────────────┬──────────────────┤
│   ChatWindow    │ SettingsWindow  │ ContextSidebar   │  ShadowWidget    │
│  (composition   │  (API/provider  │ (history, files, │ (per-monitor     │
│   root + chat)  │     config)     │  reminders tab)  │  hover overlay)  │
│  ChatWindow.xaml│ SettingsWindow  │ ContextSidebar   │ ShadowWidget     │
│      .cs        │      .xaml.cs   │      .xaml.cs    │     .xaml.cs     │
└────────┬────────┴────────┬────────┴────────┬─────────┴────────┬─────────┘
         │                 │                 │                  │
         └─────────┬───────┴─────────────────┴──────────────────┘
                   ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                   Cognition Layer (Services/)                            │
│                                                                          │
│   OpenAIService ──────────────► ToolRegistry ──────► NativeTools (ITool) │
│   (ReAct loop, streaming,       (level-gated         (12 native tools)   │
│    warmup, history trim)         dispatch)                               │
│                                                                          │
│   ShadowAssistantService ──────► WindowTextExtractor / OcrService        │
│   (dwell-based passive observe)  (UIA preferred, OCR fallback)           │
└────────┬─────────────────────────────────────────────────────┬──────────┘
         │                                                     │
         ▼                                                     ▼
┌──────────────────────────────────┐    ┌──────────────────────────────────┐
│  Persistence / OS Layer          │    │  Hardware I/O Layer              │
│  - SettingsService (DPAPI)       │    │  - VoiceService (NAudio+Whisper) │
│  - CredentialService (DPAPI)     │    │  - ScreenshotService (multi-mon) │
│  - MemoryService (LiteDB+BGE)    │    │  - OcrService (Windows.Media.Ocr)│
│  - ChatHistoryService (JSON)     │    │  - WindowTextExtractor (UIA)     │
│  - ReminderService (JSON)        │    │  - WebSearchService (DDG HTTP)   │
│  - SkillService (filesystem)     │    │  - CommandService (cmd.exe)      │
│  - DirectoryService (paths)      │    │                                  │
└──────────────────────────────────┘    └──────────────────────────────────┘
         │                                            │
         ▼                                            ▼
┌──────────────────────────────────┐    ┌──────────────────────────────────┐
│  External: Ollama / OpenAI       │    │  Local FS:                       │
│  HTTP at 127.0.0.1:11434/v1      │    │  %USERPROFILE%\.AIB\             │
│  (or any OpenAI-compatible)      │    │   ├─ profile.dat (DPAPI)         │
│                                  │    │   ├─ memory.db (LiteDB)          │
│                                  │    │   ├─ credentials\*.bin (DPAPI)   │
│                                  │    │   ├─ skills\<name>\skill.json    │
│                                  │    │   └─ .default_skills\            │
│                                  │    │  %LOCALAPPDATA%\AIB\Models\      │
│                                  │    │   └─ ggml-base.bin               │
└──────────────────────────────────┘    └──────────────────────────────────┘
```

## Component Responsibilities — AIBWindows

| Component | Responsibility | File |
|-----------|----------------|------|
| `App` | WPF entry point. Creates `ChatWindow`, tray icon, global hotkey. | `AIBWindows/App.xaml.cs:12` |
| `ChatWindow` | Composition root. Owns service instances, drives ReAct UI, renders bubbles, handles smart-paste / voice. | `AIBWindows/Views/ChatWindow.xaml.cs:23` |
| `OpenAIService` | Talks to LLM. Owns `_history`, runs the streaming ReAct loop, warmup, token-count event, dual-mode tool dispatch. | `AIBWindows/Services/OpenAIService.cs:13` |
| `ToolRegistry` | Holds `Dictionary<string, ITool>`. `GetActiveTools(userLevel)` filters by `RequiredLevel`. Dispatches `ExecuteToolAsync`. | `AIBWindows/Services/ToolRegistry.cs:14` |
| `ITool` | Contract: `Name`, `Description`, `ChatToolDefinition` (OpenAI schema), `RequiredLevel`, `ExecuteAsync`. | `AIBWindows/Services/ITool.cs:11` |
| `NativeTools.cs` | Ships 12 `ITool` implementations (memory, vault, file, command, screen, web, skill, clipboard, reminder, glob, grep, list_dir). | `AIBWindows/Services/NativeTools.cs:71` |
| `LevelService` | Static. XP→level (1–9) and level→max-tokens table. Gates tool access. | `AIBWindows/Services/LevelService.cs:5` |
| `MemoryService` | RAG: BGE embeddings via `SmartComponents.LocalEmbeddings` + LiteDB store + cosine similarity (top-3, threshold 0.2). | `AIBWindows/Services/MemoryService.cs:12` |
| `CredentialService` | Per-system credential vault. AES via Windows DPAPI (`ProtectedData.Protect`). | `AIBWindows/Services/CredentialService.cs:11` |
| `SettingsService` | Loads/saves `UserAppSettings` as DPAPI-encrypted JSON at `profile.dat`. Reads Ollama `/api/tags`. | `AIBWindows/Services/SettingsService.cs:44` |
| `DirectoryService` | Centralised path resolution (`~/.AIB`, temp dirs). Migrates from old `%AppData%\AIB`. | `AIBWindows/Services/DirectoryService.cs:6` |
| `VoiceService` | NAudio `WaveInEvent` 16 kHz mono → `Whisper.net` local transcription (`ggml-base.bin`). Wake-word "AIB". | `AIBWindows/Services/VoiceService.cs:19` |
| `OcrService` | Windows Runtime `Windows.Media.Ocr` (pt-BR preferred). Active-screen or all-screens capture. | `AIBWindows/Services/OcrService.cs:12` |
| `WindowTextExtractor` | UI Automation walker (preferred over OCR for browsers/editors). 800 ms timeout, 4000-char cap. | `AIBWindows/Services/WindowTextExtractor.cs:22` |
| `ShadowAssistantService` | Dwell detector (cursor still ≥3 s) → extract window text → ask LLM for one-shot suggestion → bubble. | `AIBWindows/Services/ShadowAssistantService.cs:23` |
| `SkillService` | Filesystem-backed dynamic skills (`~/.AIB/skills`). Seeded from `DefaultSkills.cs` on first run. | `AIBWindows/Services/SkillService.cs:19` |
| `CommandService` | Wraps `cmd.exe /c` with 20 s timeout, stdout/stderr capture, 50 KB truncation. | `AIBWindows/Services/CommandService.cs:8` |
| `WebSearchService` | DuckDuckGo Instant Answer JSON API. | `AIBWindows/Services/WebSearchService.cs:10` |
| `ReminderService` | Persistent `ObservableCollection<Reminder>` in `reminders.json`. | `AIBWindows/Services/ReminderService.cs:34` |
| `ChatHistoryService` | Persists past chat sessions in `chat_history.json` (top-50, by recency). | `AIBWindows/Services/ChatHistoryService.cs:18` |
| `ShadowHistoryService` | In-memory `ObservableCollection<ShadowSuggestion>` (cap 5). Not persisted. | `AIBWindows/Services/ShadowHistoryService.cs:24` |
| `CommandConfirmationWindow` | Modal: user approves/denies any `run_command` payload before it executes. | `AIBWindows/Views/CommandConfirmationWindow.xaml.cs:6` |

## Pattern Overview — AIBWindows

**Overall:** Layered MVVM-ish + ReAct agent loop. There is no IoC container — the `App` → `ChatWindow` chain is the composition root and instantiates services with `new` (see `App.xaml.cs:33` for `SettingsService`, `ChatWindow.xaml.cs:42-65` for `OpenAIService`, `ShadowAssistantService`, `VoiceService`).

**Key Characteristics:**
- No DI container; manual constructor injection from `ChatWindow`.
- Most cross-cutting state is exposed as static collections (`ContextService.ActiveFiles`, `ReminderService.ActiveReminders`, `ShadowHistoryService.Suggestions`) bound directly into XAML.
- Tools follow a single interface (`ITool`). The registry filters by integer `RequiredLevel` rather than capability flags — gamification doubles as authorisation.
- Streaming is the default I/O shape: `IAsyncEnumerable<string>` from `OpenAIService.StreamResponseAsync` to UI.
- Persistence is split across DPAPI-encrypted JSON (`profile.dat`, `*.bin` credentials), plain JSON (`reminders.json`, `chat_history.json`), LiteDB (`memory.db`), and a `skills/` filesystem tree.

## Layers

**UI Layer (`AIBWindows/Views/`):**
- Purpose: WPF windows and user controls. Frameless, transparent, custom Acrylic.
- Depends on: `Services` for everything except pure styling.
- Used by: `App.xaml.cs` (creates `ChatWindow`).

**Cognition Layer (`AIBWindows/Services/OpenAIService.cs`, `ToolRegistry.cs`, `NativeTools.cs`):**
- Purpose: LLM dialog and tool dispatch. Owns the active conversation `_history`.
- Depends on: `OpenAI` SDK, `Microsoft.ML.Tokenizers`, persistence services.
- Used by: `ChatWindow`, `ShadowAssistantService`.

**Persistence Layer (`SettingsService`, `MemoryService`, `CredentialService`, `ChatHistoryService`, `ReminderService`, `SkillService`):**
- Purpose: Local-disk state. Mostly static classes (no DI needed because the resources are filesystem-scoped per OS user).
- Depends on: `DirectoryService`, `LiteDB`, `System.Security.Cryptography.ProtectedData`, `SmartComponents.LocalEmbeddings`.
- Used by: `OpenAIService`, tools in `NativeTools.cs`.

**Hardware / OS Layer (`VoiceService`, `OcrService`, `ScreenshotService`, `WindowTextExtractor`, `CommandService`):**
- Purpose: Adapters over OS APIs (`Windows.Media.Ocr`, `System.Windows.Automation`, `cmd.exe`, NAudio, Whisper.net).
- Depends on: Windows-specific runtime packages — this layer is the reason `AIBLinux` cannot be ported "as-is".
- Used by: tools in `NativeTools.cs`, `ShadowAssistantService`, `ChatWindow` voice button.

## Data Flow — AIBWindows

### Primary Request Path (ReAct loop)

1. User hits `Ctrl+Shift+Space` → global hotkey fires → `ChatWindow.ToggleWindow()` (`App.xaml.cs:85`, `ChatWindow.xaml.cs:138`).
2. User types and presses Enter → `ChatWindow.SendUserMessageAsync()` (called from `InputBox_PreviewKeyDown`).
3. `OpenAIService.StreamResponseAsync(userMessage)` (`OpenAIService.cs:183`) appends a `UserChatMessage` to `_history`, computes `userLevel` via `LevelService.GetLevel(MessageCount)`, and grabs `ToolRegistry.GetActiveTools(userLevel)` (`ToolRegistry.cs:24`).
4. Loop (up to 18 iterations, `OpenAIService.cs:203`):
   - Calls `ChatClient.CompleteChatStreamingAsync(_history, chatOptions, ct)`.
   - State machine splits chunks into three modes: `Streaming` (emit to UI), `InsideThink` (route to `onTechnicalContent`), `WaitingFinal` (buffer until Harmony channel marker, e.g. gemma4) — see `OpenAIService.cs:240`.
   - If `FinishReason == ToolCalls`, accumulator (indexed by `tcUpdate.Index`, `OpenAIService.cs:414`) collects partial tool calls, then dispatches them through `ToolRegistry.ExecuteToolAsync(name, json, userLevel)`.
   - Tool result is appended as `ToolChatMessage` and the loop continues.
   - Fallback: a regex (`TextActionRegex`, `OpenAIService.cs:34`) catches `Action: tool_name({...})` written in plain text and re-dispatches.
5. On final stop, `ChatWindow.UpdateLoadingState(false)` reveals the buffered Markdown via `MarkdownViewer`, animates the bubble (`AnimateBubbleIn`, `ChatWindow.xaml.cs:301`), increments XP via `RefreshLevelUI(true)`, persists session via `ChatHistoryService.SaveCurrentSession`.

### Warmup / Heartbeat Path

1. `OpenAIService` constructor schedules `WarmupAndKeepAliveAsync()` on a background `Task` (`OpenAIService.cs:63`).
2. POSTs `keep_alive=-1` + `num_ctx=16384` to `{apiUrl}/api/generate` to pin model in Ollama VRAM (`OpenAIService.cs:86-94`).
3. Sends a ghost `[SYSTEM_HEARTBEAT]` user message through the real `_history` to force grammar compilation, then strips it (`OpenAIService.cs:107-130`).
4. Emits `OnWarmupStateChanged(true/false)` so `ChatWindow.HandleWarmupState` can lock/unlock the input box.

### Shadow Assistant Path (passive observer)

1. `ShadowAssistantService` polls cursor every 400 ms (`ShadowAssistantService.cs:25`).
2. When cursor moves <6 px for ≥3 s, it identifies the window under the cursor, dedups by window title.
3. Tries `WindowTextExtractor.ExtractTextAsync(hwnd)` (UI Automation, ~50–300 ms). If null, falls back to `OcrService.ExtractTextFromActiveScreenAsync()`.
4. Calls a separate single-shot LLM completion with the Shadow `SYSTEM_PROMPT` (`ShadowAssistantService.cs:62`), routes the result to `OnSuggestionReceived` → `ShadowWidget.ShowSuggestion`.

### Voice Path

1. `VoiceService.StartListening` creates `WaveInEvent` (16 kHz, 16-bit, mono) and pushes to `_audioBuffer` (`VoiceService.cs:73`).
2. Background `ProcessingLoop` cuts on 1200 ms of silence, feeds the chunk through `WhisperProcessor.ProcessAsync` (Portuguese forced, `VoiceService.cs:55`).
3. Emits `OnTranscriptionUpdated`; `ChatWindow` writes the text into `InputBox` and triggers `SendUserMessageAsync` on the final transcript.

**State Management:**
- LLM conversation: `List<ChatMessage> _history` in `OpenAIService` (in-process; index 0 protected as system prompt — `OpenAIService.cs:147`).
- Settings: round-tripped through `SettingsService.LoadSettings()` each call (no in-memory cache).
- UI-bound collections: static `ObservableCollection`s in `ContextService`, `ReminderService`, `ShadowHistoryService`.

## Key Abstractions

**`ITool`:**
- Purpose: Uniform contract for everything the LLM can invoke. `ChatToolDefinition` produces the OpenAI function schema; `ExecuteAsync(json, userLevel)` runs the action and returns a `string` consumed back into `_history` as a `ToolChatMessage`.
- Examples: `AIBWindows/Services/NativeTools.cs:71` (`ManageMemoryTool`), `:281` (`RunCommandTool`), `:447` (`ExecuteSkillTool`).
- Pattern: each tool is a stateless class; the schema is a verbatim JSON literal embedded in C# raw strings.

**`UserAppSettings`:**
- Purpose: The single mutable config record (provider, model, levels gate, opt-ins).
- Persisted: DPAPI-encrypted JSON at `DirectoryService.SettingsPath` (`AIBWindows/Services/SettingsService.cs:46`).
- Pattern: plain POCO with default initializers (`AIBWindows/Services/SettingsService.cs:12`).

**`MemoryRecord`:**
- Purpose: One RAG fact. Fields: `Id`, `Title`, `Content`, `float[] Vector`, `CreatedAt` (`AIBWindows/Services/MemoryService.cs:30`).
- Persisted: LiteDB collection `"memories"` at `~/.AIB/memory.db`.

**`SkillMetadata`:**
- Purpose: Describes a dynamic skill on disk (`skill.json`).
- Fields: `Name`, `Description`, `ScriptFile`, `Interpreter` (`python` / `powershell` / `cmd` / `markdown`), `Dependencies` (`AIBWindows/Services/SkillService.cs:10`).

## Entry Points

**WPF process entry:**
- Location: `AIBWindows/App.xaml.cs:26` (`OnStartup`).
- Triggers: process launch (`AIB.csproj` sets `OutputType=Exe`, `UseWPF=true`).
- Responsibilities: ensure data dirs (`DirectoryService.EnsureDirectories`), load settings, instantiate `ChatWindow`, install `TaskbarIcon`, register `HotkeyManager` for `Ctrl+Shift+Space`.

**Global hotkey:**
- Location: `AIBWindows/App.xaml.cs:61` registers via `NHotkey.Wpf.HotkeyManager`.
- Triggers: any `Ctrl+Shift+Space` system-wide.
- Routes to: `ChatWindow.ToggleWindow()`.

**Tray click:**
- Location: `AIBWindows/App.xaml.cs:57` (`TrayLeftMouseDown`).
- Routes to: `ChatWindow.ToggleWindow()`.

## Architectural Constraints — AIBWindows

- **Threading:**
  - WPF UI thread owns all `MessagesPanel.Children.Add` calls and animations.
  - `OpenAIService.StreamResponseAsync` is `async` but its `yield return` is consumed back on the UI thread (the caller `await foreach`s and renders).
  - `WarmupAndKeepAliveAsync` runs on a `Task.Run` background thread; its UI-relevant signal goes through the `OnWarmupStateChanged` event, which `ChatWindow.HandleWarmupState` re-marshals via `Dispatcher.BeginInvoke` (`ChatWindow.xaml.cs:112`).
  - `VoiceService.ProcessingLoop` runs on a `Task.Run` background thread (`VoiceService.cs:91`); it must marshal back to UI via subscribers handling the event.
  - `ShadowAssistantService` uses a WPF `DispatcherTimer` so its poll runs on the UI thread; the LLM call inside it is `await`ed and may take seconds — interactive UI can stutter when Shadow is active (see `CONCERNS.md`).
- **Global state:**
  - `ContextService.ActiveFiles`, `ContextService.RecentFiles` — static `ObservableCollection<ContextFile>` (`ContextService.cs:35`).
  - `ReminderService.ActiveReminders` — static `ObservableCollection<Reminder>` (`ReminderService.cs:38`).
  - `ShadowHistoryService.Suggestions` — static `ObservableCollection<ShadowSuggestion>` (`ShadowHistoryService.cs:32`).
  - `MemoryService._embedder` — lazy static `LocalEmbedder` singleton (`MemoryService.cs:15`).
  - `DirectoryService._dataDir`, `_tempDir` — static mutable paths (`DirectoryService.cs:8`).
- **Process model:**
  - Single-instance assumption: the global hotkey + tray icon both target the same `_chatWindow` reference (`App.xaml.cs:15`). No `Mutex` is taken — two instances will register competing hotkeys and `Settings.dat` writes can race.
- **No formal DI:** every service in `ChatWindow.xaml.cs:42-65` is `new`'d in the constructor. Most data-layer services (`MemoryService`, `CredentialService`, `ChatHistoryService`, `ReminderService`, `WebSearchService`, `CommandService`, `FileService`, `SkillService`, `DefaultSkills`, `LevelService`, `ShadowHistoryService`, `DirectoryService`, `WindowTextExtractor`) are `static` classes.
- **Validating `documentação/01_Architecture_Overview.md`:** The doc claims tools must use `[SYSTEM_HEARTBEAT]` warmup to compile the JSON grammar (confirmed at `OpenAIService.cs:110`), that `_history[0]` is protected from trimming (confirmed at `OpenAIService.cs:147`), and that there's a regex fallback for tool-as-text (confirmed at `OpenAIService.cs:34`). The doc also describes a `TrimHistoryAsync` method that prunes nodes 1 and 2 — this method is not present under that name in the current `OpenAIService.cs`; the actual pruning is integrated into the streaming loop and the system-prompt protection is enforced at `ResetHistory` time. **Documentation drift:** Tool count claimed in `04_Tools_and_Capabilities.md` (10 tools) is now **12** in `ToolRegistry.RegisterNativeTools()` (`ToolRegistry.cs:65`): `GlobTool`, `GrepTool`, `ListDirTool` were added (see "filesystem exploration tools (commit 2)" comment).

## Anti-Patterns

### Composition Root in a UI Class

**What happens:** `ChatWindow` instantiates `SettingsService`, `OpenAIService`, `ShadowAssistantService`, `VoiceService` directly in its constructor (`ChatWindow.xaml.cs:42-65`) and stores them in private fields.
**Why it's wrong here:** any new feature that wants `OpenAIService` has to either go through `ChatWindow` or `new` its own copy and re-create the warmup + history overhead. `SettingsWindow`, for example, instantiates a **second** `SettingsService` (`SettingsWindow.xaml.cs:12`) — fine for stateless reads but a foot-gun if the service ever caches.
**Do this instead:** keep service construction in `App.OnStartup` (already partly there for `SettingsService`) and inject into `ChatWindow`'s constructor; or extract a small `Locator` static.

### `new SettingsService()` everywhere

**What happens:** `App.xaml.cs:33`, `ChatWindow.xaml.cs:42`, `SettingsWindow.xaml.cs:12`, `OpenAIService` constructor receives one, etc. Every consumer constructs its own and calls `LoadSettings()` (which is a full DPAPI decrypt + JSON deserialize) on every read.
**Why it's wrong here:** every level/token check (`LevelService.GetLevel(_settingsService.LoadSettings().MessageCount)` — see e.g. `OpenAIService.cs:196`, `ChatWindow.xaml.cs:69`) hits disk. The hot path of `StreamResponseAsync` does this inside the per-loop body.
**Do this instead:** cache `UserAppSettings` in `SettingsService` and invalidate on `SaveSettings`.

### Settings-loaded-per-keystroke for hot-path booleans

**What happens:** `_settingsService.LoadSettings().EnableIntelligentTools` and `.VerboseConsoleLogging` are read inside the streaming hot loop in `OpenAIService.StreamResponseAsync` (`OpenAIService.cs:213, :258`).
**Why it's wrong here:** see above — DPAPI decrypt per chunk frame is wasteful and surprising.
**Do this instead:** snapshot once at the top of `StreamResponseAsync`.

## Error Handling

**Strategy:** "Swallow and stringify" — most service methods wrap their bodies in `try/catch(Exception ex) { return "ERRO: " + ex.Message; }`. The returned error string is fed back into the LLM as a `ToolChatMessage`, letting the agent self-correct in the next ReAct iteration. Examples: `MemoryService.RememberAsync` (`MemoryService.cs:60`), `CredentialService.StoreCredentialAsync` (`CredentialService.cs:47`), `CommandService.ExecuteAsync` (`CommandService.cs:68`), every `*Tool.ExecuteAsync`.

**Patterns:**
- LLM-facing errors: prefixed `ERRO: ` or `ACESSO NEGADO: ` (in Portuguese) so the agent can pattern-match in its reasoning.
- UI-facing fatals: only `App.OnStartup` shows a `MessageBox` and calls `Current.Shutdown()` (`App.xaml.cs:73`).
- Console-only telemetry: `Console.WriteLine("[REGISTRY] ...")`, `[WARMUP] ...`, `[STREAM-DBG] ...`, `[DEBUG-COFRE] ...`. No structured logger.

## Cross-Cutting Concerns

**Logging:** Plain `Console.WriteLine` with bracketed tags. Verbose mode is gated by `UserAppSettings.VerboseConsoleLogging`. There is no log file.

**Validation:** Tool argument validation lives inside each tool (`ToolArgParser.Get` at `NativeTools.cs:16` does a JSON parse with multiple fallbacks). There is no shared validation library.

**Authentication:** None for tools internally; `RequiredLevel` filtering in `ToolRegistry.GetActiveTools(userLevel)` is the access mechanism. Provider credentials live in `UserAppSettings.ApiKey` (DPAPI-encrypted on disk).

**Security:** `CommandConfirmationWindow` (`AIBWindows/Views/CommandConfirmationWindow.xaml.cs:6`) modal-gates `run_command` payloads. Sandboxing for `RunCommandTool` is enforced by level (see `documentação/04_Tools_and_Capabilities.md`) and is implemented inline in `NativeTools.cs:281`.

---

## System Overview — AIBLinux

The Linux variant is a single-process PyQt6 app with three modules and a hotkey thread:

```text
┌──────────────────────────────────────────────────────────┐
│  PyQt6 main thread                                       │
│  ┌──────────────────────────────────────────────────┐    │
│  │  App (main.py)                                   │    │
│  │   ├─ ChatWindow (chat_window.py)                 │    │
│  │   │   └─ DimOverlay, MessageBubble, StreamWorker │    │
│  │   ├─ QSystemTrayIcon                             │    │
│  │   └─ _show_signal (cross-thread to hotkey)       │    │
│  └──────────────────────────────────────────────────┘    │
└──────────────────────────────────────────────────────────┘
              ▲                          │
              │ pyqtSignal               ▼
┌──────────────────────────┐  ┌──────────────────────────┐
│  HotkeyListener thread   │  │  StreamWorker QThread    │
│  pynput.GlobalHotKeys    │  │  OpenAIClient.send_msg() │
│  Ctrl+Shift+Space        │  │  (yields chunks)         │
│  (hotkey.py:6)           │  │  (chat_window.py:32)     │
└──────────────────────────┘  └──────────────────────────┘
                                       │
                                       ▼
                          ┌──────────────────────────┐
                          │  openai SDK (HTTP)       │
                          │  base_url = $URL         │
                          │  api_key  = $OPENAI_API_KEY
                          │  model    = $MODEL       │
                          └──────────────────────────┘

  Optional input:
  screenshot.capture_screen() — mss → PIL → base64 JPEG (primary monitor only)
```

## Component Responsibilities — AIBLinux

| Component | Responsibility | File |
|-----------|----------------|------|
| `main.main()` | Builds `QApplication`, instantiates `App`, calls `app.exec()`. | `AIBLinux/main.py:96` |
| `App` (in main.py) | Tray icon, hotkey listener wiring, cross-thread signal to show/hide window. | `AIBLinux/main.py:39` |
| `ChatWindow` | The whole UI — frameless gradient-bubble chat, glass overlay, screenshot toggle. | `AIBLinux/app/chat_window.py:168` |
| `StreamWorker` | `QObject` moved to a `QThread`; pulls chunks from `OpenAIClient.send_message` and emits `chunk_received`. | `AIBLinux/app/chat_window.py:32` |
| `OpenAIClient` | OpenAI SDK wrapper. Holds `self.history` list of role/content dicts, sends with `stream=True`. | `AIBLinux/app/openai_client.py:13` |
| `HotkeyListener` | `threading.Thread` daemon. `pynput.keyboard.GlobalHotKeys` for `<ctrl>+<shift>+<space>`. | `AIBLinux/app/hotkey.py:6` |
| `screenshot.capture_screen` / `image_to_base64` | `mss.mss().monitors[1]` primary-monitor capture + PIL → base64 JPEG (1920×1080 max, q=85). | `AIBLinux/app/screenshot.py:8` |
| `verify_ollama.py` | One-shot sanity check that `OpenAIClient` initialises against an Ollama URL. | `AIBLinux/verify_ollama.py` |

## Data Flow — AIBLinux

1. User presses `Ctrl+Shift+Space` → `HotkeyListener` callback → `App._show_signal.emit()` (thread-safe Qt signal).
2. `_toggle_window` → `ChatWindow.show_window()` (animations + optional dim overlay).
3. User types message, optionally toggles screenshot include → `ChatWindow` builds a `StreamWorker(client, text, image_base64)`, moves it to a `QThread`, connects `chunk_received` → label update.
4. `StreamWorker.run()` iterates `OpenAIClient.send_message(text, image_base64)`:
   - Builds a `content` array (image + text if screenshot, else text-only).
   - Appends `{role:"user", content:...}` to `self.history`.
   - Calls `client.chat.completions.create(model, messages=[system, *history], stream=True, max_tokens=2048)`.
   - Yields each delta string back to the worker.
5. After stream ends, full response is appended as `{role:"assistant", content:full_response}`.

## Divergences from AIBWindows

| Concern | AIBWindows | AIBLinux |
|---------|------------|----------|
| LLM client | `OpenAI` C# SDK 2.10.0 with `ChatCompletionOptions.Tools` (ReAct) | `openai` Python SDK, plain `chat.completions.create` (no tools) |
| Agent loop | 18-iteration ReAct with two fallback parsers | Single round-trip |
| Tools | 12 native `ITool` + dynamic skills | None |
| Memory | LiteDB + BGE embeddings (`MemoryService`) | None (per-process `self.history`) |
| Credentials | DPAPI vault (`CredentialService`) | None (relies on `.env`) |
| Voice | NAudio + Whisper.net local | None |
| OCR / Vision | Windows.Media.Ocr + UIA fallback | Screenshot only (sent to multimodal model) |
| Settings storage | DPAPI JSON at `~/.AIB/profile.dat` | `.env` via `python-dotenv` |
| Hotkey | `NHotkey.Wpf.HotkeyManager` (Win32) | `pynput.keyboard.GlobalHotKeys` |
| Shadow Assistant | Yes (`ShadowAssistantService`) | No |
| Token accounting | `Microsoft.ML.Tokenizers.TiktokenTokenizer` + level cap | `max_tokens=2048` hard-coded |
| Packaging | `dotnet publish` single-file self-contained `win-x64` (`AIB.csproj:11`) | PyInstaller `--onefile --windowed` (`build.sh:17`) |

The Linux variant should be considered a reference / proof-of-concept rather than a maintained client. `AIBLinux/README.md:48` confirms: *"o desenvolvimento principal está focado na versão Windows"*.

---

*Architecture analysis: 2026-05-28*
