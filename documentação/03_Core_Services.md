# 03. Core Services (Cognição e Controle)

A pasta `Services/` isola as lógicas computacionalmente densas. O "cérebro" do AIB não é mais um
serviço só: o antigo `OpenAIService` foi **deletado** e repartido em camadas com fronteiras
explícitas. Este documento descreve cada uma delas e diz onde foi parar cada comportamento
intencional que o serviço antigo carregava.

## 0. Por que o `OpenAIService` deixou de existir

O `OpenAIService.StreamResponseAsync` ocupava sozinho ~490 linhas e fazia: seleção de provider,
montagem de `chatOptions`, o laço ReAct de 18 iterações, o parsing do stream, a máquina de estados
de canal, a acumulação de tool calls, o fallback por regex, a execução das ferramentas, o append no
histórico, a poda e a contagem de tokens. Os defeitos não eram independentes — eram sintomas disso:

- as definições de ferramenta nunca eram anexadas no caminho OpenAI (`chatOptions.Tools` ficava
  vazio), então function calling nativo jamais acontecia por lá;
- o fallback por regex varria também o conteúdo de `<think>`, fazendo o raciocínio privado do modelo
  virar execução real;
- esse mesmo fallback montava JSON por interpolação de string, envenenando o histórico de forma
  permanente;
- a poda de histórico só era alcançável no ramo de texto puro;
- o teto de 18 iterações encerrava o stream em silêncio e reportava sucesso.

A divisão atual é: **`ConversationService`** (estado), **`AgentLoop`** (decisão), **`IChatProvider`**
(transporte e interpretação), **`ToolRegistry`** (ação).

---

## 1. ConversationService (`ConversationService.cs`)

Dona **única** do histórico da conversa. Nenhum outro objeto do processo segura a lista viva: a
propriedade pública `History` deixou de existir, e tudo que sai daqui é cópia.

### 1.1 Sincronização
- `private readonly List<ChatMessage> _history` protegida por `private readonly object _gate`.
  Toda leitura e toda escrita acontecem sob esse lock, sem exceção — inclusive `ResetHistory`,
  `CountTokens` e `Trim`.
- `Snapshot()` devolve `_history.ToArray()` sob o lock. Providers e persistência só veem cópias.
- Nenhum `await` dentro do lock. A contagem de tokens e a poda são síncronas; o `TrimHistoryAsync`
  assíncrono de antes existia só por causa de um sumarizador já removido.
- `SemaphoreSlim _turnGate` serializa turnos: `StreamResponseAsync` e `AskStatelessAsync` o tomam,
  e ele é liberado no `finally` — inclusive em cancelamento.
- O `CancellationTokenSource` do turno vive sob um segundo lock (`_ctsGate`), porque o Stop vem da
  thread de UI enquanto o turno corre numa thread do pool. `CancelGeneration()` só cancela o CTS;
  nunca toca no histórico.

### 1.2 Memória de Curto Prazo e Auto-Poda (`Trim`)
- A contagem de tokens é feita pelo `TokenCounter` (tokenizer `gpt-4o` / o200k_base, via
  `Microsoft.ML.Tokenizers`). Ele conta o texto de cada parte da mensagem e, em mensagens assistant
  com `tool_calls`, serializa as chamadas e conta o JSON também.
- `Trim(userLevel)` é uma **janela deslizante**: enquanto o histórico estiver acima do orçamento do
  nível (`LevelService.GetMaxTokensForLevel`), remove a mensagem removível mais antiga.
- **Proteção sistêmica, com a nuance que importa:** o índice `0` é intocável **quando ele é de fato
  um `SystemChatMessage`**. O código calcula
  `firstRemovable = (_history[0] is SystemChatMessage) ? 1 : 0`. Isso é deliberado: com a setting
  `SendSystemPrompt` desligada não existe system prompt nenhum, e travar o índice 0 às cegas
  tornaria a primeira mensagem do usuário imortal.
