# 02. Interface Visual (UI & Views)

O AIB é construído em **Windows Presentation Foundation (WPF)** (.NET 8). O WPF foi escolhido por fornecer controle em baixo nível da renderização de Desktop do Windows, permitindo `AllowsTransparency="True"`, sombreados vetoriais complexos (`DropShadowEffect`) e suporte fluido a animações nativas.

## 1. ChatWindow (`ChatWindow.xaml` e `.xaml.cs`)
Esta é a classe vital da interface. Ela centraliza não apenas a parte visual, mas também é o *Entry Point* (Ponto de Entrada) e o controlador de ciclo de vida da aplicação inteira.

### 1.1 Funções Core
- **Inicialização e Posicionamento**: No construtor da classe, ocorre a injeção manual dos serviços (OpenAI, Voice, Settings). A janela não usa as bordas do SO. Em vez disso, ela acessa `SystemParameters.WorkArea.Bottom` e `PrimaryScreenWidth` para auto-posicionar sua UI exatos 45 pixels acima da barra de tarefas, sempre fluindo centralizada, imitando nativamente o layout do sistema.
- **Smart Clipboard (`InputBox_PreviewKeyDown`)**: O interceptador global do teclado na caixa de texto. Além de lidar com o envio de mensagens (`Enter` sem `Shift`), ele monitora interações `CTRL+V`. Quando detecta que a área de transferência (`System.Windows.Clipboard`) não possui texto, mas sim um formato `FileDropList` nativo do Windows Explorer, ele coleta e injeta programaticamente os caminhos absolutos desses arquivos envoltos em aspas (ex: `"C:\Docs\arquivo.pdf"`), anulando erros de caminhos com espaço.
- **Animações Fluidas (`AddAgentBubble` e `AddUserBubble`)**: A interface não cospe o markdown cru instantaneamente. Ela instância o `MarkdownViewer`, o empacota num balão estilizado e invoca animações `DoubleAnimation` tanto na propriedade `Opacity` quanto em um `TranslateTransform`. O resultado visual é que a mensagem brota deslizando suavemente de baixo para cima.
- **Inteligência Estática de Loading (`UpdateLoadingState`)**: O streaming letra a letra causa solavancos severos de renderização de markdown em WPF. O AIB contorna isso escondendo o texto, exibindo um balão com pontilhados piscantes ("● ● ●") gerados por keyframes, e revelando o bloco inteiro num fade-in apenas quando o Loop ReAct ou o envio termina de forma definitiva.

### 1.2 UX de Stealth (Ghosting)
- O AIB abraça um perfil incrivelmente invasivo porém silencioso. Se o usuário clicar fora da janela (em um jogo, em outro programa), a interface do chat é sumariamente ocultada (via o evento `.Deactivated`). A janela nunca fica "atrapalhando" o desktop do usuário, operando apenas on-demand, de forma similar ao calendário padrão do Windows 11.

## 2. Configurações (`SettingsWindow.xaml` e `.xaml.cs`)
- A Janela de Configurações permite definir a OpenAI API Key e ajustar o modelo-base utilizado. 
- Ela é bloqueada para instâncias simultâneas.
- **Level Info Lock**: Informações sobre Limites de Contexto (`Max Tokens`) são renderizados aqui porém estão bloqueados como **Somente-Leitura**. Isso reflete a decisão arquitetural da gamificação implementada: Tokens são recompensas de Nível, não números manipuláveis nas configurações locais.

## 3. Segurança do Terminal (`CommandConfirmationWindow.xaml.cs`)
- O Agente possui a capacidade assustadora de invocar o prompt do Windows sob o usuário local (nível de tool 2+). Como contra-medida arquitetural de segurança ("Zero-Trust"):
- **Sandboxing Visual**: Todo comando PowerShell formatado em JSON pelo modelo entra num funil, bloqueando a pipeline e invocando a `CommandConfirmationWindow` (Modal UI). A janela informa exaustivamente a string de código que a IA pretende rodar no background.
- **Fallback Educativo**: Se o usuário rejeitar a ação, o LLM recebe o retorno textual `"Ação Rejeitada pelo Usuário."`. O modelo cognitivo interpreta isso no ReAct loop e prontamente responde se desculpando ou propondo um comando / via / script alternativo sem quebrar a execução geral do chat.
