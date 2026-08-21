# 04. Ferramentas e Capacidades (Skills do AIB)

> **Fonte de verdade:** o conjunto efetivo de ferramentas vive em
> `ToolRegistry.RegisterNativeTools()`. Esta documentação reflete esse registro. Se divergir, o
> código vence.
>
> **Aviso desta versão:** este documento descrevia um arsenal (memória, cofre, OCR, clipboard, web,
> lembretes, skills dinâmicas) declarado em `NativeTools.cs`. **Esse arquivo foi deletado.** O texto
> abaixo descreve o que existe hoje, incluindo a postura de segurança real — que é bem mais frágil
> do que a versão anterior deste documento dava a entender.

## 1. O registro efetivo

`RegisterNativeTools()` registra exatamente **três** ferramentas, todas em `Services/Tools/`, todas
com **`RequiredLevel = 1`**:

| Ferramenta | Classe | RequiredLevel | O que faz |
|---|---|---|---|
| `run_command` | `RunCommandTool` | **1** | Executa PowerShell arbitrário na sessão do usuário |
| `read_file` | `ReadFileTool` | **1** | Lê um arquivo de texto por caminho absoluto |
| `write_file` | `WriteFileTool` | **1** | Cria ou sobrescreve um arquivo por caminho absoluto |

Não existe mais nenhuma outra. `manage_memory`, `manage_vault`, `read_screen`, `manage_clipboard`,
`search_web`, `set_reminder`, `materialize_skill` e `execute_skill` **não estão registradas** e não
são apresentadas ao modelo.

## 2. Postura de segurança — leia esta seção inteira

O mecanismo de `RequiredLevel` do `ToolRegistry` funciona (doc 03 §6.1), mas as três ferramentas
registradas pedem **nível 1**. Como todo usuário começa no nível 1, **não há gating efetivo nenhum**:
um usuário recém-instalado tem, desde a primeira mensagem, execução de PowerShell, leitura de
arquivo e escrita de arquivo disponíveis para o modelo.

### 2.1 O modal de confirmação está MORTO

`CommandConfirmationWindow.ShowAsync` existe, está implementado (com semáforo de reentrância, hop de
Dispatcher, banner de denylist e preview de script) e tem **zero chamadores**. Uma busca por
`ShowAsync` no projeto inteiro só encontra a própria definição.

Consequências, ditas sem eufemismo:

- **Nenhum comando passa por confirmação do usuário.** Não existe funil, não existe sandboxing
  visual, não existe pipeline bloqueada. O `run_command` executa direto.
- **Não existe o retorno `"Ação Rejeitada pelo Usuário."`**, porque não existe rejeição.
- **A setting `ConfirmDangerousCommands` não controla coisa alguma.** O comentário XML dela ainda
  descreve um denylist pós-modal que não existe mais: `CommandFloorList`, `CommandService` e
  `AlwaysAllowSession` foram deletados.
- `CommandConfirmationContext` sobreviveu como DTO sem produtor.

### 2.2 As sandboxes por nível não existem

A versão anterior deste documento descrevia, para o `run_command`, bloqueio de diretórios de sistema
abaixo do nível 9, confinamento em `Documents`/`Downloads` até o nível 4, bloqueio de comandos de
escrita abaixo do nível 8 e de rede abaixo do nível 7, com matching por palavra. **Nada disso está no
código.** `RunCommandTool.ExecuteAsync` valida apenas que o parâmetro `command` existe e não é vazio;
o parâmetro `userLevel` é recebido e **ignorado**.

Do mesmo modo, **`ReadFileTool` não é confinada a raiz alguma**: ela lê qualquer caminho para o qual
`File.Exists` devolva `true`. E `WriteFileTool` escreve em qualquer caminho, criando os diretórios
que faltarem.

Em resumo: hoje o único portão real entre o modelo e o sistema de arquivos/shell do usuário é o
próprio modelo. Reconstruir o portão de confirmação é pré-requisito para qualquer aumento do arsenal.

## 3. As três ferramentas, em detalhe

### 3.1 `run_command` (`RunCommandTool`)
- **Schema:** `{ command: string }`, obrigatório.
- **Execução:** `powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "<comando>"`, com
  `CreateNoWindow = true`, `UseShellExecute = false`, saída e erro redirecionados, e
  `WorkingDirectory = Environment.CurrentDirectory`.
- **Timeout:** 30 segundos (`Task.WhenAny` contra `Task.Delay(30000)`); no estouro o processo é
  morto e a tool devolve o erro de timeout.
- **Saída:** `stdout + "\n" + stderr`, truncada em 8.000 caracteres. Vazio vira
  `"Comando executado com sucesso (sem saída)."`.
- **Escape frágil:** o comando é interpolado no argumento com um simples
  `command.Replace("\"", "\\\"")`. Isso não é um escape robusto de linha de comando do Windows;
  comandos com aspas aninhadas ou caracteres de citação incomuns podem ser reinterpretados.
- A descrição enviada ao modelo diz "PowerShell" e o processo é `powershell.exe` — o Windows
  PowerShell 5.1, não `pwsh`.