- **Coerência de pares:** se a mensagem removida era um assistant com `tool_calls`, as
  `ToolChatMessage` imediatamente seguintes são removidas junto. A API rejeita `tool_calls` órfãs.
- Dois freios: a poda nunca esvazia abaixo de `firstRemovable + 2` mensagens, e um `safetyCounter`
  aborta o laço em 200 iterações.
- **Quem chama:** o `AgentLoop`, ao fim de **toda** iteração — a de tool call, a de texto final e a
  de stream vazio. Essa é a correção do "poda só alcançável no ramo de texto puro".

### 1.3 System Prompt
`ResetHistory()` salva a sessão corrente em disco (`ChatHistoryService.SaveCurrentSession`, fora do
lock) e reconstrói o prompt. O prompt contextual é a constante `SYSTEM_PROMPT` (versão comprimida,
~120 tokens) mais o diretório home do usuário e, quando houver, a lista de habilidades dinâmicas.

> **Realidade:** `SkillService.ListLocalSkills()` é hoje um stub que devolve lista vazia, então o
> bloco de habilidades dinâmicas nunca é anexado. Pior: o `SYSTEM_PROMPT` continua instruindo o
> modelo a chamar `manage_memory(action=recall)` antes de dizer "não sei" — e `manage_memory` **não
> está registrada** (doc 04). Quando o modelo obedece, recebe de volta
> `ERRO: Ferramenta 'manage_memory' não encontrada no registry`.

### 1.4 Tradução dos eventos para a UI
`StreamResponseAsync` consome `AgentEvent` e traduz:

| AgentEvent | destino |
|---|---|
| `Text` | `yield return` — vira chunk no `await foreach` da `ChatWindow` |
| `Technical` | callback `onTechnicalContent` (console + linha "🔧 Usando ferramenta") |
| `TokenUsage` | evento `OnTokenCountChanged` |
| `Completed(IterationLimitReached)` | log técnico **e** um chunk visível de aviso ao usuário |
| `Completed(EmptyResponse)` | nada extra — a `ChatWindow` já renderiza *Ação executada com sucesso.* |

Os três eventos/callbacks passam por `Post(...)` sobre o `SynchronizationContext` capturado no
construtor. Sem contexto (testes, modo CLI) executam direto.

Cancelamento e exceções de provider **propagam** para fora, como antes, para que o `catch` existente
da `ChatWindow` continue produzindo o mesmo balão de erro.

---

## 2. AgentLoop (`Agent/AgentLoop.cs`) — o Loop ReAct

Orquestração pura. Não conhece HTTP, não faz parsing de stream, não remove token de template e não
roda regex. **Não tem campo mutável**: tudo de um turno vive em locais do iterador, então dois turnos
simultâneos não se enxergam.

Por iteração (`1..MaxIterations`, `MaxIterations = 18`):

1. Lê as settings **uma vez** (servidas do cache do `SettingsService`) e pede o provider à fábrica.
2. Aplica o opt-out `EnableIntelligentTools`: desligado, a lista de tools vira vazia.
3. Tira `store.Snapshot()` — cópia imutável — e entrega ao provider.
4. Consome os `StreamChunk`:
   - `TextDelta{Final}` → acumula em `finalText` e emite `AgentEvent.Text`;
   - `TextDelta{Reasoning}` → emite `AgentEvent.Technical`;
   - `ToolCallDelta` → acumula por `CallKey` em **ordem de primeira aparição** (`Id` e `Name` só são
     escritos enquanto vazios; os fragmentos de argumento são concatenados);
   - `Usage` → recalcula `baseline + streamed` e emite `AgentEvent.TokenUsage`;
   - `Done` → guarda o texto cru e encerra o stream.
