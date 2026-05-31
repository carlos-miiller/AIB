using System;
using System.Windows;
using System.Windows.Input;
using Hardcodet.Wpf.TaskbarNotification;
using NHotkey;
using NHotkey.Wpf;
using AIB.Views;
using AIB.Services;

namespace AIB;

public partial class App : System.Windows.Application
{
    private TaskbarIcon? _notifyIcon;
    private ChatWindow? _chatWindow;
    private readonly SettingsService _settingsService = new();

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
            DirectoryService.EnsureDirectories();
            var settings = _settingsService.LoadSettings();
            DirectoryService.ApplyFromSettings(settings);

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

            _chatWindow = new ChatWindow();

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
        var win = new FirstRunWindow();
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

    protected override void OnExit(ExitEventArgs e)
    {
        _notifyIcon?.Dispose();
        base.OnExit(e);
    }
}
