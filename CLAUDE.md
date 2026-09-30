# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

AIB: assistente de IA para Windows (C# / .NET 8 / WPF). Vive na bandeja, abre a conversa por
`Ctrl+Shift+Space`, pelo ícone ou pelo orbe, e conversa com um agente que chama ferramentas
(arquivos, PowerShell, skills, e-mail IMAP só leitura, navegador) atrás de um portão de
confirmação humana.

**Idioma:** código, comentários, commits, textos de tela e documentação são em português (PT-BR).

## Comandos

Não há `.sln`: cada projeto se constrói pelo seu `.csproj`. Rodar da raiz do repositório.

```powershell
dotnet build AIBWindows\AIB.csproj
dotnet run --project AIBWindows\AIB.csproj

dotnet test AIB.Tests
dotnet test AIB.Tests --filter "FullyQualifiedName~ToolRegistryTests"   # uma classe
```

- **Com o AIB aberto**, o `AIB.exe` trava a pasta de saída. Mande o build dos testes para outra
  pasta **dentro do repositório** (fora dele, `ThemeResourcesTests` não acha `AIBWindows/` subindo
  a partir do binário e falha):
  `dotnet test AIB.Tests "-p:OutDir=AIB.Tests\bin\san\" -p:SelfContained=false`
- Integração com Ollama real (opt-in): `$env:AIB_INTEGRACAO="1"; dotnet test AIB.Tests --filter Categoria=Integracao`.
  PNGs das telas: `AIB_UI_PNG=1` (ver `.env.example`). O app em si não lê variáveis de ambiente.
- **`AIB.Avaliacao` custa dinheiro.** Ele manda casos ao modelo configurado no app; com OpenRouter,
  cada caso é uma requisição cobrada. Não rode sem saber os argumentos (ele não tem `--help`: um
  argumento desconhecido vira rótulo e a rodada começa). Estime o custo antes e comece com
  `--casos` e poucas repetições. Uso: `dotnet run --project AIB.Avaliacao -- <rótulo> AIB.Avaliacao\resultados [opções]`,
  opções em `documentação/tecnica/08-testes-e-avaliacao.md`.
- `prototipos/` fica fora dos projetos (ex.: `ProtoNavegador`, bancada do leitor de página).

## Documentação

`documentação/tecnica/01..08` descreve o código como ele está; leia o **01-arquitetura** antes de
mudanças grandes. Quando o comportamento muda, o documento correspondente é corrigido **no mesmo
commit**. `Regras de Identidade/` (SEGURANCA, CODIGO_LIMPO, VISUAL) são regras, não sugestões.
`ideias_futuras/` não existe no código.

## Arquitetura (o que exige ler vários arquivos)

- **Composition root sem DI:** tudo nasce com `new`, uma vez, em `App.OnStartup`
  (`AIBWindows/App.xaml.cs`), na ordem que importa (diretórios → settings → registro → portão →
  `ToolRegistry` → providers → `AgentLoop` → `ConversationService` → `ChatWindow` → vigia de
  e-mail → orbe → bandeja). Nada pesado em construtor.
- **Caminho de uma mensagem:** `ChatWindow` → `ConversationService.StreamResponseAsync` (trava de
  turno, memória no prompt) → `AgentLoop.RunAsync` (laço ReAct, ferramentas em paralelo) →
  `ChatProviderFactory` (só dois provedores: Ollama nativo `/api/chat` e OpenRouter; HTTP direto,
  do pacote `OpenAI` só os tipos) → `ToolRegistry.ExecuteToolAsync` (nível, `Validar`, dispensa
  por pasta, floor list, cartão, auditoria antes de executar) → `ITool.ExecutarAutorizadoAsync`.
- **O orbe não roda turno próprio:** ele chama `ChatWindow.AbrirComMensagem(..., mostrarJanela: false)`
  e a conversa roda o turno escondida. No modo orbe, a confirmação vai para a
  `ConfirmacaoDoOrbeWindow` (o mesmo `ConfirmCardView`), posicionada por `PertoDoOrbe`.
- **Portão:** `ChatConfirmationPrompt` é a única porta; sem UI conectada, recusa. Cada ferramenta
  decide se pede confirmação (`ITool.PedeConfirmacao(args)`) e descreve a ação
  (`BuildConfirmationContext`; null recusa). Negações começam com `ACESSO NEGADO`
  (`ArtifactExtractor.Falhou` depende disso).
- **Memória:** `raw.jsonl` guarda a conversa crua para sempre; o modelo recebe capítulos, atos e
  fatos (`Services/Memory/`, `Compactor`).
- **Conteúdo de terceiros** (arquivo lido, saída de comando, e-mail, página web) é informação, não
  instrução: marcado por `AgentLoop.MarcaDeConteudo` / `ConteudoDeTerceiros`, redigido em todo
  caminho para o disco, e com e-mail no contexto o "sempre permitir" e as dispensas deixam de valer.
- **Navegador** (`Services/Navegador/`, ferramenta `browser`): Playwright com Edge, perfil
  persistente em `~/.AIB/navegador/perfil`. A página é lida por `Snapshot.js` (recurso embutido) e
  vira uma leitura pesquisável (`LeituraDaPagina`), mantida só em RAM. `.playwright/` precisa estar ao lado do `AIB.exe`.
- **Shell e senhas** (`Services/Terminal/`): o PowerShell roda sem console e com a entrada
  fechada; ssh/scp/git pedem senha pelo askpass, que é o próprio `AIB.exe` (detectado no topo de
  `OnStartup`) falando por pipe nomeado com a instância que rodou o comando.
- **Interface:** cores só de `Themes/Tokens.xaml` via `StaticResource` (em código, `FindResource`
  ou `Ui/PincelDoTema`); hex literal em View é proibido. `App.xaml` carrega `Controls.xaml`, que
  mescla os tokens.

## Regras que pegam quem chega

- **Testes nunca escrevem no `~/.AIB` real.** Toda classe que grava em disco aceita outra raiz, e o
  teste usa pasta temporária (tabela de portas em `08-testes-e-avaliacao.md`; auditoria e histórico
  já são desviados por `[ModuleInitializer]` em `TestAuditRedirect.cs`).
- WPF só em `WpfHost.EmSta(...)` (uma thread STA para a suíte inteira); `WpfHost.GarantirRecursos()`
  carrega os estilos. `JanelaDeEnsaio` monta uma `ChatWindow` descartável. Estado estático
  compartilhado exige coleção xUnit declarada (`ContextoGlobal`, `Escrita`, `Historico`, `Skills`).
- **Um teste por correção**, com o caso real que motivou a correção no comentário. Comentários
  explicam o porquê e o sintoma visto.
- Nomes de ferramenta só via `Ferramentas.cs`. A soma das `Description` das ferramentas tem
  orçamento travado (`ToolRegistryTests.OOrcamentoDasDescricoes_NaoCRESCE`): elas vão ao modelo em
  toda requisição, e são pagas.
- Mudança no prompt de sistema se mede com `AIB.Avaliacao` antes de entrar.
- WPF e WinForms estão ligados juntos: `Color`, `Brush`, `Point`, `Size`, `KeyEventArgs`,
  `MouseButtonEventArgs` etc. são ambíguos — use alias ou o nome completo.
- Chaves e senhas só nos cofres DPAPI (`CredentialService`, `MailVault`). E-mail é só leitura e o
  corpo nunca vai para disco. `raw.jsonl` nunca é apagado (o reset renomeia para `.bak`).
