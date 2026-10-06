# 06 — Interface

Este documento descreve as janelas do AIB, o que cada uma faz e por que foi desenhada assim. Tudo
mora em `AIBWindows/Views/`, `AIBWindows/Themes/` e `AIBWindows/Ui/`. As specs visuais que o código
cita como fonte de verdade estão em `refactor-interface/`:

| Spec | Cobre |
|---|---|
| `tela-chat-v3.html` | Janela de conversa, bolhas, cadeia de ações, cartão de confirmação, painel lateral, modo e-mail |
| `tela-configuracoes.html` | Tela de configurações e o fluxo de contas de e-mail |
| `shadow-assistant.html` | O orbe sobre a área de trabalho |

Os comentários do código citam seções dessas specs (`§3.5`, `§5.3`, `O7`...). O `§0` de cada spec
é um contrato: as regras `O1`–`O10` do chat (sem blur nativo, neon como única cor saturada, sombra
obrigatória na moldura, raio 18 uniforme, bolha do usuário sem sombra, ações colapsadas em chip,
confirmação antes de ação destrutiva, tooltip fora de área rolável, chips da mesma altura, primário
= enviar) não são sugestão.

## Mapa das janelas

| Arquivo | Tipo | Papel |
|---|---|---|
| `ChatWindow.xaml.cs` + `ChatWindow.Email.cs` | `Window` | A conversa e o modo e-mail. É a `MainWindow` do app. |
| `ConfirmCardView.xaml.cs` | `UserControl` | Cartão de confirmação de ação destrutiva, dentro da conversa |
| `ToolChainView.xaml.cs` | `UserControl` | Cadeia de ações de uma fala da IA |
| `SidePanelWindow.xaml.cs` | `Window` | Painel lateral: histórico, arquivos no contexto, histórico de ações |
| `ShadowAssistantWindow.xaml.cs` | `Window` | O orbe do Shadow Assistant |
| `MailListItem.xaml.cs` | `UserControl` | Item de e-mail, usado no modo e-mail e no orbe |
| `FirstRunWindow.xaml.cs` | `Window` | Primeiro arranque (assistente em cinco passos) |
| `SettingsWindow.xaml.cs` | `Window` | Configurações, oito páginas |
| `ConfirmDialog.xaml.cs` | `Window` | Confirmação modal fora da conversa |
| `ContextSidebar.xaml.cs` | `UserControl` | Estacionado de propósito, sem chamador |
| `ShadowWidget.xaml.cs` | `Window` | Resto do Shadow antigo de captura de tela; inerte |

## Abrir e fechar: bandeja, atalho e primeiro arranque

`App.xaml.cs` é a raiz de composição. Ele cria os serviços, a `ChatWindow`, o ícone da bandeja
(`TaskbarIcon`, do Hardcodet.NotifyIcon) e o atalho global (`NHotkey`).

- **Atalho global:** `Ctrl+Shift+Space`, registrado em `OnStartup` com o nome `ToggleChat`. Se o
  registro falhar (outro programa já usa a combinação), o erro vai para o console e o app segue.
- **Bandeja:** clique esquerdo no ícone alterna a conversa. O menu tem "✦ Abrir Chat",
  "✦ Shadow no desktop" (liga e desliga o orbe), "Ler os e-mails agora" (roda o digest na hora,
  se a triagem estiver ligada) e "Sair".
- `ShutdownMode="OnExplicitShutdown"` em `App.xaml`: fechar ou esconder janelas não encerra o
  processo. Só "Sair", o cancelamento do primeiro arranque e o reset de fábrica encerram.

**Uma porta só.** Atalho, "Abrir Chat" e clique no ícone chamam `App.AlternarChat`. Ele pergunta
`NeedsFirstRun` antes de abrir a conversa. A razão está no comentário do método: antes só o atalho
checava, e pela bandeja o chat abria sem provedor ou sem chave, e o primeiro turno falhava com 401.
`NeedsFirstRun` devolve verdadeiro quando `AiProvider` está vazio ou quando o provedor exige chave e
a chave **dele** não está no cofre (`CredentialService.LerDoSistema`, leitura estrita, sem busca
nos outros arquivos do cofre). O campo `_primeiroArranqueAberto` impede um segundo clique na
bandeja de abrir outro primeiro arranque por cima.

`ShowFirstRunWindow` abre a `FirstRunWindow` como diálogo. Com `DialogResult == true`, mostra a
conversa. Com cancelamento, grava `firstrun_cancelled` na auditoria e encerra o app. A janela do
primeiro arranque só define `DialogResult`; quem encerra é o `App`.

## ChatWindow — a conversa