5. **Com tool calls:** anexa a mensagem assistant com todas as chamadas, emite as linhas técnicas
   (`[PARALELO] …` quando há mais de uma, e um `[FERRAMENTA] Nome: … | Args: …` por chamada),
   executa tudo com `Task.WhenAll`, e anexa os resultados **na ordem original** mesmo que tenham
   terminado fora de ordem. Depois: `Trim`, `NotifyTokenCount`, e volta ao passo 1.
6. **Com texto final:** anexa ao histórico, `Trim`, `NotifyTokenCount`, encerra com
   `TurnOutcome.Answered`.
7. **Sem texto e sem ferramenta:** `Trim`, `NotifyTokenCount`, encerra com
   `TurnOutcome.EmptyResponse`.
8. **Teto estourado:** emite `TurnOutcome.IterationLimitReached`. A `ConversationService` mostra isso
   ao usuário. Nunca é sucesso silencioso.

> As strings técnicas são contrato: a `ChatWindow` casa
> `\[(?:FALLBACK REGEX )?FERRAMENTA\] Nome: ([a-zA-Z_]+)` para desenhar a linha
> "🔧 Usando ferramenta". Mudar o texto quebra a UI sem quebrar a compilação.

**O que vai para o histórico como resposta final:** o `Done` carrega `RawAssistantText` — o texto cru
do stream, com os blocos `<think>` intactos. É ele que é anexado, para que o modelo releia o próprio
raciocínio nas iterações seguintes. O usuário nunca vê esse conteúdo: só o canal final virou
`AgentEvent.Text`. Se o provider não publicar texto cru, cai no texto do canal final.

**`IMessageStore`** é a costura que permite ao `AgentLoop` rodar tanto sobre a conversa viva
(`ConversationService`) quanto sobre uma lista descartável (`EphemeralMessageStore`, usado pelo
aquecimento). O loop nunca segura um `List<ChatMessage>`.

---

## 3. Camada de Provider (`Services/Ai/`)

### 3.1 `IChatProvider` e os chunks
`StreamAsync` devolve `IAsyncEnumerable<StreamChunk>`. A pergunta *"isso é texto ou tool call?"* é
respondida **uma vez, dentro do provider** — nunca no orquestrador. Contrato de emissão, obrigatório
para todo provider:

1. `Done` é emitido exatamente uma vez, como último chunk, em toda enumeração que não lançar.
2. `TextDelta{Final}` já vem passado pelo `ChatTemplateSanitizer`. O orquestrador não remove nada.
3. `TextDelta{Reasoning}` é cru — é diagnóstico de console.
4. Provider que emitiu qualquer `ToolCallDelta` reporta `Done` com `ToolCalls`.
5. `TextDelta` de texto vazio não é emitido.

**`CallKey`** é a chave de agregação, opaca para o orquestrador e única por chamada lógica dentro de
uma enumeração:
- `OpenRouterProvider` usa `"or:" + índice` — o índice do delta é estável entre os chunks de uma
  mesma chamada.
- `OllamaProvider` usa um contador **local ao iterador** (`"ol:" + emittedCalls++`). O Ollama manda a
  tool call inteira dentro de uma linha NDJSON e **reinicia o índice do array em 0 a cada linha**;
  indexar por posição de array fundia duas chamadas distintas numa só, corrompida.

### 3.2 `ChannelSplitter` — a máquina de estados de canal
Era o `mode == 0/1/2` embutido no método gigante; hoje é um objeto com estado, **uma instância por
stream**.

| modo | significado | destino do texto |
|---|---|---|
| 0 `Streaming` | texto normal | canal **Final** (após strip de tokens de template) |
| 1 `InsideThink` | dentro de `<think>…</think>` | canal **Reasoning** |
| 2 `WaitingFinal` | depois de `</think>`, esperando `<channel\|>` / `<\|message\|>` | bufferiza; ecoa em **Reasoning** |

