using System;
using System.Windows;
using System.Windows.Input;
using Hardcodet.Wpf.TaskbarNotification;
using NHotkey;
using NHotkey.Wpf;
using AIB.Views;

namespace AIB;

public partial class App : System.Windows.Application
{
    private TaskbarIcon? _notifyIcon;
    private ChatWindow? _chatWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            // Load Environment (.env) looking upwards in the directory tree
            DotNetEnv.Env.TraversePath().Load();

            // Initialize Window
            _chatWindow = new ChatWindow();
            
            // Initialize System Tray
            _notifyIcon = new TaskbarIcon
            {
                Icon = System.Drawing.SystemIcons.Information,
                ToolTipText = "AIB (Ctrl+Shift+Space)"
            };

            var contextMenu = new System.Windows.Controls.ContextMenu();
            
            var openItem = new System.Windows.Controls.MenuItem { Header = "✦  Abrir Chat" };
            openItem.Click += (s, ev) => _chatWindow.ToggleWindow();
            
            var exitItem = new System.Windows.Controls.MenuItem { Header = "Sair" };
            exitItem.Click += (s, ev) => Current.Shutdown();
            
            contextMenu.Items.Add(openItem);
            contextMenu.Items.Add(new System.Windows.Controls.Separator());
            contextMenu.Items.Add(exitItem);

            _notifyIcon.ContextMenu = contextMenu;
            _notifyIcon.TrayLeftMouseDown += (s, ev) => _chatWindow.ToggleWindow();
            
            _notifyIcon.ShowBalloonTip("AIB iniciado", "Pressione Ctrl+Shift+Space para abrir o chat.", BalloonIcon.Info);

            // Initialize Global Hotkey
            try
            {
                HotkeyManager.Current.AddOrReplace("ToggleChat", Key.Space, ModifierKeys.Control | ModifierKeys.Shift, OnHotkeyDetected);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to register hotkey: {ex.Message}", "HotKey Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Crash fatal na inicialização:\n\n{ex.Message}\n\n{ex.StackTrace}", "Erro Crítico", MessageBoxButton.OK, MessageBoxImage.Error);
            Current.Shutdown();
        }
    }

    private void OnHotkeyDetected(object? sender, HotkeyEventArgs e)
    {
        _chatWindow?.ToggleWindow();
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _notifyIcon?.Dispose();
        base.OnExit(e);
    }
}
