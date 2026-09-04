using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Hardcodet.Wpf.TaskbarNotification;
using NHotkey;
using NHotkey.Wpf;
using AIB.Views;
using AIB.Services;
using AIB.Services.Agent;
using AIB.Services.Ai;

namespace AIB;

public partial class App : System.Windows.Application
{
    private TaskbarIcon? _notifyIcon;
    private ChatWindow? _chatWindow;
    private ShadowAssistantWindow? _orbe;

    /// <summary>O item da bandeja que liga o orbe. Guardado para não sair de sincronia.</summary>
    private System.Windows.Controls.MenuItem? _itemDoOrbe;

    // Composition root: os serviços são construídos aqui, uma única vez, e injetados.
    // O SettingsService precisa nascer DEPOIS de EnsureDirectories para enxergar o caminho certo.
    private readonly System.Net.Http.HttpClient _httpClient = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    private SettingsService _settingsService = null!;
    private ToolRegistry _toolRegistry = null!;
    private ChatConfirmationPrompt _confirmationPrompt = null!;
    private TokenCounter _tokenCounter = null!;
    private IToolCallHealer _healer = null!;
    private IChatProviderFactory _providerFactory = null!;
    private AgentLoop _agentLoop = null!;
    private ConversationService _conversation = null!;

    /// <summary>
    /// Liga e desliga o orbe, gravando a escolha. Existe para o orbe poder ser visto sem
    /// passar pela tela de configuracoes — enquanto ele nao tem funcao nenhuma, a bandeja e o
    /// unico lugar de onde da para experimenta-lo.
    /// </summary>
    private void AlternarOrbe(bool ligado)
    {
        var settings = _settingsService.LoadSettings();
        settings.ShadowAssistantEnabled = ligado;
        _settingsService.SaveSettings(settings);

        SincronizarOrbe();
    }

    /// <summary>
    /// Põe o orbe no estado que a configuração manda, agora.
    /// <para>
    /// Existe porque a mesma chave passou a ter DUAS portas: a bandeja e a página Shadow da
    /// tela de configurações. Sem um lugar só aplicando a decisão, salvar nas configurações
    /// gravaria o arquivo e não mudaria nada na tela até o próximo arranque — a chave pareceria
    /// quebrada — e o visto da bandeja continuaria contando a história antiga.
    /// </para>
    /// </summary>
    public static void AplicarEstadoDoOrbe()
    {
        if (Current is App app) app.SincronizarOrbe();
    }

    private void SincronizarOrbe()
    {
        bool ligado = _settingsService.LoadSettings().ShadowAssistantEnabled;

        if (ligado)
        {
            _orbe ??= CriarOrbe();
            _orbe.Show();
        }
        else
        {
            _orbe?.Close();
            _orbe = null;
        }

        if (_itemDoOrbe != null) _itemDoOrbe.IsChecked = ligado;
    }

    /// <summary>
    /// Monta o orbe ja ligado a janela de chat. O nome do personagem vem daqui porque a
    /// janela do orbe nao conhece SettingsService — ela desenha, e quem sabe quem esta ativo
    /// e quem a criou.
    /// </summary>
    private ShadowAssistantWindow CriarOrbe()
    {
        var configuracoes = _settingsService.LoadSettings();

        var orbe = new ShadowAssistantWindow
        {
            NomeDoAgente = configuracoes.ActiveCharacter,
            TetoDeEmails = configuracoes.ShadowMailPreviewCount
        };

        // O orbe manda a mensagem e a janela de chat roda o turno inteiro ESCONDIDA: laco de
        // stream, ferramentas, portao de confirmacao, historico e XP acontecem la, como sempre.
        // O orbe so exibe o texto final. Duas implementacoes de turno divergiriam em qual delas
        // grava o que, e o usuario acabaria com metade da conversa em cada lugar.
        orbe.MensagemEnviada += texto => _chatWindow?.AbrirComMensagem(texto, mostrarJanela: false);

        if (_chatWindow != null)
            _chatWindow.TurnoConcluido += texto => orbe.ResponderTurno(texto);

        return orbe;
    }

    /// <summary>
    /// ANDAIME de demonstracao: enfileira um digest falso para o orbe. Some quando o
    /// MailDigestService existir.
    /// </summary>
    private void SimularDigest()
    {
        if (_orbe == null)
        {
            ShowNotification("AIB", "Ligue o orbe primeiro (menu da bandeja).");
            return;
        }

        _orbe.ComecarAProcessarEmail();

        var relogio = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };

