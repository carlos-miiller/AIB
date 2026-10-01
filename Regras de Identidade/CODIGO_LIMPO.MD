# Regras de Código Limpo e Arquitetura (AIB)

Normas de engenharia do AIB. O objetivo é código leve, explícito e fácil de mexer sem repetir erros já corrigidos. Visão completa em `documentação/tecnica/01-arquitetura.md`.

## 1. Composição na raiz, sem container de DI
- O composition root é `AIBWindows/App.xaml.cs` (`App.OnStartup`). Os serviços de vida longa (`SettingsService`, `ToolRegistry`, `ChatConfirmationPrompt`, `TokenCounter`, `ChatProviderFactory`, `AgentLoop`, `ConversationService`, `MailDigestService`) nascem ali, uma vez, com `new`, e vão pelo construtor a quem precisa.
- Não usamos `Microsoft.Extensions.DependencyInjection`. A ordem de construção fica visível e os tempos de vida são explícitos num app que passa o dia na bandeja.
- Nada de trabalho pesado em construtor: aquecimento, laços e timers começam por chamada explícita do `App`, depois que a UI existe.

## 2. Serviços em `Services/`, code-behind só com UI
- A lógica mora em `AIBWindows/Services/` e subpastas (`Agent/`, `Ai/`, `Memory/`, `Mail/`, `Tools/`). Cada responsabilidade tem sua classe.
- O `.xaml.cs` trata cliques, teclado, foco e desenho. Regra de negócio no code-behind é regra que o teste não alcança.
- Um valor com significado (padrão, teto, nome) existe em **um** lugar, como `const` nomeada ou membro, e os outros leem dele. Número repetido em três arquivos sai de sincronia na primeira mudança.

## 3. Ferramentas: o contrato `ITool`
- Toda ferramenta nativa implementa `ITool` (`Services/ITool.cs`), mora em `Services/Tools/` e é registrada em `ToolRegistry.RegisterNativeTools`. Skills do usuário não viram ferramentas: chegam pela ferramenta `skill`.
- Membros obrigatórios: `Name`, `Description`, `ChatToolDefinition`, `RequiredLevel`, `ExecuteAsync`. Com padrão na interface: `RequiresConfirmation` (falso), `Validar` (pré-voo; texto devolvido recusa antes do portão), `BuildConfirmationContext` (o que o cartão mostra; null recusa), `DispensaConfirmacao` (pasta de confiança), `PassaPelaFloorList` (só linha de comando) e `ExecutarAutorizadoAsync` (executa sabendo o que foi autorizado — o único ponto que o registry chama).
- Ferramenta que altera a máquina responde `RequiresConfirmation => true` e sabe descrever a ação. Sem descrição, não roda.
- **Ferramenta nunca derruba o app.** O resultado é sempre texto: erro vira uma frase em português começando com `ERRO`, que diz ao modelo o que corrigir. O `ToolRegistry` ainda captura qualquer exceção e a devolve como `ERRO ao executar…`, mas isso é rede, não desculpa.
- **Nomes de ferramenta só via `Ferramentas.cs`** (`Ferramentas.Ler`, `Ferramentas.Shell`…), e rótulos de tela via `Ferramentas.Rotulo`. Literal espalhado não quebra a compilação: vira um `switch` que cai no default.

## 4. Thread de interface
- Rede, disco e modelo são sempre assíncronos. Nada bloqueia a thread de UI.
- A camada de serviço usa `ConfigureAwait(false)` e não toca controles. Evento que nasce fora da UI (vigia, timers) chega à tela por `Dispatcher.BeginInvoke`/`Invoke`, ou pelo `SynchronizationContext` capturado, como faz o `ConversationService`.
- Estado compartilhado entre threads tem dono e trava explícitos; quem lê recebe cópia.

## 5. Dados do usuário só em `~/.AIB`
- Tudo o que é do usuário vive em `~/.AIB`, com caminho vindo de `DirectoryService`. A pasta do programa é só padrão de fábrica e fallback de leitura. Nada se grava fora disso.
- `raw.jsonl` nunca é apagado. Chave e senha só nos cofres DPAPI (`CredentialService`, `MailVault`), nunca em configuração, log, console ou tela; a auditoria guarda no máximo `key_last4`.
- Toda classe que grava em disco aceita uma raiz alternativa, para o teste não tocar os dados reais.

## 6. Comentários em português, explicando o porquê
- Comente a razão da decisão, o caso real que a motivou e o erro que ela evita. O "o quê" o código já diz.
- Ao corrigir um defeito, deixe no comentário o sintoma visto; é o que impede a próxima pessoa de desfazer a correção.

## 7. Um teste para cada correção
- Toda correção vem com um teste em `AIB.Tests` que falha sem ela. Siga as convenções de `documentação/tecnica/08-testes-e-avaliacao.md`: pastas temporárias, `WpfHost.EmSta` para janelas, coleção declarada para estado estático.
- Mudança no prompt de sistema se mede com `AIB.Avaliacao` antes de entrar.

## 8. Conflitos de nome
- WPF e WinForms estão ligados juntos, e vários tipos existem nos dois (`Color`, `Brush`, `Brushes`, `Path`, `Clipboard`). Use alias no topo (`using Brush = System.Windows.Media.Brush;`) ou o nome completo.

## 9. Nomes curtos
O código é em português, e o português alonga nome à toa: artigo e preposição em cada junção. Nome comprido não pesa no programa (vira referência na compilação), pesa em quem lê.
- **Sem artigos nem preposições** quando o sentido sobrevive: `UsaJanelaOrbe`, não `DeveUsarJanelaDoOrbe`; `RetOrbe` ou `AreaOrbe`, não `RetanguloDoOrbe`.
- **Não repita o contexto.** A classe, o namespace e o tipo já dizem metade: dentro de `AskpassServidor`, `ResponderAsync`, não `ResponderPedidoDeSenhaAsync`; um `bool` não precisa de `Esta`/`Deve` se o nome já soa como pergunta (`Confiavel`, `Visivel`).
- **Variável local é curta** quando o escopo é curto: `pedido`, `resp`, `dir`, não `pedidoDeSenhaRecebido`.
- **Frase só em nome de teste.** Lá o nome vira a mensagem da falha e vale ser longo (`OSeletor_MostraOTemaGravado`).
- **Abreviação só a consagrada** (`Ret`, `Dir`, `Msg`, `Cfg`, `Max`, `Min`); sigla inventada é pior que nome longo.
- **Nomes existentes** encurtam quando o arquivo é mexido por outro motivo, não em mutirão. Cuidado com o que depende do texto do nome: `x:Name`/`FindName` no XAML, nomes de ferramenta (vão ao modelo), chaves de JSON gravado em `~/.AIB` e chaves de recurso dos temas.
