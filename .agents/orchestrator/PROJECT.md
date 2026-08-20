# Project: AIB (Autonomous Intelligence for Business)

## Architecture
AIB is a desktop AI assistant for Windows written in .NET 8 (WPF). It consists of:
- **UI/UX Layer**: ChatWindow and sidebar widgets with custom Glassmorphism/Acrylic styling.
- **AI Core**: OpenAIService running a ReAct loop with OpenAI API or local Ollama.
- **RAG Local Memory**: MemoryService storing semantic embeddings in LiteDB (`memory.db`).
- **Dynamic Skills**: SkillService running interpreter-dispatched plugins (Python, PowerShell).

## Milestones
| # | Name | Scope | Dependencies | Status |
|---|---|---|---|---|
| M1 | Exploration & Cleanup | Validate `dotnet build`, check for legacy Telegram/Bitrix references, check existing code. | None | DONE |
| M2 | Test Suite Setup (Tiers 1-4) | Design and write automated test scripts/cases for RAG memory and dynamic tool provisioning. | M1 | DONE |
| M3 | Vector RAG Adaptation | Refactor/verify `MemoryService` and RAG persistence across conversation sessions. | M2 | DONE |
| M4 | Dynamic Tool Provisioning | Refactor/verify agent's ability to create and execute dynamic tools on-the-fly. | M3 | DONE |
| M5 | E2E Integration & Verification | Run the full test suite (Tiers 1-4) and confirm acceptance criteria. | M4 | DONE |
| M6 | Security & Integrity Audit | Forensic Auditor execution and packages reputation verification. | M5 | DONE |

## Interface Contracts
- **Memory API**: `MemoryService.RememberAsync(key, info)` and `MemoryService.Recall(query)`.
- **Skill Materializer**: `SkillService.MaterializeSkillAsync(name, content, interpreter)`.
- **Skill Runner**: `SkillService.RunSkillAsync(name, arguments)`.

## Code Layout
- `AIBWindows/`
  - `AIB.csproj` — Project definition
  - `App.xaml` & `App.xaml.cs` — Main entrypoint (contains CLI test hook `--test-all`)
  - `Services/`
    - `MemoryService.cs` — RAG Memory
    - `SkillService.cs` — Habilidades dinâmicas
    - `OpenAIService.cs` — OpenAI API integration
    - `NativeTools.cs` — Built-in agent tools
    - `TestRunner.cs` — CLI test execution suite
  - `Views/`
    - `ChatWindow.xaml` — Symmetric Chat bubbles, transparent layout
