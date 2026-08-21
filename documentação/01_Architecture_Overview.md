# 01. Visão Geral Arquitetural

> **Regra desta pasta:** documentamos o que o código FAZ, não o que ele deveria fazer.
> Onde a realidade é feia, ela está escrita como é.

## 1. Padrão de Projeto e Composition Root

O **AIB** não usa container de Injeção de Dependência. A composição é manual, por construtor, e
acontece num único lugar: **`App.OnStartup` (`App.xaml.cs`)**.

Antes o Composition Root era a `ChatWindow`: o code-behind da janela dava `new` em
`SettingsService` e `OpenAIService`, que por sua vez dava `new` em `ToolRegistry`, enquanto o `App`
mantinha um *segundo* `SettingsService` só para si. Isso foi desfeito, por três motivos concretos:

1. **O ciclo de vida da aplicação pertence ao processo da bandeja, não a uma janela.**
   O `App.xaml` declara `ShutdownMode="OnExplicitShutdown"`: o AIB vive no System Tray e só morre
   por `Current.Shutdown()`. A `ChatWindow` se esconde sozinha no evento `.Deactivated` e é
   mostrada/ocultada pelo atalho global. Um objeto que passa a maior parte do tempo escondido não
   pode ser dono dos serviços que precisam continuar vivos.
2. **Ordem de inicialização.** O `SettingsService` só pode nascer *depois* de
   `DirectoryService.EnsureDirectories()`, e o aquecimento do modelo só pode disparar *depois* de a
   UI existir. Com a janela como raiz, essas duas ordens eram impossíveis de garantir.
3. **Instância única.** O `SettingsService` mantém cache em memória das settings (doc 03 §5). Cache
   com duas instâncias é cache errado. Hoje existe exatamente um por processo, criado no `App` e
   injetado em tudo — nenhum outro arquivo do projeto dá `new SettingsService()`.

### 1.1 A árvore construída no `App`

```
App  (Composition Root — dono do tempo de vida, vive na bandeja)
 ├─ HttpClient                 (único, Timeout = InfiniteTimeSpan; descartado no OnExit)
 ├─ SettingsService            (instância única, cache em memória, invalidado no save)
 ├─ ToolRegistry               (instância única)
 ├─ TokenCounter               (instância única — o tokenizer é caro de criar)
 ├─ RegexToolCallHealer        (IToolCallHealer)
 ├─ ChatProviderFactory        (IChatProviderFactory — só reconstrói o provider quando
 │                              provider/modelo/URL/credencial mudam)
 ├─ AgentLoop                  (orquestração ReAct; não conhece HTTP)
 ├─ ConversationService        (DONA ÚNICA do histórico; todo acesso sincronizado)
 │   └─ WarmupService          (heartbeat / keep_alive = -1, sobre lista descartável)
 └─ ChatWindow                 (recebe ConversationService + SettingsService prontos)
```

O aquecimento é a última linha do `OnStartup` (`_ = _conversation.StartWarmupAsync();`) —
explícito, depois da UI, **nunca** disparado de dentro de um construtor.

## 2. O Fluxo Principal de Processamento (o Loop ReAct)

A cascata deixou de ser `ChatWindow → OpenAIService → ToolRegistry`. O `OpenAIService` foi
**deletado**: as responsabilidades que se acumulavam no seu método único de ~490 linhas foram
repartidas em quatro camadas com fronteiras explícitas.

```
ChatWindow            → só interface. Consome IAsyncEnumerable<string> e desenha balões.
 ConversationService  → dona do histórico. Traduz AgentEvent em chunk/callback/evento.
  AgentLoop           → o ReAct. Só decide o que fazer com StreamChunks já classificados.
   IChatProvider      → HTTP, payload, parsing de stream, classificação de canal, healer.
   ToolRegistry       → execução das ferramentas nativas (ITool), com trava por nível.
```

Um turno, do começo ao fim:

1. **Entrada do usuário**: a `ChatWindow` chama
   `_conversation.StreamResponseAsync(texto, callbackTecnico)`.
2. **Portão de turno**: a `ConversationService` pega o `SemaphoreSlim _turnGate` — dois turnos
   nunca intercalam escritas no histórico —, cria o `CancellationTokenSource` do turno e anexa a
   mensagem do usuário sob o lock `_gate`. O contador de tokens da UI é atualizado na hora.
3. **Iteração ReAct** (`AgentLoop.RunAsync`, até `MaxIterations = 18`):
   - lê as settings uma vez por iteração (servidas do cache) e pede o provider à fábrica;
   - tira um **snapshot imutável** do histórico e entrega ao provider — o provider nunca vê a lista
     viva e nunca a muta;
   - consome os `StreamChunk`: `TextDelta{Final}` vira texto para o usuário,
     `TextDelta{Reasoning}` vira log técnico, `ToolCallDelta` é acumulado por `CallKey`, `Usage`
     vira contador, `Done` encerra o stream.
4. **Se houve tool call**: a mensagem assistant com todas as `tool_calls` é anexada, as ferramentas
   são executadas **em paralelo** (`Task.WhenAll`) pelo `ToolRegistry`, os resultados são anexados
   na ordem original, o histórico é podado, e a iteração recomeça.
5. **Se houve texto final**: o texto é anexado ao histórico, o histórico é podado, e o turno termina
   com `TurnOutcome.Answered`.
