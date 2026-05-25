# 01. Visão Geral Arquitetural

## 1. Padrão de Projeto e Estrutura Principal
O **AIB** não utiliza um container de Injeção de Dependência formal (como o `Microsoft.Extensions.DependencyInjection` clássico). Em vez disso, a classe raiz `ChatWindow` atua como o **Composition Root**. Todos os serviços centrais são instanciados e conectados via construtores e eventos dentro do code-behind da tela principal de chat, mantendo o ciclo de vida dos objetos atrelado ao ciclo de vida da janela (que funciona como um Singleton persistente no System Tray).

## 2. O Fluxo Principal de Processamento (O Loop ReAct)

A espinha dorsal da inteligência do assistente reside na interação entre `ChatWindow`, `OpenAIService` e `ToolRegistry`. O fluxo segue esta cascata:

1. **Entrada do Usuário**: O texto digitado pelo usuário (ou captado via voz/OCR) é enviado ao método `SendUserMessageAsync()` da `ChatWindow`.
2. **Gerenciamento de Contexto**: A mensagem é armazenada no array de estado do `OpenAIService`. O sistema roda um cálculo de tokens em tempo real (`NotifyTokenCount`). Se o tamanho total violar o *Token Limit* permitido pelo Nível (`LevelService`), a janela de contexto desliza (as mensagens antigas são apagadas) para preservar a sanidade da requisição e economizar custos.
3. **Loop de Inferência (ReAct Mode)**: O request assíncrono à OpenAI é disparado. 
   - Se o LLM responder com texto direto, o Loop se encerra, acionando a `ChatWindow` para animar e renderizar o Markdown.
   - Se o LLM decidir utilizar uma ferramenta (detectado por `FinishReason == ToolCalls` ou por um fallback em Regex de um JSON malformado), o `ToolRegistry` é acionado.
4. **Execução Nativa**: O `ToolRegistry` localiza a `ITool` requisitada (como `ReadFileTool` ou `RunCommandTool`). O método `ExecuteAsync(jsonArgs)` é rodado debaixo dos panos.
5. **Ciclo Recursivo**: O retorno da ferramenta (o conteúdo do arquivo lido, a saída do terminal, etc) é adicionado ao histórico da IA, e o Loop do Passo 3 recomeça imediatamente. Esse balé ocorre de forma oculta enquanto a UI apresenta um Loading piscante, até que a IA dê a resposta por concluída.

## 3. Categorização de Serviços (`/Services`)

Todo o poder cognitivo do AIB está concentrado em serviços estritos e singulares de Responsabilidade Única (SOLID):
- **Camada Física/OS**: `ScreenshotService`, `VoiceService`, `OcrService`. Lidamos com telas e microfones do Windows nativamente.
- **Camada Cognitiva**: `OpenAIService`, `WebSearchService`, `MemoryService` (RAG local).
- **Camada de Core Application**: `SettingsService` (persiste as chaves de API e contadores no disco local), `LevelService` (Gamifica e limita o consumo da API).
- **Camada de Ações (Tools)**: `ToolRegistry` acopla dinamicamente todas as funções descritas em `NativeTools.cs` (Interface `ITool`), permitindo o LLM invocar código C# direto da nuvem.