- Markers vigiados: `<think>`, `</think>`, `<channel|>`, `<|channel|>`, `<|message|>`.
- **Carry dinâmico:** `FindTrailingMarkerPrefix` retém no buffer só o sufixo que ainda pode ser o
  início de um marker, para que chunks pequenos não partam um marker ao meio (`"<th"` + `"ink>x"`).
- **Flush em camadas**, no fim do stream: modo 2 com buffer não vazio → o buffer **é** a resposta
  (qwen com thinking, modelos sem harmony); modo 1 com think não fechado → vaza o conteúdo do think
  como resposta, mas **só** se nada foi emitido como final e não houve tool call (é o último recurso
  contra um balão vazio).
- `FinalText` é o texto do canal final acumulado — a entrada do healer. `RawText` é o texto cru,
  com `<think>` intacto — o que vai para o histórico.

O **`ChatTemplateSanitizer`** guarda a lista de tokens de chat-template que vazam crus de alguns
modelos (Harmony/gemma4, ChatML, Llama 3, GPT, marcadores de role) e também tags `<think>` órfãs —
os pares válidos já foram tratados pela máquina de estados antes.

### 3.3 O Healer de tool call por texto (Regex Fallback) — recurso INTENCIONAL

Modelos de parâmetro menor às vezes "esquecem" de emitir a flag de tool call e escrevem a chamada
como prosa: `Action: read_file(caminho.txt)`. O fallback cura isso. Ele **continua existindo de
propósito** — mas mudou de lugar e de comportamento.

**Onde vive:** `Ai/RegexToolCallHealer` (interface `IToolCallHealer`), **dentro da camada de
provider**. O `ToolRegistry` não tem mais nada a ver com isso, e o `AgentLoop` desconhece sua
existência.

**Quando roda:** apenas no fim do stream, e apenas se **nenhuma** tool call nativa foi emitida
**e** a lista de tools ativas não está vazia. Recebe **só** `ChannelSplitter.FinalText`.

**Os três defeitos corrigidos:**
1. **Nunca mais lê `<think>`.** O contrato da interface diz que só recebe texto já classificado como
   canal final. Antes, o raciocínio privado do modelo podia virar execução real.
2. **JSON sempre por `JsonSerializer`.** A montagem por interpolação de string envenenava o
   histórico de forma permanente. Interpolação de JSON é proibida dentro desse tipo.
3. **Nomes de parâmetro vêm do schema real.** O healer lê `ChatTool.FunctionParameters`, coleta
   `properties` (com os tipos) e `required`, e só liga argumentos a nomes que existem de verdade.

**Ligação dos argumentos**, em ordem: sem argumentos → `{}`, válido só se `required` estiver vazio;
objeto JSON → reserializa mantendo só o que existe no schema, e recusa se faltar um `required`;
pares `nome=valor` / `nome: valor` → todos os nomes precisam existir no schema, e o que sobra fora
dos pares só pode ser separador; valor solto → liga ao primeiro `required` (ou à primeira
propriedade declarada), com aspas removidas. Recusa se o schema não declarar propriedade alguma, ou
se a ferramenta não existir nas tools ativas. Valores são coagidos para o tipo declarado
(`integer`/`number`/`boolean`), e na dúvida ficam string.

Em caso de sucesso o provider emite um `ToolCallDelta` com `CallKey = "heal:0"` e id
`fallback_<guid>`, seguido de `Done(ToolCalls, "healed")`.

> **Mudança de comportamento aceita e documentada:** a prosa que precede a chamada curada pode já ter
> chegado ao usuário. Isso também era verdade antes — o código antigo só dava `continue` *depois* de
> os chunks anteriores terem sido emitidos. O defeito real era o healer enxergar `<think>`, e esse
> acabou.

### 3.4 Aquecimento, Heartbeat e `keep_alive = -1` — recurso INTENCIONAL

Mascarar a latência de carga do modelo continua sendo objetivo do sistema. O mecanismo agora tem
dono: **`Agent/WarmupService`**, disparado pelo `App` depois que a UI existe.