6. **Se o teto de 18 estourou** com o modelo ainda pedindo ferramentas: o turno termina com
   `TurnOutcome.IterationLimitReached`, e a `ConversationService` transforma isso num chunk
   **visível** para o usuário (`⚠️ Limite de 18 etapas atingido…`). Estouro de teto nunca vira
   sucesso silencioso.

A poda de contexto (`Trim`) roda ao fim de **toda** iteração — inclusive nas que só executaram
ferramentas. Antes ela só era alcançável no ramo de texto puro, e uma sequência longa de tool calls
estourava o orçamento sem nunca podar.

## 3. Contrato de Threading

Regra dura, e ela explica várias decisões do código:

- **Podem tocar o Dispatcher:** `ChatWindow`, `SettingsWindow`, `FirstRunWindow`, `ContextSidebar`,
  `ShadowWidget`. Só isso.
- **Não podem, e não assumem contexto de sincronização:** `ConversationService`, `AgentLoop`,
  `WarmupService`, todo `IChatProvider`, `ChannelSplitter`, `RegexToolCallHealer`,
  `ChatProviderFactory`, `OllamaNativeClient`, `ToolRegistry`, todo `ITool`, `SettingsService`,
  `TokenCounter`. Todos usam `ConfigureAwait(false)`.
- **Travessia da fronteira:** o `await foreach` da `ChatWindow` é escrito *sem* `ConfigureAwait` de
  propósito — o contexto da WPF é capturado ali e cada chunk volta para a thread de UI. Já os
  callbacks e eventos (`onTechnicalContent`, `OnTokenCountChanged`, `OnWarmupStateChanged`) nascem
  numa thread do pool, e por isso a `ConversationService` captura o `SynchronizationContext` no
  construtor e devolve os três por `Post`.
- **IO bloqueante é proibido abaixo da `ConversationService`.** O `OllamaNativeClient` usava
  `StreamReader.EndOfStream` — uma propriedade síncrona que faz leitura bloqueante de socket e
  ignora o `CancellationToken`. Rodando sob o contexto capturado, isso congelava a janela enquanto o
  modelo pensava. Hoje o laço é `while (!ct.IsCancellationRequested)` com
  `await reader.ReadLineAsync(ct)`, e linha nula é fim de stream.
- **Estado mutável compartilhado** existe em exatamente três lugares, cada um com seu lock:
  `ConversationService._history` (`_gate`), `SettingsService._cache` (`_gate` próprio) e o provider
  em cache da `ChatProviderFactory` (`_gate` próprio). O aquecimento roda numa thread do pool sobre
  um `EphemeralMessageStore` e **nunca** escreve no histórico vivo.

## 4. Categorização de Serviços (`/Services`)

### 4.1 Camada cognitiva — implementada e viva
- **`ConversationService`**: dona do histórico, do system prompt, da poda e dos eventos da UI.
- **`Agent/AgentLoop`**, **`Agent/AgentEvent`**, **`Agent/IMessageStore`**,
  **`Agent/EphemeralMessageStore`**, **`Agent/WarmupService`**: a orquestração ReAct.
- **`Ai/IChatProvider`** e as implementações **`Ai/OllamaProvider`** / **`Ai/OpenAiProvider`**, mais
  **`Ai/ChannelSplitter`**, **`Ai/ChatTemplateSanitizer`**, **`Ai/RegexToolCallHealer`**,
  **`Ai/ChatProviderFactory`** e o transporte **`OllamaNativeClient`**.
- **`TokenCounter`**, **`SettingsService`**, **`LevelService`**, **`ToolRegistry`**, **`ITool`**.
- **`CredentialService`**: cofre DPAPI real (`ProtectedData`), usado pelo onboarding e pela fábrica.
- **`ChatHistoryService`**: sessões antigas em JSON **em texto claro** (doc 05 §3).
- **`AuditLogService`**, **`GibberishVoiceService`**, **`AgentProfile`**, **`TestRunner`**.

### 4.2 Camada de ações
- **`ToolRegistry` + `Services/Tools/`**: hoje são **três** ferramentas nativas — `run_command`,
  `read_file`, `write_file`. O `NativeTools.cs` que declarava o arsenal antigo (memória, cofre, OCR,
  clipboard, web, lembretes, skills) foi **deletado**. O doc 04 lista o que sobrou e o que isso
  significa em termos de segurança.

### 4.3 Camada física/OS — hoje são *stubs*
Esta seção descrevia `ScreenshotService`, `VoiceService` e `OcrService` como funcionalidades nativas
do Windows. A realidade atual:

- **`ScreenshotService` não existe mais** — o arquivo foi deletado.
- **`VoiceService`** tem 15 linhas: `InitializeAsync()` devolve `Task.CompletedTask`,
  `StartListening()`/`StopListening()` são corpos vazios e o evento `OnTranscriptionUpdated` nunca é
  disparado. O botão de microfone da `ChatWindow` chama esses métodos e não transcreve nada.
- **`OcrService`** tem 6 linhas: `ExtractTextFromActiveScreenAsync()` devolve string vazia.
- **`MemoryService`**, **`SkillService`**, **`ReminderService`**, **`ContextService`** e
  **`ShadowAssistantService`** também são stubs (6 a 15 linhas, retornos vazios ou corpos vazios).

Os pacotes `NAudio`, `Whisper.net`, `PdfPig`, `DocumentFormat.OpenXml`, `LiteDB` e
`SmartComponents.LocalEmbeddings` continuam referenciados no `AIB.csproj`, mas **nenhum código vivo
os chama**. São dívida de referência, não capacidade instalada.
