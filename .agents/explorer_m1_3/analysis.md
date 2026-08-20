# Analysis Report - Explorer 3

## 1. Project Build Status
* **Compilation Command:** `dotnet build`
* **Working Directory:** `c:\Users\Carlo\CPAPS\AIB\AIBWindows`
* **Result:** Build succeeded cleanly with **0 warnings** and **0 errors**.
* **Build Output Snippet:**
  ```text
  Determinando os projetos a serem restaurados...
  Todos os projetos estão atualizados para restauração.
  AIB -> C:\Users\Carlo\CPAPS\AIB\AIBWindows\bin\Debug\net8.0-windows10.0.19041.0\win-x64\AIB.dll
  Compilação com êxito.
      0 Aviso(s)
      0 Erro(s)
  Tempo Decorrido 00:00:02.96
  ```

---

## 2. Legacy Integration Scan (Telegram & Bitrix)
A comprehensive scan was conducted across the codebase directory (`c:\Users\Carlo\CPAPS\AIB`) to search for occurrences of "Telegram" and "Bitrix" (case-insensitive). 

### Source Code & Configuration
* **No references** to "Telegram" or "Bitrix" were found in any C# source files (`.cs`), XAML UI files (`.xaml`), project configurations (`.csproj`), or application config files (`.json`, `.config`).
* All legacy integration code (such as `TelegramService.cs` or `Telegram.Bot` reference in `.csproj`) has been completely removed.

### Documentation & Agent Log References
The only occurrences of "Telegram" and "Bitrix" are located in markdown documentation files and agent-internal files:

| File Path | Line No. | Verbatim Content / Context |
| :--- | :--- | :--- |
| `AIBWindows\funcionalities.md` | 46 | `... (ex: buscar um lead no Bitrix, pesquisar o site da empresa no Google, e salvar um resumo na memória).` |
| `AIBWindows\funcionalities.md` | 51 | `- **Cofre de Credenciais (DPAPI):** Armazenamento criptografado de chaves (Bitrix, Telegram, OpenAI) em ...` |
| `AIBWindows\funcionalities.md` | 52 | `- **Busca Global Resiliente:** Se o agente solicitar uma chave de um sistema (ex: `bitrix`) mas ela estiver em outro (ex: `telegram`)...` |
| `AIBWindows\funcionalities.md` | 73 | `### 3.1 Bot do Telegram (Modo Híbrido)` |
| `AIBWindows\funcionalities.md` | 74 | `- **Descrição:** O AIB deve responder tanto pela janela local quanto via Telegram.` |
| `AIBWindows\funcionalities.md` | 76 | `- **Streaming Remoto:** Envio de mensagens para o Telegram com o mesmo motor de IA da interface local.` |
| `AIBWindows\funcionalities.md` | 82 | `### 3.3 Bitrix24 Agentic Skill` |
| `AIBWindows\funcionalities.md` | 83 | `- **Call Command:** Comando genérico no script `bitrix.ps1` que permite à IA chamar qualquer endpoint da REST API do Bitrix...` |
| `GRAVITY.MD` | 8 | `A aplicação foi rigorosamente limpa e focada. A integração legada com Telegram foi removida do projeto (assembly `Telegram.Bot` desreferenciado em `AIB.csproj`, `TelegramService.cs` deletado).` |
| `.agents/` logs & request files | Multiple | Mentions of the task requirements/briefings to scan for Telegram/Bitrix. |

---

## 3. RAG Memory & Session Adaptation Analysis
The long-term semantic memory of the application is managed cooperatively by `MemoryService.cs` and `OpenAIService.cs`.