`RunAsync(historySeed, ct)`:
1. Retorna imediatamente se `AiProvider != "Ollama"`.
2. `provider.WarmupAsync(ct)` → `POST {baseUrl}/api/generate` com
   `{ model, keep_alive = -1, options = { num_ctx = 16384 } }`, que trava o modelo na VRAM.
3. Só então trava a UI (`OnWarmupStateChanged(true)`).
4. Monta um **`EphemeralMessageStore`** a partir de uma cópia do histórico real, anexa **nele** a
   mensagem fantasma `[SYSTEM_HEARTBEAT] …` e chama `provider.CompleteAsync` com as tools ativas e
   `ChatRequestOptions.Default with { KeepAliveSeconds = -1 }`. Isso força a compilação da gramática
   JSON das ferramentas em background.
5. Publica as métricas de prefill no contador de tokens e joga o store fora inteiro.
6. Nunca lança: qualquer falha vira `[WARMUP ERRO]` no console. A UI só é destravada se chegou a ser
   travada.

**O que mudou e por quê:** o aquecimento antigo escrevia na conversa viva e limpava a sujeira com um
`RemoveRange(realHistoryCount, …)` num `finally` — que apagava mensagens que uma requisição
concorrente do usuário tinha acabado de anexar. Hoje **não existe caminho algum** em que o
aquecimento escreva no histórico vivo. Também não é mais disparado de dentro de um construtor.

Detalhe correlato no `OllamaProvider`: `keep_alive` viaja em **toda** requisição de chat
(default `-1`). Omitir o campo faz o Ollama reaplicar o default de 5 minutos e desfazer, em
silêncio, a trava feita pelo aquecimento.

### 3.5 `ChatProviderFactory`
Concentra a seleção de provider e a normalização de URL/credencial que viviam espalhadas em
`EnsureClient` e `AskStatelessAsync`:

- `AiProvider` normalizado: `OpenRouter` → `OpenRouterProvider`; o resto → `OllamaProvider`.
- Ollama: URL default `http://127.0.0.1:11434`, `/v1` removido, e `localhost` → `127.0.0.1` (a
  resolução IPv6 de `localhost` causava timeouts de 2 minutos).
- OpenRouter: a chave é lida do cofre do próprio provedor (`ChatProviderFactory.ChaveDe`); vazia,
  a requisição sai sem autorização e o 401 volta com a frase que diz onde configurar.
- **Cache:** uma instância por tupla `(provedor, ModelName, ApiUrl, credencial efetiva, opções do
  OpenRouter)`. A credencial entra na chave porque trocar a chave no cofre não muda nada nas
  configurações — sem isso, um cliente morto era reaproveitado e o 401 só sumia reiniciando o app.
- `GetProvider` é thread-safe por `lock`: o aquecimento e um turno podem chegar juntos na
  inicialização.

### 3.6 `OllamaNativeClient` — transporte
Fala `/api/chat` em NDJSON. Mudanças que importam: o laço de leitura é totalmente assíncrono
(`await reader.ReadLineAsync(ct)`; linha nula é fim), o histórico entra como `IReadOnlyList`, e
`numCtx`/`keepAliveSeconds` chegam até o payload. O índice da tool call é global ao stream, não por
linha. Depois deste refactor, `ChatUpdateDto` é detalhe de implementação do
`OllamaNativeClient` + `OllamaProvider`; nada mais deve referenciá-lo.

---

## 4. SettingsService (`SettingsService.cs`)

Persiste o perfil em `DirectoryService.SettingsPath` (`~/.AIB/profile.dat`), criptografado com
**DPAPI** (`ProtectedData.Protect`, escopo `CurrentUser`).

Duas correções estruturais:

1. **Caminho resolvido a cada operação.** O campo `static readonly SettingsPath` congelava no
   carregamento do tipo, e por isso `DirectoryService.ApplyFromSettings` **nunca** conseguia de fato
   realocar o arquivo de settings. Hoje é `ResolvePath()`, chamado em cada IO.