        relogio.Tick += (s, e) =>
        {
            relogio.Stop();
            _orbe?.TerminarDeProcessarEmail(
                "Li os 38 e-mails da manha. Tres precisam de voce hoje; o resto nao pede nada.",
                new[]
                {
                    new MailSummary(
                        "Juridico - contrato Vertex",
                        "Pedem sua assinatura no aditivo ate as 18h de hoje, senao a renovacao volta para a fila do trimestre que vem.",
                        MailUrgency.Maxima,
                        Url: "https://mail.google.com/mail/u/0/#inbox",
                        Account: "corporativo"),
                    new MailSummary(
                        "Marina Costa - revisao do orcamento",
                        "Enviou a planilha com os cortes de infra e quer sua confirmacao antes da reuniao de quinta.",
                        MailUrgency.Media,
                        Account: "corporativo"),
                    new MailSummary(
                        "Notion - resumo semanal",
                        "Relatorio automatico de atividade do workspace. Nada pendente, so numeros da semana.",
                        MailUrgency.Baixa,
                        Account: "pessoal"),
                    new MailSummary("Quarto e-mail", "para exercitar a linha de excedente", MailUrgency.Baixa)
                });
        };

        relogio.Start();
    }

    public void ShowNotification(string title, string message)
    {
        if (_notifyIcon != null)
        {
            _notifyIcon.ShowBalloonTip(title, message, BalloonIcon.Info);
            System.Media.SystemSounds.Beep.Play();
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            GibberishVoiceService.Initialize();
            DirectoryService.EnsureDirectories();
            _settingsService = new SettingsService();

            if (e.Args.Length > 0)
            {
                System.Threading.SynchronizationContext.SetSynchronizationContext(null);
                RunCliCommandAsync(e.Args).GetAwaiter().GetResult();
                return;
            }

            var settings = _settingsService.LoadSettings();
            DirectoryService.ApplyFromSettings(settings);
            // ApplyFromSettings pode ter movido o diretório de dados: o cache aponta para o caminho antigo.
            _settingsService.InvalidateCache();

            // D-11 one-shot migration: force re-entry by overwriting any non-sentinel ApiKey.
            // Idempotent — guard skips already-migrated installs ("use-vault") and pure Ollama installs ("ollama").
            // SECURITY: do NOT copy the previous key into the vault — rotation must happen first (D-12).
            if (settings.ApiKey != "use-vault" && settings.ApiKey != "ollama")
            {
                bool hadKey = !string.IsNullOrEmpty(settings.ApiKey);
                settings.ApiKey = "use-vault";
                _settingsService.SaveSettings(settings);
                _ = AuditLogService.AppendAsync(new
                {
                    ts = DateTime.UtcNow.ToString("o"),
                    outcome = "migration_clear_apikey",
                    previous_key_present = hadKey
                });
                Console.WriteLine("[MIGRATION] settings.ApiKey replaced with 'use-vault' sentinel (D-11).");
            }

            // Portão humano ligado aqui: é o único lugar do app onde existe UI para pedir
            // autorização. Sem este argumento o registry recusa toda ferramenta destrutiva.
            //
            // A pergunta agora acontece DENTRO da conversa (§5.3 da spec de chat), e não numa
            // janela modal. Quem apresenta é a ChatWindow, que se conecta logo abaixo; até lá
            // — e se ela morrer — o prompt recusa por padrão.
            _confirmationPrompt = new ChatConfirmationPrompt();
            _toolRegistry = new ToolRegistry(_confirmationPrompt, _settingsService);
            _tokenCounter = new TokenCounter();
            _healer = new RegexToolCallHealer();
            _providerFactory = new ChatProviderFactory(_httpClient, _healer);
            _agentLoop = new AgentLoop(_toolRegistry, _providerFactory, _settingsService, _tokenCounter);
            _conversation = new ConversationService(_settingsService, _toolRegistry, _agentLoop,
                                                    _tokenCounter, _providerFactory);

            _chatWindow = new ChatWindow(_conversation, _settingsService);

            // A partir daqui o portão tem onde perguntar. Antes desta linha, e depois que a
            // janela morrer, ele recusa por padrão.
            _confirmationPrompt.Conectar(_chatWindow.PerguntarConfirmacaoAsync);

            // MainWindow explícito: o modal de confirmação usa Application.Current.MainWindow
            // como Owner. Sem atribuir, o WPF elege a primeira janela criada — que pode ser a
            // FirstRunWindow já fechada, e definir Owner como janela fechada lança.
            MainWindow = _chatWindow;

            // O orbe do Shadow Assistant. Opt-in: a setting ja nascia false, e ela continua
            // mandando — quem nao ligou nao ganha uma bola nova sobre o desktop depois de
            // atualizar. Ligar/desligar em tempo de execucao vem junto com o resto do estado
            // do orbe; por ora ele e lido uma vez, na abertura.
            if (settings.ShadowAssistantEnabled)
            {
                _orbe = CriarOrbe();
                _orbe.Show();
            }

            _notifyIcon = new TaskbarIcon
            {
                Icon = System.Drawing.SystemIcons.Information,
                ToolTipText = "AIB (Ctrl+Shift+Space)"
            };

            var contextMenu = new System.Windows.Controls.ContextMenu();

            var openItem = new System.Windows.Controls.MenuItem { Header = "✦ Abrir Chat" };
            openItem.Click += (s, ev) => _chatWindow.ToggleWindow();

            var orbeItem = new System.Windows.Controls.MenuItem
            {
                Header = "✦ Shadow no desktop",
                IsCheckable = true,
                IsChecked = _orbe != null
            };
            orbeItem.Click += (s, ev) => AlternarOrbe(orbeItem.IsChecked);
            _itemDoOrbe = orbeItem;

            // ANDAIME — sai quando o vigia de e-mail existir de verdade. Ate la e o unico
            // jeito de ver o pulso, o anel de varredura e a lista do balao na tela, e a §-1 da
            // spec pede que cada passo seja verificavel isolado.
            var exemploItem = new System.Windows.Controls.MenuItem { Header = "Ver exemplo de aviso" };
            exemploItem.Click += (s, ev) => SimularDigest();

            var exitItem = new System.Windows.Controls.MenuItem { Header = "Sair" };
            exitItem.Click += (s, ev) => Current.Shutdown();

            contextMenu.Items.Add(openItem);
            contextMenu.Items.Add(orbeItem);
            contextMenu.Items.Add(exemploItem);
            contextMenu.Items.Add(new System.Windows.Controls.Separator());
            contextMenu.Items.Add(exitItem);

            _notifyIcon.ContextMenu = contextMenu;
            _notifyIcon.TrayLeftMouseDown += (s, ev) => _chatWindow.ToggleWindow();

            try
            {
                HotkeyManager.Current.AddOrReplace(
                    "ToggleChat",
                    Key.Space,
                    ModifierKeys.Control | ModifierKeys.Shift,
                    OnHotkeyDetected);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HOTKEY] Não foi possível registrar o atalho global: {ex.Message}");
            }

            // Aquecimento só depois que a UI existe — nunca de dentro de um construtor.
            _ = _conversation.StartWarmupAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Erro crítico ao iniciar o AIB:\n\n{ex.Message}",
                "AIB — Erro de Inicialização",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Current.Shutdown();
        }
    }

    private void OnHotkeyDetected(object? sender, HotkeyEventArgs e)
    {
        e.Handled = true;
        var settings = _settingsService.LoadSettings();

        // D-01 + D-03 + D-08: provider-aware first-run detector; show FirstRunWindow before ChatWindow.
        if (NeedsFirstRun(settings))
        {
            ShowFirstRunWindow();
            return;
        }

        _chatWindow?.ToggleWindow();
    }

    private static bool NeedsFirstRun(UserAppSettings s)
    {
        // D-03 provider-aware skip — Ollama users never see FirstRunWindow.
        if (s.AiProvider == "Ollama") return false;
        // Vault read; ERRO-prefix on miss (CredentialService never throws).
        // RESEARCH §Pitfall 1: global-fallback false-negative window is narrow and accepted for this phase.
        return CredentialService.RetrieveCredential("openai", "ApiKey").StartsWith("ERRO");
    }

    private void ShowFirstRunWindow()
    {
        var win = new FirstRunWindow(_settingsService);
        bool? ok = win.ShowDialog();
        if (ok == true)
        {
            // D-08 sequencing — ChatWindow only appears after Save.
            _chatWindow?.ToggleWindow();
        }
        else
        {
            // D-04 + Pitfall 6 — parent owns shutdown on UI thread; window only set DialogResult.
            _ = AuditLogService.AppendAsync(new
            {
                ts = DateTime.UtcNow.ToString("o"),
                outcome = "firstrun_cancelled"
            });
            Current.Shutdown();
        }
    }

    private async Task RunCliCommandAsync(string[] args)
    {
        bool success = false;
        if (args.Contains("--test-rag"))
        {
            success = await Services.TestRunner.RunRagTestAsync();
        }
        else if (args.Contains("--test-tool"))
        {
            success = await Services.TestRunner.RunToolTestAsync();
        }
        else if (args.Contains("--test-all"))
        {
            bool ragSuccess = await Services.TestRunner.RunRagTestAsync();
            bool toolSuccess = await Services.TestRunner.RunToolTestAsync();
            success = ragSuccess && toolSuccess;
        }
        else
        {
            Console.WriteLine($"Unknown argument(s): {string.Join(" ", args)}");
            Console.WriteLine("Available test arguments: --test-rag, --test-tool, --test-all");
            Environment.Exit(1);
        }

        Environment.Exit(success ? 0 : 1);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _notifyIcon?.Dispose();
        _httpClient.Dispose();
        base.OnExit(e);
    }
}
