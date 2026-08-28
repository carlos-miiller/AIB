using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using AIB.Services;
using Markdig.Wpf;

// Aliases para eliminar ambiguidade entre System.Drawing e System.Windows.Media
using WColor   = System.Windows.Media.Color;
using WBrushes = System.Windows.Media.Brushes;
using WPoint   = System.Windows.Point;
using LinearGB = System.Windows.Media.LinearGradientBrush;
using SolidCB  = System.Windows.Media.SolidColorBrush;
using GStop    = System.Windows.Media.GradientStop;
using WTranslate = System.Windows.Media.TranslateTransform;

namespace AIB.Views;

public partial class ChatWindow : Window
{
    private readonly ConversationService _conversation;
    private readonly ShadowAssistantService _shadowService;
    // Um widget por monitor: o da tela do cursor fica com opacidade 0.7 (ativo),
    // os outros com 0.3. O balão de sugestão aparece só no widget ativo.
    private readonly List<ShadowWidget> _shadowWidgets = new();
    private int _activeScreenIndex = -1;
    private bool _isShadowModeEnabled = false;
    private readonly SettingsService _settingsService;
    private readonly VoiceService _voiceService;

    private bool _voiceReady = false;
    private bool _voiceListening = false;
    private bool _isSending = false;

    public ChatWindow(ConversationService conversation, SettingsService settingsService)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _conversation = conversation;
        _conversation.OnTokenCountChanged += UpdateTokenCounterUI;
        _conversation.OnWarmupStateChanged += HandleWarmupState;

        _shadowService = new ShadowAssistantService(_conversation, _settingsService);
        _shadowService.OnSuggestionReceived += OnShadowSuggestion;
        _shadowService.OnActiveScreenChanged += OnActiveScreenChanged;

        StateChanged += ChatWindow_StateChanged;
        IsVisibleChanged += ChatWindow_IsVisibleChanged;

        // Registra os HWNDs do próprio AIB no Shadow (evita auto-OCR da janela do chat
        // e do widget). Precisa esperar Loaded para o HWND existir.
        this.Loaded += (s, e) =>
        {
            var helper = new System.Windows.Interop.WindowInteropHelper(this);
            _shadowService.RegisterOwnWindow(helper.Handle);
        };

        // Mostra/esconde botão do Shadow conforme setting opt-in
        ApplyShadowAssistantSetting();

        _voiceService = new VoiceService();

        // Inicializa UI
        RefreshLevelUI(false);
        int userLevel = LevelService.GetLevel(_settingsService.LoadSettings().MessageCount);
        int maxTokens = LevelService.GetMaxTokensForLevel(userLevel);
        UpdateTokenCounterUI(_conversation.CurrentTokenCount, maxTokens);
        ApplyCharacterUI();
        InputBox.Focus();

        ContextSidebarControl.OnRecoverChat += (session) =>
        {
            if (string.IsNullOrWhiteSpace(session.Content)) return;
            
            _conversation.AppendRecoveredContext(session.Title, session.Content);
            
            AddUserBubble($"Recuperando contexto: {session.Title}");
            AddAgentBubble("Contexto antigo carregado com sucesso. Como deseja continuar?");
            
            // Auto close sidebar after recovery
            if (_isSidebarOpen) SidebarButton_Click(null, null);
        };



        // Posiciona a janela: centralizada horizontal, flutuando 45px acima da barra de tarefas
        this.Left = (SystemParameters.PrimaryScreenWidth - this.Width) / 2;
        this.Top = SystemParameters.WorkArea.Bottom - this.ActualHeight - 45;

        // Reposiciona após a janela ter tamanho real (SizeToContent)
        this.Loaded += (s, e) => RepositionWindow();
        this.SizeChanged += (s, e) => RepositionWindow();

        // Esconde ao clicar fora
        this.Deactivated += Window_Deactivated;

        // Inicialização assíncrona do Whisper (evita travamento na UI Thread)
        _ = InitVoiceAsync();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Controle de Visibilidade
    // ─────────────────────────────────────────────────────────────────────────

    public void ApplyCharacterUI()
    {
        var settings = _settingsService.LoadSettings();

        string nome = string.IsNullOrEmpty(settings.ActiveCharacter) ? "AIB" : settings.ActiveCharacter;

        if (AgentNameText != null) AgentNameText.Text = nome.ToUpper();

        // O placeholder e o estado vazio falam com o nome do personagem ativo, como o
        // "Fale com a KAI..." de §3.7(b).
        if (InputPlaceholder != null) InputPlaceholder.Text = $"Fale com {nome}...";
        if (EmptyTitleText != null) EmptyTitleText.Text = $"Converse com {nome}";

        // Limpa a tela
        if (MessagesPanel != null) MessagesPanel.Children.Clear();
        // Limpa o contexto do OpenAI Service (injeta o SOUL.MD atual)
        _conversation.ResetHistory();

        AddWelcomeBubble();
        AtualizarEstadoVazio();
    }

