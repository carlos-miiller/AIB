# 04. Ferramentas e Capacidades (Skills do AIB)

Este módulo documenta o arsenal de Skills implementadas nativamente no AIB. Todas residem fundamentalmente em `NativeTools.cs` (obedecendo e implementando a interface técnica de `ITool`) ou em serviços paralelos que lidam em baixo-nível com o Hardware e o Sistema Operacional Windows.

> **Fonte de verdade:** o conjunto efetivo de ferramentas registradas vive em `ToolRegistry.RegisterNativeTools()`. Esta documentação reflete esse registro — se divergir, o código vence.

## 1. Ferramentas Locais (`NativeTools.cs`)

O arquivo concentra a declaração técnica unificada de ferramentas para o Payload JSON da OpenAI.

### 1.1 Sistema de Memória e Vault de Credenciais
- **`manage_memory`** (RequiredLevel: 1): Lida com o banco vetorial local. `action="remember"` salva, `action="recall"` busca por similaridade semântica via embeddings BGE (detalhado em `05_Database_and_RAG`).
- **`manage_vault`** (RequiredLevel: 7): Salva (`action="store"`) ou extrai (`action="retrieve"`) credenciais críticas (senhas, chaves de API, MFA). Não usa o LiteDB — chama `CredentialService.cs`, que invoca o **Data Protection API (DPAPI)** do Windows via `System.Security.Cryptography.ProtectedData`. Criptografia at-rest amarrada à sessão do usuário logado: apenas o hardware autenticado consegue descriptografar o `.bin`.

### 1.2 Leitura e Manipulação Dinâmica de I/O
- **`read_file`** (RequiredLevel: 1, `ReadFileTool`): Habilidade nativa para devorar arquivos pesados esquivando-se do Console e sintaxes de aspas:
  - `.txt`, `.json`, `.cs`: Parsing direto (`File.ReadAllText`).
  - `.pdf`: Integração in-memory usando `UglyToad.PdfPig`.
  - `.docx` e `.xlsx`: Parseia parágrafos/planilhas via `DocumentFormat.OpenXml` — sem requerer Office instalado.
- **`run_command`** (RequiredLevel: 2, `RunCommandTool`): Encaminha o parâmetro `command` para `cmd.exe /c` em background invisível (timeout 20s). Para cmdlets PowerShell, prefixe com `powershell -NoProfile -Command "..."`. A tool aplica uma sandbox por nível:
  - Bloqueia diretórios de sistema (`appdata`, `windows`, `program files`, `programdata`) abaixo de Nível 9.
  - Nível ≤4: restrito a `Documents`/`Documentos` (e `Downloads` a partir de Nível 3).
  - Nível <8: bloqueia comandos de escrita/exclusão (`rm`, `del`, `remove-item`, `set-content`, `>`, `>>`, etc.).
  - Nível <7: bloqueia comandos de rede (`curl`, `wget`, `invoke-webrequest`, `ping`, etc.).
  - Matching é feito por palavra (regex `\b`), não substring, para evitar falsos positivos.

### 1.3 Visão Computacional e Monitoramento do SO
- **`read_screen`** (RequiredLevel: 3, `ReadScreenTool`): Combina título da janela ativa (via `user32!GetForegroundWindow` + `GetWindowText`) com OCR completo de todas as telas (`OcrService` + Tesseract local em `\tessdata`). Retorna texto pronto sem upload de imagem para a nuvem.
- **`manage_clipboard`** (RequiredLevel: 1 para read; 6 para write): Lê (`action="read"`) ou grava (`action="write"`) na área de transferência via `System.Windows.Clipboard` no Dispatcher.

### 1.4 Web e Lembretes
- **`search_web`** (RequiredLevel: 3, `SearchWebTool`): Pesquisa em tempo real via DuckDuckGo (`WebSearchService.SearchAsync`).
- **`set_reminder`** (RequiredLevel: 1, `SetReminderTool`): Agenda um lembrete por `delay_minutes` ou `target_time` (HH:mm). Backend em `ReminderService`.

### 1.5 Auto-Programação (Dynamic Skills e Lazy Loading)
- **`materialize_skill`** (RequiredLevel: 5): A IA emite via payload JSON o `script_content` (Python ou PowerShell) e o `skill_name`. O módulo salva em `~/.AIB/skills/<nome>/<nome>.{py|ps1}` via `SkillService.MaterializeSkillAsync`. O AIB ensina a si mesmo novos poderes (scrappers, automações).
- **`execute_skill`** (RequiredLevel: 1): Para evitar inflar a gramática do LLM com dezenas de tools, scripts customizados **não** são expostos individualmente no payload da API. Os nomes e descrições são injetados como texto no System Prompt (em `OpenAIService.ResetHistory`). Quando a IA precisa rodar uma delas, usa `execute_skill` passando `skill_name` (+ `arguments` opcionais) — a tool resolve o interpretador e dispara via `SkillService.RunSkillAsync`.
- **Skills Padrão (Seed)**: Na primeira inicialização, `SkillService.EnsureDir()` popula `.default_skills/` com seeds embutidas em `DefaultSkills.cs` (ex: `consultar_cep.py`, `system_info.ps1`).

## 2. Componentes UI de Hardware (Media e Voz)

Certas ações ocorrem sem invocação via Payload JSON "Role: Tool". Elas dependem da UI front-end:
- **`VoiceService.cs`**: Captura áudio via **`NAudio`** (`WaveInEvent` 16 kHz, 16-bit, mono PCM) e transcreve **localmente** com **Whisper.net** (modelo `ggml-base.bin` baixado uma vez em `%LOCALAPPDATA%\AIB\Models`). Wake-word configurada como `"AIB"`, com silence-cutoff em 1200 ms. Sem chamadas para a nuvem da OpenAI no caminho de voz.
- **`OcrService.cs` (botão de UI)**: O ícone de lupa lateral do InputBox não chama a IA — extrai o frame estático e usa **Tesseract** local (modelos `por`/`eng` em `\tessdata`) para devolver Markdown editável em ~500 ms sem internet.