Janela sem moldura do sistema (`WindowStyle="None"`, transparente, `Topmost`, fora da barra de
tarefas), 820×605 por padrão, com a área útil de 770×520 (o resto é a margem da sombra).
`RepositionWindow` a centraliza na tela principal com a base 22 px acima da barra de tarefas
(`FolgaDaBarraDeTarefas`; era 45 e o vão chamava mais atenção que a conversa). A janela não se
arrasta. A altura muda pelo `PuxadorDeAltura`, uma faixa de 6 px na borda de cima da casca: a base
fica presa, então só o topo sobe ou desce. `AlturaNoLimite` prende entre `AlturaMinima` (445) e a
área de trabalho, e a altura escolhida vai para `UserAppSettings.AlturaDaConversa` ao soltar. A
largura é fixa. Sem moldura do Windows, `ResizeMode` não daria borda nenhuma, daí o `Thumb`.

### Ghosting: some ao perder o foco

A conversa se esconde quando o usuário vai fazer outra coisa. `Window_Deactivated` agenda
`EsconderSeOFocoSaiuDoAib` com prioridade `ApplicationIdle`: no instante do `Deactivated` a janela
que recebeu o foco ainda não se declarou ativa, e perguntar na hora responderia sempre "ninguém do
AIB está ativo". Se o foco foi para outra janela do próprio AIB (o painel lateral, um diálogo), a
conversa fica. Se foi para fora, `EsconderTudo` esconde a conversa **e** o painel: esconder a dona
não esconde as janelas que ela possui.

`Services/ModalGuard.cs` existe por causa disso. Abrir um modal também tira o foco da conversa, e
ela sumia atrás do próprio diálogo que acabara de abrir. Quem abre modal declara com
`using (ModalGuard.Enter())`; enquanto houver algum escopo aberto, `IsAnyModalOpen` é verdadeiro e o
ghosting não dispara. O contador é inteiro, e não booleano, porque um modal pode abrir outro; o
`Dispose` é protegido contra chamada dupla, que deixaria o contador negativo e desligaria o
ghosting para o resto da sessão. Usam o guarda: o cartão de confirmação (enquanto espera
resposta), a exclusão de conversa do histórico, o descarte de conversa de e-mail, o "Ignorar" e o
`MessageBox` do Shadow antigo. A abertura da `SettingsWindow` (`SettingsButton_Click` e
`AbrirConfiguracoesDeEmail`) ainda usa o padrão anterior: desinscreve `Window_Deactivated` antes do
`ShowDialog` e reinscreve depois.

`Esc` no campo de texto esconde a janela.

### Cabeçalho, rodapé e estado vazio

- **Cabeçalho:** nome do personagem ativo em caixa alta, pill de nível (tooltip com XP e barra de
  progresso colorida pela faixa), alternador **Chat / E-mail**, e os botões Painel lateral,
  Configurações, Limpar conversa e Fechar.
- **Título da conversa:** "Nova conversa" até o modelo dar um nome (`OnTitleChanged` →
  `AplicarTitulo`).
- **Contador do rodapé** (`UpdateTokenCounterUI`): custo cru riscado e seta quando há compactação
  (`1.204 > 812`), tokens no prompt, teto do nível e, no OpenRouter, o gasto em dólares. A cor mede
  ocupação (`CorDaOcupacao`): vermelho perto da janela do modelo (a poda de emergência descarta sem
  substituto), laranja acima do teto do nível, neutro no resto. O tooltip (`DicaDoContador`) mostra
  a conta parcela por parcela.
- **Estado vazio:** "Nenhuma conversa ainda" enquanto `MessagesPanel` está vazio
  (`AtualizarEstadoVazio`).
- **Barra de digitação:** a caixa cresce com o texto até metade da altura do quadro
  (`ChatWindow.TetoDaDigitacao`, recalculado no `SizeChanged` do `MainAreaGrid`) e depois rola por
  dentro. Os itens da barra (✦, câmera, microfone, enviar) ficam numa faixa da altura de uma linha
  ancorada embaixo: com a caixa alta, continuam na borda inferior.
- **Personagem:** `ApplyCharacterUI` escreve o nome no cabeçalho, no estado vazio e no placeholder.
  É chamada também ao salvar configurações, e por isso só começa conversa nova quando o personagem
  **mudou**: a alma (SOUL) está no prompt de sistema, e continuar com outra alma misturaria duas
  vozes no mesmo histórico.

### O balão da IA só aparece completo

Decisão do dono: a resposta **não** é desenhada em streaming. `SendButton_Click` consome o
`IAsyncEnumerable<ChatStreamItem>` de `ConversationService.StreamResponseAsync` e acumula o texto em
silêncio. O Markdown é reconstruído inteiro a cada atribuição (`ZerarMargemDoDocumento` explica), e
texto brotando palavra a palavra faz o balão mudar de tamanho a cada quadro.

O que aparece durante o turno:

