# 03. Core Services (Cognição e Controle)

A pasta `Services/` isola e processa as lógicas computacionalmente densas. O "cérebro" do assistente AIB gira em torno de três serviços estritamente correlacionados: a chamada da API da OpenAI, o gerenciamento de níveis de uso (Gamificação/Permissão), e o mapeamento das Ferramentas locais.

## 1. OpenAIService (`OpenAIService.cs`)

O Serviço mais complexo do sistema, responsável pela abstração da biblioteca `OpenAI.Chat`. 

### 1.1 Gerenciamento de Memória de Curto Prazo (Context Pruning)
- O histórico ativo (Curto Prazo) reside de forma persistente em tempo de execução numa propriedade de lista interna (`_history`).
- Quando uma nova mensagem do usuário é anexada, o evento `NotifyTokenCount` realiza um *Approximation* computacional (Baseado em O200kBase) de tokens consumidos globalmente por aquele histórico.
- **Auto-Poda (`TrimHistoryAsync`)**: Se o array atingir ou ultrapassar o Limite de Tokens definido pelo Nível Atual do usuário, a rotina poda (`RemoveAt`) de forma iterativa os nós 1 e 2.
- **Proteção Sistêmica**: O index `0` (Zero) da lista é a *Instrução do Sistema* (Prompt Arquitetural do Bot). A rotina matemática do Trim possui um hard-lock para jamais arrancar o System Prompt da memória, independente de quão sufocado o contexto estiver.

### 1.2 O Loop ReAct (Reasoning and Acting)
O método base `SendMessageStreamAsync` implementa o paradigma ReAct:
1. Varre o catálogo dinâmico de ferramentas registradas do `ToolRegistry` e converte cada `ChatToolDefinition` nativo C# para o standard Tool Schema da API da OpenAI, acoplando as funções viáveis ao Request HTTP.
2. **Avaliação da Resposta (Streaming/Yielding)**: O serviço aguarda o fluxo da API em blocos (Chunks). Se for detectada intenção do LLM de engatilhar uma Skill/Tool (Motivo de encerramento = `ToolCalls`), ele bloqueia qualquer renderização de Markdown para a interface.
3. Invoca o `ToolRegistry` de forma reflexiva com os argumentos providos no JSON assíncrono para o ambiente de Desktop.
4. **Alimentação Fantasma (Ghosting Loop)**: A string resultante do C# (ex: log de saída de um executável, o parseamento numérico do liteDB) é forçadamente acoplada na lista de `_history` com o identificador de Role `"tool"`. 
5. Automaticamente, dispara uma *re-chamada* para a OpenAI com todo o conjunto anexado, até que a IA dê um `Stop` definitivo emitindo a resposta final conclusiva.

## 2. LevelService (`LevelService.cs`)

Classe de Governança e Gamificação que atua como barreira natural aos limites econômicos da API.
- **Progressão via Engajamento**: Em vez de expor uma barra de "Modificar Contexto" solta no layout, a memória livre do bot espelha o esforço de uso do aplicativo. Isso recupera dados de `Settings.MessageCount`.
- **Cálculo Matemático de Progressão**:
  - `GetLevel`: Avança os níveis através de metas numéricas lineares-exponenciais (começando no Nível 1; escalando em dezenas, depois centenas de interações).
  - `GetMaxTokensForLevel`: Inicia com conservadores **3.072 Tokens**. Libera blocos de progresso consideráveis com taxas que vão de `+2.048` até exponenciais `+10.240` a partir do estágio avançado de Nível 7, controlando a economia semântica sem burocracias ou senhas complexas.

## 3. ToolRegistry (`ToolRegistry.cs`)

O "Portão de IO" que transforma texto em código real em runtime. Mantém um dicionário global em memória de tudo o que implementa `ITool`.

### 3.1 Dual-Mode Execution (Regex Fallback Mechanism)
Em certas alucinações (Hallucinations) do LLM, ou modelos de peso de parametro menor instáveis, a IA pode "esquecer" que deveria emitir uma flag de ToolCall embutida e tentar transcrever a Tool direto para o usuário na caixa de texto bruto: `[call: windows_console_execution { "command": "dir" }]`.
- Se a IA fizer isso, o módulo `ProcessFallbackToolCall` varre o retorno final. Uma expressão Regex complexa intercepta exatamente a sintaxe de call falsa. O `ToolRegistry` então rasga o texto fora da resposta da UI, gera um Task nativo simulado, engole a requisição falha e roda a rotina local localmente, "curando" a alucinação da IA e mantendo o ciclo vivo.

### 3.2 Isolamento de Segurança Reflexivo (`RequiredLevel`)
A interface `ITool` obriga um inteiro de segurança `RequiredLevel`.
- Quando o `ToolRegistry` entrega a Payload da camada 1 para a OpenAI ver o que "pode" ser feito com a máquina local do usuário, ele filtra por `userLevel`. Ou seja, se um usuário recém embarcado estiver no Nível 1, Skills de manipulação de pastas base ou alteração de chaves criptográficas do sistema nem sequer são descritas para a IA tentar prever ou pedir autorização, configurando uma proteção *Air-Gapped* de software contra comandos invasivos.
