# Technology Stack

**Analysis Date:** 2026-05-28

AIB is a dual-stack desktop AI assistant with two parallel implementations:

- **AIBWindows** — Production-focused .NET 8 WPF app (C#). Primary target.
- **AIBLinux** — Lightweight PyQt6 reference implementation (Python). Maintenance mode (per `AIBLinux/README.md` line 48: "desenvolvimento principal está focado na versão Windows").

Both stacks talk to the same local LLM runtime (Ollama, OpenAI-compatible API). See `INTEGRATIONS.md` for the external service surface.

## Languages

**Primary:**
- **C# 12 / .NET 8** — AIBWindows. 32 `.cs` files across `AIBWindows/Services/` and `AIBWindows/Views/`. `Nullable=enable`, `ImplicitUsings=enable` (`AIBWindows/AIB.csproj` lines 6-7).
- **Python 3.10+** — AIBLinux runtime. 4 modules under `AIBLinux/app/` plus `AIBLinux/main.py` and `AIBLinux/verify_ollama.py`. Bullseye-based Docker build pins Python 3.11 (`AIBLinux/build_universal.sh` line 10) or 3.12 (`AIBLinux/docker_build.sh` line 2).

**Markup / UI:**
- **XAML** — 6 `.xaml` files under `AIBWindows/Views/` (`ChatWindow.xaml` 21.2K, `ContextSidebar.xaml` 26.2K, `SettingsWindow.xaml` 11.7K, `ShadowWidget.xaml` 5.8K, `CommandConfirmationWindow.xaml` 3.3K) plus `AIBWindows/App.xaml`.

**Shell:**
- Bash — `AIBLinux/build.sh`, `AIBLinux/build_universal.sh`, `AIBLinux/docker_build.sh`, `AIBLinux/run.sh`.

**Embedded interpreters (skill scripts):**
- Python and PowerShell scripts are emitted to `~/.AIB/skills/` and executed via `SkillService` / `DynamicSkillTool` (`AIBWindows/Services/SkillService.cs` lines 196-202, `AIBWindows/Services/DynamicSkillTool.cs` lines 47-53). Defaults shipped in `AIBWindows/Services/DefaultSkills.cs`: `consultar_cep` (Python, ViaCEP) and `system_info` (PowerShell, WMI).

## Runtime

**AIBWindows:**
- **Target framework:** `net8.0-windows10.0.19041.0` (`AIBWindows/AIB.csproj` line 5). Requires Windows 10 build 19041 (20H1) or newer for `Windows.Media.Ocr` and other WinRT projections.
- **Output:** `Exe`, single-file self-contained, `win-x64` (`AIB.csproj` lines 4, 10-12).
- **UI frameworks enabled:** WPF (`UseWPF=true`) and WinForms (`UseWindowsForms=true`) — both are needed because the app uses `System.Windows.Forms.Screen` and `System.Windows.Forms.Cursor` for multi-monitor / cursor tracking while rendering via WPF (`AIBWindows/Services/OcrService.cs` lines 39, 92; `AIBWindows/Services/ScreenshotService.cs` line 12).

**AIBLinux:**
- **Runtime:** CPython 3.10+. Build image uses `python:3.11-bullseye` (Debian 11) for GLIBC compatibility (`build_universal.sh` line 10, with comment explaining choice).
- **GUI:** PyQt6 (Qt 6). Build pins `PyQt6<6.8` to keep Linux backward-compat with older distros (`build_universal.sh` line 26).
- **Packaging:** PyInstaller `--onefile --windowed` producing `dist/AIB`. Hidden imports `pynput.keyboard._xorg` / `pynput.mouse._xorg` are forced for X11 hotkey support (`build_universal.sh` lines 28-30).

## Package Manager

**AIBWindows:**
- **NuGet** via SDK-style csproj (`AIBWindows/AIB.csproj`). No `packages.config`. No lockfile committed (no `packages.lock.json` in tree).

**AIBLinux:**
- **pip** with `AIBLinux/requirements.txt` (6 packages, all `>=` constraints, no hash locking).
- No virtualenv tooling beyond the bash convenience (`AIBLinux/run.sh` calls `.venv/bin/python3`).

## Frameworks

**UI / Desktop:**
- **WPF (.NET 8)** — `AIBWindows`. Hosted via `System.Windows.Application` in `AIBWindows/App.xaml.cs`. Borderless transparent windows with custom acrylic/glassmorphism (`GRAVITY.MD` §1.1).
- **PyQt6 >=6.6.0** — `AIBLinux`. `QApplication` + `QSystemTrayIcon` + custom `QPainter` tray icon (`AIBLinux/main.py` lines 16-36, 56-80).

**LLM client SDKs:**
- **OpenAI .NET SDK 2.10.0** (`AIB.csproj` line 25). Used via `OpenAI.Chat.ChatClient` with a custom `Endpoint` so it can talk to Ollama's OpenAI-compatible endpoint (`AIBWindows/Services/OpenAIService.cs` lines 6-8, 718-720).
- **openai Python SDK >=1.0.0** (`AIBLinux/requirements.txt` line 3). Same OpenAI client repointed via `base_url` (`AIBLinux/app/openai_client.py` lines 19-22).

**Tokenization:**
- `Microsoft.ML.Tokenizers` 2.0.0 + `Microsoft.ML.Tokenizers.Data.O200kBase` 2.0.0 (`AIB.csproj` lines 21-22). Used to count tokens against `gpt-4o`'s tiktoken (`OpenAIService.cs` line 60: `TiktokenTokenizer.CreateForModel("gpt-4o")`) for level-based context budgeting.

**Markdown rendering:**
- **Markdig.Wpf 0.5.0.1** (`AIB.csproj` line 20). Renders assistant bubbles via `MarkdownViewer` (`AIBWindows/Views/ChatWindow.xaml.cs` lines 10, 253; `ChatWindow.xaml` line 4: `xmlns:markdig="clr-namespace:Markdig.Wpf;assembly=Markdig.Wpf"`).

**Voice / Speech-to-Text:**
- **Whisper.net 1.9.0** + **Whisper.net.Runtime 1.9.0** + **Whisper.net.Runtime.Clblast 1.5.0** (`AIB.csproj` lines 30-32). Local STT engine. Model file `ggml-base.bin` downloaded on first run to `%LocalAppData%\AIB\Models\` from HuggingFace (`AIBWindows/Services/VoiceService.cs` lines 40-43, 59-67). Forced Portuguese: `.WithLanguage("pt")` (line 55).
- **NAudio 2.3.0** (`AIB.csproj` line 23). 16 kHz / 16-bit / mono capture into a `MemoryStream` buffer (`VoiceService.cs` lines 73-92).

**Embeddings (local RAG):**
- **SmartComponents.LocalEmbeddings 0.1.0-preview10148** (`AIB.csproj` line 27). Ships the `bge-micro-v2` ONNX model in-process (~80 MB RAM). Lazy-instantiated singleton in `AIBWindows/Services/MemoryService.cs` lines 14-28. Cosine similarity computed manually (lines 65-76).

**Document parsing:**
- **PdfPig 0.1.14** (UglyToad.PdfPig) (`AIB.csproj` line 26) — PDF text extraction (`NativeTools.cs` lines 7, 207-218).
- **DocumentFormat.OpenXml 3.5.1** (`AIB.csproj` line 16) — `.docx` (`Wordprocessing`) and `.xlsx` (`Spreadsheet`) extraction (`NativeTools.cs` lines 8-9, 220-275).

**Embedded database:**
- **LiteDB 5.0.21** (`AIB.csproj` line 19). Single-file BSON document store at `~/.AIB/memory.db` (`MemoryService.cs` line 14). Collection: `memories` typed as `MemoryRecord` (id, title, content, `float[] Vector`, createdAt).

**System tray / Hotkeys:**
- **Hardcodet.NotifyIcon.Wpf 2.0.1** (`AIB.csproj` line 18) — `TaskbarIcon` with context menu (`App.xaml.cs` lines 4, 38-57).
- **NHotkey.Wpf 4.0.0** (`AIB.csproj` line 24) — Global `Ctrl+Shift+Space` registration via `HotkeyManager` (`App.xaml.cs` lines 5-6, 61-66).
- **pynput >=1.7.6** (`AIBLinux/requirements.txt` line 2) — equivalent global hotkey via `pynput.keyboard.GlobalHotKeys` (`AIBLinux/app/hotkey.py` lines 2, 16).

**Image / Drawing:**
- **System.Drawing.Common 10.0.5** (`AIB.csproj` line 28) — screen capture via `Graphics.CopyFromScreen` (`ScreenshotService.cs` lines 20-30, `OcrService.cs` lines 119-129).
- **Pillow >=10.0.0** (`AIBLinux/requirements.txt` line 5) — PIL `Image` resize / JPEG encode (`AIBLinux/app/screenshot.py` lines 5, 17-24).
- **mss >=9.0.1** (`AIBLinux/requirements.txt` line 4) — cross-platform multi-monitor capture (`AIBLinux/app/screenshot.py` lines 4, 8-13).

**Configuration / Secrets:**
- **DotNetEnv 3.1.1** (`AIB.csproj` line 17) — listed as dependency though no `Env.Load()` call was found in the C# code; settings are sourced from the encrypted `profile.dat` (DPAPI) instead. The `.env` files in the repo are legacy for the Python flow.
- **System.Security.Cryptography.ProtectedData 10.0.7** (`AIB.csproj` line 29) — Windows DPAPI used to encrypt:
  - User settings → `~/.AIB/profile.dat` (`SettingsService.cs` lines 60-88).
  - Credential vault → `~/.AIB/credentials/{system}.bin` (`CredentialService.cs` lines 41-44, 100-109).
- **python-dotenv >=1.0.0** (`AIBLinux/requirements.txt` line 6) — actively used: `load_dotenv()` at startup (`AIBLinux/main.py` lines 4, 6).

## Testing

**Not detected.** No test project, no `xunit` / `pytest` / `dotnet test` references. No `*.Tests.csproj`, no `tests/`, no `conftest.py`. The closest thing is `AIBLinux/verify_ollama.py`, a 30-line ad-hoc smoke script that pokes the Python OpenAI client against `http://localhost:11434/v1` with model `llama3.2`.

## Build / Dev Tooling

**AIBWindows:**
- `dotnet build` / `dotnet run` (`AIBWindows/README.md` line 31).
- `dotnet publish` produces `PublishSingleFile=true SelfContained=true` `win-x64` binary (`AIB.csproj` lines 10-12).
- `obj/` and `bin/` directories tracked locally (visible in `AIBWindows/` listing) — not in source control by convention (standard `.gitignore`).

**AIBLinux:**
- `bash build.sh` — quick local PyInstaller `--onefile --windowed` build inside an existing `.venv` (`AIBLinux/build.sh` lines 4, 17).
- `bash build_universal.sh` — Dockerized build on `python:3.11-bullseye` with `binutils`, `libgl1-mesa-dev`, `libx11-xcb-dev`, `libxcb-xinerama0`, `libxcb-cursor0` to produce a portable GLIBC-compatible binary; pins `PyQt6<6.8`; restores host UID/GID after build (`build_universal.sh` lines 9-32, 46-50).
- `bash docker_build.sh` — older Docker-based build on `python:3.12-slim-bullseye`, no PyQt pinning (`docker_build.sh` lines 2-11).
- `bash run.sh` — convenience: runs `.venv/bin/python3 main.py` from script directory (`run.sh` lines 4-5).

**No CI:** No `.github/workflows/`, no `azure-pipelines.yml`, no `Jenkinsfile`.

## Key Dependencies (top-of-mind)

**Critical (breaks the agent):**
- `OpenAI` 2.10.0 — ChatClient + tool-calling streaming. Pinned via `Endpoint` to Ollama (`OpenAIService.cs` lines 715-723).
- `LiteDB` 5.0.21 — persistent memory store. If schema migrates (LiteDB 6.x), `MemoryRecord` reads break.
- `SmartComponents.LocalEmbeddings` 0.1.0-**preview** — preview release of ONNX embedder; underlying `bge-micro-v2` ONNX model bundled. Treat as unstable.
- `Whisper.net` 1.9.0 + `Whisper.net.Runtime.Clblast` 1.5.0 — version skew between core and Clblast runtime; runtime is on 1.5.x while core is on 1.9.x. Mentioned in `AIBWindows/README.md` line 16 ("Runtime Clblast incluído").

**Infrastructure:**
- `Microsoft.ML.Tokenizers` 2.0.0 (+ `O200kBase`) — token counting for `gpt-4o` BPE. If model changes to Qwen/Gemma/Llama tokenizers, counts will be approximate.
- `Hardcodet.NotifyIcon.Wpf` 2.0.1 — tray icon. Drives the only persistent UI affordance when window is hidden.
- `NHotkey.Wpf` 4.0.0 — global hotkey. Failure is caught and logged but does not crash startup (`App.xaml.cs` lines 67-70).

## Configuration

**Environment (`.env` files):**

Three identical-shaped `.env` files exist (contents not shown — secrets):
- `./.env` — repo root.
- `./AIBLinux/.env` — Python project root.
- `./AIBWindows/.env` — WPF project root.

Documented keys (per `AIBWindows/README.md` lines 22-26):
- `OPENAI_API_KEY` — for cloud OpenAI, or literal `ollama` when targeting local Ollama.
- `URL` — OpenAI-compatible base URL (e.g., `http://localhost:11434/v1` for Ollama).
- `MODEL` — default model name (e.g., `qwen3:4b`).

**AIBLinux** reads these directly via `python-dotenv` in `AIBLinux/main.py` (line 6) and `AIBLinux/app/openai_client.py` lines 15-22.

**AIBWindows** does **not** read `.env` at runtime — settings are persisted DPAPI-encrypted in `~/.AIB/profile.dat` via `SettingsService` (`AIBWindows/Services/SettingsService.cs` lines 49-88). The `DotNetEnv` dependency is declared but unused; the `.env` file in `AIBWindows/` appears legacy.

**Defaults from `UserAppSettings` (`SettingsService.cs` lines 14-42):**
- `ApiUrl = "http://127.0.0.1:11434/v1"`
- `ApiKey = "ollama"`
- `ModelName = "qwen2.5:7b"`
- `AiProvider = "Ollama"` (alternative: `"Google Gemini"`)
- `ShadowModelName = "qwen2.5:7b"`
- `MaxContextTokens = 30000`
- `SearchEngine = "DuckDuckGo"` (alternatives: Google, Bing — but stubs)
- `ShadowAssistantEnabled = false` (opt-in)

**Build configuration:**
- `AIBWindows/AIB.csproj` — single SDK-style project.
- No `appsettings.json`, no `web.config`. No `tsconfig`, no `package.json`.
- `AIBLinux/requirements.txt` — flat pip requirements list.

## Platform Requirements

**Development (AIBWindows):**
- Windows 10 build 19041+ with Windows OCR language packs (per `OcrService.cs` line 25-26 / 133, OCR engine returns null if pack missing).
- .NET 8 SDK.
- Ollama running locally (default port 11434) OR a remote OpenAI-compatible endpoint reachable.
- Optional GPU for Whisper Clblast acceleration (`AIBWindows/README.md` line 16).
- Microphone + access to record audio for `AIB Live`.

**Development (AIBLinux):**
- Linux/macOS with X11 (for `pynput` X11 backend).
- Python 3.10+ with pip.
- Qt6 runtime libraries (`libgl1-mesa-dev`, `libxcb-*` — see `build_universal.sh` lines 14-18).
- Ollama running locally.

**Production:**
- **AIBWindows:** Self-contained single-file `win-x64` exe. No .NET install needed on target. Per `AIBWindows/README.md` line 14, Windows 10/11.
- **AIBLinux:** PyInstaller-built `dist/AIB` single binary. The "universal" build (bullseye, GLIBC ≤ 2.31) targets any modern Linux distro.

**Runtime data layout (Windows):**
- `~/.AIB/` — primary data root (`DirectoryService.cs` line 8).
  - `profile.dat` — encrypted settings.
  - `memory.db` — LiteDB RAG store.
  - `skills/` — user skills.
  - `.default_skills/` — bundled skills bootstrapped on first run.
  - `credentials/*.bin` — DPAPI vault.
  - `chat_history.json` — past sessions (kept top-50 by timestamp; `ChatHistoryService.cs` lines 20, 44-47).
  - `reminders.json` — pending reminders (`ReminderService.cs` line 36).
- `%LocalAppData%\AIB\Models\ggml-base.bin` — Whisper model (`VoiceService.cs` lines 40-43).
- `%Temp%\AIB\` — screenshot cache, OCR cache, cmd output (`DirectoryService.cs` lines 9, 19-22).

---

*Stack analysis: 2026-05-28*