| Item do stream | Efeito na tela |
|---|---|
| `Thinking` | Mostra os três pontos (`AddTypingIndicator`). É o primeiro sinal real de que há token saindo. |
| `Text` | Acumula em `fullText`; mostra os três pontos se ainda não estiverem. Se o acumulado já tem a marca `⁂` (`QuebraDeFala`), o que veio antes dela vira balão na hora. |
| `ToolStarted` / `ToolFinished` | Alimenta a `ToolChainView` e o registro de ações (`RegistrarAcao`). O `remember` fica fora da cadeia: ao terminar, vira a linha discreta "✦ Ellen lembrará disso…" (`AddAvisoDeMemoria`, o fato no tooltip). |
| `SegmentBreak` | A IA terminou uma fala e vai usar ferramenta: a fala acumulada vira balão e a cadeia seguinte começa abaixo dela. |

Os três pontos somem depois de 3 s sem novidade (`idleTimer`) e voltam com o próximo pedaço. No
`finally`, a última fala vira balão; se o turno inteiro não teve fala, entra "*Ação executada com
sucesso.*". Erros viram balão com "❌ **Erro:**". Com a janela escondida ou sem foco, o fim do
turno gera uma notificação da bandeja (`App.ShowNotification`) com até 200 caracteres. O evento
`TurnoConcluido` entrega o texto final ao orbe.

**Falas separadas.** A persona pode dividir uma resposta em mensagens (a reação a uma piada, depois
o resultado) com uma linha só com `⁂`; a instrução está na alma do personagem (a Ellen, §3.D), não
no prompt de sistema. `QuebraDeFala.Dividir` faz um balão por parte, no streaming, no `finally` e ao
restaurar uma conversa gravada. A marca fica no histórico, para o modelo ver o próprio padrão, e
sai do texto do orbe e da notificação (`QuebraDeFala.Limpar`). Símbolo raro de propósito: `---` e
linha em branco são Markdown comum e partiriam uma explicação no meio.

Enquanto o turno roda, o botão de enviar vira **parar** (`_isSending` → `CancelGeneration`).

### Bolhas

- Usuário: `AddUserBubble`, estilo `UserBubble` de `Controls.xaml` (degradê `PrimaryBrush`, sem
  sombra — O5). O estilo é buscado com `FindResource`, nunca `Resources[...]`: o indexador olha só o
  dicionário da janela e devolvia `null` depois que o estilo foi para o tema global.
- IA: `AddAgentBubble`, um `MarkdownViewer` (Markdig.Wpf) com o pipeline `Ui/MarkdownPipelines`.
  A casca (`CascaDaIa`) põe a sombra numa camada **irmã** do conteúdo, e não mãe: um `Effect`
  rasteriza a subárvore e desliga o ClearType, e o texto ficava borrado. `Ui/ShrinkWrap` com
  `Ui/FlowDocumentMeasure` faz a bolha encolher até o texto, porque o `FlowDocument` aceita toda a
  largura oferecida.
- Cada bolha mora numa linha (`NovaLinha`) com a hora do lado oposto, visível só no hover. Largura
  máxima: 74% da lista. Entrada com deslize de 20 px e fade (`AnimateBubbleIn`).
- Para remover uma bolha use `RemoverLinha`, que sobe até a linha: `Children.Remove` na bolha falha
  em silêncio porque ela não é filha direta do painel.

### Faixa de espera e compactação

A faixa de sistema (`StatusBar`) tem três usos:

1. **Aquecimento** (`HandleWarmupState`): campo e botão desabilitados, com a frase de aquecimento.
2. **Espera longa** (`MostrarEspera` / `EsconderEspera`): anel girando, tempo decorrido atualizado
   a cada segundo e um botão de ação. Usada pela compactação: `ConversationService.CompactacaoAndou`
   chama `AnunciarCompactacao` ("Compactando a memória — capítulo N"), com o botão "Interromper"
   (`InterromperCompactacao`); `CompactacaoAcabou` fecha. Ela **não** some sozinha: uma espera que
   desaparece com o trabalho rodando volta a parecer travamento.
3. **Aviso curto** (`MostrarFaixa`): frase que some em 6 s, para o que não é fala da IA e não pode
   virar bolha (por exemplo, "Não consegui reler este e-mail.").

`Girar` é o único lugar do giro de 0,9 s; a faixa e o botão "Recarregar" do e-mail usam o mesmo.

### Comandos de barra

Tratados em `SendButton_Click` antes de qualquer chamada ao modelo. O popup de sugestões
(`_slashCommands`) oferece só os comandos que existem:

| Comando | Faz |
|---|---|
| `/compact` | `ForcarCompactacaoAsync`: fecha um capítulo agora e promove um ato se couber. A resposta, inclusive a recusa, vem do serviço. Trava a entrada como um turno (`RodarComandoDeMemoria`). |
| `/memoria` ou `/memória` | `MemoriaEmTexto`: a conta da memória, capítulo por capítulo, num bloco de código. Não chama o modelo. |
| `/skills` | Lista as habilidades nativas e as instaladas (`ShowSkillsList`). |
| `/unlock_level N` | Trapaça de desenvolvimento (N de 1 a 9). Fica fora do popup de propósito. |