    private void HandleWarmupState(bool isWarmingUp)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (isWarmingUp)
            {
                InputBox.IsEnabled = false;
                SendButton.IsEnabled = false;
                StatusBar.Visibility = Visibility.Visible;
                StatusText.Text = "⏳ AIB conectando e aquecendo motores (pode levar 2 min na 1ª vez)...";
                InputBox.Text = "";
            }
            else
            {
                InputBox.IsEnabled = true;
                SendButton.IsEnabled = true;
                StatusBar.Visibility = Visibility.Collapsed;
                StatusText.Text = "🧠 Pensando...";
                InputBox.Focus();
            }
        });
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        // Perder o foco para um modal do próprio AIB não é o usuário indo fazer outra coisa.
        if (ModalGuard.IsAnyModalOpen) return;

        if (this.Visibility == Visibility.Visible) this.Hide();
    }

    public void ToggleWindow()
    {
        if (this.Visibility == Visibility.Visible)
        {
            this.Hide();
        }
        else
        {
            RepositionWindow();
            this.Show();
            this.Activate();
            InputBox.Focus();
        }
    }

    private void RepositionWindow()
    {
        double taskbarBottom = SystemParameters.WorkArea.Bottom;
        this.Left = (SystemParameters.PrimaryScreenWidth - this.Width) / 2;
        this.Top = taskbarBottom - this.ActualHeight - 45;
    }

    public void RefreshLevelUI(bool incrementXp = false)
    {
        var settings = _settingsService.LoadSettings();

        if (incrementXp)
        {
            int oldLevel = LevelService.GetLevel(settings.MessageCount);
            settings.MessageCount++;
            _settingsService.SaveSettings(settings);

            int newLevel = LevelService.GetLevel(settings.MessageCount);
            if (newLevel > oldLevel)
            {
                AddAgentBubble($"🎉 **LEVEL UP!** Parabéns, AIB atingiu o **Nível {newLevel}**!\nNovas habilidades e pastas podem ter sido desbloqueadas.");
                ChatScrollViewer.ScrollToEnd();
            }
        }

        var s = settings;
        int lvl = LevelService.GetLevel(s.MessageCount);
        int xpBase = LevelService.GetXPForCurrentLevel(lvl);
        int xpNext = LevelService.GetXPForNextLevel(lvl);
        int currentXpInLevel = s.MessageCount - xpBase;
        int requiredXpInLevel = xpNext - xpBase;

        LevelText.Text = $"Nível {lvl}";

        // Tooltip e Progress Bar
        TooltipLevelInfo.Text = $"Nível {lvl} ({s.MessageCount}/{xpNext} XP)";
        XpProgressBar.Maximum = requiredXpInLevel == 0 ? 1 : requiredXpInLevel;
        XpProgressBar.Value = currentXpInLevel;

        // Faixa (Bronze, Prata, Ouro, Platina, Diamante). Ela pinta só a barra de XP, dentro
        // do ToolTip. O texto da pill fica sempre em lilás: §3.3(c) diz que é EXATAMENTE a
        // pill da tela de seleção, e a moldura neon precisa seguir sendo a única cor saturada
        // do header (O2). A informação da faixa não se perde — vive onde o usuário vai olhar
        // para saber do nível.
        WColor corDaFaixa;
        if (lvl <= 2) corDaFaixa = WColor.FromRgb(0xCD, 0x7F, 0x32);        // Bronze
        else if (lvl <= 4) corDaFaixa = WColor.FromRgb(0xC0, 0xC0, 0xC0);  // Prata
        else if (lvl <= 6) corDaFaixa = WColor.FromRgb(0xFF, 0xD7, 0x00);  // Ouro
        else if (lvl <= 8) corDaFaixa = WColor.FromRgb(0x00, 0xFF, 0x7F);  // Platina
        else corDaFaixa = WColor.FromRgb(0x00, 0xBF, 0xFF);                 // Diamante

        XpProgressBar.Foreground = new SolidCB(corDaFaixa);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Mensagem de Boas-vindas
    // ─────────────────────────────────────────────────────────────────────────

    private void AddWelcomeBubble()
    {   //removido para deixar o inicio limpo
        //AddAgentBubble("✦ **AIB Online.** Como posso ajudar?"); 

    }

    // ─────────────────────────────────────────────────────────────────────────
    // Criação de Bolhas de Chat
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Monta a LINHA que hospeda uma bolha. §3.4 e §3.9.
    /// <para>
    /// A hora fica FORA da bolha, na coluna oposta ao alinhamento: nas linhas do usuário à
    /// esquerda, nas da IA à direita. Se ela fosse irmã da bolha na mesma direção, ocuparia
    /// espaço e empurraria a bolha para longe da borda. A5.
    /// </para>
    /// <para>
    /// Aparece só no hover da linha, e a bolha é limitada a 74% da largura da lista.
    /// </para>
    /// </summary>
    private Grid NovaLinha(FrameworkElement bolha, bool doUsuario)
    {
        var linha = new Grid { Margin = new Thickness(0, 0, 0, 14), Background = WBrushes.Transparent };
        linha.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        linha.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var hora = new TextBlock
        {
            Style = (Style)Resources["StampText"],
            Text = DateTime.Now.ToString("HH:mm")
        };

        bolha.MaxWidth = Math.Max(120, ChatScrollViewer.ActualWidth > 0
            ? ChatScrollViewer.ActualWidth * 0.74
            : 770 * 0.74);

        if (doUsuario)
        {
            linha.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
            Grid.SetColumn(hora, 0);
            Grid.SetColumn(bolha, 1);
        }
        else
        {
            linha.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            Grid.SetColumn(bolha, 0);
            Grid.SetColumn(hora, 1);
        }

        linha.Children.Add(hora);
        linha.Children.Add(bolha);

        linha.MouseEnter += (_, _) => hora.Opacity = 1;
        linha.MouseLeave += (_, _) => hora.Opacity = 0;

        MessagesPanel.Children.Add(linha);
        AtualizarEstadoVazio();
        return linha;
    }

    /// <summary>
    /// Tira do painel a LINHA que hospeda esta bolha.
    /// <para>
    /// Desde que a bolha passou a morar dentro de um Grid de linha, remover a bolha direto do
    /// MessagesPanel não faz nada: ela não é mais filha dele, e Children.Remove de quem não é
    /// filho falha em silêncio. O indicador de "digitando" ficaria para sempre na tela.
    /// </para>
    /// </summary>
    private void RemoverLinha(FrameworkElement? bolha)
    {
        if (bolha == null) return;

        DependencyObject? atual = bolha;
        while (atual != null)
        {
            if (atual is FrameworkElement fe && MessagesPanel.Children.Contains(fe))
            {
                MessagesPanel.Children.Remove(fe);
                AtualizarEstadoVazio();
                return;
            }

            atual = System.Windows.Media.VisualTreeHelper.GetParent(atual)
                    ?? LogicalTreeHelper.GetParent(atual);
        }
    }

    /// <summary>Some com o §5.6 assim que existe qualquer mensagem, e o traz de volta ao limpar.</summary>
    private void AtualizarEstadoVazio()
    {
        EmptyState.Visibility = MessagesPanel.Children.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void AddUserBubble(string text)
    {
        var border = new Border
        {
            Style = (Style)Resources["UserBubble"],
            Child = new TextBlock
            {
                Text = text,
                Foreground = WBrushes.White,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14,
                LineHeight = 21
            }
        };

        var linha = NovaLinha(border, doUsuario: true);
        AnimateBubbleIn(linha);
        ChatScrollViewer.ScrollToEnd();
    }

    private MarkdownViewer AddAgentBubble(string? initialText = null)
    {
        // A casca vem do estilo: surfaceCard + borderCard + cardShadow + r18. É a mesma do
        // card NÃO selecionado da tela de seleção (§3.5 B).
        var border = new Border { Style = (Style)Resources["AgentBubble"] };

        var viewer = new MarkdownViewer
        {
            Markdown = initialText ?? "",
            Foreground = (System.Windows.Media.Brush)FindResource("TextBodyBrush"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Margin = new Thickness(-6),
        };

        // §5.5 — código inline em lilás sobre surfaceCode; bloco em textBody sobre
        // surfaceCodeBlock. Os dois em mono 12,5. Antes o inline saía ciano, uma cor que não
        // existe em nenhum dos dois arquivos de spec.
        var fonteMono = (System.Windows.Media.FontFamily)FindResource("MonoFontFamily");
        var tamanhoCodigo = (double)FindResource("FontSizeCode");

        var inlineCodeStyle = new Style();
        inlineCodeStyle.Setters.Add(new Setter(System.Windows.Documents.TextElement.BackgroundProperty, FindResource("SurfaceCodeBrush")));
        inlineCodeStyle.Setters.Add(new Setter(System.Windows.Documents.TextElement.ForegroundProperty, FindResource("AccentLilacBrush")));
        inlineCodeStyle.Setters.Add(new Setter(System.Windows.Documents.TextElement.FontFamilyProperty, fonteMono));
        inlineCodeStyle.Setters.Add(new Setter(System.Windows.Documents.TextElement.FontSizeProperty, tamanhoCodigo));

        var blockCodeStyle = new Style();
        blockCodeStyle.Setters.Add(new Setter(System.Windows.Documents.TextElement.BackgroundProperty, FindResource("SurfaceCodeBlockBrush")));
        blockCodeStyle.Setters.Add(new Setter(System.Windows.Documents.TextElement.ForegroundProperty, FindResource("TextBodyBrush")));
        blockCodeStyle.Setters.Add(new Setter(System.Windows.Documents.TextElement.FontFamilyProperty, fonteMono));
        blockCodeStyle.Setters.Add(new Setter(System.Windows.Documents.TextElement.FontSizeProperty, tamanhoCodigo));

        viewer.Resources.Add(Markdig.Wpf.Styles.CodeStyleKey, inlineCodeStyle);
        viewer.Resources.Add(Markdig.Wpf.Styles.CodeBlockStyleKey, blockCodeStyle);

        // Permite que o scroll do mouse funcione mesmo com o ponteiro sobre a bolha Markdown
        viewer.PreviewMouseWheel += (s, e) =>
        {
            if (!e.Handled)
            {
                e.Handled = true;
                var eventArg = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                {
                    RoutedEvent = UIElement.MouseWheelEvent,
                    Source = s
                };
                ChatScrollViewer.RaiseEvent(eventArg);
            }
        };

        border.Child = viewer;
        var linha = NovaLinha(border, doUsuario: false);
        AnimateBubbleIn(linha);
        ChatScrollViewer.ScrollToEnd();
        return viewer;
    }

    /// <summary>
    /// Animação de entrada da bolha: desliza 18px para cima + fade-in em 280ms.
    /// Estilo WhatsApp / iMessage — suave e direto.
    /// </summary>
    private static void AnimateBubbleIn(FrameworkElement element)
    {
        var transform = new WTranslate(0, 20);
        element.RenderTransform = transform;
        element.Opacity = 0;

        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(260));

        var slideUp = new DoubleAnimation(20, 0, duration) { EasingFunction = ease };
        var fadeIn  = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(200)));

        Storyboard.SetTarget(slideUp, element);
        Storyboard.SetTargetProperty(slideUp,
            new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));

        Storyboard.SetTarget(fadeIn, element);
        Storyboard.SetTargetProperty(fadeIn, new PropertyPath("Opacity"));

        var sb = new Storyboard();
        sb.Children.Add(slideUp);
        sb.Children.Add(fadeIn);
        sb.Begin();
    }

    /// <summary>
    /// Adiciona uma bolha "digitando" com 3 pontos animados.
    /// Retorna o Border e o DispatcherTimer para que o chamador possa pará-los.
    /// </summary>
    private (Border bubble, System.Windows.Threading.DispatcherTimer timer) AddTypingIndicator()
    {
        // §3.6 — mesma casca da bolha da IA, com padding próprio e três pontos de 6px.
        var border = new Border
        {
            Style = (Style)Resources["AgentBubble"],
            Padding = new Thickness(18, 15, 18, 15)
        };

        var pontos = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        var bolinhas = new List<System.Windows.Shapes.Ellipse>();

        for (int i = 0; i < 3; i++)
        {
            var bolinha = new System.Windows.Shapes.Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = (System.Windows.Media.Brush)FindResource("AccentLilacBrush"),
                Margin = new Thickness(i == 0 ? 0 : 5, 0, 0, 0),
                Opacity = 0.25,
                VerticalAlignment = VerticalAlignment.Center
            };

            bolinhas.Add(bolinha);
            pontos.Children.Add(bolinha);
        }

        border.Child = pontos;
        var linha = NovaLinha(border, doUsuario: false);
        ChatScrollViewer.ScrollToEnd();

        // Opacidade 0.25 -> 1 em 1,2s, em laço, com atraso escalonado de 160ms. O timer
        // continua sendo devolvido porque o chamador já sabe pará-lo; aqui ele só serve para
        // encerrar as animações junto.
        for (int i = 0; i < bolinhas.Count; i++)
        {
            var pulso = new DoubleAnimation(0.25, 1.0, new Duration(TimeSpan.FromMilliseconds(600)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromMilliseconds(i * 160)
            };

            bolinhas[i].BeginAnimation(UIElement.OpacityProperty, pulso);
        }

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1200)
        };
        timer.Start();

        return (border, timer);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Ações Locais (Skills)
    // ─────────────────────────────────────────────────────────────────────────
    
    private void ShowSkillsList()
    {
        try
        {
            var (natives, dynamics) = _conversation.Registry.GetCategorizedTools();
            var sb = new System.Text.StringBuilder();
            
            sb.AppendLine("## ⚙️ Skills Nativas (Embutidas)\n");
            foreach (var n in natives)
            {
                sb.AppendLine($"- **`{n.Name}`**\n  *_{n.Description.Replace("\n", " ")}_*");
            }

            sb.AppendLine("\n---\n## 🧩 Skills Custom (Locais)\n");
            if (dynamics.Count == 0)
            {
                sb.AppendLine("*Nenhuma skill customizada detectada.*");
            }
            else
            {
                foreach (var d in dynamics)
                {
                    sb.AppendLine($"- **`{d.Name}`**\n  *_{d.Description.Replace("\n", " ")}_*");
                }
            }

            sb.AppendLine("\n---\n## 🧑‍💻 Suas Próprias Skills\n");
            sb.AppendLine("Sabia que eu posso aprender novos truques sozinha? Você pode me pedir para criar uma nova skill!\n");
            sb.AppendLine("*Basta me explicar o que você precisa que eu automatize, pesquise ou execute. Eu mesma escreverei o código (em Python ou PowerShell), salvarei na pasta e a nova ferramenta aparecerá aqui automaticamente.*");

            AddAgentBubble(sb.ToString());
            ChatScrollViewer.ScrollToEnd();
        }
        catch (Exception ex)
        {
            AddAgentBubble($"❌ **Erro ao carregar skills:** {ex.Message}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helper para Colar Arquivos
    // ─────────────────────────────────────────────────────────────────────────

    private void InsertFilePaths(string[] files)
    {
        string pathsToPaste = string.Join(" ", files.Select(f => $"\"{f}\"")) + " ";
        int caretIndex = InputBox.CaretIndex;
        InputBox.Text = InputBox.Text.Insert(caretIndex, pathsToPaste);
        InputBox.CaretIndex = caretIndex + pathsToPaste.Length;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Envio de Mensagem
    // ─────────────────────────────────────────────────────────────────────────

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        // Enquanto grava, o primário é PARAR (§3.7(e)): ele encerra a escuta em vez de enviar.
        // A transcrição final ainda chega por OnTranscriptionUpdated e dispara o envio.
        if (_voiceListening)
        {
            VoiceButton_Click(sender, e);
            return;
        }

        if (_isSending)
        {
            _conversation.CancelGeneration();
            return;
        }
        
        string text = InputBox.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        // Se o usuário digitar /skills, lidamos localmente e nem chamamos a OpenAI
        if (text.Equals("/skills", StringComparison.OrdinalIgnoreCase))
        {
            InputBox.Clear();
            AddUserBubble(text);
            ShowSkillsList();
            return;
        }

        // Comando secreto para desbloquear nível
        if (text.StartsWith("/unlock_level", StringComparison.OrdinalIgnoreCase))
        {
            InputBox.Clear();
            AddUserBubble(text);
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && int.TryParse(parts[1], out int targetLevel) && targetLevel >= 1 && targetLevel <= 9)
            {
                int neededXP = LevelService.GetXPForCurrentLevel(targetLevel);
                var settings = _settingsService.LoadSettings();
                settings.MessageCount = neededXP;
                _settingsService.SaveSettings(settings);
                
                int maxTokens = LevelService.GetMaxTokensForLevel(targetLevel);
                UpdateTokenCounterUI(0, maxTokens);
                AddAgentBubble($"Cheat ativado! Avançando MessageCount para {neededXP}. Você agora é Nível {targetLevel} e possui {maxTokens} tokens de memória local livre.");
            }
            else
            {
                AddAgentBubble("Uso incorreto. Tente: /unlock_level 9");
            }
            return;
        }

        _isSending = true;
        InputBox.Clear();
        InputBox.IsEnabled = false;
        SendButton.Content = "■"; // Ícone de Stop
        SendButton.Foreground = new SolidCB(WColor.FromRgb(0xFF, 0x55, 0x55));

        AddUserBubble(text);
        Console.WriteLine($"\n[USER] [{DateTime.Now:HH:mm:ss}]: {text}");

        string fullText = "";

        // Texto das falas já fechadas em balão. fullText guarda só a fala corrente; este
        // acumula o turno inteiro, que é o que vai para o log e para a notificação.
        string allText = "";
        string? errorText = null;
        
        Border? typingBubble = null;
        System.Windows.Threading.DispatcherTimer? typingTimer = null;
        var idleTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        idleTimer.Tick += (s, ev) =>
        {
            if (typingBubble != null)
            {
                typingTimer?.Stop();
                RemoverLinha(typingBubble);
                typingBubble = null;
                typingTimer = null;
            }
            idleTimer.Stop();
        };

        try
        {
            var stream = _conversation.StreamResponseAsync(text, tech =>
            {
                // Log técnico interno
                Console.Write(tech);

                // Exibe visualmente o uso de ferramentas
                var match = System.Text.RegularExpressions.Regex.Match(tech, @"\[(?:FALLBACK REGEX )?FERRAMENTA\] Nome: ([a-zA-Z_]+)");
                if (match.Success)
                {
                    string toolName = match.Groups[1].Value;
                    Dispatcher.BeginInvoke(() =>
                    {
                        var toolInfo = new TextBlock
                        {
                            Text = $"🔧 Usando ferramenta: {toolName}...",
                            Foreground = new SolidCB(WColor.FromRgb(0x88, 0x88, 0x99)),
                            FontSize = 11,
                            FontStyle = FontStyles.Italic,
                            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                            Margin = new Thickness(12, 2, 0, 4)
                        };
                        // Insere antes dos 3 pontos se estiverem na tela, senão no final
                        int insertIndex = typingBubble != null ? Math.Max(0, MessagesPanel.Children.Count - 1) : MessagesPanel.Children.Count;
                        MessagesPanel.Children.Insert(insertIndex, toolInfo);
                        ChatScrollViewer.ScrollToEnd();
                    });
                }
            });

            // Acumula a resposta completa silenciosamente.
            // SEM ConfigureAwait(false) de propósito: este laço é de interface, não de rede.
            // A leitura do socket já roda fora do Dispatcher porque toda a camada de serviço
            // usa ConfigureAwait(false); capturar o contexto aqui garante que o corpo do laço,
            // o catch e o finally continuem na thread de UI, como sempre foi.
            await foreach (var item in stream)
            {
                // Fronteira de fala: o agente terminou de dizer o que ia dizer e vai usar uma
                // ferramenta. Fecha o balão com o que foi acumulado e recomeça o acúmulo — o
                // balão continua sendo renderizado só quando completo, como sempre foi.
                if (item is ChatStreamItem.SegmentBreak)
                {
                    if (!string.IsNullOrWhiteSpace(fullText))
                    {
                        if (typingBubble != null)
                        {
                            typingTimer?.Stop();
                            RemoverLinha(typingBubble);
                            typingBubble = null;
                            typingTimer = null;
                        }

                        AddAgentBubble(fullText);
                        ChatScrollViewer.ScrollToEnd();

                        allText += fullText;
                        fullText = "";
                    }
                    continue;
                }

                if (item is not ChatStreamItem.Text textItem) continue;
                string chunk = textItem.Value;

                fullText += chunk;
                GibberishVoiceService.SpeakChunk(chunk);

                // Mostra os 3 pontos apenas quando a IA estiver enviando pedaços de texto (escrevendo)
                if (typingBubble == null)
                {
                    var tuple = AddTypingIndicator();
                    typingBubble = tuple.Item1;
                    typingTimer = tuple.Item2;
                }

                // Reseta o timer de inatividade de 3 segundos
                idleTimer.Stop();
                idleTimer.Start();

                await Task.Yield(); // Força a liberação da thread de UI para renderizar os updates
            }

            Console.WriteLine($"\n[AIB]  [{DateTime.Now:HH:mm:ss}]: {fullText}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] [{DateTime.Now:HH:mm:ss}]: {ex.Message}");
            errorText = $"❌ **Erro:** {ex.Message}";
        }
        finally
        {
            // Limpa ambos os timers e a bolha de digitação
            idleTimer.Stop();
            if (typingBubble != null)
            {
                typingTimer?.Stop();
                RemoverLinha(typingBubble);
            }

            // Exibe a fala final de uma só vez. Quando o turno teve várias falas, as anteriores
            // já viraram balão na fronteira de cada ferramenta; aqui fecha só a última.
            if (errorText != null)
            {
                AddAgentBubble(errorText);
            }
            else if (!string.IsNullOrWhiteSpace(fullText))
            {
                AddAgentBubble(fullText);
            }
            else if (string.IsNullOrWhiteSpace(allText))
            {
                // Nada foi dito no turno inteiro. Só aqui o aviso faz sentido: se já houve
                // falas anteriores, um "Ação executada" solto no fim seria ruído.
                AddAgentBubble("*Ação executada com sucesso.*");
            }

            ChatScrollViewer.ScrollToEnd();
            // Restaura a UI do InputBox
            StatusBar.Visibility = Visibility.Collapsed;
            InputBox.IsEnabled = true;
            SendButton.Content = "➔"; // Ícone de Enviar
            SendButton.Foreground = WBrushes.White;
            InputBox.Focus();
            _isSending = false;

            if (errorText == null)
            {
                RefreshLevelUI(true);
            }

            // Se a janela estiver invisível ou sem foco (usuário fazendo outra coisa), emite notificação
            if (!this.IsActive || this.Visibility != Visibility.Visible)
            {
                string turnText = (allText + fullText).Trim();
                string notifyText = errorText ?? (string.IsNullOrWhiteSpace(turnText) ? "*Ação executada com sucesso.*" : turnText);
                if (notifyText.Length > 200) notifyText = notifyText.Substring(0, 197) + "...";
                
                ((App)System.Windows.Application.Current).ShowNotification("AIB", notifyText);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Botão de Visão (OCR)
    // ─────────────────────────────────────────────────────────────────────────

    private async void VisionButton_Click(object sender, RoutedEventArgs e)
    {
        VisionButton.IsEnabled = false;
        VisionButton.Content = "⏳";
        StatusBar.Visibility = Visibility.Visible;
        StatusText.Text = "👁 Lendo tela...";

        try
        {
            var ocr = new OcrService();
            string text = await ocr.ExtractTextFromActiveScreenAsync();

            if (string.IsNullOrWhiteSpace(text))
            {
                AddAgentBubble("👁 Não encontrei texto legível na tela.");
            }
            else
            {
                // Injeta o conteúdo da tela diretamente no InputBox como contexto
                InputBox.Text = $"[Contexto da tela]: {text.Substring(0, Math.Min(text.Length, 400))}";
                InputBox.Focus();
                InputBox.CaretIndex = InputBox.Text.Length;
            }
        }
        catch (Exception ex)
        {
            AddAgentBubble($"❌ Erro na leitura de tela: {ex.Message}");
        }
        finally
        {
            StatusBar.Visibility = Visibility.Collapsed;
            VisionButton.Content = "📷";
            VisionButton.IsEnabled = true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Botão de Voz (Whisper Local)
    // ─────────────────────────────────────────────────────────────────────────

    private async Task InitVoiceAsync()
    {
        try
        {
            await Task.Run(async () => await _voiceService.InitializeAsync());
            _voiceReady = true;
            Dispatcher.BeginInvoke(() =>
            {
                AplicarEstadoDeGravacao(false);
                VoiceButton.IsEnabled = true;
                Console.WriteLine("[VOICE] Motor Whisper inicializado e pronto.");
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VOICE] Erro ao inicializar: {ex.Message}");
            Dispatcher.BeginInvoke(() =>
            {
                VoiceButton.IsEnabled = false;
                VoiceButton.ToolTip = "Voz indisponível";
            });
        }
    }

    private void VoiceButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_voiceReady)
        {
            AddAgentBubble("⏳ O motor de voz ainda está inicializando. Aguarde...");
            return;
        }

        if (!_voiceListening)
        {
            // Inicia escuta
            _voiceService.OnTranscriptionUpdated += OnTranscriptionUpdated;
            _voiceService.StartListening();
            _voiceListening = true;
            AplicarEstadoDeGravacao(true);
            InputBox.Text = "";
            StatusBar.Visibility = Visibility.Visible;
            StatusText.Text = "🎙 Ouvindo...";
        }
        else
        {
            // Para escuta
            _voiceService.StopListening();
            _voiceService.OnTranscriptionUpdated -= OnTranscriptionUpdated;
            _voiceListening = false;
            AplicarEstadoDeGravacao(false);
            StatusBar.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Troca a aparência do botão primário entre ENVIAR e PARAR — §3.7(e).
    /// <para>
    /// O estado de gravação mora no botão primário, e não no microfone: o primário é sempre o
    /// aviãozinho, e o microfone é um botão separado à esquerda. Já foram trocados por engano
    /// uma vez (A7).
    /// </para>
    /// </summary>
    private void AplicarEstadoDeGravacao(bool gravando)
    {
        if (gravando)
        {
            // Quadrado vermelho de 13px, r3 — o "parar" do §3.7(e).
            SendButton.Content = new Border
            {
                Width = 13,
                Height = 13,
                CornerRadius = new CornerRadius(3),
                Background = (System.Windows.Media.Brush)FindResource("DangerBrush")
            };
            SendButton.ToolTip = "Parar de gravar";
            VoiceButton.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            VoiceButton.ToolTip = "Ouvindo… clique para parar.";
            return;
        }

        SendButton.Content = new System.Windows.Shapes.Path
        {
            Data = System.Windows.Media.Geometry.Parse("M2,9 L16,2.5 L10.4,15.5 L8.6,10.4 Z"),
            Fill = WBrushes.White,
            Width = 18,
            Height = 18,
            Stretch = System.Windows.Media.Stretch.None
        };
        SendButton.ToolTip = "Enviar";
        VoiceButton.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
        VoiceButton.ToolTip = "Falar com o AIB (Whisper)";
    }

    private void OnTranscriptionUpdated(object? sender, TranscriptionEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            InputBox.Text = e.Text;
            // Auto-submit quando a transcrição está finalizada (silêncio detectado)
            if (e.IsFinal && !string.IsNullOrWhiteSpace(e.Text))
            {
                _voiceService.StopListening();
                _voiceService.OnTranscriptionUpdated -= OnTranscriptionUpdated;
                _voiceListening = false;
                AplicarEstadoDeGravacao(false);
                StatusBar.Visibility = Visibility.Collapsed;
                SendButton_Click(this, new RoutedEventArgs());
            }
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Handlers de controle da janela
    // ─────────────────────────────────────────────────────────────────────────

    private readonly string[] _slashCommands = { "/skills", "/clear", "/help", "/vault" };

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string text = InputBox.Text;

        // O placeholder é um TextBlock por baixo, e não a marca d'água do controle: o TextBox
        // do WPF não tem placeholder nativo.
        InputPlaceholder.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (text.StartsWith("/") && text.Length >= 1)
        {
            var matches = _slashCommands.Where(c => c.StartsWith(text, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Any())
            {
                CommandsList.ItemsSource = matches;
                CommandsPopup.IsOpen = true;
                CommandsList.SelectedIndex = 0;
            }
            else
            {
                CommandsPopup.IsOpen = false;
            }
        }
        else
        {
            CommandsPopup.IsOpen = false;
        }
    }

    private void InputBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Intercepta CTRL+V para colar caminhos de arquivos copiados
        if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (System.Windows.Clipboard.ContainsFileDropList())
            {
                var files = System.Windows.Clipboard.GetFileDropList();
                if (files != null && files.Count > 0)
                {
                    string[] fileArray = new string[files.Count];
                    files.CopyTo(fileArray, 0);
                    InsertFilePaths(fileArray);
                    e.Handled = true;
                    return;
                }
            }
        }

        if (CommandsPopup.IsOpen)
        {
            if (e.Key == Key.Down)
            {
                if (CommandsList.SelectedIndex < CommandsList.Items.Count - 1) CommandsList.SelectedIndex++;
                e.Handled = true;
                return;
            }
            else if (e.Key == Key.Up)
            {
                if (CommandsList.SelectedIndex > 0) CommandsList.SelectedIndex--;
                e.Handled = true;
                return;
            }
            else if (e.Key == Key.Enter || e.Key == Key.Tab)
            {
                if (CommandsList.SelectedItem is string cmd)
                {
                    InputBox.Text = cmd + " ";
                    InputBox.CaretIndex = InputBox.Text.Length;
                    CommandsPopup.IsOpen = false;
                    e.Handled = true;
                    return;
                }
            }
            else if (e.Key == Key.Escape)
            {
                CommandsPopup.IsOpen = false;
                e.Handled = true;
                return;
            }
        }

        // Comportamento de quebra de linha vs envio:
        //   - Enter sozinho            -> envia mensagem
        //   - Ctrl+Enter ou Shift+Enter -> quebra linha
        // Shift+Enter o TextBox processa nativamente (AcceptsReturn=True). Já Ctrl+Enter
        // precisa de injeção manual porque WPF ignora Ctrl como modifier de inserção.
        if (e.Key == Key.Enter)
        {
            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

            if (ctrl)
            {
                int caret = InputBox.CaretIndex;
                InputBox.Text = InputBox.Text.Insert(caret, Environment.NewLine);
                InputBox.CaretIndex = caret + Environment.NewLine.Length;
                e.Handled = true;
                return;
            }
            if (shift) return; // deixa o TextBox inserir \n nativamente

            if (!_isSending)
            {
                SendButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
        }
        if (e.Key == Key.Escape) this.Hide();
    }

    private void CommandsList_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Tratado no InputBox_PreviewKeyDown quando focado, mas como fallback:
        if (e.Key == Key.Enter || e.Key == Key.Tab)
        {
            if (CommandsList.SelectedItem is string cmd)
            {
                InputBox.Text = cmd + " ";
                InputBox.CaretIndex = InputBox.Text.Length;
                CommandsPopup.IsOpen = false;
                e.Handled = true;
                InputBox.Focus();
            }
        }
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) this.DragMove();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        this.Deactivated -= Window_Deactivated; // Previne esconder o chat
        var settingsWin = new SettingsWindow(_settingsService);
        settingsWin.Owner = this;
        settingsWin.ShowDialog();
        this.Deactivated += Window_Deactivated; // Retorna o comportamento

        // Settings podem ter mudado a flag Shadow Assistant — atualiza o botão.
        // Se o usuário desligou o setting com o Shadow ativo, paramos o serviço.
        ApplyShadowAssistantSetting();
    }

    /// <summary>
    /// Mostra/esconde o botão do olho conforme a setting ShadowAssistantEnabled.
    /// Se a setting estiver OFF e o Shadow estava rodando, para tudo e fecha os widgets.
    /// </summary>
    private void ApplyShadowAssistantSetting()
    {
        bool enabled = _settingsService.LoadSettings().ShadowAssistantEnabled;
        BtnToggleShadow.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;

        if (!enabled && _isShadowModeEnabled)
        {
            _isShadowModeEnabled = false;
            BtnToggleShadow.Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#888899"));
            ManageShadowState();
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        MessagesPanel.Children.Clear();
        _conversation.ResetHistory();
        int userLevel = LevelService.GetLevel(_settingsService.LoadSettings().MessageCount);
        int maxTokens = LevelService.GetMaxTokensForLevel(userLevel);
        _lastCachedTokens = null;
        UpdateTokenCounterUI(0, maxTokens);
        ChatTitleText.Text = "Nova conversa";
        AddWelcomeBubble();
        AtualizarEstadoVazio();
    }

    /// <summary>
    /// Cor do contador por ECONOMIA de contexto, não por ocupação — §3.8.
    /// <para>
    /// A tela antiga pintava de verde a laranja conforme o histórico enchia. A spec inverte o
    /// que o número comunica: o que importa ali é quanto o cache de prefixo está economizando.
    /// Verde quer dizer "o cache está trabalhando"; magenta, "cada turno está sendo reenviado
    /// inteiro".
    /// </para>
    /// </summary>
    private System.Windows.Media.Brush CorDaEconomia(int? economiaPct)
    {
        if (!economiaPct.HasValue) return (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
        if (economiaPct.Value >= 50) return (System.Windows.Media.Brush)FindResource("SuccessBrush");
        if (economiaPct.Value >= 20) return (System.Windows.Media.Brush)FindResource("WarnBrush");
        return (System.Windows.Media.Brush)FindResource("MagentaBrush");
    }

    private int? _lastCachedTokens = null;

    private void UpdateTokenCounterUI(int current, int max, int? cached = null)
    {
        Dispatcher.Invoke(() =>
        {
            string texto = $"{current}/{max} tokens";

            if (cached.HasValue) _lastCachedTokens = cached.Value;

            int? economia = null;
            if (_lastCachedTokens.HasValue && current > 0)
            {
                economia = (int)Math.Round((double)_lastCachedTokens.Value / current * 100);
                economia = Math.Clamp(economia.Value, 0, 100);

                // Negativo = economia, como a spec escreve o exemplo "909/8704 tokens (-91%)".
                texto += $" (-{economia}%)";
            }

            TokenCounterText.Text = texto;
            TokenCounterText.Foreground = CorDaEconomia(economia);
        });
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => this.Hide();

    // ─────────────────────────────────────────────────────────────────────────
    // Side Panel (Dashboard) Handlers
    // ─────────────────────────────────────────────────────────────────────────

    private bool _isSidebarOpen = false;

    private void SidebarButton_Click(object sender, RoutedEventArgs e)
    {
        _isSidebarOpen = !_isSidebarOpen;
        
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
        
        // A janela de chat tem largura FIXA de 770: ela NÃO encolhe quando o painel abre, o
        // painel é que aparece ao lado (A2). O que cresce é só a Window que hospeda os dois —
        // e na etapa seguinte o painel vira janela própria, como manda §6.
        double targetWindowWidth = _isSidebarOpen ? 1130 : 820;
        double targetSidebarWidth = _isSidebarOpen ? 300 : 0;

        var winAnim = new DoubleAnimation(targetWindowWidth, new Duration(TimeSpan.FromMilliseconds(300))) { EasingFunction = ease };
        var sidebarAnim = new DoubleAnimation(targetSidebarWidth, new Duration(TimeSpan.FromMilliseconds(300))) { EasingFunction = ease };

        this.BeginAnimation(Window.WidthProperty, winAnim);
        SidebarPanel.BeginAnimation(Border.WidthProperty, sidebarAnim);
        
        if (_isSidebarOpen)
        {
            ContextSidebarControl.Refresh();
        }
    }



    protected override void OnClosed(EventArgs e)
    {
        _conversation?.ResetHistory();
        _voiceService?.Dispose();
        _shadowService?.Stop();
        CloseAllShadowWidgets();
        base.OnClosed(e);
    }

    private void BtnToggleShadow_Click(object sender, RoutedEventArgs e)
    {
        if (!_isShadowModeEnabled)
        {
            MessageBoxResult result;
            using (ModalGuard.Enter())
            {
                result = System.Windows.MessageBox.Show(
                    "O Shadow Assistant roda em segundo plano capturando o texto da sua tela e tentando prever o que você precisa.\n\n" +
                    "Como é uma função Alpha, a AIB às vezes pode alucinar ou interpretar a tela erroneamente.\n\n" +
                    "Tem certeza que deseja ativar o monitoramento em segundo plano?",
                    "Shadow Assistant (Alpha)", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            }

            if (result == MessageBoxResult.Yes)
            {
                _isShadowModeEnabled = true;
                BtnToggleShadow.Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#9B51E0"));
                ManageShadowState();
            }
        }
        else
        {
            _isShadowModeEnabled = false;
            BtnToggleShadow.Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#888899"));
            ManageShadowState();
        }
    }

    private void ChatWindow_StateChanged(object? sender, EventArgs e)
    {
        ManageShadowState();
    }

    private void ChatWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        ManageShadowState();
    }

    private void ManageShadowState()
    {
        if (!_isShadowModeEnabled)
        {
            _shadowService.Stop();
            CloseAllShadowWidgets();
            return;
        }

        if (this.Visibility == Visibility.Visible && this.WindowState == WindowState.Normal)
        {
            _shadowService.Stop();
            foreach (var w in _shadowWidgets) w.Hide();
        }
        else
        {
            EnsureShadowWidgetsForAllScreens();
            foreach (var w in _shadowWidgets) w.Show();

            // Sincroniza opacidade inicial baseada na tela do cursor agora
            _activeScreenIndex = ShadowAssistantService.GetCurrentScreenIndex();
            ApplyActiveScreenOpacity();

            _shadowService.Start();
        }
    }

    private void EnsureShadowWidgetsForAllScreens()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (_shadowWidgets.Count == screens.Length) return; // já está OK

        // Configuração mudou (monitor conectado/desconectado): recria do zero
        CloseAllShadowWidgets();

        for (int i = 0; i < screens.Length; i++)
        {
            var screen = screens[i];
            var widget = new ShadowWidget();

            // Posiciona centralizado horizontal na WorkingArea da tela, flutuando 40px da base
            widget.Left = screen.WorkingArea.X + (screen.WorkingArea.Width / 2.0) - (widget.Width / 2.0);
            widget.Top = screen.WorkingArea.Bottom - widget.Height - 40;

            widget.SetActiveState(false); // todos começam inativos (0.3)

            // Registra HWND no Shadow para não fazer auto-OCR do próprio widget
            widget.SourceInitialized += (s, args) =>
            {
                var helper = new System.Windows.Interop.WindowInteropHelper(widget);
                _shadowService.RegisterOwnWindow(helper.Handle);
            };

            _shadowWidgets.Add(widget);
        }
    }

    private void CloseAllShadowWidgets()
    {
        foreach (var w in _shadowWidgets)
        {
            try { w.Close(); } catch { }
        }
        _shadowWidgets.Clear();
        _activeScreenIndex = -1;
    }

    private void ApplyActiveScreenOpacity()
    {
        for (int i = 0; i < _shadowWidgets.Count; i++)
        {
            _shadowWidgets[i].SetActiveState(i == _activeScreenIndex);
        }
    }

    private void OnActiveScreenChanged(int screenIdx)
    {
        Dispatcher.Invoke(() =>
        {
            _activeScreenIndex = screenIdx;
            ApplyActiveScreenOpacity();
        });
    }

    private void OnShadowSuggestion(string suggestion)
    {
        Dispatcher.Invoke(() =>
        {
            // Mostra o balão APENAS no widget da tela ativa (a do cursor)
            if (_activeScreenIndex >= 0 && _activeScreenIndex < _shadowWidgets.Count)
            {
                var target = _shadowWidgets[_activeScreenIndex];
                if (target.IsVisible) target.ShowSuggestion(suggestion);
            }
        });
    }
}
