# 02. Interface Visual (UI & Views)

O AIB é construído em **Windows Presentation Foundation (WPF)** (.NET 8). O WPF foi escolhido por fornecer controle em baixo nível da renderização de Desktop do Windows, permitindo `AllowsTransparency="True"`, sombreados vetoriais complexos (`DropShadowEffect`) e suporte fluido a animações nativas.

> **Nota de versão:** o comportamento visual desta camada foi congelado de propósito no refactor da
> camada cognitiva — animações, layout, posicionamento, *ghosting* e o clipboard inteligente estão
> byte a byte como estavam. As únicas correções abaixo são: (a) a `ChatWindow` deixou de ser o
> Composition Root, e (b) a seção 3 descrevia como ativo um modal que não tem chamadores.

## 1. ChatWindow (`ChatWindow.xaml` e `.xaml.cs`)
Esta é a classe vital da interface — e, desde o refactor, **apenas** isso. Ela não é mais o
*Entry Point* nem a dona do ciclo de vida da aplicação: quem constrói os serviços e controla o tempo
de vida é o `App` (`App.xaml.cs`), porque o AIB vive no System Tray e a janela se oculta sozinha ao
perder o foco. Ver doc 01 §1.

### 1.1 Funções Core
- **Inicialização e Posicionamento**: O construtor passou a **receber** `ConversationService` e
  `SettingsService` já prontos (`ChatWindow(conversation, settingsService)`), em vez de dar `new`
  neles; ele apenas assina `OnTokenCountChanged` e `OnWarmupStateChanged` e segue com a montagem
  visual. A janela não usa as bordas do SO. Em vez disso, ela acessa `SystemParameters.WorkArea.Bottom` e `PrimaryScreenWidth` para auto-posicionar sua UI exatos 45 pixels acima da barra de tarefas, sempre fluindo centralizada, imitando nativamente o layout do sistema.
- **Smart Clipboard (`InputBox_PreviewKeyDown`)**: O interceptador global do teclado na caixa de texto. Além de lidar com o envio de mensagens (`Enter` sem `Shift`), ele monitora interações `CTRL+V`. Quando detecta que a área de transferência (`System.Windows.Clipboard`) não possui texto, mas sim um formato `FileDropList` nativo do Windows Explorer, ele coleta e injeta programaticamente os caminhos absolutos desses arquivos envoltos em aspas (ex: `"C:\Docs\arquivo.pdf"`), anulando erros de caminhos com espaço.
- **Animações Fluidas (`AddAgentBubble` e `AddUserBubble`)**: A interface não cospe o markdown cru instantaneamente. Ela instância o `MarkdownViewer`, o empacota num balão estilizado e invoca animações `DoubleAnimation` tanto na propriedade `Opacity` quanto em um `TranslateTransform`. O resultado visual é que a mensagem brota deslizando suavemente de baixo para cima.
- **Inteligência Estática de Loading (`UpdateLoadingState`)**: O streaming letra a letra causa solavancos severos de renderização de markdown em WPF. O AIB contorna isso escondendo o texto, exibindo um balão com pontilhados piscantes ("● ● ●") gerados por keyframes, e revelando o bloco inteiro num fade-in apenas quando o Loop ReAct ou o envio termina de forma definitiva.

### 1.2 UX de Stealth (Ghosting)
- O AIB abraça um perfil incrivelmente invasivo porém silencioso. Se o usuário clicar fora da janela (em um jogo, em outro programa), a interface do chat é sumariamente ocultada (via o evento `.Deactivated`). A janela nunca fica "atrapalhando" o desktop do usuário, operando apenas on-demand, de forma similar ao calendário padrão do Windows 11.

## 2. Configurações (`SettingsWindow.xaml` e `.xaml.cs`)
- A Janela de Configurações permite definir a OpenAI API Key e ajustar o modelo-base utilizado. 
- Ela é bloqueada para instâncias simultâneas.
- **Injeção**: `SettingsWindow` e `FirstRunWindow` agora recebem o `SettingsService` por construtor,
  em vez de instanciar o seu próprio. Existe exatamente **um** `SettingsService` por processo — ele
  mantém cache em memória das settings, e duas instâncias tornariam esse cache incoerente.
- **Level Info Lock**: Informações sobre Limites de Contexto (`Max Tokens`) são renderizados aqui porém estão bloqueados como **Somente-Leitura**. Isso reflete a decisão arquitetural da gamificação implementada: Tokens são recompensas de Nível, não números manipuláveis nas configurações locais.

## 3. Segurança do Terminal (`CommandConfirmationWindow.xaml.cs`) — **INATIVA**

> Esta seção descrevia um funil de confirmação "Zero-Trust" como se estivesse em operação. **Ele não
> está.** Um documento que superestima a postura de segurança é pior do que documento nenhum, então
> aqui vai o estado real.

- A `CommandConfirmationWindow` existe e está implementada — modal, semáforo de reentrância, hop de
  Dispatcher, banner de denylist, preview de corpo de script. Mas o helper `ShowAsync` tem **zero
  chamadores** no projeto inteiro: uma busca por `ShowAsync` só encontra a própria definição.
- **Nenhum comando passa por confirmação.** O `run_command` é executado direto pelo `ToolRegistry`,
  sem funil, sem bloqueio de pipeline e sem janela. Não existe o retorno
  `"Ação Rejeitada pelo Usuário."`, porque não existe rejeição.
- As peças que alimentavam esse fluxo (`CommandService`, `CommandFloorList`, `AlwaysAllowSession`)
  foram **deletadas**. Sobrou o DTO `CommandConfirmationContext`, sem produtor, e a setting
  `ConfirmDangerousCommands`, que hoje não controla nada.
- A ferramenta `run_command` está registrada com `RequiredLevel = 1` — ou seja, disponível desde a
  primeira mensagem de um usuário novo. O detalhamento da postura de segurança real (e o que
  reconstruir primeiro) está no doc 04 §2.