Teclas no campo: `Enter` envia; `Shift+Enter` e `Ctrl+Enter` quebram linha; `Ctrl+V` com arquivos
copiados no Explorer cola os caminhos entre aspas; com o popup aberto, setas escolhem e
`Enter`/`Tab` completam.

### Cartão de confirmação na conversa

`App` conecta `ChatConfirmationPrompt` a `ChatWindow.PerguntarConfirmacaoAsync`. Antes dessa
ligação, e depois que a janela morre, o prompt recusa por padrão. `PerguntarConfirmacaoAsync`:

1. Cria o `ConfirmCardView` na thread de interface, põe o chip da cadeia em "Aguardando" e mostra a
   janela se ela estiver escondida.
2. Espera a resposta dentro de `ModalGuard.Enter()`, para a conversa não sumir com a pergunta
   pendente.
3. Tira o cartão da conversa ao decidir. O que foi autorizado vira ícone na cadeia e linha no
   histórico de ações; a recusa vira ícone vermelho.

`DescartarConfirmacaoPendente` responde "recusado" quando a conversa é limpa, trocada ou fechada:
sem isso a ferramenta esperaria para sempre.

### Nova conversa, histórico e painel

`NovaConversa` (botão Limpar) descarta confirmação pendente, limpa as bolhas, chama
`ResetHistory` (que **arquiva** a conversa anterior) e limpa o registro de ações e os arquivos no
contexto, que são da conversa. `RecuperarChat` soma uma conversa antiga à atual como contexto;
`AbrirChat` substitui a atual pela antiga, falando cada fala no seu papel. As duas passam pelo painel
lateral. `RestaurarConversaGravada` é o caminho único de restauração, usado também pela reabertura
de e-mail.

### Botões de visão e voz

