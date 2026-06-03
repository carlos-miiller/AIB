using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using AIB.Services;

namespace AIB.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settingsService = new();
    private UserAppSettings _currentSettings;

    public SettingsWindow()
    {
        InitializeComponent();
        _currentSettings = _settingsService.LoadSettings();
        LoadUiValues();
        // Não bloqueia o UI Thread
        Dispatcher.BeginInvoke(new Action(async () => await RefreshModelsAsync()));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        EnableBlur();
    }

    private void Window_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
            this.DragMove();
    }

    private void LoadUiValues()
    {
        ProviderComboBox.Text = string.IsNullOrEmpty(_currentSettings.AiProvider) ? "Ollama" : _currentSettings.AiProvider;
        UrlTextBox.Text = _currentSettings.ApiUrl;
        ModelComboBox.Text = _currentSettings.ModelName;
        ShadowModelComboBox.Text = _currentSettings.ShadowModelName;

        DataDirTextBox.Text = string.IsNullOrEmpty(_currentSettings.DataDirectory) 
            ? DirectoryService.DataDir : _currentSettings.DataDirectory;
        TempDirTextBox.Text = string.IsNullOrEmpty(_currentSettings.TempDirectory) 
            ? DirectoryService.TempDir : _currentSettings.TempDirectory;

        int userLevel = LevelService.GetLevel(_currentSettings.MessageCount);
        int maxTokens = LevelService.GetMaxTokensForLevel(userLevel);
        MaxHistoryTextBox.Text = maxTokens.ToString();
        ConfirmCmdCheckBox.IsChecked = _currentSettings.ConfirmDangerousCommands;
        EphemeralSkillCheckBox.IsChecked = _currentSettings.EphemeralSkillContext;
        SendSystemPromptCheckBox.IsChecked = _currentSettings.SendSystemPrompt;
        EnableIntelligentToolsCheckBox.IsChecked = _currentSettings.EnableIntelligentTools;
        ShadowAssistantEnabledCheckBox.IsChecked = _currentSettings.ShadowAssistantEnabled;
        VerboseLoggingCheckBox.IsChecked = _currentSettings.VerboseConsoleLogging;
        SearchEngineComboBox.Text = _currentSettings.SearchEngine;

        UpdateUiForProvider();
        RefreshKeyTextBoxLabel();
    }

    private void RefreshKeyTextBoxLabel()
    {
        // UI-SPEC Secondary surface — friendly read-only label per sentinel.
        // KeyTextBox no longer carries an editable value; FirstRunWindow owns the write path.
        string key = _currentSettings.ApiKey ?? string.Empty;
        if (key == "use-vault")
        {
            KeyTextBox.Text = "Configurada (cofre DPAPI)";
        }
        else if (key == "ollama")
        {
            KeyTextBox.Text = "(não necessário para Ollama)";
        }
        else if (key.StartsWith("sk-"))
        {
            // Defensive case — should not occur after first boot post-Phase-2 D-11 migration.
            KeyTextBox.Text = "Configurada (legado)";
        }
        else
        {
            KeyTextBox.Text = "(não configurada)";
        }
    }

    private void AlterarChave_Click(object sender, RoutedEventArgs e)
    {
        // Settings path — NOT first-launch hotkey path. Do NOT shut down the app
        // on Cancel (T-02-16 mitigation). User stays in SettingsWindow if they cancel.
        var win = new FirstRunWindow();
        bool? ok = win.ShowDialog();
        if (ok == true)
        {
            _currentSettings = _settingsService.LoadSettings();
            RefreshKeyTextBoxLabel();
        }
    }

    private void ProviderComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateUiForProvider();
    }

    private void UpdateUiForProvider()
    {
        if (AdvancedConnectionPanel == null) return;

        var selected = (ProviderComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? ProviderComboBox.Text;
        
        if (selected == "Google Gemini")
        {
            AdvancedConnectionPanel.Visibility = Visibility.Collapsed;
            // Se o usuário mudou para Google, sugerimos preencher a URL se estiver vazia/ollama
            if (UrlTextBox.Text.Contains("localhost") || UrlTextBox.Text.Contains("127.0.0.1"))
            {
                UrlTextBox.Text = "https://generativelanguage.googleapis.com/v1beta/openai/";
                KeyTextBox.Text = "";
                ModelComboBox.Text = "gemini-1.5-flash";
            }
        }
        else if (selected == "OpenAI")
        {
            // RESEARCH Q1 + Q3 — keep panel visible; clear localhost Ollama default so SDK uses
            // its built-in default endpoint (https://api.openai.com/v1).
            AdvancedConnectionPanel.Visibility = Visibility.Visible;
            if (UrlTextBox.Text == "http://localhost:11434/v1" || UrlTextBox.Text == "http://127.0.0.1:11434/v1")
            {
                UrlTextBox.Text = "";
            }
        }
        else
        {
            AdvancedConnectionPanel.Visibility = Visibility.Visible;
        }
    }

    private async System.Threading.Tasks.Task RefreshModelsAsync()
    {
        LoadingProgress.Visibility = Visibility.Visible;
        try
        {
            var models = await _settingsService.GetOllamaModelsAsync(UrlTextBox.Text);
            if (models.Any())
            {
                var currentModel = ModelComboBox.Text;
                var currentShadowModel = ShadowModelComboBox.Text;
                
                ModelComboBox.ItemsSource = models;
                ShadowModelComboBox.ItemsSource = models;
                
                ModelComboBox.Text = currentModel;
                ShadowModelComboBox.Text = currentShadowModel;
            }
        }
        finally
        {
            LoadingProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void UrlTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _ = RefreshModelsAsync();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _currentSettings.AiProvider = (ProviderComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? ProviderComboBox.Text;
        _currentSettings.ApiUrl = UrlTextBox.Text;
        // T-02-15 mitigation — KeyTextBox is now display-only (friendly label per sentinel).
        // FirstRunWindow owns settings.ApiKey writes (via AlterarChave_Click → ShowDialog).
        _currentSettings.ModelName = ModelComboBox.Text;
        _currentSettings.ShadowModelName = ShadowModelComboBox.Text;

        _currentSettings.DataDirectory = DataDirTextBox.Text;
        _currentSettings.TempDirectory = TempDirTextBox.Text;

        // Limite de tokens agora é dinâmico por nível, não salvamos mais.

        _currentSettings.ConfirmDangerousCommands = ConfirmCmdCheckBox.IsChecked ?? true;
        _currentSettings.EphemeralSkillContext = EphemeralSkillCheckBox.IsChecked ?? true;
        _currentSettings.SendSystemPrompt = SendSystemPromptCheckBox.IsChecked ?? true;
        _currentSettings.EnableIntelligentTools = EnableIntelligentToolsCheckBox.IsChecked ?? true;
        _currentSettings.ShadowAssistantEnabled = ShadowAssistantEnabledCheckBox.IsChecked ?? false;
        _currentSettings.VerboseConsoleLogging = VerboseLoggingCheckBox.IsChecked ?? false;
        _currentSettings.SearchEngine = SearchEngineComboBox.Text;

        _settingsService.SaveSettings(_currentSettings);
        
        // Aplica os diretórios imediatamente
        DirectoryService.ApplyFromSettings(_currentSettings);
        Close();
    }

    private void BrowseDataDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog {
            Title = "Selecione o Diretório de Dados",
            Filter = "Diretórios|*.none",
            FileName = "Selecione esta pasta"
        };
        // Hack simples para selecionar pasta no WPF sem System.Windows.Forms
        var folderDialog = new System.Windows.Forms.FolderBrowserDialog {
            Description = "Selecione o Diretório de Dados do AIB",
            SelectedPath = DataDirTextBox.Text
        };

        if (folderDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            DataDirTextBox.Text = folderDialog.SelectedPath;
        }
    }

    private void BrowseTempDir_Click(object sender, RoutedEventArgs e)
    {
        var folderDialog = new System.Windows.Forms.FolderBrowserDialog {
            Description = "Selecione o Diretório Temporário do AIB",
            SelectedPath = TempDirTextBox.Text
        };

        if (folderDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            TempDirTextBox.Text = folderDialog.SelectedPath;
        }
    }

    private void WipeData_Click(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            "Tem certeza que deseja restaurar o AIB para as configurações de fábrica?\n\nIsso apagará irreversivelmente o histórico de chat, a chave da API do cofre e todas as preferências do usuário.",
            "Atenção - Reset de Fábrica",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            // 1. Apaga Cofre de Credenciais
            CredentialService.WipeAllCredentials();

            // 2. Apaga Histórico de Chat
            ChatHistoryService.ClearHistory();

            // 3. Reseta configurações (salva um objeto limpo)
            var cleanSettings = new UserAppSettings();
            _settingsService.SaveSettings(cleanSettings);

            // 4. Loga e fecha
            _ = AuditLogService.AppendAsync(new
            {
                ts = DateTime.UtcNow.ToString("o"),
                outcome = "factory_reset"
            });

            System.Windows.MessageBox.Show("O AIB foi resetado com sucesso e será encerrado. Por favor, inicie-o novamente.", "Reset Concluído", MessageBoxButton.OK, MessageBoxImage.Information);
            System.Windows.Application.Current.Shutdown();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    #region Blur Effect (Acrylic)
    [DllImport("user32.dll")]
    internal static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    internal void EnableBlur()
    {
        var windowHelper = new WindowInteropHelper(this);
        var accent = new AccentPolicy { AccentState = 4, GradientColor = 0x01000000 };
        var accentStructSize = Marshal.SizeOf(accent);
        var accentPtr = Marshal.AllocHGlobal(accentStructSize);
        Marshal.StructureToPtr(accent, accentPtr, false);

        var data = new WindowCompositionAttributeData
        {
            Attribute = 19,
            SizeOfData = accentStructSize,
            Data = accentPtr
        };

        SetWindowCompositionAttribute(windowHelper.Handle, ref data);
        Marshal.FreeHGlobal(accentPtr);
    }
    #endregion
}
