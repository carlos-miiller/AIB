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
    private readonly OpenAIService _openAIService;
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

    public ChatWindow()
    {
        InitializeComponent();
        _settingsService = new SettingsService();
        _openAIService = new OpenAIService(_settingsService);
        _openAIService.OnTokenCountChanged += UpdateTokenCounterUI;
        _openAIService.OnWarmupStateChanged += HandleWarmupState;

        _shadowService = new ShadowAssistantService(_openAIService, _settingsService);
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

        _voiceService = new VoiceService();

        // Inicializa UI
        RefreshLevelUI(false);
        int userLevel = LevelService.GetLevel(_settingsService.LoadSettings().MessageCount);
        int maxTokens = LevelService.GetMaxTokensForLevel(userLevel);
        UpdateTokenCounterUI(0, maxTokens);
        AddWelcomeBubble();
        InputBox.Focus();

        ContextSidebarControl.OnRecoverChat += (session) =>
        {
            if (string.IsNullOrWhiteSpace(session.Content)) return;
            
            _openAIService.History.Add(OpenAI.Chat.ChatMessage.CreateUserMessage($"[CONTEXTO RECUPERADO DO CHAT: {session.Title}]\n\n{session.Content}"));
            _openAIService.History.Add(OpenAI.Chat.ChatMessage.CreateAssistantMessage($"Contexto compreendido. Em que posso ajudar com isso?"));
            
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

        // Cores (Bronze, Prata, Ouro, Platina, Diamante)
        WColor rankColor;
        if (lvl <= 2) rankColor = WColor.FromRgb(0xCD, 0x7F, 0x32); // Bronze
        else if (lvl <= 4) rankColor = WColor.FromRgb(0xC0, 0xC0, 0xC0); // Prata
        else if (lvl <= 6) rankColor = WColor.FromRgb(0xFF, 0xD7, 0x00); // Ouro
        else if (lvl <= 8) rankColor = WColor.FromRgb(0x00, 0xFF, 0x7F); // Platina (Verde esmeralda/ciano)
        else rankColor = WColor.FromRgb(0x00, 0xBF, 0xFF); // Diamante (Azul neon)

        var brush = new SolidCB(rankColor);
        LevelText.Foreground = brush;
        XpProgressBar.Foreground = brush;
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

    private void AddUserBubble(string text)
    {
        var border = new Border
        {
            Style = (Style)Resources["UserBubble"],
        };
        border.Background = new LinearGB(
            WColor.FromRgb(0x4B, 0x6B, 0xFF),
            WColor.FromRgb(0x7B, 0x3B, 0xCC),
            new WPoint(0, 0), new WPoint(1, 1));

        border.Child = new TextBlock
        {
            Text = text,
            Foreground = WBrushes.White,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13.5
        };

        MessagesPanel.Children.Add(border);
        AnimateBubbleIn(border);
        ChatScrollViewer.ScrollToEnd();
    }

    private MarkdownViewer AddAgentBubble(string? initialText = null)
    {
        var border = new Border
        {
            Style = (Style)Resources["AgentBubble"],
            Background = new SolidCB(WColor.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidCB(WColor.FromArgb(0x15, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1)
        };

        var viewer = new MarkdownViewer
        {
            Markdown = initialText ?? "",
            Foreground = new SolidCB(WColor.FromRgb(0xE0, 0xE0, 0xF0)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Margin = new Thickness(-6),
        };

        // Força tema escuro nos blocos de código do Markdig
        var inlineCodeStyle = new Style();
        inlineCodeStyle.Setters.Add(new Setter(System.Windows.Documents.TextElement.BackgroundProperty, new SolidCB(WColor.FromRgb(0x30, 0x30, 0x3A))));
        inlineCodeStyle.Setters.Add(new Setter(System.Windows.Documents.TextElement.ForegroundProperty, new SolidCB(WColor.FromRgb(0x8B, 0xE9, 0xFD))));
        inlineCodeStyle.Setters.Add(new Setter(System.Windows.Documents.TextElement.FontFamilyProperty, new System.Windows.Media.FontFamily("Consolas")));

        var blockCodeStyle = new Style();
        blockCodeStyle.Setters.Add(new Setter(System.Windows.Documents.TextElement.BackgroundProperty, new SolidCB(WColor.FromRgb(0x1E, 0x1E, 0x24))));
        blockCodeStyle.Setters.Add(new Setter(System.Windows.Documents.TextElement.ForegroundProperty, new SolidCB(WColor.FromRgb(0xE0, 0xE0, 0xF0))));
        blockCodeStyle.Setters.Add(new Setter(System.Windows.Documents.TextElement.FontFamilyProperty, new System.Windows.Media.FontFamily("Consolas")));

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
        MessagesPanel.Children.Add(border);
        AnimateBubbleIn(border);
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
        var border = new Border
        {
            Style = (Style)Resources["AgentBubble"],
            Background = new SolidCB(WColor.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidCB(WColor.FromArgb(0x15, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
        };

        var dots = new TextBlock
        {
            Text = "●",
            Foreground = new SolidCB(WColor.FromRgb(0x9B, 0x51, 0xE0)),
            FontSize = 12,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4, 0, 4, 0),
        };

        border.Child = dots;
        MessagesPanel.Children.Add(border);
        ChatScrollViewer.ScrollToEnd();

        // Animação 400ms: ● → ●● → ●●● → ●
        int frame = 0;
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(420)
        };
        timer.Tick += (s, e) =>
        {
            frame = (frame + 1) % 3;
            dots.Text = frame switch
            {
                0 => "●",
                1 => "● ●",
                _ => "● ● ●"
            };
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
            var (natives, dynamics) = _openAIService.Registry.GetCategorizedTools();
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
        if (_isSending)
        {
            _openAIService.CancelGeneration();
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
        string? errorText = null;
        
        Border? typingBubble = null;
        System.Windows.Threading.DispatcherTimer? typingTimer = null;
        var idleTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        idleTimer.Tick += (s, ev) =>
        {
            if (typingBubble != null)
            {
                typingTimer?.Stop();
                MessagesPanel.Children.Remove(typingBubble);
                typingBubble = null;
                typingTimer = null;
            }
            idleTimer.Stop();
        };

        try
        {
            var stream = _openAIService.StreamResponseAsync(text, tech =>
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

            // Acumula a resposta completa silenciosamente
            await foreach (var chunk in stream)
            {
                fullText += chunk;

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
                MessagesPanel.Children.Remove(typingBubble);
            }

            // Exibe a resposta completa de uma só vez
            if (errorText != null)
            {
                AddAgentBubble(errorText);
            }
            else if (string.IsNullOrWhiteSpace(fullText))
            {
                AddAgentBubble("*Ação executada com sucesso.*");
            }
            else
            {
                AddAgentBubble(fullText);
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
                string notifyText = errorText ?? (string.IsNullOrWhiteSpace(fullText) ? "*Ação executada com sucesso.*" : fullText.Trim());
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
                VoiceButton.Content = "🎙";
                VoiceButton.IsEnabled = true;
                VoiceButton.ToolTip = "Falar com o AIB (Whisper offline)";
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
            VoiceButton.Content = "🔴";
            VoiceButton.Foreground = new SolidCB(WColor.FromRgb(0xFF, 0x44, 0x44));
            VoiceButton.ToolTip = "Ouvindo... Clique para parar.";
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
            VoiceButton.Content = "🎙";
            VoiceButton.Foreground = new SolidCB(WColor.FromRgb(0x88, 0x88, 0x99));
            VoiceButton.ToolTip = "Falar com o AIB (Whisper offline)";
            StatusBar.Visibility = Visibility.Collapsed;
        }
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
                VoiceButton.Content = "🎙";
                VoiceButton.Foreground = new SolidCB(WColor.FromRgb(0x88, 0x88, 0x99));
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

        if (e.Key == Key.Enter && !_isSending)
        {
            SendButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
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
        var settingsWin = new SettingsWindow();
        settingsWin.Owner = this;
        settingsWin.ShowDialog();
        this.Deactivated += Window_Deactivated; // Retorna o comportamento
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        MessagesPanel.Children.Clear();
        _openAIService.ResetHistory();
        int userLevel = LevelService.GetLevel(_settingsService.LoadSettings().MessageCount);
        int maxTokens = LevelService.GetMaxTokensForLevel(userLevel);
        UpdateTokenCounterUI(0, maxTokens);
        AddWelcomeBubble();
    }

    private WColor GetTokenColor(double percentage)
    {
        if (percentage < 0.5) return WColor.FromRgb(76, 175, 80); // Verde
        if (percentage < 0.8) return WColor.FromRgb(255, 193, 7); // Amarelo
        if (percentage < 0.95) return WColor.FromRgb(255, 152, 0); // Laranja
        return WColor.FromRgb(213, 63, 140); // Rosa Alerta
    }

    private void UpdateTokenCounterUI(int current, int max)
    {
        Dispatcher.Invoke(() =>
        {
            if (max < 5120)
            {
                TokenCounterText.Text = $"{current}/{max} tokens";
            }
            else
            {
                double kCurrent = current / 1000.0;
                double kMax = max / 1000.0;
                TokenCounterText.Text = $"{kCurrent:0.#}k/{kMax:0.#}k tokens";
            }
            
            double percentage = max > 0 ? (double)current / max : 0;
            TokenCounterText.Foreground = new SolidCB(GetTokenColor(percentage));
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
        
        double targetWindowWidth = _isSidebarOpen ? 1065 : 760;
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
        _openAIService?.ResetHistory();
        _voiceService?.Dispose();
        _shadowService?.Stop();
        CloseAllShadowWidgets();
        base.OnClosed(e);
    }

    private void BtnToggleShadow_Click(object sender, RoutedEventArgs e)
    {
        if (!_isShadowModeEnabled)
        {
            var result = System.Windows.MessageBox.Show(
                "O Shadow Assistant roda em segundo plano capturando o texto da sua tela e tentando prever o que você precisa.\n\n" +
                "Como é uma função Alpha, a AIB às vezes pode alucinar ou interpretar a tela erroneamente.\n\n" +
                "Tem certeza que deseja ativar o monitoramento em segundo plano?",
                "Shadow Assistant (Alpha)", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                
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
