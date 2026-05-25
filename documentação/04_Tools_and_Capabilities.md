# 04. Ferramentas e Capacidades (Skills do AIB)

Este módulo documenta o arsenal de Skills implementadas nativamente no AIB. Todas residem fundamentalmente em `NativeTools.cs` (obedecendo e implementando a interface técnica de `ITool`) ou em serviços paralelos que lidam em baixo-nível com o Hardware e o Sistema Operacional Windows.

## 1. Ferramentas Locais (`NativeTools.cs`)

O arquivo concentra a declaração técnica unificada de ferramentas para o Payload JSON da OpenAI.

### 1.1 Sistema de Memória e Vault de Credenciais
- **`remember` / `recall`**: Lidam de forma silenciosa com o banco de dados Vetorial Local (detalhado em `05_Database_and_RAG`).
- **`manage_vault` / `retrieve_vault`**: O Agente salva ou extrai informações severamente críticas (senhas de rede, chaves de API alheias, MFA). Estas ferramentas não chamam o banco local LiteDB. Elas batem de frente no Serviço `CredentialService.cs` que invoca nativamente o **Data Protection API (DPAPI)** do Windows (via P/Invoke Win32 Criptográfico). Isso garante Criptografia At-Rest imbatível: apenas a seção autenticada atual do hardware daquele usuário logado tem o condutor matemático para descriptografar os segredos, isolando os dados de qualquer hacker remoto em posse do `.db` em HD externo.

### 1.2 Leitura e Manipulação Dinâmica de I/O
- **`read_file` (ReadFileTool)**: Habilidade nativa para devorar arquivos pesados esquivando-se da complexidade do Console e sintaxes de aspas (`"`) de diretórios:
  - `.txt`, `.json`, `.cs`: Parsing brutalmente direto (`File.ReadAllText`).
  - `.pdf`: Integração in-memory usando `UglyToad.PdfPig` que rastreia os tokens do Acrobat PDF página por página.
  - `.docx` e `.xlsx`: Parseia planilhas em Tabelas e documentos de word iterando parágrafos pelo Standard `DocumentFormat.OpenXml` — extraindo texto puro estritamente e dispensando qualquer requisição do Pacote Office fisicamente instalado.
- **`windows_console_execution` (RunCommandTool)**: Encaminha um parâmetro `"command"` para instâncias invisíveis de PowerShell em background. Dispara o Hard-Lock visual na Interface, ativando uma `CommandConfirmationWindow` (Janela Modal) para interceptação de segurança humana manual antes da emissão.

### 1.3 Visão Computacional e Monitoramento do SO
- **`capture_screen`**: Levanta o frame de matriz gráfica integral do monitor secundário e primário via `ScreenshotService`. Esta imagem crua vira Base64 e ativa a capacidade multimodal do modelo (`gpt-4o`), onde a IA entende os pixels da tela no momento do prompt sem upload persistente em disco.
- **`ocr_screen`**: Abordagem levíssima que extrai a matriz da tela, manda para o pacote OCR embutido (Tesseract) no hardware do usuário e repassa um amontoado massivo de strings legíveis, ignorando custos pesados de processamento de imagem remota.
- **`active_window` / `clipboard_read` / `clipboard_write`**: Um triplete de funções sistêmicas de Win32. Invoca bibliotecas dinâmicas `user32.dll` (ex: `GetForegroundWindow`) permitindo que a IA sinta exatamente a caixa de transferência de CTRL+V do usuário, manipule-a, ou deduza qual contexto está sob foco de teclado em primeiro plano (um Code Editor, um navegador Web, etc).

### 1.4 Auto-Programação (Dynamic Materialization)
- **`materialize_skill`**: Tool avançada engatilhada apenas nas extremidades de progressão de nível. A IA emite via payload JSON o Código C# Fonte de uma nova `ITool`. O módulo interno injeta esse código solto num Pipeline de compilação Paralela em Tempo-de-Execução (Runtime Compilation `.NET`), resultando instantaneamente numa `.dll` gerada em ram. O `ToolRegistry` adiciona essa ferramenta à sua lista sob demanda. O AIB consegue ensinar a si mesmo novos poderes sem que um programador escreva e builde a source do C#.

## 2. Componentes UI de Hardware (Media e Voz)

Certas ações ocorrem sem a invocação via Payload JSON "Role: Tool". Elas dependem ativamente do Click e da UI Front-End do sistema.
- **`VoiceService.cs`**: Integrado à biblioteca `NAudio` (Padrão de indústria) para manipulação de placas de áudio `WaveInEvent` em canais curtos PCM. Após captar o som, o serviço efetua a invocação para o end-point *Whisper-1* em cloud da OpenAI. O Whisper converte para string nativa perfeitamente polida e entrega de bandeja no InputBox, dispensando que o ChatWindow interaja com os drivers pesados de placa de som nativa da Microsoft.
- **`OcrService.cs` (Pelo Botão de UI)**: O ícone de lupa lateral do InputBox não chama a IA, ele puxa passivamente o frame estático e, utilizando o diretório embuste `\tessdata` (Modelos pré-treinados *por/eng*), cospe magicamente o conteúdo estático no bloco em branco. Em 500ms, sem internet, a tela se transforma num Markdown editável.
