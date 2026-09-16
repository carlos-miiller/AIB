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

    // Chave do OpenRouter: sk-or- seguido de pelo menos 20 caracteres. Só formato — quem diz se
    // ela vale é o OpenRouter, na primeira requisição.
    private static readonly Regex _keyRegex = new(@"^sk-or-[a-zA-Z0-9_-]{20,}$", RegexOptions.Compiled);

    private static readonly System.Net.Http.HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private System.Collections.Generic.IReadOnlyList<AIB.Services.Ai.ModeloDoOpenRouter> _catalogo =
        Array.Empty<AIB.Services.Ai.ModeloDoOpenRouter>();

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
            string key = KeyTextBox.Password.Trim();
            NextButton.IsEnabled = _keyRegex.IsMatch(key)
                                   && (OpenRouterModelComboBox.Text ?? "").Trim().Length > 0;
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
        OpenRouterBranch.Visibility = Visibility.Collapsed;
        ValidateStep3SaveButton();
    }

    private async void OpenRouterRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (OpenRouterBranch == null) return;
        OpenRouterBranch.Visibility = Visibility.Visible;
        OllamaBranch.Visibility = Visibility.Collapsed;
        if (ErrorLabel != null) ErrorLabel.Visibility = Visibility.Collapsed;
        ValidateStep3SaveButton();

        if (_catalogo.Count > 0) return;

        // Só modelos que aceitam ferramentas: sem elas a IA não lê nem grava nada.
        _catalogo = (await AIB.Services.Ai.CatalogoDoOpenRouter.ListarAsync(_http)).Where(m => m.UsaFerramentas).ToList();
        OpenRouterModelComboBox.ItemsSource = _catalogo.Select(m => m.Id).ToList();
        OpenRouterModelInfo.Text = _catalogo.Count > 0
            ? "Só aparecem modelos que aceitam ferramentas. Dá para digitar o id."
            : "Não consegui ler o catálogo do OpenRouter. Digite o id do modelo (ex.: provedor/modelo).";
    }

    private void OpenRouterModel_LostFocus(object sender, RoutedEventArgs e)
    {
        string id = (OpenRouterModelComboBox.Text ?? "").Trim();
        var modelo = _catalogo.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
        if (modelo != null) OpenRouterModelInfo.Text = modelo.Resumo();
        ValidateStep3SaveButton();
    }

    private void KeyTextBox_PasswordChanged(object sender, RoutedEventArgs e) => ValidateStep3SaveButton();

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
        string key = KeyTextBox.Password.Trim();
        if (_keyRegex.IsMatch(key))
        {
            if (ErrorLabel != null) ErrorLabel.Visibility = Visibility.Collapsed;
            ValidateStep3SaveButton();
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
                    provider = ProvedoresDeIa.OpenRouter
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
            await SaveOpenRouterBranch();
        }
    }

    private async System.Threading.Tasks.Task SaveOpenRouterBranch()
    {
        string key = KeyTextBox.Password.Trim();
        string modelo = (OpenRouterModelComboBox.Text ?? "").Trim();

        if (!_keyRegex.IsMatch(key) || modelo.Length == 0)
        {
            if (ErrorLabel != null) ErrorLabel.Visibility = _keyRegex.IsMatch(key) ? Visibility.Collapsed : Visibility.Visible;
            if (NextButton != null) NextButton.IsEnabled = false;
            _ = AuditLogService.AppendAsync(new
            {
                ts = DateTime.UtcNow.ToString("o"),
                outcome = "firstrun_invalid_key",
                provider = ProvedoresDeIa.OpenRouter
            });
            return;
        }

        // A chave vai para o cofre DO OPENROUTER, nunca para as configurações.
        string result = await CredentialService.StoreCredentialAsync(
            ProvedoresDeIa.SistemaDaChave(ProvedoresDeIa.OpenRouter)!, ProvedoresDeIa.NomeDaChave, key);
        if (result.StartsWith("ERRO"))
        {
            ErrorLabel.Text = $"Erro ao salvar: {result}";
            ErrorLabel.Visibility = Visibility.Visible;
            return;
        }

        var settings = _settingsService.LoadSettings();
        var perfil = settings.PerfilDe(ProvedoresDeIa.OpenRouter);
        perfil.Modelo = modelo;

        // A janela do modelo, quando o catálogo diz — a mesma regra da tela de configurações.
        if (AIB.Services.Ai.CatalogoDoOpenRouter.NoCache(modelo) is { Janela: > 0 } doCatalogo)
            perfil.JanelaDeContexto = Math.Clamp(doCatalogo.Janela, PerfilDeProvedor.JanelaMinima, PerfilDeProvedor.JanelaMaxima);

        settings.Ativar(ProvedoresDeIa.OpenRouter, perfil);
        settings.ApiKey = "use-vault";

        // Quem escolheu nuvem no primeiro arranque pode não ter Ollama: a triagem acompanha, e a
        // página E-mail deixa trocar.
        settings.MailTriageProvider = ProvedoresDeIa.OpenRouter;
        settings.MailTriageModel = modelo;

        if (AgentsListBox.SelectedItem is AgentProfile selectedAgent)
            settings.ActiveCharacter = selectedAgent.DirectoryName;

        _settingsService.SaveSettings(settings.Sanear());

        // Só os quatro últimos caracteres da chave.
        _ = AuditLogService.AppendAsync(new
        {
            ts = DateTime.UtcNow.ToString("o"),
            outcome = "firstrun_saved",
            provider = ProvedoresDeIa.OpenRouter,
            model = modelo,
            key_last4 = key[^4..]
        });

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
        var perfil = settings.PerfilDe(ProvedoresDeIa.Ollama);
        perfil.Modelo = selectedModel;
        settings.Ativar(ProvedoresDeIa.Ollama, perfil);
        settings.ApiKey = "ollama";
        settings.ShadowModelName = selectedModel;   // D-09 mirror by default
        settings.MailTriageProvider = ProvedoresDeIa.Ollama;
        settings.MailTriageModel = selectedModel;
        _settingsService.SaveSettings(settings.Sanear());

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