`VisionButton_Click` chama `OcrService.ExtractTextFromActiveScreenAsync` e `VoiceButton_Click` usa
`VoiceService`. Os dois serviços são esqueletos: o OCR devolve texto vazio (a conversa mostra "Não
encontrei texto legível na tela.") e o `VoiceService` não dispara transcrição. Os botões estão na
barra, mas não entregam o recurso.

## ChatWindow.Email.cs — o modo e-mail

Parte da mesma classe, em arquivo separado por tamanho. O alternador Chat/E-mail troca o conteúdo
da mesma linha da grade; a janela não muda de tamanho. São três estados, e só
`AplicarEstadoDoModo` decide qual está na tela, derivado do alternador e de `_emailEmLeitura`:

| Estado | Mostra |
|---|---|
| Convite | Nenhuma caixa com senha no cofre (`HaCaixaDeEmailPronta`): "Nenhum e-mail conectado" e o botão "Configurar e-mail". |
| Lista | `MontarCaixaDeEntrada`: os `MailListItem` vindos do vigia, já ordenados por urgência, e a linha "N e-mail(s) · N urgente(s) · N tokens". Barra de entrada e rodapé somem. |
| Leitura | O cabeçalho vira caminho "Caixa de entrada › assunto", com "Abrir no cliente", "Recarregar" e "Descartar esta conversa". A conversa é a mesma do chat. |

A janela não conhece o vigia. O `App` injeta três delegados: `FonteDeEmails` (último digest),
`IgnorarEmail` e `RecarregarEmail`.

- **Abrir com a IA** (`AbrirEmailNoChat`): se já existe conversa sobre aquela thread, volta a ela
  sem chamar o modelo (`RetomarConversaDoEmail`). Senão, o turno começa com
  `EnquadramentoDoEmail`: remetente, assunto, data, urgência e resumo — nunca o corpo. Na tela, esse
  turno aparece como o cartão "E-MAIL EM CONTEXTO" (`CartaoDoEmail`), e não como bolha do usuário
  (`_bolhaDoTurno`). O corpo só entra se o modelo chamar `mail_read`.
- **Ignorar** e **Descartar conversa** pedem confirmação no `ConfirmDialog` e dizem o que fica e o
  que sai. Ignorar não toca o servidor.
- **Recarregar** relê uma conversa no servidor e refaz o resumo; custa uma chamada ao modelo, por
  isso o botão desabilita e o ícone gira.
- A engrenagem e "Configurar e-mail" abrem a `SettingsWindow` direto na página E-mail. O painel
  lateral, que é `Topmost`, é baixado enquanto o diálogo está aberto; senão o diálogo abriria atrás
  dele.

## ConfirmCardView — o cartão de confirmação

Substitui a janela modal: a pergunta aparece na coluna da IA, logo abaixo da ação que a provocou.
`Preencher(CommandConfirmationContext)` monta título e consequência por ferramenta:

| Ferramenta | Título |
|---|---|
| `write` | "Criar este arquivo?" ou "Sobrescrever este arquivo?" (o `WriteFileTool` escreve `CRIAR ` no início do alvo quando o arquivo não existe) |
| `edit` | "Editar este arquivo?" |
| `shell` | "Executar este comando?" |
| `skill` | "Executar esta habilidade?" ou "Ler o manual desta habilidade?" |

O cartão mostra a pill "Ação destrutiva", o alvo literal, a prévia do conteúdo ou do trecho
trocado (`ScriptBody`) e, quando cabem, três avisos: motivo de bloqueio, "Há texto de um e-mail
nesta conversa..." e o aviso de caminho fora das pastas sem confirmação. Botões: **Permitir**
(`DangerFilledButton`) e **Recusar**. A caixa "Sempre permitir este comando" só aparece para `shell`
e some quando há e-mail no contexto.

Regras que o código garante: o foco nasce em "Recusar" (Enter sem ler não executa nada); a
resposta é negativa por omissão (`Descartar` devolve recusa); decidir trava os botões.

## ToolChainView — a cadeia de ações

Uma cadeia por **fala** da IA, e não por turno: a `ChatWindow` fecha a cadeia no `SegmentBreak`,
e as ferramentas anunciadas pela fala seguinte abrem outra logo abaixo do balão.

- `Iniciar` mostra o chip em curso com spinner, rótulo "Executando", nome e argumento. Ferramentas
  paralelas dividem o mesmo chip, com contagem.
- `Aguardar` troca o rótulo para "Aguardando" enquanto o cartão de confirmação está na tela.
- `Concluir` põe a conclusão numa fila; `DesenharProxima` desenha uma a cada 500 ms
  (`TempoMinimoVisivel`). O atraso é só de desenho: a ferramenta já rodou.
- Sucesso vira ícone de 22 px na trilha (`Ui/ToolIcons`, que desenha a **operação**). Falha deixa o
  chip vermelho com "Falhou" ou "Recusado" e a mensagem; ela só recolhe para a trilha quando a
  próxima ação começa ou o turno acaba (`RecolherFalhaPendente`).
- O tooltip de cada ícone é configurado por `Ui/TooltipDeAcao`.

## SidePanelWindow — o painel lateral

Segunda janela de 300×520, e não um painel embutido: embutido, ele dividia a largura e encolhia a
conversa ao abrir. `ChatWindow.PosicionarPainel` o encosta à direita da conversa, alinhado pela
base, e ele a acompanha em `LocationChanged`. Nem a conversa nem o painel se arrastam: a conversa é fixa,
centrada acima da barra de tarefas (`RepositionWindow`).
O painel entra na mesma regra de foco; fechar pelo X dele registra que o usuário não quer que ele
volte sozinho (`FechadoPeloUsuario`).

| Aba | Conteúdo |
|---|---|
| Histórico de chats | Conversas arquivadas. Clique: abre a conversa (substitui a da tela). Botão direito: "Recuperar contexto na conversa atual", "Abrir conversa", "Excluir". A conversa em andamento aparece marcada e não responde. |
| Arquivos no contexto | Arquivos lidos ou criados pela IA e os anexados em "+ Adicionar" (`ContextService`). |
| Histórico de ações | `ActionLogService.Entries`, com tooltip em bloco (resultado, saída bruta, antes e depois). |

## ShadowAssistantWindow — o orbe

Opt-in (`ShadowAssistantEnabled`, falso por padrão). Ligado pela bandeja ("✦ Shadow no desktop")
ou pela página Shadow das configurações; as duas portas terminam em `App.SincronizarOrbe`. O orbe
fica no monitor primário, centralizado, 45 px acima da barra de tarefas
(`Services/ScreenAnchorService`).

- **Some com a conversa na tela** (`DeveAparecer(ligado, conversaNaTela)`). O `App` escuta
  `IsVisibleChanged` da conversa, o que cobre atalho, bandeja e perda de foco. Ele é escondido, não
  fechado, para não perder as falas.
- **Clique** transforma o círculo em barra de texto (`AbrirBarra`, morph de 0,28 s; o raio segue a
  altura por `Ui/AlturaParaRaioConverter`, porque WPF não anima `CornerRadius`). `Esc` ou perder o
  foco volta ao círculo.
- **Enviar** pela barra dispara `MensagemEnviada`; o `App` chama
  `ChatWindow.AbrirComMensagem(texto, mostrarJanela: false)`. O turno inteiro roda na conversa,
  escondida. O orbe só mostra o texto final (`ResponderTurno`): com a barra aberta, a resposta
  entra na pilha de falas (`Ui/FalaDoOrbe`); fechada, ela é enfileirada e o orbe pulsa.
- **Pulso** (`Pulsar`): anel lilás, ou vermelho quando urgente. Não expira sozinho e não abre
  balão: o texto só aparece quando o usuário clica.
- **Passo do turno** (`ChatWindow.PassoDoTurnoMudou` → `MostrarEstado(passo, ferramenta)`): o
  anel gira enquanto o turno anda, e o rótulo do passo vai para o tooltip. Com uma ferramenta
  rodando (ou esperando no cartão), o **ícone dela** (`ToolIcons`, o mesmo do chip da cadeia) entra
  no lugar do ✦; pensando, volta o ✦. Um símbolo só na célula, por prioridade: caixa de entrada da
  varredura, depois a ferramenta, depois o ✦ (`MostrarSimbolo`). Antes o orbe só girava o anel, e
  com a janela oculta não dava para saber o que ela estava fazendo. O fim da varredura não apaga o
  anel de um turno em curso.
- **E-mail**: o vigia chama `ComecarAProcessarEmail` (ícone de caixa de entrada no lugar do ✦),
  `TerminarDeProcessarEmail` (frase do digest e itens) e `PararDeProcessarEmail`. A fala do digest
  leva até `TetoDeEmails` itens (`ShadowMailPreviewCount`); o excedente vira uma linha de texto.

### Fala por iniciativa (lembretes)

A persona também fala sem ter sido chamada. `App.IniciarAgenda` roda um `DispatcherTimer` de 20 s
que retira os lembretes vencidos (`Lembretes.Retirar`) e os entrega por `FalarPorIniciativa`; o
primeiro passe é no arranque, para os que venceram com o app fechado, que chegam com "(Era para
15:00.)" (`Lembretes.Atrasado`). Com turno em andamento (`ChatWindow.Ocupada`), espera a batida
seguinte.