### Technical Architecture of RAG Memory
1. **Embedded Database:** Memory is persisted locally using **LiteDB**, stored in a file named `memory.db` within the application data directory (`DirectoryService.DataDir`).
2. **Local Embeddings:** Embedding vectors are generated using Microsoft's `SmartComponents.LocalEmbeddings.LocalEmbedder`. This computes embeddings on the client machine using a local ONNX model, eliminating any external network dependencies or API costs for vector generation.
3. **Core Functions (`MemoryService.cs`):**
   * **`RememberAsync(key, content)`**: Formats input as `"{key}\n{content}"`, computes its vector embedding via `LocalEmbedder`, and inserts a new `MemoryRecord` (containing title, content, vector, and creation timestamp) into the LiteDB `"memories"` collection.
   * **`Recall(query)`**: Generates a vector for the query, computes `CosineSimilarity` against all records in LiteDB, filters out matches below a `0.2` (20%) similarity threshold, takes the top 3 most similar matches, and formats them into a markdown text block.
   * **`ListMemories()`** and **`DeleteMemory(id)`**: Allow listing and managing memories.
4. **Agent Integration (`OpenAIService.cs`):**
   * The agent exposes the `manage_memory` tool to the LLM (implemented by `ManageMemoryTool` in `NativeTools.cs`).
   * The `SYSTEM_PROMPT` enforces: `"- Antes de dizer "não sei", chame manage_memory(action=recall)."`
   * When the agent invokes `manage_memory`, it triggers either `RememberAsync` or `Recall` depending on the action requested.

### Session-to-Session Adaptation Demonstration
Because LiteDB writes to a persistent `memory.db` file on disk, memory is preserved across application restarts and history resets. A demonstration flow of session-to-session adaptation follows:
* **Session 1 (Store Preference):**
  1. The user states: *"Lembre-se de que meu editor favorito é o VS Code."*
  2. The LLM processes this and calls the tool: `manage_memory(action="remember", key="editor_favorito_do_usuario", info="VS Code")`.
  3. `MemoryService.RememberAsync` calculates the embedding for `"editor_favorito_do_usuario\nVS Code"`, and saves it to `memory.db`.
  4. The LLM confirms to the user: *"Entendido! Salvei em minha memória que seu editor favorito é o VS Code."*
  5. The user closes the application, ending Session 1.
* **Session 2 (Retrieve & Use Preference):**
  1. The user launches the application (starting a fresh session with cleared conversation history) and asks: *"Quero criar um script Python. Qual IDE ou editor eu deveria usar para abri-lo?"*
  2. The LLM's system prompt directs it to query memory before responding.
  3. The LLM calls the tool: `manage_memory(action="recall", key="IDE editor python")`.
  4. `MemoryService.Recall` embeds the query, searches `memory.db` using Cosine Similarity, locates the record matching "VS Code" (with high similarity), and returns it to the LLM.
  5. The LLM adapts its response using the recalled memory: *"Como você prefere o VS Code, recomendo abrir o script nele. Quer que eu escreva o comando para iniciá-lo?"*

---

## 4. Dynamic Tool Provisioning Analysis
Dynamic tool provisioning allows the agent to construct and execute custom tools (scripts) at runtime. This mechanism is defined by `SkillService.cs` and coordinated by two tools in `NativeTools.cs`: `MaterializeSkillTool` and `ExecuteSkillTool`.

