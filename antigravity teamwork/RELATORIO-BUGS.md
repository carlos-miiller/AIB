# Relatório de Bugs — AIB (branch `feature/redesign-first-run`)

Auditoria multi-agente: 7 lentes independentes de análise + passe adversarial de refutação por lente.
77 achados sobreviveram à verificação; após deduplicação → **~40 defeitos distintos**.

Estado da build: `dotnet build` = 0 erros / 0 warnings. `dotnet test` = 24/24 passando.
**Todos os bugs abaixo são defeitos de runtime/lógica — nenhum é pego pelo compilador nem pelos testes atuais.**

---

## Resumo

O problema mais grave não é um bug isolado: é que **toda a camada de segurança do refactor foi removida e nada a substituiu**.
`CommandConfirmationWindow.ShowAsync` existe, está completa, e tem **zero chamadores em toda a árvore**.
`AuditLogService` virou um stub vazio. A `CommandFloorList` foi deletada. `run_command`, `read_file` e `write_file`
ficaram todos com `RequiredLevel => 1` — ou seja, um usuário recém-instalado dá ao LLM PowerShell arbitrário,
leitura de qualquer arquivo do disco e escrita em qualquer caminho, **sem uma única confirmação**.
O wizard de onboarding, na mesma tela, promete ao usuário exatamente o contrário.

O segundo eixo é o caminho de tool-call: no provider OpenAI **nenhuma tool é anexada à requisição**, então
100% das chamadas de ferramenta caem no fallback por regex — que casa texto dentro de blocos `<think>`.
O raciocínio privado do modelo vira PowerShell executado de verdade.

O terceiro eixo é concorrência: o warmup em background muta a mesma `List<ChatMessage>` que a UI, e apaga mensagens reais.

Ordem prática: corrigir o portão de confirmação primeiro, depois o caminho de tools, depois a corrida no `_history`.

---

## CRÍTICO

### 1. Ferramentas destrutivas executam sem nenhuma confirmação — o modal é código morto

- **Local:** `AIBWindows/Services/Tools/RunCommandTool.cs:57`, `WriteFileTool.cs:59`, `ReadFileTool.cs:50`
- **Prova:** `grep -rn "CommandConfirmationWindow" AIBWindows --include=*.cs` retorna apenas a própria
  definição da janela. `ShowAsync` (linha 63) não é chamada de lugar nenhum.
- **Gatilho:** qualquer usuário, nível 1, primeira mensagem. O modelo emite
  `run_command {"command":"Remove-Item -Recurse -Force $HOME"}` e isso executa direto.
- **Impacto:** execução arbitrária de código orientada pelo LLM. Como o conteúdo do chat inclui texto de
  páginas/arquivos lidos, isso é alcançável por prompt-injection: um arquivo lido com instruções embutidas
  consegue disparar comandos. Não há confirmação, não há denylist, e o `AuditLogService` é stub — não fica registro.
- **Correção:**
  1. Reintroduzir o gate em `ToolRegistry.ExecuteToolAsync`, antes de `tool.ExecuteAsync`:

     ```csharp
     if (tool.RequiresConfirmation)
     {
         var ctx = new CommandConfirmationContext(tool.Name, argumentsJson);
         var (allowed, always) = await CommandConfirmationWindow.ShowAsync(ctx);
         if (!allowed) return "NEGADO: o usuário recusou a execução desta ferramenta.";
     }
     ```
  2. Adicionar `bool RequiresConfirmation { get; }` a `ITool` — `true` para `run_command` e `write_file`.
  3. Elevar `RequiredLevel` de `run_command` de volta para 7 (era 7 antes do refactor) e `write_file` para 2,
     ou então corrigir o texto do wizard, que hoje mente para o usuário.
  4. Reimplementar `AuditLogService` de verdade (append síncrono, antes da execução, não depois).

### 2. `read_file` / `write_file` sem confinamento de raiz

- **Local:** `ReadFileTool.cs:50`, `WriteFileTool.cs:59`
- **Gatilho:** modelo pede `read_file {"path":"C:/Users/Carlo/.ssh/id_rsa"}`.
- **Impacto:** o conteúdo entra no histórico de chat e é **enviado para o provider remoto**. Exfiltração de
  chaves SSH, credenciais AWS, tokens de browser. No `write_file`, escrita na pasta Startup = persistência
  de código executado no boot.