`FalarPorIniciativa` põe a fala na conversa (`ChatWindow.ReceberIniciativa`: balão e
`AppendAssistantText`, para a resposta do usuário continuar o assunto) e, com a conversa fora da
tela, avisa pelo orbe (`EnfileirarFala`: pulso, texto só no clique) ou, sem orbe, pela bandeja.
Lembrete não chama o modelo: o texto foi escrito pela persona no pedido.

## MailListItem

O mesmo item nas duas telas (orbe e modo e-mail). As diferenças são propriedades
(`CornerRadius`, `RealceLilas`, `EscalaDeJanela`, `MostrarMetadados`, `TemConversa`,
`PodeIgnorar`), não arquivos. Clique no item expande (acordeão, um aberto por vez); os botões do
corpo disparam `PediuAbrirComIA`, `PediuAbrirNoCliente`, `PediuIgnorar` e
`PediuDescartarConversa`. Quem decide o que fazer é a tela dona. `AbrirNoNavegador` é o único
caminho para sair do app e ignora URL vazia. Cor e rótulo de urgência vêm de
`Ui/UrgenciaConverter`; o estado da conta, de `Ui/ContaDeEmailConverter`.

## FirstRunWindow — o primeiro arranque

Assistente em cinco passos, 40% × 80% da tela principal:

1. Boas-vindas e aviso de fase Beta.
2. "Escolha sua Inteligência": personagens lidos de `~/.AIB/character/*/info.json`.
3. "Motor de Inteligência": **Ollama (Local)** ou **OpenRouter (Nuvem)**.
   - Ollama: lista os modelos instalados; se o Ollama não responde, oferece "Tentar novamente" e o
     modelo padrão (`ProvedoresDeIa.ModeloPadraoDoOllama`).
   - OpenRouter: chave (validada por `ProvedoresDeIa.ChaveValida` só no LostFocus, Enter ou Salvar)
     e modelo, do catálogo filtrado para modelos que aceitam ferramentas e respondem na hora
     (`ModeloDoOpenRouter.ServeParaConversa`: as variantes `:batch`, que respondem em até 24 h, ficam fora).
4. "Níveis de Autonomia" e 5. "Seu Crescimento": texto explicativo.

"Próximo" no passo 3 só habilita com modelo escolhido (e chave válida, no OpenRouter). Ao
concluir, a chave vai para o cofre DPAPI (`CredentialService.StoreCredentialAsync`), nunca para as
configurações; a janela do modelo vem do catálogo quando ele informa; a triagem de e-mail passa a
usar o mesmo provedor e modelo; a auditoria grava só os quatro últimos caracteres da chave. "Sair"
fecha com `DialogResult = false`, e o `App` encerra.

Esta janela ainda tem paleta própria em `Window.Resources` (hex literais) e não usa os tokens.

## SettingsWindow — configurações

Implementa `tela-configuracoes.html`. A lista de campos e os rótulos são normativos. Menu lateral
com oito páginas (`PaginaDeConfiguracoes`); trocar de página só troca `Visibility`, e o estado
"sujo" é global: "Salvar" grava tudo o que mudou em qualquer página e só habilita quando algo mudou.
A janela abre direto numa página pelo parâmetro do construtor (é o que o modo e-mail usa).

