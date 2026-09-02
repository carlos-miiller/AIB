using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AIB.Services;

namespace AIB.Views;

/// <summary>
/// Tela de Configurações — implementação de refactor-interface/tela-configuracoes.html.
/// <para>
/// A lista de campos é normativa (§5, contrato O1): nada é acrescentado, removido, renomeado
/// ou reordenado, e os rótulos são copiados verbatim. O que muda de tela para tela é só o
/// visual; o caminho de persistência continua sendo o <see cref="SettingsService"/>.
/// </para>
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>
    /// Provedores de §5 campo 2. O <c>ChatProviderFactory</c> só distingue Ollama do resto —
    /// os demais vão pelo cliente compatível com a API da OpenAI, mudando a Base URL. Por isso
    /// trocar esta lista é seguro no código.
    /// </summary>
    private static readonly string[] Provedores = { "Ollama", "OpenAI", "Anthropic", "LmStudio" };

    /// <summary>Base URL sugerida ao trocar de provedor (§5 campo 2, "efeito").</summary>
    private static readonly Dictionary<string, string> UrlPadrao = new(StringComparer.Ordinal)
    {
        ["Ollama"] = "http://127.0.0.1:11434/v1",
        ["OpenAI"] = "https://api.openai.com/v1",
        ["Anthropic"] = "https://api.anthropic.com/v1",
        ["LmStudio"] = "http://127.0.0.1:1234/v1"
    };

    private readonly SettingsService _settingsService;
    private UserAppSettings _currentSettings;

    /// <summary>
    /// Enquanto os valores estão sendo carregados nos controles, os eventos de mudança
    /// disparam sozinhos. Sem esta trava, "Salvar" nasceria habilitado — que é justamente o
    /// que A8 proíbe.
    /// </summary>
    private bool _carregando;

    private bool _sujo;

    public SettingsWindow(SettingsService settingsService)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _currentSettings = _settingsService.LoadSettings();

        LoadUiValues();

        // Não bloqueia o UI Thread.
        Dispatcher.BeginInvoke(new Action(async () => await RefreshModelsAsync()));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Carga
    // ─────────────────────────────────────────────────────────────────────

    private void LoadUiValues()
    {
        _carregando = true;
        try
        {
            LoadCharacters();
            LoadProviders();

            UrlTextBox.Text = _currentSettings.ApiUrl;
            ModelComboBox.Text = _currentSettings.ModelName;

            int userLevel = LevelService.GetLevel(_currentSettings.MessageCount);
            MaxHistoryTextBox.Text = LevelService.GetMaxTokensForLevel(userLevel).ToString();

            SendSystemPromptSwitch.IsChecked = _currentSettings.SendSystemPrompt;
            VerboseLoggingSwitch.IsChecked = _currentSettings.VerboseConsoleLogging;

            SelecionarKeepAlive(_currentSettings.KeepAlive);
            RefreshKeyTextBoxLabel();
        }
        finally
        {
            _carregando = false;
        }

        MarcarLimpo();
    }

    private void LoadCharacters()
    {
        var personagens = new List<string> { "Ayano" };

        try
        {
            string dir = DirectoryService.CharactersDir;
            if (System.IO.Directory.Exists(dir))
            {
                personagens = System.IO.Directory.GetDirectories(dir)
                    .Select(System.IO.Path.GetFileName)
                    .Where(nome => !string.IsNullOrEmpty(nome))
                    .Select(nome => nome!)
                    .ToList();

                if (!personagens.Contains("Ayano")) personagens.Insert(0, "Ayano");
            }
        }
        catch
        {
            // Pasta de personagens ilegível: a lista mínima ainda deixa a tela utilizável.
            personagens = new List<string> { "Ayano" };
        }

        CharacterComboBox.ItemsSource = personagens;
        CharacterComboBox.SelectedItem = personagens.Contains(_currentSettings.ActiveCharacter ?? "")
            ? _currentSettings.ActiveCharacter
            : personagens[0];
    }

    private void LoadProviders()
    {
        var lista = Provedores.ToList();

        // Um provedor gravado que saiu da lista (a tela antiga oferecia "Google Gemini")
        // continua aparecendo. Sumir com ele em silêncio trocaria a configuração do usuário
        // sem que ele pedisse.
        string salvo = _currentSettings.AiProvider ?? "";
        if (salvo.Length > 0 && !lista.Contains(salvo, StringComparer.Ordinal))
            lista.Add(salvo);

        ProviderComboBox.ItemsSource = lista;
        ProviderComboBox.SelectedItem = salvo.Length > 0 ? salvo : "Ollama";
    }

    private void SelecionarKeepAlive(string? valor)
    {
        foreach (ComboBoxItem item in KeepAliveComboBox.Items)
        {
            if (item.Tag?.ToString() == valor)
            {
                KeepAliveComboBox.SelectedItem = item;
                return;
            }
        }

        KeepAliveComboBox.SelectedIndex = 1;   // "5 Minutos (Recomendado)"
    }

    private void RefreshKeyTextBoxLabel()
    {
        // A chave atual nunca é exibida (§5 campo 4). O que aparece é o ESTADO dela; a escrita
        // pertence ao fluxo "Alterar".
        string chave = _currentSettings.ApiKey ?? string.Empty;

        bool configurada = chave == "use-vault" || chave.StartsWith("sk-", StringComparison.Ordinal);

        // Marcadores em vez de frase: a coluna tem 210px divididos com o botão "Alterar", e
        // qualquer texto descritivo entra cortado. O estado por extenso vai no ToolTip.
        KeyTextBox.Text = configurada ? "••••••••••••" : "—";

        KeyTextBox.ToolTip = chave switch
        {
            "use-vault" => "Configurada — guardada no cofre DPAPI",
            _ when chave.StartsWith("sk-", StringComparison.Ordinal) => "Configurada — formato legado",
            _ => "Não configurada"
        };
    }

    // ─────────────────────────────────────────────────────────────────────
    // Estado sujo — §4 "GATILHOS DE IsDirty"
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Mudança de seleção, de texto ou de switch sujam. Foco, hover e rolagem não.
    /// </summary>
    private void Campo_Mudou(object sender, RoutedEventArgs e) => MarcarSujo();

    private void MarcarSujo()
    {
        if (_carregando) return;

        _sujo = true;
        SaveButton.IsEnabled = true;
    }

    private void MarcarLimpo()
    {
        _sujo = false;
        SaveButton.IsEnabled = false;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Interações
    // ─────────────────────────────────────────────────────────────────────

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        // §4 TECLADO: Esc cancela; Enter salva, se houver alteração.
        if (e.Key == Key.Escape)
        {
            Cancel_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && _sujo)
        {
            Save_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private void ProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        MarcarSujo();

        if (_carregando) return;
        if (ProviderComboBox.SelectedItem is not string provedor) return;
        if (!UrlPadrao.TryGetValue(provedor, out string? sugerida)) return;

        // Só sugere quando a URL atual é o padrão de OUTRO provedor. Uma URL que o usuário
        // digitou não pode ser sobrescrita por uma troca de combo.
        bool ehPadraoDeOutro = UrlPadrao.Values.Any(u =>
            string.Equals(u, UrlTextBox.Text.Trim(), StringComparison.OrdinalIgnoreCase));

        if (UrlTextBox.Text.Trim().Length == 0 || ehPadraoDeOutro)
            UrlTextBox.Text = sugerida;
    }

    private void UrlTextBox_LostFocus(object sender, RoutedEventArgs e) => _ = RefreshModelsAsync();

    private async System.Threading.Tasks.Task RefreshModelsAsync()
    {
        LoadingProgress.Visibility = Visibility.Visible;
        try
        {
            var modelos = await _settingsService.GetOllamaModelsAsync(UrlTextBox.Text);
            if (modelos.Any())
            {
                string atual = ModelComboBox.Text;

                bool antes = _carregando;
                _carregando = true;
                try
                {
                    ModelComboBox.ItemsSource = modelos;
                    ModelComboBox.Text = atual;
                }
                finally
                {
                    _carregando = antes;
                }
            }
        }
        catch
        {
            // Provedor fora do ar não é erro de configuração: o campo continua editável à mão.
        }
        finally
        {
            LoadingProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void AlterarChave_Click(object sender, RoutedEventArgs e)
    {
        // Caminho de Configurações, NÃO o de primeiro uso: cancelar aqui não encerra o app.
        var win = new FirstRunWindow(_settingsService);
        if (win.ShowDialog() == true)
        {
            _currentSettings = _settingsService.LoadSettings();
            RefreshKeyTextBoxLabel();
        }
    }

    private void DebugLink_Click(object sender, RoutedEventArgs e)
    {
        VerboseLoggingSwitch.IsChecked = true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _currentSettings.ActiveCharacter = CharacterComboBox.SelectedItem?.ToString() ?? "Ayano";
        _currentSettings.AiProvider = ProviderComboBox.SelectedItem?.ToString() ?? "Ollama";
        _currentSettings.ApiUrl = UrlTextBox.Text;
        _currentSettings.ModelName = ModelComboBox.Text;

        // A chave NÃO é escrita a partir daqui: o campo é somente leitura e quem grava é o
        // FirstRunWindow, pelo botão "Alterar".

        if (KeepAliveComboBox.SelectedItem is ComboBoxItem ka && ka.Tag != null)
            _currentSettings.KeepAlive = ka.Tag.ToString() ?? _currentSettings.KeepAlive;

        // O máximo de tokens vem do nível do usuário: é leitura, não preferência.

        _currentSettings.SendSystemPrompt = SendSystemPromptSwitch.IsChecked ?? true;
        _currentSettings.VerboseConsoleLogging = VerboseLoggingSwitch.IsChecked ?? false;

        _settingsService.SaveSettings(_currentSettings);
        MarcarLimpo();

        var chat = System.Windows.Application.Current.Windows.OfType<ChatWindow>().FirstOrDefault();
        chat?.ApplyCharacterUI();

        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // §3.9: descarta alterações e fecha; se houver alteração, confirma o descarte.
        if (_sujo)
        {
            bool descartar = ConfirmDialog.Perguntar(
                this,
                "Descartar as alterações?",
                "As mudanças feitas nesta tela não foram salvas e serão perdidas.",
                dica: "nada foi gravado ainda");

            if (!descartar) return;
        }

        Close();
    }

    private void WipeData_Click(object sender, RoutedEventArgs e)
    {
        // Ação destrutiva: confirmação obrigatória ANTES de executar. O8.
        bool permitido = ConfirmDialog.Perguntar(
            this,
            "Restaurar o AIB para as configurações de fábrica?",
            "Apaga o histórico de chat, a chave da API guardada no cofre e todas as "
                + "preferências. Não é possível desfazer. O AIB será encerrado em seguida.",
            ferramenta: "factory_reset",
            alvo: DirectoryService.MemoryDir,
            dica: "irreversível");

        if (!permitido) return;

        CredentialService.WipeAllCredentials();
        ChatHistoryService.ClearHistory();
        _settingsService.SaveSettings(new UserAppSettings());

        _ = AuditLogService.AppendAsync(new
        {
            ts = DateTime.UtcNow.ToString("o"),
            outcome = "factory_reset"
        });

        System.Windows.MessageBox.Show(
            "O AIB foi resetado e será encerrado. Inicie-o novamente.",
            "Reset concluído",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        System.Windows.Application.Current.Shutdown();
    }
}