- **Correção:**

  ```csharp
  string full = Path.GetFullPath(path);
  string root = Path.GetFullPath(DirectoryService.DataDir); // ou workspace escolhido pelo usuário
  if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
      return "ERRO: acesso fora do diretório permitido.";
  ```

  Mais: denylist rígida (`.ssh`, `.aws`, `.gnupg`, `Login Data`, o próprio vault de credenciais) recusada
  mesmo após aprovação; e recusar reparse points (`FileAttributes.ReparsePoint`) para não seguir symlink.

---

## ALTO

### 3. No provider OpenAI, nenhuma tool é anexada à requisição

- **Local:** `AIBWindows/Services/OpenAIService.cs:246`
- **Impacto:** para todo usuário OpenAI, o caminho nativo de function-calling está morto. 100% da execução de
  ferramentas passa pelo fallback de regex — sem validação de schema, sem respeitar `EnableIntelligentTools`,
  sem o filtro por `userLevel` que `GetActiveTools` foi escrito para fazer, e truncando qualquer argumento que contenha `)`.
- **Correção:** dentro do `while` (onde `chatOptions` é construído):

  ```csharp
  if (settingsAi.EnableIntelligentTools)
      foreach (var t in tools) chatOptions.Tools.Add(t);
  ```

### 4. Fallback por regex casa texto dentro de `<think>` — raciocínio do modelo vira comando executado

- **Local:** `AIBWindows/Services/OpenAIService.cs:340` (e a construção do JSON em `:662`)
- **Impacto:** comando que o modelo cogitou e **descartou** é executado de verdade. Combinado com o item 1,
  vira PowerShell ao vivo. O `continue` também engole o bloco de `ToolCallUpdates` daquele chunk, descartando
  tool-calls nativas legítimas, e a resposta visível do turno nunca é entregue ao usuário.
- **Correção:** rodar `TextActionRegex` apenas sobre o texto já classificado como canal final (um
  `StringBuilder finalChannelText` alimentado no ramo `mode == 0`), nunca sobre `fullResponse` cru.
  Ancorar em início de linha `(?m)^\s*(?:Action|Ação):` e pular o fallback quando `toolCallsByIndex.Count > 0`.

### 5. Fallback por regex monta JSON com interpolação de string crua

