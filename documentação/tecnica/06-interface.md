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
  ocupação (`TokenReport.PincelDaOcupacao`): vermelho perto da janela do modelo (a poda de emergência descarta sem
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
e `browser`, e some quando há e-mail no contexto. Em botão do navegador que decide algo
(`SempreSegurando`) ela diz "Sempre permitir este botão neste site (segure 5 s)" e só marca com o
clique segurado por 5 s (contagem no rótulo; soltar antes zera; Espaço não marca).

Regras que o código garante: o foco nasce em "Recusar" (Enter sem ler não executa nada); a
resposta é negativa por omissão (`Descartar` devolve recusa); decidir trava os botões.

## ToolChainView — a cadeia de ações

Uma cadeia por **fala** da IA, e não por turno: a `ChatWindow` fecha a cadeia no `SegmentBreak`,
e as ferramentas anunciadas pela fala seguinte abrem outra logo abaixo do balão.

- `Iniciar` mostra o chip em curso com spinner, rótulo "Executando", nome e argumento. Ferramentas
  paralelas dividem o mesmo chip, com contagem.
  O argumento vem de `ArtifactExtractor.ResumirParaTela`: igual ao resumo da memória, menos no
  navegador, onde a ref do elemento vira o nome dele (`clicar botão "OK"`, por `NomesDeElemento`)
  e a ação sai em português. O nome é texto da página e fica só na tela: o resumo que a memória
  guarda (`ResumirArgumento`) continua com a ref.
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
  foco volta ao círculo. A janela não muda de tamanho no morph: a linha da casca tem altura fixa
  (56) e a pilha de falas, fechada, fica `Hidden` em vez de `Collapsed`, surgindo e sumindo por
  opacidade no tempo da barra (`MostrarPilha`). Com `SizeToContent`, cada mudança de altura era
  um redimensionar e reposicionar da janela, visível como salto. Ao fechar, o que estava digitado e não foi enviado fica no campo para a próxima
  abertura (só enviar o esvazia).
- **Contador de tokens** (`MostrarTokens`), só com a conversa própria do orbe e com a barra
  aberta: abaixo da barra, à direita, como no rodapé da janela. O texto é o mesmo do rodapé
  (`Ui/ContadorDeTokens`): custo cru riscado e seta quando há compactação, contexto, teto do
  nível e gasto, na mesma cor de ocupação (`TokenReport.PincelDaOcupacao`). Como flutua sobre
  o desktop, leva o halo `FloatingTextShadow` (escuro no tema escuro, claro no claro). A dica
  traz capítulos e atos, o poupado e o gasto. `App.LigarOrbe` o alimenta pelo
  `OnTokenCountChanged` da conversa do orbe.
- **Parar:** com um turno rodando (`EmTurno`, pelo passo anunciado), o botão de enviar vira o
  quadrado vermelho de parar e dispara `ParadaPedida`; `App.LigarOrbe` cancela o turno de quem
  estiver rodando (`ConversaDoOrbe.Parar` ou `ChatWindow.PararTurno`). A barra recebe o que já
  tinha sido dito, ou "Parei.". Enquanto o turno roda o Enter não envia: o texto fica no campo.
- **Enviar** pela barra dispara `MensagemEnviada`, e o turno roda na **conversa do orbe**
  (`Services/ConversaDoOrbe`), separada da janela: um `ConversationService` em sessão fixa
  (`memory/shadow`, fora de `sessions/`: é uma só), reaberto de onde parou no arranque, fora do `chat_history.json` e sem
  título, com laço de agente próprio. As duas conversas se encontram só nos fatos duráveis; os
  guardas de e-mail do registry são SOMADOS (o mais restritivo vale). O orbe mostra o passo
  (`PassoMudou`) e o texto final (`Respondeu` → `ResponderTurno`): com a barra aberta, a
  resposta entra na pilha de falas (`Ui/FalaDoOrbe`); fechada, ela é enfileirada e o orbe pulsa.
  A fala da IA é desenhada em Markdown pelo mesmo visualizador da janela de chat
  (`Ui/VisorDeMarkdown`, usado no orbe pelo controle `Ui/TextoDaIA`): interpretador, cores de
  código e margem moram num lugar só. O fundo do balão continua sendo o vidro (`GlassBrush`) e
  não o `SurfaceCardBrush` da janela, porque o balão flutua sobre a área de trabalho.
  No arranque (e ao religar o orbe), `LigarOrbe` devolve à pilha as últimas 12 falas do contexto
  vivo (`ConversationService.UltimasFalas` → `RestaurarFalas`), escondidas até a barra abrir; o
  que já virou capítulo não volta como bolha.
  A resposta de um turno da JANELA não entra nessa pilha, nem com a janela fechada no meio do
  turno: o orbe só acende o anel do passo (`PassoDoTurnoMudou`). `OrbeDeveFalar` vale só para a
  ligação sem conversa própria (ensaios).
  Ela se mantém leve pela compactação de sempre e por mais uma: parada há 2 h (`Pausa`), com algo
  novo desde a última, roda `ForcarCompactacaoAsync` — rajadas curtas ao longo do dia quase nunca
  enchem o contexto sozinhas. Lembretes e iniciativas, com orbe, caem nesta conversa.
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

`FalarPorIniciativa`, com orbe, põe a fala na conversa do orbe (`ReceberFalaPropria`, para a
resposta pela barra continuar o assunto) e pulsa (`EnfileirarFala`, texto só no clique). Sem
orbe (só lembrete chega aqui), vai para a conversa da janela (`ChatWindow.ReceberIniciativa`) e,
com ela fechada, para a bandeja. A fala por iniciativa entra no histórico vivo, mas não no
`raw.jsonl`, que só grava dentro de um turno.
Lembrete não chama o modelo: o texto foi escrito pela persona no pedido.

**Iniciativa** (`Services/Iniciativa.cs`, opt-in `IniciativaLigada`): na mesma batida,
`TalvezPuxarAssuntoAsync` vê se a persona pode puxar assunto. `Iniciativa.Impedimento` decide,
sem custo: fora do horário de silêncio (`SilencioInicio`/`SilencioFim`, atravessa a meia-noite);
orbe na tela, conversa fora da tela e sem turno; usuário presente (`Presenca`: entrada nos
últimos 5 min e `SHQueryUserNotificationState` aceitando avisos — sem tela cheia nem "não
perturbe"); 15 min sem conversa; fora da pausa pedida; a última fala dela já respondida; menos
do teto de mensagens do dia (`MensagensPorDia`, configurável de 1 a 20, padrão 6; cada uma é uma requisição paga). Podendo, **sorteia** de 10 em 10 min:
`chance = 1,5% × geral × faixa` (`Iniciativa.Chance`; ~1,3 mensagem num dia de 14 h com tudo
neutro). Sem número fixo de falas por dia e sem intervalo mínimo, por decisão do usuário.

**O algoritmo decide quando; ela só escreve** (decisão do usuário). Acertando o sorteio, o código
escolhe o **gancho** (`Iniciativa.EscolherGancho`): uma pendência de assunto da memória da
conversa do orbe ou um fato "sobre o usuário", sorteados com o mesmo peso — ela não está ajudando
no trabalho naquela hora, então a pendência não vale mais que o resto —, evitando os 5 usados por
último; sem nenhum, "puxe um assunto para conhecer melhor". O gancho é o que evita o "oi, como
vai?" de quem foi mandado falar sem assunto.

`ConversationService.EscreverIniciativaAsync` chama o modelo fora de banda (sem ferramentas,
temperatura 0,8, até 160 tokens) com um papel, não uma decisão (`MaterialDaIniciativa`): a
**alma inteira** — decisão do usuário: o sorteio controla quantas vezes ela é chamada e toda
chamada vira mensagem, então os ~4.000 tokens só são pagos quando ela fala; sem alma, a persona
curta do `info.json` (nome, descrição, personalidade, fala de exemplo) —, "Você está sem fazer nada.
Carlo está online. Vocês conversaram pela última vez há 3 h. Você decide mandar uma mensagem para
Carlo.", como terminou a última iniciativa (`UltimoDesfecho`), o gancho, as últimas 4 falas e as
últimas 5 mensagens por iniciativa ("não repita", `Recentes`, sobrevive ao arranque). Sem saída
NADA: o pedido custa ~4.500–5.000 tokens (a alma é quase tudo) e toda chamada é uma mensagem. Com o geral ≥ 1,5, ganha
"vocês têm conversado bastante ultimamente": o afeto aparece na voz, não só na frequência.

O nome vem de `NomeDoUsuario` (Configurações > Identidade, e o primeiro passo da primeira
inicialização); vazio é "o usuário".

**Aprendizado**: multiplicador geral e um por faixa de 2 h, entre 0,2 e 3. Na memória é um
estado só (`EstadoDaIniciativa`); no disco são dois arquivos cifrados (`ArquivoCifrado`, DPAPI):

- `~/.AIB/iniciativa.dat` — o que é do **usuário**: as faixas de horário, a pausa do "agora
  não", o último sorteio e a contagem do dia;
- `~/.AIB/character/<Nome>/vinculo.dat` — o que é da relação com **um personagem** (`Vinculo`):
  o geral, a fala que espera resposta, a conversa espontânea aberta, as mensagens e ganchos
  recentes e o último desfecho.

`Iniciativa.Trocar` grava o vínculo do personagem que sai e carrega o do que entra (ou começa do
zero); o `App` chama a cada batida e a cada mensagem no orbe (`AcompanharPersonagem`). O
`iniciativa.json` em texto claro de antes é lido uma vez, vira o vínculo do personagem ativo e é
apagado.

**Atributos** (`Atributos`, `StatusDosPersonagens`, `Temperamento.De`): cada personagem tem cinco
atributos: quatro de 1 a 5 (tabela abaixo) e o afeto. Os **de fábrica** ficam no `info.json` dele (`Atributos`); os **que valem
agora** ficam no arquivo de status, `~/.AIB/memory/shadow/status.json` (texto claro, editável à
mão). O arquivo **nasce vazio**: o personagem entra na primeira vez em que é o ativo, com os
padrões do `info.json` (sem eles, tudo em 3), e dali em diante vale o que está no arquivo — o AIB
só acrescenta quem falta. O 3 é o comportamento de antes dos
atributos. Arquivo ilegível não é sobrescrito (valem os neutros); a edição passa a valer na
batida seguinte. Não são as estrelas da tela de escolha (`AgentStats`, no `info.json`).

| Atributo | O que muda | 1 | 2 | 3 | 4 | 5 |
|---|---|---|---|---|---|---|
| `Iniciativa` | multiplica a chance base do sorteio | ×0,5 | ×0,75 | ×1 | ×1,3 | ×1,6 |
| `Resiliencia` | fator da fala ignorada / do "agora não" | 0,60 / 0,35 | 0,68 / 0,42 | 0,75 / 0,50 | 0,83 / 0,60 | 0,90 / 0,70 |
| `Constancia` | quanto o geral volta para 1 por dia | 20% | 15% | 10% | 7% | 5% |
| `Curiosidade` | chance de, tendo gancho, pedir para conhecer o usuário | 0% | 10% | 20% | 35% | 50% |

O **afeto** (`Afeto`) é o quinto e tem escala própria, de **-5 a 5**, com 0 de neutro: -5 é o
personagem direto, que evita conversa longa; 5, o expressivo e apegado. Na conta ele é o teto do
fator de conversa, `1,50 + 0,10 × afeto` (`Temperamento.TetoDoAfeto`): 1,00 com -5 (conversa não
o faz puxar mais assunto), 1,70 com +2 (o valor que nasceu com a Ellen), 2,00 com +5. O jeito de
falar muda pelo DESVIO (afeto de agora menos o de fábrica), numa linha "Convivência" do bloco de
contexto do prompt (`ConversationService.LinhaDoAfeto`): nada abaixo de 0,75 de desvio; para cima, um
texto a partir de 0,75 e outro a partir de 1,5; para baixo, um só (o mais seco foi medido e
atrapalhava o relato de ferramenta que falha). Medido em 09/10/2026 — os relatórios `afeto-*`
estão em `AIB.Avaliacao/resultados/`. Com desvio zero o prompt fica byte a
byte o que era. O `AIB.Avaliacao --afeto <desvio>` força o desvio para medir cada texto.

O afeto é o único atributo que **anda sozinho**: a cada iniciativa classificada ou conversa
espontânea encerrada, `Iniciativa.AfetoMoveu` dá o passo e `StatusDosPersonagens.Mover` grava no
arquivo de status — conversa +0,10, resposta rápida +0,03, conversa puxada pelo usuário (2 turnos
ou mais) +0,05, lida sem resposta -0,02, ignorada -0,05, "agora não" -0,15. Fica entre -5 e 5 e a
no máximo 2 pontos do afeto de fábrica do `info.json` (`Atributos.FolgaDoAfeto`).

**Saudade** (`Iniciativa.Saudade`): a chance do sorteio é multiplicada pelo tempo sem contato —
1 até 2 h, 2 com 8 h, 3 com 24 h, e daí não passa. Contato é mensagem do usuário no orbe, turno
concluído na janela (`Iniciativa.Contato`) ou fala dela; fica no vínculo (`ContatoUtc`). O
crescimento é multiplicado pelo afeto, 6% por ponto (`Temperamento.SaudadeDoAfeto`): com um dia,
×2,76 no afeto -2 e ×3,48 no +4. Existe porque, a 1,6% por sorteio, um dia inteiro de orbe na
tela passava sem mensagem quase metade das vezes.

A constância só mexe no **geral**, que é do personagem; as faixas de horário são do usuário e
esquecem sempre 10%. A curiosidade é a única em que o 3 muda algo: antes o pedido de conhecer o
usuário só saía sem gancho nenhum. O desfecho dito à persona vem do que aconteceu, não do fator
(`Desfecho(recusou, respondeu, leu, fator)`): o "agora não" de um resiliente vale 0,70.

 Cada fala é classificada uma vez — 30 min depois da primeira resposta na conversa do orbe, ou
8 h sem resposta (`Classificar`) — e o fator (`Fator`) vai inteiro para a faixa em que ela falou
e pela raiz para o geral:

| O que aconteceu | Fator |
|---|---|
| Conversa (2+ turnos em 30 min) | 1,10 + 0,05 por turno além do 2º, até 1,70 |
| Resposta única longa (≥ 12 palavras) | 1,10 |
| Resposta curta | 1,05 |
| Leu (abriu o pulso, `FalasLidas`) e não respondeu | 0,90 |
| Não abriu em 8 h | 0,75 |
| "Agora não" (`EhRecusa`, na primeira resposta) | 0,50, e pausa de 4 h ou até o fim do dia |

Ele puxar conversa também conta: mensagem dele na conversa do orbe sem fala dela esperando abre
uma conversa espontânea (`ConversaUtc`), fechada 30 min depois de começar. O fator
(`FatorEspontaneo`) vai para a faixa em que ele começou e pela raiz para o geral: 1,02 com uma
mensagem, 1,05 com duas, +0,02 por turno a mais, até 1,20. Só sobe, sobe menos que a resposta a
uma iniciativa (boa parte do que ele manda ali é pedido de trabalho) e não olha "agora não".

A cada dia os multiplicadores voltam 10% para 1 no logaritmo (`Esquecer`). A página Shadow mostra
o resumo (`Iniciativa.Resumo`: geral e a melhor e a pior faixa) e "Zerar aprendizado", que vale na
hora pela instância viva do `App`.

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
| Identidade | Personagem ativo (pastas de `~/.AIB/character`), como te chamar (`NomeDoUsuario`), tema. |
| Conexão LLM | Provedor. Cada provedor tem perfil próprio (`PerfilDeProvedor`) guardado em `_perfis`: trocar e voltar não perde nada. Ollama: endereço, modelo, keep-alive. OpenRouter: chave ("Alterar"), modelo (catálogo com janela e preço), "Só provedores que não guardam dados", "Provedor preferido". Comuns: janela de contexto, raciocínio, orçamento do nível (calculado, só leitura), "Enviar System Prompt a cada requisição". |
| E-mail | Contas (adicionar com endereço e senha de app, principal, trocar senha, zerar leitura, remover), janela de leitura em dias, tempo limite por caixa, dias de diário da triagem, provedor e modelo da triagem, raciocínio na triagem. |
| Shadow | Mostrar o orbe, deixar o Shadow tratar os e-mails, e-mails mostrados na fala, puxar assunto sozinha (iniciativa), máximo de mensagens por dia, horário de silêncio. |
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