2. **Cache em memória.** `LoadSettings()` devolve `_cache.Clone()` quando o cache é válido para o
   caminho corrente; só volta ao disco quando não é. Isso mata as **6+ descriptografias DPAPI por
   mensagem enviada** que o serviço antigo provocava (ele chamava `LoadSettings()` em seis pontos
   diferentes de um único turno). `SaveSettings` grava e substitui o cache; `InvalidateCache()`
   descarta — o `App` o chama logo após `ApplyFromSettings`, porque o caminho pode ter mudado.

`LoadSettings` sempre devolve **cópia** (`UserAppSettings.Clone`, `MemberwiseClone` — todos os campos
são string ou tipo de valor). O fallback de migração de arquivo em texto claro foi preservado, assim
como o caminho "arquivo ausente → grava defaults". O `HttpClient` de `GetOllamaModelsAsync` virou
`static readonly`; a classe alocava um por instância.

A correção do cache depende de existir **um** `SettingsService` por processo — o que o Composition
Root garante (doc 01 §1).

---

## 5. LevelService (`LevelService.cs`)

Governança e gamificação: o orçamento de contexto espelha o uso do aplicativo, via
`Settings.MessageCount`. Valores reais do código:

- **`GetLevel(xp)`** contra os limiares `{0, 20, 50, 100, 200, 350, 600, 1000, 1500}` — nível 1 a 9.
- **`GetMaxTokensForLevel(level)`**:

| Nível | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9+ |
|---|---|---|---|---|---|---|---|---|---|
| Tokens | 3.072 | 5.120 | 7.168 | 12.288 | 17.408 | 22.528 | 32.768 | 43.008 | 53.248 |

Esse número é o orçamento que a poda da `ConversationService` persegue, e é o `max` publicado no
contador de tokens da UI.

---

## 6. ToolRegistry (`ToolRegistry.cs`)

Dicionário em memória (case-insensitive) de tudo que implementa `ITool`, populado uma vez no
construtor por `RegisterNativeTools()`. É construído **uma única vez, no `App`**, e injetado — o
`new ToolRegistry()` que ficava dentro do serviço cognitivo desapareceu com o arquivo.

### 6.1 Isolamento por `RequiredLevel` — recurso INTENCIONAL, mantido
A interface `ITool` obriga um inteiro `RequiredLevel`, aplicado em dois pontos:
- **`GetActiveTools(userLevel)`** filtra por `RequiredLevel <= userLevel`. Ferramentas acima do nível
  do usuário nem sequer são **descritas** para o modelo — ele não pode pedir o que não enxerga.
- **`ExecuteToolAsync`** revalida o nível e devolve `ACESSO NEGADO: …` se a chamada chegar assim
  mesmo. Defesa em profundidade, não confiança no filtro anterior.

> **Realidade:** as três ferramentas registradas hoje têm `RequiredLevel = 1`. Ou seja, o mecanismo
> de gating está implementado e funcionando, mas **não está gatilhado por nada** — inclusive o
> `run_command`. Ver doc 04 §2.

### 6.2 O que o registry NÃO faz mais
- `GetCategorizedTools()` devolve todas as ferramentas como "nativas" e uma lista de dinâmicas
  **sempre vazia**. A sidebar de skills da `ChatWindow` mostra apenas as três nativas.
- `Refresh()` é um no-op que só imprime uma linha no console. O `AgentLoop` ainda o chama quando a
  ferramenta executada se chama `materialize_skill` — ferramenta que não existe mais no registry.
- Exceções de ferramenta são capturadas e viram string `ERRO ao executar '<nome>': …`, que volta ao
  modelo como resultado; ferramenta desconhecida vira `ERRO: Ferramenta '<nome>' não encontrada no
  registry`, com a lista das disponíveis anexada.
