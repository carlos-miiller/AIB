using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using AIB.Services;

using System.Windows.Media;
using System.Globalization;
using System.Windows.Data;
using AIB.Services;

namespace AIB.Views;

public class ValueToStarForegroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int statValue && parameter is string starIndexStr && int.TryParse(starIndexStr, out int starIndex))
        {
            // If stat >= starIndex, filled gold, else empty color (depends on selected state if we wanted, but let's just use gold vs gray)
            return statValue >= starIndex ? new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#e8b84b")) 
                                          : new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#3a3640"));
        }
        return new SolidColorBrush(Colors.Transparent);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public partial class FirstRunWindow : Window
{
    private readonly SettingsService _settingsService;
    private string? _fallbackModel = null;
    private int _currentStep = 1;

    // Regex per D-05: key must start with sk- followed by at least 20 alphanumeric/dash/underscore chars
    private static readonly Regex _keyRegex = new(@"^sk-[a-zA-Z0-9_-]{20,}$", RegexOptions.Compiled);

    public FirstRunWindow(SettingsService settingsService)
    {
        InitializeComponent();
        _settingsService = settingsService;
        
        // 40% width, 80% height relative to the primary screen
        this.Width = System.Windows.SystemParameters.PrimaryScreenWidth * 0.40;
        this.Height = System.Windows.SystemParameters.PrimaryScreenHeight * 0.80;

        UpdateStepsUI();
        LoadAgents();

        // Queue Ollama model refresh on UI thread (default branch is Ollama per D-07 / UI-SPEC S0)
        // Não bloqueia o UI Thread
        Dispatcher.BeginInvoke(new Action(async () => await RefreshModelsAsync()));
    }

    private void LoadAgents()
    {
        var charsDir = DirectoryService.CharactersDir;

        if (!System.IO.Directory.Exists(charsDir)) return;

        var agents = new ObservableCollection<AgentProfile>();
        foreach (var dir in System.IO.Directory.GetDirectories(charsDir))
        {
            var infoPath = System.IO.Path.Combine(dir, "info.json");
            if (System.IO.File.Exists(infoPath))
            {
                try
                {
                    string json = System.IO.File.ReadAllText(infoPath);
                    var profile = System.Text.Json.JsonSerializer.Deserialize<AgentProfile>(json);
                    if (profile != null)
                    {
                        profile.DirectoryName = new System.IO.DirectoryInfo(dir).Name;
                        agents.Add(profile);
                    }
                }
                catch { /* skip invalid */ }
            }
        }
        
        if (AgentsListBox != null)
        {
            AgentsListBox.ItemsSource = agents;
            if (agents.Any())
            {
                AgentsListBox.SelectedIndex = 0;
            }
        }
    }

    private void UpdateStepsUI()
    {
        if (Step1_Welcome != null) Step1_Welcome.Visibility = _currentStep == 1 ? Visibility.Visible : Visibility.Collapsed;
        if (Step2_Profile != null) Step2_Profile.Visibility = _currentStep == 2 ? Visibility.Visible : Visibility.Collapsed;
        if (Step3_Provider != null) Step3_Provider.Visibility = _currentStep == 3 ? Visibility.Visible : Visibility.Collapsed;
        if (Step4_Levels != null) Step4_Levels.Visibility = _currentStep == 4 ? Visibility.Visible : Visibility.Collapsed;
        if (Step5_UserLevels != null) Step5_UserLevels.Visibility = _currentStep == 5 ? Visibility.Visible : Visibility.Collapsed;

        if (BackButton != null) BackButton.Visibility = _currentStep > 1 ? Visibility.Visible : Visibility.Collapsed;
        
        if (NextButton != null)
        {
            if (_currentStep == 5)
            {
                NextButton.Content = "Concluir";
                NextButton.IsEnabled = true;
            }
            else
            {
                NextButton.Content = "Próximo";
                if (_currentStep == 3)
                    ValidateStep3SaveButton();
                else
                    NextButton.IsEnabled = true;
            }
        }
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_currentStep < 5)
        {
            _currentStep++;
            UpdateStepsUI();
        }
        else
        {
            Save_Click(sender, e);
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_currentStep > 1)
        {
            _currentStep--;
            UpdateStepsUI();
        }
    }

    private void ValidateStep3SaveButton()
    {
        if (NextButton == null) return;
        bool ollamaSelected = OllamaRadio.IsChecked == true;
        if (ollamaSelected)
        {
            NextButton.IsEnabled = (ModelComboBox.SelectedItem != null || _fallbackModel != null);
        }
        else
        {
            string key = KeyTextBox.Text.Trim();
            NextButton.IsEnabled = _keyRegex.IsMatch(key);
        }
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            this.DragMove();
    }

    private void AgentsListBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is System.Windows.Controls.ListBox listBox && System.Windows.Media.VisualTreeHelper.GetChild(listBox, 0) is Border border && border.Child is ScrollViewer scrollViewer)
        {
            // Translates vertical wheel delta directly to horizontal pixel scrolling for a smooth fluid effect
            scrollViewer.ScrollToHorizontalOffset(scrollViewer.HorizontalOffset - e.Delta);
            e.Handled = true;
        }
    }

    // ─── Radio button handlers ───────────────────────────────────────────────

    private void OllamaRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (OllamaBranch == null) return;
        OllamaBranch.Visibility = Visibility.Visible;
        OpenAiBranch.Visibility = Visibility.Collapsed;
        ValidateStep3SaveButton();
    }

    private void OpenAiRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (OpenAiBranch == null) return;
        OpenAiBranch.Visibility = Visibility.Visible;
        OllamaBranch.Visibility = Visibility.Collapsed;
        // Reset error label; disable Salvar until valid key is entered
        if (ErrorLabel != null) ErrorLabel.Visibility = Visibility.Collapsed;
        ValidateStep3SaveButton();
    }

    // ─── Ollama model ComboBox ────────────────────────────────────────────────

    private void ModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ValidateStep3SaveButton();
    }

    // ─── Ollama refresh ───────────────────────────────────────────────────────

    private async System.Threading.Tasks.Task RefreshModelsAsync()
    {
        if (LoadingProgress == null) return;

        LoadingProgress.Visibility = Visibility.Visible;
        OllamaErrorBlock.Visibility = Visibility.Collapsed;
        ModelComboBox.Visibility = Visibility.Visible;

        try
        {
            var models = await _settingsService.GetOllamaModelsAsync("http://127.0.0.1:11434/v1");
            if (models.Any())
            {
                ModelComboBox.ItemsSource = models;
                // UI-SPEC S1: models loaded — Salvar still disabled until user picks one
                ModelComboBox.Visibility = Visibility.Visible;
                LoadingProgress.Visibility = Visibility.Collapsed;
            }
            else
            {
                // UI-SPEC state S3: Ollama unreachable or no models installed
                ModelComboBox.Visibility = Visibility.Collapsed;
                OllamaErrorBlock.Visibility = Visibility.Visible;
                LoadingProgress.Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            LoadingProgress.Visibility = Visibility.Collapsed;
        }
    }

    // ─── Retry button (S4) ───────────────────────────────────────────────────

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        // UI-SPEC S4 → S1/S3: hide error, show loading, retry fetch
        OllamaErrorBlock.Visibility = Visibility.Collapsed;
        ModelComboBox.Visibility = Visibility.Visible;
        _ = RefreshModelsAsync();
    }

    // ─── Fallback link (S5) ──────────────────────────────────────────────────

    private void FallbackLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        // Handled ANTES de agir: o Hyperlink carrega um NavigateUri sentinela ("aib:fallback-model")
        // que só existe porque o RequestNavigate não dispara sem ele. Marcando primeiro, uma
        // exceção em ApplyFallbackModel nunca deixa o WPF tentar navegar até o esquema falso.
        e.Handled = true;
        ApplyFallbackModel();
    }

    private void ApplyFallbackModel()
    {
        // UI-SPEC S5: hide error block, show read-only fallback display, enable Salvar
        _fallbackModel = "qwen2.5:7b";
        OllamaErrorBlock.Visibility = Visibility.Collapsed;
        ModelComboBox.Visibility = Visibility.Collapsed;
        FallbackModelDisplay.Visibility = Visibility.Visible;
        if (NextButton != null) NextButton.IsEnabled = true;
    }

    // ─── OpenAI key validation ────────────────────────────────────────────────

    private void KeyTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        ValidateOpenAiKey(emitAuditOnFail: false);
    }

    private void KeyTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ValidateOpenAiKey(emitAuditOnFail: true);
        }
    }

    /// <summary>
    /// Validates the OpenAI key per D-05 regex.
    /// Validation timing rule: only on LostFocus, Enter, or Save click — NOT per-keystroke.
    /// </summary>
    private bool ValidateOpenAiKey(bool emitAuditOnFail = true)
    {
        string key = KeyTextBox.Text.Trim();
        if (_keyRegex.IsMatch(key))
        {
            if (ErrorLabel != null) ErrorLabel.Visibility = Visibility.Collapsed;
            if (NextButton != null) NextButton.IsEnabled = true;
            return true;
        }
        else
        {
            if (ErrorLabel != null) ErrorLabel.Visibility = Visibility.Visible;
            if (NextButton != null) NextButton.IsEnabled = false;
            if (emitAuditOnFail && !string.IsNullOrEmpty(key))
            {
                _ = AuditLogService.AppendAsync(new
                {
                    ts = DateTime.UtcNow.ToString("o"),
                    outcome = "firstrun_invalid_key",
                    provider = "OpenAI"
                });
            }
            return false;
        }
    }

    // ─── Cancel handler ──────────────────────────────────────────────────────

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // D-04 + Pitfall 6: this window ONLY sets DialogResult=false + Close.
        // The parent App.xaml.cs ShowFirstRunWindow inspects the DialogResult,
        // emits the firstrun_cancelled audit, and calls Application.Current.Shutdown().
        // Do NOT emit firstrun_cancelled here — that would double-fire the audit line.
        // Do NOT call Application.Current.Shutdown() here — parent owns shutdown.
        DialogResult = false;
        Close();
    }

    // ─── Save handler ─────────────────────────────────────────────────────────

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        bool ollamaSelected = OllamaRadio.IsChecked == true;

        if (ollamaSelected)
        {
            await SaveOllamaBranch();
        }
        else
        {
            await SaveOpenAiBranch();
        }
    }

    private async System.Threading.Tasks.Task SaveOpenAiBranch()
    {
        string key = KeyTextBox.Text.Trim();

        // Re-validate on Save click (UI-SPEC §Save click step 2)
        if (!_keyRegex.IsMatch(key))
        {
            if (ErrorLabel != null) ErrorLabel.Visibility = Visibility.Visible;
            if (NextButton != null) NextButton.IsEnabled = false;
            _ = AuditLogService.AppendAsync(new
            {
                ts = DateTime.UtcNow.ToString("o"),
                outcome = "firstrun_invalid_key",
                provider = "OpenAI"
            });
            return;
        }

        // D-06: vault write via CredentialService (DPAPI-encrypted)
        string result = await CredentialService.StoreCredentialAsync("openai", "ApiKey", key);
        if (result.StartsWith("ERRO"))
        {
            ErrorLabel.Text = $"Erro ao salvar: {result}";
            ErrorLabel.Visibility = Visibility.Visible;
            return;
        }

        // D-06 sentinel + RESEARCH Q1 + Q3: settings mutation
        var settings = _settingsService.LoadSettings();
        settings.AiProvider = "OpenAI";      // RESEARCH Q1 resolution
        settings.ApiKey = "use-vault";       // D-06 sentinel — never store the real key in profile.dat
        settings.ApiUrl = "";                // RESEARCH Q3 — empty → SDK default (https://api.openai.com/v1)
        _settingsService.SaveSettings(settings);

        // Audit: key_last4 only — NEVER the full key (T-02-07 mitigation)
        string key_last4 = key.Length >= 4 ? key[^4..] : "----";
        _ = AuditLogService.AppendAsync(new
        {
            ts = DateTime.UtcNow.ToString("o"),
            outcome = "firstrun_saved",
            provider = "OpenAI",
            key_last4
        });

        // Save selected agent
        if (AgentsListBox.SelectedItem is AgentProfile selectedAgent)
        {
            var currentSettings = _settingsService.LoadSettings();
            currentSettings.ActiveCharacter = selectedAgent.DirectoryName;
            _settingsService.SaveSettings(currentSettings);
        }

        DialogResult = true;
        Close();
    }

    private async System.Threading.Tasks.Task SaveOllamaBranch()
    {
        // Determine selected model: from ComboBox or fallback
        string? selectedModel = _fallbackModel;
        if (selectedModel == null && ModelComboBox.SelectedItem != null)
            selectedModel = ModelComboBox.SelectedItem.ToString();

        if (string.IsNullOrEmpty(selectedModel))
        {
            // Should not reach here (SaveButton disabled when no model), but defensive
            return;
        }

        // Settings mutation for Ollama branch
        var settings = _settingsService.LoadSettings();
        settings.AiProvider = "Ollama";
        settings.ApiKey = "ollama";
        settings.ModelName = selectedModel;
        settings.ShadowModelName = selectedModel;   // D-09 mirror by default
        _settingsService.SaveSettings(settings);

        _ = AuditLogService.AppendAsync(new
        {
            ts = DateTime.UtcNow.ToString("o"),
            outcome = "firstrun_saved",
            provider = "Ollama",
            model = selectedModel
        });

        // Save selected agent
        if (AgentsListBox.SelectedItem is AgentProfile selectedAgent)
        {
            var currentSettings = _settingsService.LoadSettings();
            currentSettings.ActiveCharacter = selectedAgent.DirectoryName;
            _settingsService.SaveSettings(currentSettings);
        }

        DialogResult = true;
        Close();

        // Satisfy async signature (no actual awaitable work in Ollama branch)
        await System.Threading.Tasks.Task.CompletedTask;
    }
}