| Página | O que configura |
|---|---|
| Identidade | Personagem ativo (pastas de `~/.AIB/character`). |
| Conexão LLM | Provedor. Cada provedor tem perfil próprio (`PerfilDeProvedor`) guardado em `_perfis`: trocar e voltar não perde nada. Ollama: endereço, modelo, keep-alive. OpenRouter: chave ("Alterar"), modelo (catálogo com janela e preço), "Só provedores que não guardam dados", "Provedor preferido". Comuns: janela de contexto, raciocínio, orçamento do nível (calculado, só leitura), "Enviar System Prompt a cada requisição". |
| E-mail | Contas (adicionar com endereço e senha de app, principal, trocar senha, zerar leitura, remover), janela de leitura em dias, tempo limite por caixa, dias de diário da triagem, provedor e modelo da triagem, raciocínio na triagem. |
| Shadow | Mostrar o orbe, deixar o Shadow tratar os e-mails, e-mails mostrados na fala. |
| Ferramentas | Ferramentas inteligentes, confirmar comandos perigosos (floor list), máximo de etapas por turno, pastas sem confirmação, autorizações "sempre permitir" da sessão ("Esquecer todas"). |
| Memória | Quem escreve a memória, gatilho de compactação, turnos e tokens por capítulo, teto de capítulos por ato, fatia de memória, esconder resultados antigos, guardar a fala junto da ferramenta, pasta da conversa. |
| Avançado | Devolver o raciocínio ao modelo. |
| Logs | Logs detalhados no console, registro de execução, diário da compactação, "Simular o primeiro envio", pasta dos logs. |

Cada página tem "Restaurar padrões desta página". O rodapé tem "Reset de Fábrica" (apaga o cofre
inteiro, o histórico de chat, as preferências e o conteúdo de `memory/` e `email/` — preservando
`raw.jsonl` e `facts.md` como `.bak`, ver [07](07-configuracoes-e-dados.md) —, e encerra o app),
"Cancelar" (pede confirmação se há
mudança) e "Salvar". Depois de salvar, a janela chama `ChatWindow.ApplyCharacterUI` e
`App.AplicarEstadoDoOrbe`, para as mudanças valerem na hora.

## ConfirmDialog

Confirmação modal para o que não acontece dentro de um turno: excluir conversa do histórico,
descartar conversa de e-mail, ignorar e-mail, remover conta, reset de fábrica, descartar alterações
das configurações. Substitui o `MessageBox` do sistema, que ignora a paleta. O caminho de uso é
`ConfirmDialog.Perguntar(dono, pergunta, consequencia, ferramenta, alvo, dica)`, que devolve `true`
só no clique em "Permitir"; X, `Esc` e "Recusar" devolvem `false`. Quem chama de dentro da conversa
envolve a chamada em `ModalGuard.Enter()`.

## Código estacionado

- **`ContextSidebar`** está sem chamador **de propósito**. O painel vivo é a `SidePanelWindow`.
  O controle guarda o desenho das abas que ficaram fora da spec (Lembretes, Memória Recente,
  Arquivos Recentes, Shadow) para revisão futura. Usa cores literais. Não religar sem migrar para
  os tokens.
- **`ShadowWidget`** e o botão `BtnToggleShadow` da conversa pertencem ao Shadow antigo de captura
  de tela. O botão fica sempre recolhido (`ApplyShadowAssistantSetting`), e o
  `ShadowAssistantService` que o alimentaria é um esqueleto sem eventos.

## Tema: tokens e estilos

Há dois temas, **Escuro** (o original, o das specs) e **Claro**, mais a opção "Seguir o Windows"
(`HKCU\…\Themes\Personalize\AppsUseLightTheme`). A escolha é `UserAppSettings.Tema`, na página
Identidade das configurações, e **vale a partir do próximo arranque**: os recursos são
`StaticResource`, resolvidos quando cada tela nasce. Trocar ao vivo exigiria converter centenas de
referências e toda bolha desenhada por código, para uma escolha que se faz uma vez.

As cores moram num arquivo por tema, com as **mesmas chaves**: `Themes/Cores.Escuro.xaml` e
`Themes/Cores.Claro.xaml`. O `App.xaml` nasce vazio; `App.OnStartup`, logo depois de ler as
configurações e antes de qualquer janela, chama `Ui/Tema.Carregar`, que mescla o arquivo de cores
do tema e, DEPOIS, `Themes/Controls.xaml` (que mescla `Themes/Tokens.xaml`, com o que não muda com o
tema: raios, fontes, medidas). A ordem é o que faz os estilos acharem as cores. O orbe segue o tema
como as outras janelas — mesclar as cores escuras só nele não funciona, porque os estilos do
`Controls.xaml` resolvem as cores contra o App. `TemasTests` cobra as mesmas chaves nos dois
arquivos e monta as janelas no claro; com `AIB_UI_TEMA=Claro` e `AIB_UI_PNG=1` a suíte salva as
telas no claro.

O claro é derivado do escuro: as camadas **brancas** translúcidas sobre o vidro escuro viram
**pretas** translúcidas sobre o vidro claro, os textos escurecem, e lilás, verde, vermelho, azul e
ouro usados como texto ficam um tom abaixo para ter contraste. Os degradês (`NeonBrush`, a marca;
`PrimaryBrush`, a bolha do usuário com texto branco) e o vermelho de ação destrutiva são iguais nos
dois.