### 3.2 `read_file` (`ReadFileTool`)
- **Schema:** `{ path: string }`, obrigatório, descrito como caminho completo e absoluto.
- **Leitura:** `StreamReader.ReadToEndAsync()` — **texto puro apenas**. Truncada em 12.000
  caracteres, com aviso no fim.
- **Não há parsing de PDF, `.docx` ou `.xlsx`.** Os pacotes `PdfPig` e `DocumentFormat.OpenXml`
  continuam no `AIB.csproj`, mas nenhum código vivo os usa. Apontar a ferramenta para um binário
  devolve o conteúdo do arquivo interpretado como texto.
- Erros são amigáveis para o modelo: `ERRO: Arquivo não encontrado em '<path>'.` etc.

### 3.3 `write_file` (`WriteFileTool`)
- **Schema:** `{ path: string, content: string }`, ambos obrigatórios.
- Cria os diretórios ausentes (`Directory.CreateDirectory`) e grava com `File.WriteAllTextAsync`.
- **Sobrescreve sem aviso e sem backup.** Não há confirmação, não há confinamento de caminho.
- Devolve `SUCESSO: Arquivo salvo corretamente em '<path>'.`

## 4. Pontas soltas que o refactor expôs

Não são defeitos introduzidos agora — são inconsistências que ficaram visíveis quando o arsenal
encolheu. Estão aqui porque um leitor precisa saber que existem:

- **O system prompt promete uma ferramenta que não existe.** O `SYSTEM_PROMPT` da
  `ConversationService` manda o modelo chamar `manage_memory(action=recall)` antes de dizer "não
  sei". O registry responde `ERRO: Ferramenta 'manage_memory' não encontrada`.
- **O `AgentLoop` ainda trata `materialize_skill` como caso especial** (chama
  `_toolRegistry.Refresh()` depois dela). A ferramenta não existe, e `Refresh()` é um no-op.
- **O rastreio de arquivos recentes nunca dispara.** `AgentLoop.TrackRecentFile` reage aos nomes
  `read_file`, `view_file`, `write_to_file`, `replace_file_content` e `multi_replace_file_content`, e
  procura as chaves `AbsolutePath` / `TargetFile` / `path`. Só `read_file` existe e só a chave `path`
  bate; e o destino, `ContextService.AddRecentFile`, é um stub de corpo vazio.
- **A listagem de skills dinâmicas nunca aparece.** `SkillService.ListLocalSkills()` devolve lista
  vazia, então o bloco "Habilidades dinâmicas disponíveis" jamais é anexado ao system prompt. Não
  existe `execute_skill` nem `materialize_skill` para chamá-las de qualquer forma.
- **A sidebar de skills da `ChatWindow`** consome `Registry.GetCategorizedTools()`, que devolve as
  três nativas e uma lista de dinâmicas sempre vazia.

## 5. Componentes de hardware (voz e OCR) — hoje são stubs

Estas capacidades não passam pelo payload de ferramentas; elas dependiam da UI chamando serviços
diretamente. Os serviços foram esvaziados:

- **`VoiceService`** (15 linhas): `InitializeAsync()` devolve `Task.CompletedTask`,
  `StartListening()` e `StopListening()` têm corpo vazio, `Dispose()` não faz nada, e o evento
  `OnTranscriptionUpdated` **nunca é disparado**. O botão de microfone da `ChatWindow` continua
  ligado a esses métodos e ao ciclo de UI, mas nenhuma transcrição chega. Não há `NAudio`, não há
  `Whisper.net`, não há wake-word — apesar de os três pacotes seguirem no `AIB.csproj`.
- **`OcrService`** (6 linhas): `ExtractTextFromActiveScreenAsync()` devolve string vazia. O botão de
  lupa do `InputBox` instancia o serviço e recebe nada. Não há Tesseract nem pasta `tessdata`.
- **`ScreenshotService`** foi deletado.

## 6. Se for reconstruir o arsenal

A ordem que o código sugere, sem inventar requisito novo:

1. **Primeiro o portão.** Dar chamadores a `CommandConfirmationWindow.ShowAsync` e reconstruir o
   denylist por nível que `ConfirmDangerousCommands` promete controlar. Enquanto isso não existir,
   subir o `RequiredLevel` de `run_command` e `write_file` é a única barreira disponível — e o
   mecanismo do `ToolRegistry` já está pronto para aplicá-la, nos dois pontos (doc 03 §6.1).
2. **Depois as ferramentas.** Toda ferramenta nova implementa `ITool` (`Name`, `Description`,
   `ChatToolDefinition`, `RequiredLevel`, `ExecuteAsync`) e entra na lista de
   `RegisterNativeTools()`. O schema declarado em `ChatToolDefinition` não é decorativo: o healer de
   tool call por texto (doc 03 §3.3) lê `properties` e `required` dele para ligar argumentos de
   chamadas escritas como prosa.
3. **Por último o system prompt.** Ele deve prometer apenas o que está registrado.