### Architecture of Dynamic Skills
1. **Directory Structure:** Skills are located under the local app data folder:
   * Built-in skills (loaded initially): `DirectoryService.DataDir\.default_skills\<skill_name>\`
   * Dynamically created user skills: `DirectoryService.DataDir\skills\<skill_name>\`
2. **Components of a Skill:**
   * A folder matching the skill's name (e.g. `skills\my_custom_tool\`).
   * A script file (e.g. `my_custom_tool.py` or `my_custom_tool.ps1`).
   * A metadata configuration: either a `skill.json` file or a markdown file named `SKILL.md` with YAML frontmatter containing `name`, `description`, `interpreter`, and `script_file` keys.
3. **Supported Interpreters:**
   * `python`: Dispatched via `py.exe`
   * `powershell`: Dispatched via `powershell.exe -NoProfile -ExecutionPolicy Bypass -File`
   * `cmd`: Dispatched via `cmd.exe /c`
   * `markdown`: Indicates a text/instruction skill that isn't executed as a script.

### Step-by-Step Runtime Provisioning Flow
* **Step 1: Materialization (Creating the Tool)**
  * The LLM decides it needs a new dynamic skill and calls the native tool `materialize_skill(skill_name, script_content, interpreter)`.
  * `MaterializeSkillTool` intercepts this request. In Windows Desktop mode, it enforces security bounds (requires `RequiredLevel >= 8`) and initiates a human-in-the-loop validation:
    * It prompts the user with a confirmation window (`CommandConfirmationWindow.ShowAsync`) displaying a complete preview of the script.
    * Once the user approves, it writes the script to `skills\<skill_name>\<skill_name>.<ext>` and generates/updates the metadata (`skill.json` or `SKILL.md`).
    * It calls `ToolRegistry.Refresh()` to register the new tool dynamically.
* **Step 2: Injection & Discovery**
  * When starting or resetting history, `OpenAIService.ResetHistory()` scans local folders via `SkillService.ListLocalSkills()`.
  * It appends descriptions of all active dynamic skills directly to the LLM system prompt context, instructing the LLM: *"Habilidades dinâmicas disponíveis (use a ferramenta 'execute_skill' para chamá-las passando 'skill_name'): [skill details]"*.
* **Step 3: Execution**
  * The LLM executes the custom skill by calling the native tool `execute_skill(skill_name, arguments)`.
  * `ExecuteSkillTool` intercepts the request:
    * It performs safety checks (`RequiredLevel >= 6`).
    * For security, it reads the script file and computes a SHA-256 hash. If it is already marked as approved (`AlwaysAllowSession`), it proceeds. Otherwise, it pops up the confirmation modal showcasing the script body.
    * Once authorized, it executes the script via `SkillService.RunSkillAsync`.
    * `RunSkillAsync` matches the interpreter (e.g., PowerShell) and securely spins up the process via `CommandService.ExecuteWithArgListAsync` (using `ProcessStartInfo.ArgumentList` to avoid shell command injection vulnerabilities).

---

## 5. Dependencies Verification
The dependencies specified in `AIB.csproj` have been evaluated. All of them are standard, reputable, and safe package libraries from trusted sources:

* **Office OpenXML Development:**
  * `DocumentFormat.OpenXml` (3.5.1) - The official Microsoft library for creating and parsing Office Open XML formats (Word, Excel, PowerPoint).
* **UI & UX Extensions:**
  * `Hardcodet.NotifyIcon.Wpf` (2.0.1) - The standard library for integrating system tray icons and notifications in WPF.
  * `Markdig.Wpf` (0.5.0.1) - A standard WPF renderer wrapper for the Markdig Markdown engine.
  * `NHotkey.Wpf` (4.0.0) - A popular utility for defining and handling global keyboard hotkeys in WPF.
* **Database & Security:**
  * `LiteDB` (5.0.21) - A lightweight, embedded NoSQL database for .NET (ideal for storing local semantic memory).
  * `System.Security.Cryptography.ProtectedData` (10.0.7) - Microsoft's official Windows DPAPI wrapper used to encrypt credentials on disk securely.
* **AI & Machine Learning:**
  * `OpenAI` (2.10.0) - The official .NET client SDK provided by OpenAI.
  * `Microsoft.ML.Tokenizers` & `Microsoft.ML.Tokenizers.Data.O200kBase` (2.0.0) - Microsoft's standard high-performance tokenization library.
  * `SmartComponents.LocalEmbeddings` (0.1.0-preview10148) - Steve Sanderson's (Microsoft team) library for executing ONNX embedding models locally.
  * `Whisper.net` & `Whisper.net.Runtime` (1.9.0) / `Whisper.net.Runtime.Clblast` (1.5.0) - A widely-trusted, wrapper package for local Whisper speech-to-text execution with OpenCL GPU acceleration support.
* **System Capabilities:**
  * `NAudio` (2.3.0) - The industry-standard .NET library for audio management, recording, and processing.
  * `PdfPig` (0.1.14) - A reputable open-source parser for extracting layout and text from PDF documents.
  * `System.Drawing.Common` (10.0.5) - Microsoft's GDI+ graphics wrapper library.