**`Themes/Cores.Escuro.xaml` + `Themes/Tokens.xaml`** são a conversão mecânica do `§1` das specs. Grupos:

| Grupo | Exemplos |
|---|---|
| Superfícies | `GlassBrush` (#E6121216, o "vidro fumê": cor sólida a 90%, sem blur), `SurfaceCardBrush`, `SurfaceInputBarBrush`, `SurfaceCodeBrush` |
| Bordas | `BorderCardBrush`, `DividerBrush`, `AccentBorderBrush`, `DangerBorderBrush` |
| Texto e cores de estado | `TextStrongBrush` a `TextMutedBrush`, `AccentLilacBrush`, `SuccessBrush`, `DangerBrush`, `WarnBrush` |
| Preenchimentos | `AccentFill08Brush` a `AccentFill14Brush`, `DangerFill*`, `WarnFill10Brush` |
| Gradientes | `NeonBrush` (roxo, azul, magenta, laranja: a moldura), `PrimaryBrush` (bolha do usuário, Salvar, enviar, switch ligado), `InputFrameBrush` |
| Sombras | `WindowShadow`, `CardShadow`, `TooltipShadow`, `SendGlow`, `OrbShadow`, `BarShadow` |
| Raios | `RadiusWindowOuter` 20, `RadiusBubble` 18, `RadiusCard` 12, `RadiusControl` 10, `RadiusIconBtn` 8, `RadiusPill` 20 |
| Tipografia | `MonoFontFamily`; tamanhos `FontSizeHeaderTitle` 20 a `FontSizeStamp` 10.5. Família de UI é o Segoe UI padrão. |
| Geometria | `HeaderHeight`, `IconButtonSize`, `InputHeight`, `ControlHeight`, `ControlColumnWidth` / `ControlColumnGrid`, `IconTrackMaxWidth` |

`ControlColumnGrid` existe porque `ColumnDefinition.Width` não aceita `Double`: ligar o token
numérico ali estoura em tempo de execução, não de compilação.

**`Themes/Controls.xaml`** tem os estilos nomeados (`UserBubble`, `SendButton`, `IconButton`,
`CloseIconButton`, `ControlTextBox`, `ControlTextBoxMono`, `ControlTextBoxLista`,
`ControlPasswordBox`, `ControlComboBox`, `InlineButton`, `OutlineButton`, `PrimaryButton`,
`DangerOutlineButton`, `DangerFilledButton`, `Switch`, `FieldLabelText`, `FieldHelpText`,
`PillBorder`, `PillText`, `AcaoDeCabecalho`, `CaminhoDeVolta`) e os estilos implícitos de
`ScrollBar`, `ToolTip`, `ContextMenu`, `MenuItem` e `Separator`, que valem para o app inteiro.

### A regra: token, não hex

Nenhuma cor literal nos arquivos de View — além de sair de sincronia, ela não muda com o tema. Antes dos tokens, `#9B51E0` estava escrito 27 vezes
em 6 XAMLs. Em XAML use `{StaticResource Chave}`. Em código, use `FindResource("Chave")` quando há
elemento à mão, ou `Ui/PincelDoTema.De(chave, hexDeReserva)` em conversores e elementos montados
fora do XAML. O hex passado a `PincelDoTema` é só reserva (ensaio sem `App`, outra thread,
dicionário ausente) e tem de ser o mesmo do token.

### Utilitários de `Ui/`

| Arquivo | Para quê |
|---|---|
| `PincelDoTema` | Pincel do tema a partir de código, congelado, com reserva |
| `UrgenciaConverter` | Cor, fundo e rótulo de urgência de e-mail, de um mapeamento só |
| `ContaDeEmailConverter` | Cor do ponto e tooltip do estado de uma caixa de e-mail |
| `AlturaParaRaioConverter` | Raio = metade da altura; é o que anima o morph do orbe |
| `MaiusculaConverter` | Rótulos em caixa alta sem alterar o dado |
| `MarkdownPipelines` | Pipeline Markdig único das bolhas da IA (quebra de linha simples vira quebra) |
| `ShrinkWrap`, `FlowDocumentMeasure` | Bolha da IA com a largura do conteúdo |
| `LetterSpacing` | `letter-spacing` das specs em `TextBlock` |
| `ToolIcons`, `FileTypeIcons` | Ícones de operação (cadeia) e de tipo de arquivo (painel) |
| `TooltipDeAcao` | Abertura e duração do tooltip das ações |
| `FalaDoOrbe` | Falas da pilha do orbe (`FalaDoUsuario`, `FalaDaIA`) |

Regras de identidade visual: [`Regras de Identidade/VISUAL.MD`](../../Regras%20de%20Identidade/VISUAL.MD).