- **Local:** `OpenAIService.cs:662`
- **Impacto:** argumento com aspas ou `\` gera JSON malformado gravado no `_history`. A partir daí **toda**
  requisição seguinte falha ao serializar — a sessão fica permanentemente quebrada.
- **Correção:** montar com `JsonSerializer.Serialize(new { command = valor })` em vez de interpolar.
- **Bônus:** o nome do parâmetro sintetizado (`"arguments"`) não bate com o schema de nenhuma tool registrada,
  então o fallback nunca poderia ter sucesso mesmo quando o JSON sai válido.

### 6. Warmup em background muta `_history` junto com a UI e apaga mensagens do usuário

- **Local:** `AIBWindows/Services/OpenAIService.cs:110-152`
- **Gatilho:** cold start com Ollama. O ctor dispara `Task.Run(WarmupAndKeepAliveAsync)` (linha 66) e a
  InputBox só é desabilitada na linha 101, **depois** do POST de load do modelo. O usuário digita e envia nesse meio.
- **Impacto:** duplo. (a) `_history.RemoveRange(realHistoryCount, ...)` no `finally` apaga tudo que a
  requisição em voo adicionou — a resposta do assistente e todas as `ToolChatMessage`. Sobra um par
  `tool_calls`/`tool-result` órfão, que o provider rejeita na próxima mensagem. (b) `List<T>` não é
  thread-safe: `Add` simultâneo corrompe o array interno ou lança `InvalidOperationException` durante o
  `foreach` de `FormatMessages`.
- **Correção:** o warmup nunca deve tocar a `_history` viva. Usar uma `List<ChatMessage>` descartável
  semeada de um snapshot. Se o compartilhamento for mesmo necessário, um `lock` único cobrindo **todo**
  acesso (incluindo `CalculateCurrentTokens`, `FormatMessages` e `ChatHistoryService.SaveCurrentSession`).

### 7. `StreamReader.EndOfStream` bloqueia a thread de UI a cada token

- **Local:** `AIBWindows/Services/OllamaNativeClient.cs:74`
- **Prova:** `while (!reader.EndOfStream && !ct.IsCancellationRequested)`. `EndOfStream` é propriedade
  **síncrona** que faz `Stream.Read()` no socket. `ChatWindow.xaml.cs:553` consome o `await foreach` sem
  `ConfigureAwait(false)` e termina o corpo com `await Task.Yield()`, o que devolve a continuação ao
  Dispatcher — o iterador interno retoma na thread de UI e bloqueia lá.
- **Impacto:** janela travando entre tokens ("Não respondendo" em pausas do modelo). Pior: o botão Stop é
  inútil — `EndOfStream` é avaliado **antes** de `ct.IsCancellationRequested` e ignora o token. Se o Ollama
  estagnar, a app congela sem saída.
- **Correção:**

  ```csharp
  while (!ct.IsCancellationRequested)
  {
      var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
      if (line == null) break;                       // EOF
      if (string.IsNullOrWhiteSpace(line)) continue;
      ...
  }
  ```

  E bombear o loop de rede em uma `Task` própria publicando chunks por `Channel<string>`, para que nenhuma
  parte da leitura possa retomar no Dispatcher.

### 8. `JsonDocument.Parse` sem guarda em `FormatMessages` envenena a sessão

- **Local:** `AIBWindows/Services/OllamaNativeClient.cs:201`
- **Gatilho:** uma tool-call com `arguments` vazio ou não-objeto entra no histórico.
- **Impacto:** a partir daí `FormatMessages` lança `JsonException` **antes** de cada requisição. Toda
  mensagem seguinte falha. Não há caminho de recuperação na UI a não ser reset de histórico.
- **Correção:** `try { using var doc = JsonDocument.Parse(args); ... } catch (JsonException) { /* trata como {} */ }`.

### 9. `EnsureClient` faz cache do `ChatClient` só pelo nome do modelo

- **Local:** `AIBWindows/Services/OpenAIService.cs:761`
- **Impacto:** o usuário cola a chave correta em Settings e o app continua devolvendo 401 até reiniciar,
  porque o client em cache ainda carrega `"placeholder"` e o endpoint antigo. Mesmo problema ao trocar `ApiUrl`.
- **Correção:** incluir chave, URL e provider na condição de rebuild
  (`_lastKey`, `_lastUrl`, `_lastProvider`), resolvendo-os **antes** do guard.

### 10. Wizard de primeira execução tem beco sem saída para quem não tem Ollama

- **Local:** `AIBWindows/Views/FirstRunWindow.xaml:314`
- **Prova:** o `Hyperlink` "Pular e usar configuração padrão" não tem `NavigateUri`, então `RequestNavigate`
  nunca dispara.
- **Impacto:** `ValidateStep3SaveButton` (`FirstRunWindow.xaml.cs:151`) exige
  `ModelComboBox.SelectedItem != null || _fallbackModel != null` → falso. Ollama é o radio marcado por padrão,
  o combo está vazio e colapsado, e "Retry" só re-falha. As únicas saídas são uma chave `sk-` real ou "Sair",
  que chama `Current.Shutdown()`. **O onboarding é impossível de concluir** para todo usuário sem Ollama e sem chave OpenAI.
- **Correção:** trocar o `Hyperlink` por `<Button Click="FallbackLink_Click">` chamando `ApplyFallbackModel()`.
  (Adicionar um `NavigateUri` dummy também funciona, mas o Button não depende da semântica de navegação.)

---

## MÉDIO

### 11. Deadlock clássico de pipe em `run_command`
`RunCommandTool.cs:64` — `ReadToEnd()` no stdout e **depois** no stderr. Qualquer comando que encha o buffer
do stderr (~4KB) trava. O usuário vê "demorou mais de 30 segundos" — diagnóstico falso.
**Correção:** `var so = process.StandardOutput.ReadToEndAsync(); var se = process.StandardError.ReadToEndAsync(); await Task.WhenAll(so, se);`

### 12. Timeout não mata a árvore de processos
`RunCommandTool.cs:73` — `process.Kill()` sem `entireProcessTree: true`. Netos sobrevivem sem supervisão e a
task leitora vaza segurando handles. O `using` também descarta o `Process` enquanto a task de fundo ainda o lê.
**Correção:** `process.Kill(entireProcessTree: true)` e aguardar a task leitora antes de sair do escopo.

### 13. Quoting de linha de comando escapa aspas mas não barras invertidas
`RunCommandTool.cs:49` — `command.Replace("\"", "\\\"")`. Além de `\` não ser o caractere de escape do
PowerShell (é a crase), qualquer comando terminado em separador de caminho Windows é corrompido antes de rodar.
**Correção:** passar o comando por `-EncodedCommand` (Base64 UTF-16LE), eliminando o problema de quoting inteiro.

### 14. Índice de tool-call reinicia a cada linha NDJSON
`OllamaNativeClient.cs:94` — o `Index` volta a 0 em cada linha do stream, então tool-calls que chegam em
chunks separados são mescladas numa só, corrompida.

### 15. Trim de histórico só roda no ramo de texto puro
`OpenAIService.cs:688` — uma conversa pesada em tools cresce sem limite e estoura a janela de contexto.
Relacionado: `TrimHistoryAsync` (`:936`) assume que o índice 0 é o system prompt, mas `ResetHistory` não
adiciona nenhum quando `SendSystemPrompt` está off — a primeira mensagem do usuário vira imortal.

### 16. Estourar o teto de 18 iterações do ReAct é reportado como sucesso
`OpenAIService.cs:229` — o stream termina em silêncio e o usuário acha que a resposta acabou.

### 17. `ContextService` e `AuditLogService` são stubs inertes
`ContextService.cs:7` — as propriedades retornam uma `List` nova a cada acesso. Arquivos de contexto,
lembretes e itens do shadow são descartados silenciosamente; a `ContextSidebar` (`:22`) faz binding nessas
propriedades, então os painéis nunca mostram nada. `AuditLogService.cs:4` é no-op — nenhum evento de segurança é registrado.

### 18. Onboarding é pulado quando o chat abre pela bandeja
`App.xaml.cs:86` — a checagem de primeira execução existe só no caminho do atalho global. Abrir pelo ícone
ou pelo menu da bandeja entra direto no chat sem configuração.

### 19. Salvar qualquer configuração destrói a conversa em andamento
`SettingsWindow.xaml.cs:217` — inclusive no meio de um streaming, sem sincronização.
No mesmo arquivo: `:211` persiste um snapshot tirado na abertura do diálogo (desfaz XP ganho enquanto estava
aberto) e `:195` reverte `AiProvider`/`ApiUrl` para valores obsoletos depois de "Alterar Chave", órfãnando a chave recém-salva.

### 20. Stop descarta a resposta inteira e mostra exceção crua
`ChatWindow.xaml.cs:575` — `catch (Exception)` engole o cancelamento; tudo que já foi transmitido é jogado fora.

### 21. Janela de primeira execução com altura fixa em 80% da tela + `ResizeMode=NoResize`
`FirstRunWindow.xaml.cs:49` — corta os cards de agente em qualquer monitor com menos de ~1020px.

### 22. Nível de autonomia prometido no wizard não existe
`FirstRunWindow.xaml:348` — a tela promete limites por nível; as três tools são `RequiredLevel 1`.

### 23. `HttpClient` novo a cada iteração do ReAct
`OpenAIService.cs:241` — até 18 por mensagem, nunca descartados. Esgotamento de sockets em sessão longa.

### 24. Timeout do `HttpClient` de warmup é o padrão de 100s
`OpenAIService.cs:82` — para uma operação que a própria UI diz levar até 2 minutos.

---

## BAIXO

- `ChatWindow.xaml.cs:136` — fim do warmup limpa a InputBox, apagando o que o usuário digitou.
- `ChatWindow.xaml.cs:755` — autocomplete oferece `/clear`, `/help`, `/vault`; nenhum tem handler, vão como texto literal ao LLM.
- `ChatWindow.xaml.cs:480` — `/unlock_level` **atribui** `MessageCount` em vez de elevá-lo, apagando o XP acumulado; e não atualiza o badge de nível.
- `ChatWindow.xaml.cs:1009` — `MessageBox.Show` desativa a ChatWindow, cujo handler `Deactivated` a esconde; o chat some.
- `FirstRunWindow.xaml.cs:391` — duplo clique em "Concluir" durante a escrita no vault seta `DialogResult` numa janela já fechada → `InvalidOperationException` não tratada.
- `FirstRunWindow.xaml.cs:361` — erro de escrita no vault é escrito na `ErrorLabel`, que fica no grid colapsado do Step 3; falha invisível.
- `FirstRunWindow.xaml.cs:267` — chave OpenAI só é validada em `LostFocus`/Enter, e clicar no botão Next desabilitado não move o foco: chave válida digitada nunca é aceita.
- `FirstRunWindow.xaml:328` — chave da API digitada em `TextBox` sem máscara: cleartext na tela, exposta a UI Automation e captura de tela.
- `SettingsService.cs:96` — escrita não-atômica; qualquer falha de leitura reseta o perfil para o padrão de fábrica e o próximo save sobrescreve o arquivo danificado.
- `ChatHistoryService.cs:30` — `LoadHistory` engole erros de parse e devolve lista vazia; o próximo save apaga as 50 conversas armazenadas.
- `ChatHistoryService.cs:20` — histórico fica em `%APPDATA%\AIB` enquanto o resto migrou para `%USERPROFILE%\.AIB`; o "Factory Reset" (`:117`) apaga só uma das duas cópias em texto plano.
- `OpenAIService.cs:49` — o SYSTEM_PROMPT anuncia `manage_memory` e outras tools deletadas no refactor: erros de tool-not-found garantidos e iterações ReAct desperdiçadas.
- `OllamaNativeClient.cs:58` — requisições de chat omitem `keep_alive`, desfazendo o `keep_alive=-1` do warmup.
- `CommandConfirmationWindow.xaml.cs:63` — `ShowAsync` é código morto (raiz do item 1).

---

## Padrões sistêmicos

1. **O refactor removeu os guard-rails e não os substituiu.** `CommandService`, `CommandFloorList`,
   `AlwaysAllowSession`, `NativeTools` foram deletados; `Tools/*` + `ToolRegistry` os substituíram *sem*
   confirmação, sem denylist e sem auditoria. O modal e o `CommandConfirmationContext` ficaram órfãos.
2. **Serviços viraram stubs mas os chamadores continuam tratando-os como reais.** `AuditLogService` e
   `ContextService` retornam vazio/no-op; a UI faz binding neles e o código chama métodos de auditoria que não gravam nada.
3. **Nenhuma sincronização em estado compartilhado.** `_history` é `List<T>` cru tocado por UI, warmup e
   persistência sem um único `lock`.
4. **Escritas em disco não-atômicas com `catch` silencioso.** Settings e histórico ambos truncam-e-escrevem,
   e ambos tratam falha de leitura como "começar do zero" — o que apaga os dados do usuário.
5. **A UI promete capacidades que o código não tem.** Wizard, SYSTEM_PROMPT e autocomplete de slash-commands
   descrevem features removidas ou nunca implementadas.

---

## Ordem de correção sugerida

1. Religar o gate de confirmação em `ToolRegistry` + confinamento de raiz em `read_file`/`write_file` + `AuditLogService` real. *(itens 1, 2)*
2. Corrigir o wizard sem saída (`NavigateUri`) — hoje bloqueia toda instalação nova sem Ollama. *(item 10)*
3. Anexar tools no caminho OpenAI e desligar/limitar o fallback por regex. *(itens 3, 4, 5)*
4. Eliminar a corrida do warmup no `_history`. *(item 6)*
5. Consertar o loop de streaming do Ollama (`EndOfStream` → `ReadLineAsync`) e o Stop. *(itens 7, 20)*
6. Guardar o `JsonDocument.Parse` e o cache do `EnsureClient`. *(itens 8, 9)*
7. `RunCommandTool`: deadlock de pipe, kill de árvore, `-EncodedCommand`. *(itens 11, 12, 13)*
8. Varredura de persistência: escrita atômica, `catch` que não apaga dados, unificar diretórios. *(baixos)*
9. Alinhar o texto do wizard/SYSTEM_PROMPT com o que o código realmente faz. *(itens 17, 22, baixos)*
