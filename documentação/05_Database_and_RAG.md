# 05. Banco de Dados e Memória (RAG e Longo Prazo)

> **Estado atual, dito de uma vez:** **o RAG não está implementado.** Não há banco vetorial, não há
> embeddings, não há similaridade de cosseno, não há `memory.db`. A versão anterior deste documento
> descrevia um motor completo — `SmartComponents.LocalEmbeddings` + `bge-micro-v2` via ONNX, LiteDB
> com a coleção `memories`, corte de relevância em `0.2`, Top 3 devolvidos ao loop ReAct. Nada disso
> existe no código hoje. O resto deste arquivo descreve o que de fato persiste em disco.

## 1. O que sobrou do `MemoryService`

`AIBWindows/Services/MemoryService.cs` tem **7 linhas**:

```csharp
public static class MemoryService {
    public static void DeleteMemory(int id) { }
    public static List<object> GetRecentMemories(int count) => new List<object>();
}
```

Ambos os membros são stubs — corpo vazio e lista vazia. O único chamador vivo é a
`ContextSidebar`, que por isso renderiza uma lista de memórias sempre vazia.

Não existe nenhuma ferramenta `manage_memory` registrada no `ToolRegistry` (doc 04 §1), então o
modelo também não tem como gravar nem recuperar fatos. O `SYSTEM_PROMPT` da `ConversationService`
ainda instrui o modelo a chamar `manage_memory(action=recall)` antes de dizer "não sei"; quando ele
obedece, recebe de volta um erro de ferramenta inexistente.

**Pacotes órfãos:** `LiteDB` (5.0.21) e `SmartComponents.LocalEmbeddings` (0.1.0-preview10148)
continuam declarados no `AIB.csproj`. Nenhum arquivo do projeto os referencia. São dívida de
referência — inflam o publish self-contained sem entregar capacidade.

## 2. Onde os dados do usuário realmente ficam (`DirectoryService`)

`DirectoryService` centraliza os caminhos e é a primeira coisa que o `App` inicializa
(`EnsureDirectories()`), antes de qualquer serviço nascer.

- **`DataDir`** = `%USERPROFILE%\.AIB` por padrão. Existe uma migração automática do local antigo
  (`%APPDATA%\AIB`): se a pasta antiga existir e a nova não, o conteúdo é copiado.
- **`TempDir`** = `%TEMP%\AIB`.
- Subpastas criadas no boot: `skills/`, `memory/`, `logs/`, `character/` e, no temporário,
  `screenshots/`, `ocr_cache/`, `cmd_output/`.
- **`ApplyFromSettings`** permite ao usuário redirecionar `DataDir` e `TempDir` pelas settings, e
  chama `EnsureDirectories()` de novo no novo destino.

> As pastas `memory/`, `screenshots/` e `ocr_cache/` são criadas em todo boot e **ficam vazias** —
> os serviços que as escreviam foram deletados ou esvaziados.

## 3. Persistência que existe de verdade

### 3.1 Perfil e configurações — criptografado (DPAPI)
`SettingsService` grava o `UserAppSettings` inteiro em **`~/.AIB/profile.dat`**, serializado em JSON
e criptografado com `ProtectedData.Protect` (**DPAPI**, escopo `CurrentUser`). At-rest amarrado à
conta do Windows logada: outra conta na mesma máquina não descriptografa.

Há um fallback de migração: se a leitura criptografada falhar, o serviço tenta ler o arquivo como
JSON em texto claro e o regrava criptografado.

Em memória, as settings ficam em cache e são servidas dali (doc 03 §4); o disco só é tocado no
primeiro `LoadSettings` de cada caminho e em cada `SaveSettings`.

### 3.2 Cofre de credenciais — criptografado (DPAPI)
`CredentialService` (138 linhas, implementação real) guarda a chave da API do provedor. O
`UserAppSettings.ApiKey` guarda apenas o sentinela `"use-vault"`; a chave real vive no cofre. A
`ChatProviderFactory` a resolve na hora de construir o cliente, e um resultado com prefixo `ERRO`
vira `"placeholder"` para o SDK falhar com erro de autenticação — sinal de que o onboarding precisa
ser refeito.

O `App` executa uma migração idempotente no boot (D-11): qualquer `ApiKey` que não seja `"use-vault"`
nem `"ollama"` é substituída pelo sentinela, e o evento é registrado no `AuditLogService`. A chave
anterior **não** é copiada para o cofre — a rotação precisa acontecer primeiro.

### 3.3 Histórico de chats — **texto claro**
`ChatHistoryService` grava as sessões antigas em
**`%APPDATA%\AIB\chat_history.json`**, com `JsonSerializer` e `WriteIndented = true`. Sem
criptografia.

- **Estrutura (`ChatSession`)**: `Id` (GUID), `Title` (as primeiras 40 letras da primeira mensagem do
  usuário), `Timestamp`, `Content` (a transcrição achatada, linhas `USER: …` / `AIB: …`).
- Só entram mensagens de usuário e de assistant com texto; mensagens que começam com `[SYSTEM` são
  puladas, e sessões com apenas o system prompt não são salvas.
- **Retenção:** as 50 sessões mais recentes; o excedente é descartado na gravação.
- **Quem dispara:** `ConversationService.ResetHistory()`, antes de limpar a lista viva — a chamada de
  disco acontece **fora** do lock do histórico.

Duas observações honestas:

1. **O conteúdo das suas conversas fica em disco sem criptografia**, ao contrário das settings e do
   cofre. Qualquer processo rodando com a sua conta lê o arquivo.
2. **`ChatHistoryService` ignora o `DirectoryService`.** Ele resolve o caminho por conta própria e
   sempre aponta para `%APPDATA%\AIB`, enquanto todo o resto do aplicativo migrou para
   `~/.AIB`. Redirecionar o `DataDirectory` nas settings **não** move o histórico de chat.

### 3.4 Log de auditoria
`AuditLogService.AppendAsync` registra eventos estruturados (migração de chave, cancelamento do
onboarding). Escreve em `DirectoryService.LogsDir`.

## 4. Contexto de longo prazo: o que o AIB usa hoje

Sem RAG, a única memória do agente é a **janela de contexto de curto prazo** administrada pela
`ConversationService`: o histórico vivo, podado por orçamento de tokens do nível, com o system prompt
travado (doc 03 §1.2). Fora do turno, o que sobrevive é a transcrição em `chat_history.json`, e ela
só volta à conversa por ação explícita do usuário — ao recuperar um chat na `ContextSidebar`, que
chama `ConversationService.AppendRecoveredContext(title, content)` e injeta a transcrição como um par
usuário/assistant no início do contexto.

Isto é: a recuperação é **manual e integral**, não semântica e seletiva. Era exatamente esse "vomitar
o histórico num único prompt" que o motor de RAG existia para evitar. Reconstruí-lo significa,
concretamente:

1. reimplementar `MemoryService` sobre os pacotes que já estão no `.csproj`;
2. registrar uma ferramenta `manage_memory` no `ToolRegistry` com um `RequiredLevel` coerente
   (doc 04 §6);
3. só então o `SYSTEM_PROMPT` volta a dizer a verdade quando manda o modelo chamar `recall`.
