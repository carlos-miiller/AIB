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

    // Composition root: os serviços são construídos aqui, uma única vez, e injetados.
    // O SettingsService precisa nascer DEPOIS de EnsureDirectories para enxergar o caminho certo.
    private readonly System.Net.Http.HttpClient _httpClient = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    private SettingsService _settingsService = null!;
    private ToolRegistry _toolRegistry = null!;
    private TokenCounter _tokenCounter = null!;
    private IToolCallHealer _healer = null!;
    private IChatProviderFactory _providerFactory = null!;
    private AgentLoop _agentLoop = null!;
    private ConversationService _conversation = null!;

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
            _toolRegistry = new ToolRegistry(new WpfConfirmationPrompt(), _settingsService);
            _tokenCounter = new TokenCounter();
            _healer = new RegexToolCallHealer();
            _providerFactory = new ChatProviderFactory(_httpClient, _healer);
            _agentLoop = new AgentLoop(_toolRegistry, _providerFactory, _settingsService, _tokenCounter);
            _conversation = new ConversationService(_settingsService, _toolRegistry, _agentLoop,
                                                    _tokenCounter, _providerFactory);

            _chatWindow = new ChatWindow(_conversation, _settingsService);

            // MainWindow explícito: o modal de confirmação usa Application.Current.MainWindow
            // como Owner. Sem atribuir, o WPF elege a primeira janela criada — que pode ser a
            // FirstRunWindow já fechada, e definir Owner como janela fechada lança.
            MainWindow = _chatWindow;

            _notifyIcon = new TaskbarIcon
            {
                Icon = System.Drawing.SystemIcons.Information,
                ToolTipText = "AIB (Ctrl+Shift+Space)"
            };

            var contextMenu = new System.Windows.Controls.ContextMenu();

            var openItem = new System.Windows.Controls.MenuItem { Header = "✦ Abrir Chat" };
            openItem.Click += (s, ev) => _chatWindow.ToggleWindow();

            var exitItem = new System.Windows.Controls.MenuItem { Header = "Sair" };
            exitItem.Click += (s, ev) => Current.Shutdown();

            contextMenu.Items.Add(openItem);
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
