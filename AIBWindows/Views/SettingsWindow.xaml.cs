using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AIB.Services;
using AIB.Services.Mail;
using AIB.Services.Memory;

namespace AIB.Views;

/// <summary>Qual página abrir — §3.10, "rota direta".</summary>
public enum PaginaDeConfiguracoes
{
    Identidade,
    Conexao,
    Email,
    Shadow,

    /// <summary>
    /// O que a IA pode fazer na máquina, e onde. Nasceu porque o confinamento de pasta e a
    /// lista de "sempre permitir" são decisões de segurança, e segurança sem tela própria
    /// acaba sendo uma linha esquecida no meio do Avançado.
    /// </summary>
    Ferramentas,

    /// <summary>O que a conversa lembra: compactação, fatia de contexto e onde isso mora.</summary>
    Memoria,

    Avancado,

    /// <summary>
    /// Diagnóstico: o que a AIB registra e onde. Última da lista de propósito — é a página que
    /// se procura quando algo deu errado, não a que se configura no primeiro dia.
    /// </summary>
    Logs
}

/// <summary>
/// Tela de Configurações — implementação de refactor-interface/tela-configuracoes (3).html.
/// <para>
/// A lista de campos é normativa (§5, contrato O1): nada é acrescentado, removido, renomeado
/// ou reordenado, e os rótulos são copiados verbatim. O que muda de tela para tela é só o
/// visual; o caminho de persistência continua sendo o <see cref="SettingsService"/>.
/// </para>
/// <para>
/// A revisão com menu lateral trocou a página rolável única por quatro páginas. O ViewModel
/// continua sendo UM (§7 A11): trocar de página só troca Visibility, e o estado sujo é global —
/// mexer em "E-mail", voltar em "Avançado" e salvar grava as duas coisas.
/// </para>
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>
    /// O perfil de cada provedor enquanto a tela está aberta. Trocar o provedor no combo guarda
    /// aqui o que estava na tela e mostra o do outro; só o "Salvar" leva ao disco.
    /// </summary>
    private readonly Dictionary<string, PerfilDeProvedor> _perfis = new(StringComparer.Ordinal);

    /// <summary>O provedor cujos campos estão na tela agora.</summary>
    private string _provedorNaTela = ProvedoresDeIa.Ollama;

    /// <summary>O catálogo do OpenRouter, quando já chegou. Dá a janela e o preço do modelo escolhido.</summary>
    private IReadOnlyList<AIB.Services.Ai.ModeloDoOpenRouter> _catalogo = Array.Empty<AIB.Services.Ai.ModeloDoOpenRouter>();

    private static readonly System.Net.Http.HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly SettingsService _settingsService;
    private UserAppSettings _currentSettings;

    /// <summary>
    /// As caixas de e-mail, com as invariantes de §7 A15/A16 aplicadas na própria coleção.
    /// </summary>
    private readonly MailAccountList _contas = new();

    private readonly MailVault _cofre;
    private readonly IMailService _servicoDeEmail;
    private readonly EstadoDasCaixas _estado;

    /// <summary>
    /// Cancela as varreduras em andamento quando a janela fecha. Sem isto, fechar a tela no
    /// meio de uma leitura deixaria sockets abertos esperando o teto de 15s de um servidor
    /// mudo.
    /// </summary>
    private readonly System.Threading.CancellationTokenSource _cancelamento = new();

    /// <summary>
    /// Janela de arranque, em dias — §Decisões do vigia: triar backlog é trabalho jogado fora,
    /// ninguém lê 300 pendências de três meses. Backlog vira comando manual explícito.
    /// <para>
    /// Sai da configuração e é lida A CADA varredura, não guardada no construtor: quem muda o
    /// número é a própria tela, e um valor capturado na abertura faria a varredura seguinte
    /// olhar uma janela que já não é a que a linha da conta anuncia.
    /// </para>
    /// </summary>
    private int JanelaDeArranqueEmDias => _currentSettings.MailWindowDays;

    /// <summary>§9 passo 4: no máximo três caixas lidas ao mesmo tempo.</summary>
    private const int CaixasEmParalelo = 3;

    /// <summary>
    /// Conta cuja senha está sendo trocada. <c>null</c> = o formulário está criando uma conta
    /// nova. O formulário é o MESMO nos dois casos: são os mesmos dois campos, e uma segunda
    /// tela para trocar senha teria de repetir a validação e o fluxo de conexão inteiros.
    /// </summary>
    private MailAccount? _contaEmTrocaDeSenha;

    /// <summary>
    /// Enquanto os valores estão sendo carregados nos controles, os eventos de mudança
    /// disparam sozinhos. Sem esta trava, "Salvar" nasceria habilitado — que é justamente o
    /// que A8 proíbe.
    /// </summary>
    private bool _carregando;

    private bool _sujo;

    /// <summary>
    /// Refaz o processamento do primeiro envio de uma conversa nova e devolve o corpo JSON.
    /// Nulo quando a janela não foi aberta pelo chat: sem o serviço de conversa não há com o
    /// que simular, e o botão diz isso em vez de montar uma aproximação.
    /// </summary>
    public Func<string>? SimularPrimeiroEnvio { get; set; }

    public SettingsWindow(SettingsService settingsService,
                          PaginaDeConfiguracoes pagina = PaginaDeConfiguracoes.Identidade,
                          IMailService? servicoDeEmail = null,
                          MailVault? cofre = null,
                          EstadoDasCaixas? estado = null)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _currentSettings = _settingsService.LoadSettings();

        // Injetáveis para os ensaios. O cofre real fica em ~/.AIB/credentials/mail, e um
        // ensaio que escrevesse lá mexeria nas senhas de verdade do usuário.
        _servicoDeEmail = servicoDeEmail
            ?? new MailKitMailService(_currentSettings.MailTimeoutSeconds);
        _cofre = cofre ?? new MailVault();
        _estado = estado ?? new EstadoDasCaixas();

        ListaDeContas.ItemsSource = _contas.Contas;

        LoadUiValues();
        IrPara(pagina);

        // A varredura de abertura roda SOLTA, em segundo plano. É o que dá sentido ao ponto
        // âmbar: "Verificando…" tem de significar que alguém está verificando, e não um estado
        // parado esperando o usuário adivinhar o que fazer.
        _ = VarrerTodasAsync();

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

            SendSystemPromptSwitch.IsChecked = _currentSettings.SendSystemPrompt;
            SemColetaSwitch.IsChecked = _currentSettings.OpenRouterSemColetaDeDados;
            ProvedorFixoComboBox.Text = _currentSettings.OpenRouterProvedorFixo;
            VerboseLoggingSwitch.IsChecked = _currentSettings.VerboseConsoleLogging;

            ShadowAssistantSwitch.IsChecked = _currentSettings.ShadowAssistantEnabled;
            ShadowMailSwitch.IsChecked = _currentSettings.ShadowHandlesMail;
            AtualizarAjudaDoShadow();

            IntelligentToolsSwitch.IsChecked = _currentSettings.EnableIntelligentTools;
            ConfirmDangerousSwitch.IsChecked = _currentSettings.ConfirmDangerousCommands;
            WriteRootsTextBox.Text = _currentSettings.WriteRoots;
            AtualizarPastasPermitidas();
            AtualizarAutorizacoes();
            ExecutionLogSwitch.IsChecked = _currentSettings.ExecutionLogging;
            CompactionLogSwitch.IsChecked = _currentSettings.CompactionLogging;
        KeepAssistantSpeechSwitch.IsChecked = _currentSettings.KeepAssistantSpeech;
        ThinkingInHistorySwitch.IsChecked = _currentSettings.ThinkingInHistory;
        MailTriageThinkingSwitch.IsChecked = _currentSettings.MailTriageThinking;

            TriagemProvedorComboBox.ItemsSource = ProvedoresDeIa.Todos.ToList();
            TriagemProvedorComboBox.SelectedItem = _currentSettings.MailTriageProvider;
            TriagemModeloComboBox.Text = _currentSettings.MailTriageModel;
            AtualizarAvisoDaTriagem();

            ShadowMailPreviewTextBox.Text = _currentSettings.ShadowMailPreviewCount.ToString();
            MailWindowTextBox.Text = _currentSettings.MailWindowDays.ToString();
            MailTimeoutTextBox.Text = _currentSettings.MailTimeoutSeconds.ToString();
        MailJournalTextBox.Text = _currentSettings.MailJournalDays.ToString();
            MaxIterationsTextBox.Text = _currentSettings.MaxTurnIterations.ToString();
            CompactionTriggerTextBox.Text = ParaPorcento(_currentSettings.CompactionTrigger);
            MemoryFractionTextBox.Text = ParaPorcento(_currentSettings.MemoryFraction);
            MostrarMemoria(_currentSettings);

            RefreshKeyTextBoxLabel();
            CarregarContas();
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
        _perfis.Clear();
        foreach (var provedor in ProvedoresDeIa.Todos)
            _perfis[provedor] = _currentSettings.PerfilDe(provedor);

        // Provedor gravado que a AIB não fala mais já foi convertido ao carregar as configurações
        // (UserAppSettings.Sanear); aqui só existem os dois.
        string salvo = ProvedoresDeIa.Todos.Contains(_currentSettings.AiProvider)
            ? _currentSettings.AiProvider
            : ProvedoresDeIa.Ollama;

        ProviderComboBox.ItemsSource = ProvedoresDeIa.Todos.ToList();
        _provedorNaTela = salvo;
        ProviderComboBox.SelectedItem = salvo;
        MostrarPerfil(salvo);
    }

    /// <summary>
    /// Põe na tela o perfil de <paramref name="provedor"/>: os campos dele, com a ajuda no sentido
    /// dele. Os campos do outro provedor somem — nada de keep-alive no OpenRouter, nem de chave no
    /// Ollama.
    /// </summary>
    private void MostrarPerfil(string provedor)
    {
        bool antes = _carregando;
        _carregando = true;
        try
        {
            var perfil = _perfis[provedor];
            bool openRouter = provedor == ProvedoresDeIa.OpenRouter;

            PainelOllama.Visibility = openRouter ? Visibility.Collapsed : Visibility.Visible;
            PainelOpenRouter.Visibility = openRouter ? Visibility.Visible : Visibility.Collapsed;

            if (openRouter)
            {
                OpenRouterUrlTextBox.Text = ProvedoresDeIa.UrlDoOpenRouter;
                OpenRouterModelComboBox.Text = perfil.Modelo;
                _modeloDaJanela = perfil.Modelo;
            }
            else
            {
                UrlTextBox.Text = perfil.Url;
                ModelComboBox.Text = perfil.Modelo;
                SelecionarKeepAlive(perfil.KeepAlive);
            }

            JanelaTextBox.Text = perfil.JanelaDeContexto.ToString();
            JanelaAjuda.Text = openRouter
                ? "Quanto da conversa a AIB manda por turno — base dos orçamentos por nível. Não passa da janela do modelo, e cada token dela é pago."
                : "O num_ctx pedido ao Ollama, e a base dos orçamentos por nível. Sem GPU, cada 16 mil tokens custam ~0,65 GB de RAM; mudar recarrega o modelo.";

            RaciocinioComboBox.ItemsSource = ProvedoresDeIa.OpcoesDeRaciocinio(provedor)
                .Select(o => new { o.Valor, o.Rotulo }).ToList();
            RaciocinioComboBox.SelectedValue = perfil.Raciocinio;
            RaciocinioAjuda.Text = openRouter
                ? "Esforço de raciocínio pedido ao modelo (parâmetro reasoning). Os tokens de raciocínio são cobrados como saída. O resumo e a triagem sempre pedem desligado."
                : "O modelo pensa antes de responder. Sem GPU custa minutos por turno — medido: 953 tokens de pensamento em 12 minutos para zero texto.";

            AtualizarOrcamento();
            AtualizarAjudaDoModelo();
        }
        finally
        {
            _carregando = antes;
        }

        _ = openRouterOuOllama(provedor);

        async System.Threading.Tasks.Task openRouterOuOllama(string p)
        {
            if (p == ProvedoresDeIa.OpenRouter) await CarregarCatalogoAsync();
            else await RefreshModelsAsync();
        }
    }

    /// <summary>Recolhe o que está na tela para o perfil do provedor mostrado.</summary>
    private void ColherPerfil(string provedor)
    {
        var perfil = _perfis[provedor];
        bool openRouter = provedor == ProvedoresDeIa.OpenRouter;

        if (openRouter)
        {
            perfil.Modelo = (OpenRouterModelComboBox.Text ?? "").Trim();
        }
        else
        {
            perfil.Url = (UrlTextBox.Text ?? "").Trim();
            perfil.Modelo = (ModelComboBox.Text ?? "").Trim();
            if (KeepAliveComboBox.SelectedItem is ComboBoxItem ka && ka.Tag != null)
                perfil.KeepAlive = ka.Tag.ToString() ?? perfil.KeepAlive;
        }

        perfil.JanelaDeContexto = Numero(JanelaTextBox, perfil.JanelaDeContexto);
        if (RaciocinioComboBox.SelectedValue is string r) perfil.Raciocinio = r;

        perfil.Sanear(provedor);
    }

    /// <summary>O orçamento do nível acompanha a janela digitada, antes mesmo de salvar.</summary>
    private void AtualizarOrcamento()
    {
        if (MaxHistoryTextBox == null) return;

        int janela = int.TryParse(JanelaTextBox.Text, out int j)
            ? Math.Clamp(j, PerfilDeProvedor.JanelaMinima, PerfilDeProvedor.JanelaMaxima)
            : PerfilDeProvedor.JanelaPadrao;

        int nivel = LevelService.GetLevel(_currentSettings.MessageCount);

        // A MESMA conta do LevelService, sobre a janela da tela e não a em vigor.
        int piso = janela / 4, teto = janela * 3 / 4;
        int passo = (teto - piso) / (LevelService.NivelMaximo - 1);
        MaxHistoryTextBox.Text = (piso + (Math.Clamp(nivel, 1, LevelService.NivelMaximo) - 1) * passo).ToString();
    }

    private void Janela_Mudou(object sender, TextChangedEventArgs e)
    {
        MarcarSujo();
        AtualizarOrcamento();
        AtualizarAjudaDoModelo();
    }

    /// <summary>
    /// A linha de ajuda do modelo do OpenRouter: janela, preço e se raciocina — e o aviso quando a
    /// janela pedida passa da do modelo, que o OpenRouter recusaria.
    /// </summary>
    private void AtualizarAjudaDoModelo()
    {
        if (OpenRouterModeloAjuda == null) return;

        string id = (OpenRouterModelComboBox.Text ?? "").Trim();
        var modelo = _catalogo.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

        const string padrao = "Só aparecem modelos que aceitam ferramentas — sem elas a IA não lê nem grava nada. Dá para digitar o id.";

        if (modelo == null)
        {
            OpenRouterModeloAjuda.Text = id.Length > 0 && _catalogo.Count > 0
                ? "Este id não está no catálogo do OpenRouter. Confira a grafia."
                : padrao;
            return;
        }

        string texto = modelo.Resumo();
        if (int.TryParse(JanelaTextBox.Text, out int janela) && modelo.Janela > 0 && janela > modelo.Janela)
            texto += $". A janela de contexto abaixo ({janela:N0}) passa da deste modelo.";

        OpenRouterModeloAjuda.Text = texto;
    }

    /// <summary>
    /// O modelo cuja janela está na tela. Trocar de modelo traz a janela dele; perder o foco da
    /// caixa com o mesmo modelo não mexe no número que você ajustou à mão.
    /// </summary>
    private string _modeloDaJanela = "";

    private void OpenRouterModelo_Mudou(object sender, RoutedEventArgs e)
    {
        MarcarSujo();

        string id = (OpenRouterModelComboBox.SelectedItem as string ?? OpenRouterModelComboBox.Text ?? "").Trim();

        // A janela segue o modelo escolhido: o catálogo diz quanto ele aguenta, e 32 mil num
        // modelo de 160 mil desperdiçava a folga que no OpenRouter não custa minutos de prefill.
        if (!_carregando && id.Length > 0 && !string.Equals(id, _modeloDaJanela, StringComparison.OrdinalIgnoreCase))
        {
            var modelo = _catalogo.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
            if (modelo is { Janela: > 0 })
            {
                _modeloDaJanela = id;
                JanelaTextBox.Text = Math.Clamp(modelo.Janela, PerfilDeProvedor.JanelaMinima, PerfilDeProvedor.JanelaMaxima).ToString();
            }
        }

        AtualizarAjudaDoModelo();
        _ = CarregarProvedoresDoModeloAsync(id);
    }

    /// <summary>
    /// A lista do "Provedor preferido": os provedores que servem o modelo na tela. Só sugere — a
    /// caixa continua editável, e o que estiver escrito fica mesmo que o modelo não o liste.
    /// </summary>
    private async System.Threading.Tasks.Task CarregarProvedoresDoModeloAsync(string? id = null)
    {
        id ??= (OpenRouterModelComboBox.Text ?? "").Trim();
        var provedores = await AIB.Services.Ai.CatalogoDoOpenRouter.ProvedoresDoModeloAsync(_http, id);

        // O modelo pode ter mudado enquanto a lista vinha.
        if (!string.Equals(id, (OpenRouterModelComboBox.Text ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) return;

        bool antes = _carregando;
        _carregando = true;
        try
        {
            string atual = ProvedorFixoComboBox.Text;
            ProvedorFixoComboBox.ItemsSource = provedores;
            ProvedorFixoComboBox.Text = atual;
        }
        finally
        {
            _carregando = antes;
        }
    }

    /// <summary>O catálogo do OpenRouter nas duas listas que o usam: modelo da conversa e da triagem.</summary>
    private async System.Threading.Tasks.Task CarregarCatalogoAsync()
    {
        if (_catalogo.Count > 0) { EncherModelosDaTriagem(); await CarregarProvedoresDoModeloAsync(); return; }

        OpenRouterLoadingProgress.Visibility = Visibility.Visible;
        try
        {
            var todos = await AIB.Services.Ai.CatalogoDoOpenRouter.ListarAsync(_http);
            _catalogo = todos.Where(m => m.UsaFerramentas).ToList();

            bool antes = _carregando;
            _carregando = true;
            try
            {
                string atual = OpenRouterModelComboBox.Text;
                OpenRouterModelComboBox.ItemsSource = _catalogo.Select(m => m.Id).ToList();
                OpenRouterModelComboBox.Text = atual;
            }
            finally
            {
                _carregando = antes;
            }

            AtualizarAjudaDoModelo();
            EncherModelosDaTriagem();
            await CarregarProvedoresDoModeloAsync();
        }
        finally
        {
            OpenRouterLoadingProgress.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Os campos da aba Memória que não são porcentagem.</summary>
    private void MostrarMemoria(UserAppSettings s)
    {
        SelecionarPorTag(MemoriaQuemEscreveComboBox, s.MemoriaComModelo ? "modelo" : "codigo");
        TurnosPorCapituloTextBox.Text = s.TurnosPorCapitulo.ToString();
        SelecionarPorTag(CapitulosPorAtoComboBox, s.CapitulosPorAto.ToString());
        SelecionarPorTag(EsconderResultadosComboBox, s.EsconderResultadosDepoisDe.ToString());
    }

    /// <summary>
    /// Seleciona o item com a Tag; valor que não está na lista (editado à mão no arquivo) cai no
    /// primeiro, que é sempre o padrão.
    /// </summary>
    private static void SelecionarPorTag(System.Windows.Controls.ComboBox combo, string valor)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (item.Tag?.ToString() == valor)
            {
                combo.SelectedItem = item;
                return;
            }
        }

        combo.SelectedIndex = 0;
    }

    private static string? TagDe(System.Windows.Controls.ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString();

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
        // A chave nunca é exibida (§5 campo 4). O que aparece é o ESTADO dela, lido do cofre DO
        // OPENROUTER — o sentinela "use-vault" das configurações dizia "configurada" mesmo quando
        // a chave guardada era de outro serviço.
        bool configurada = AIB.Services.Ai.ChatProviderFactory.ChaveDe(ProvedoresDeIa.OpenRouter).Length > 0;

        // Marcadores em vez de frase: a coluna é dividida com o botão "Alterar".
        KeyTextBox.Text = configurada ? "••••••••••••" : "—";
        KeyTextBox.ToolTip = configurada ? "Configurada — guardada no cofre DPAPI" : "Não configurada";
        AlterarChaveBotao.Content = configurada ? "Alterar" : "Adicionar";
    }

    private void NovaChave_Mudou(object sender, RoutedEventArgs e)
    {
        GuardarChaveBotao.IsEnabled = NovaChaveBox.Password.Trim().Length >= 20;
        ChaveErro.Visibility = Visibility.Collapsed;
    }

    private void CancelarChave_Click(object sender, RoutedEventArgs e)
    {
        NovaChaveBox.Clear();
        NovaChaveLinha.Visibility = Visibility.Collapsed;
        ChaveErro.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Guarda a chave AGORA, no cofre do OpenRouter, como as senhas de e-mail — não espera o
    /// "Salvar". A chave não é uma preferência da tela: é uma credencial, e "Cancelar" a tela não
    /// deveria deixar para trás uma chave digitada e perdida.
    /// </summary>
    private async void GuardarChave_Click(object sender, RoutedEventArgs e)
    {
        string chave = NovaChaveBox.Password.Trim();

        if (!chave.StartsWith("sk-or-", StringComparison.Ordinal))
        {
            ChaveErro.Text = "Não parece uma chave do OpenRouter: elas começam com sk-or-.";
            ChaveErro.Visibility = Visibility.Visible;
            return;
        }

        string resultado = await CredentialService.StoreCredentialAsync(
            ProvedoresDeIa.SistemaDaChave(ProvedoresDeIa.OpenRouter)!, ProvedoresDeIa.NomeDaChave, chave);

        if (resultado.StartsWith("ERRO", StringComparison.Ordinal))
        {
            ChaveErro.Text = resultado;
            ChaveErro.Visibility = Visibility.Visible;
            return;
        }

        // Só os quatro últimos caracteres: o bastante para reconhecer qual chave foi, nada de uso.
        _ = AuditLogService.AppendAsync(new
        {
            ts = DateTime.UtcNow.ToString("o"),
            outcome = "chave_guardada",
            provider = ProvedoresDeIa.OpenRouter,
            key_last4 = chave[^4..]
        });

        NovaChaveBox.Clear();
        NovaChaveLinha.Visibility = Visibility.Collapsed;
        RefreshKeyTextBoxLabel();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Estado sujo — §4 "GATILHOS DE IsDirty"
    // ─────────────────────────────────────────────────────────────────────

    // ─────────────────────────────────────────────────────────────────────
    // §3.10  Menu lateral
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Abre uma página específica — §3.10, "rota direta".</summary>
    public void IrPara(PaginaDeConfiguracoes pagina)
    {
        var botao = pagina switch
        {
            PaginaDeConfiguracoes.Conexao => NavConexao,
            PaginaDeConfiguracoes.Email => NavEmail,
            PaginaDeConfiguracoes.Shadow => NavShadow,
            PaginaDeConfiguracoes.Ferramentas => NavFerramentas,
            PaginaDeConfiguracoes.Memoria => NavMemoria,
            PaginaDeConfiguracoes.Avancado => NavAvancado,
            PaginaDeConfiguracoes.Logs => NavLogs,
            _ => NavIdentidade
        };

        botao.IsChecked = true;
    }

    /// <summary>Página visível agora. Diagnóstico e ensaio.</summary>
    public PaginaDeConfiguracoes PaginaAtiva =>
        NavConexao.IsChecked == true ? PaginaDeConfiguracoes.Conexao :
        NavEmail.IsChecked == true ? PaginaDeConfiguracoes.Email :
        NavShadow.IsChecked == true ? PaginaDeConfiguracoes.Shadow :
        NavFerramentas.IsChecked == true ? PaginaDeConfiguracoes.Ferramentas :
        NavMemoria.IsChecked == true ? PaginaDeConfiguracoes.Memoria :
        NavAvancado.IsChecked == true ? PaginaDeConfiguracoes.Avancado :
        NavLogs.IsChecked == true ? PaginaDeConfiguracoes.Logs :
        PaginaDeConfiguracoes.Identidade;

    /// <summary>
    /// Troca de página. Só mexe em Visibility — §7 A11: não instanciar um ViewModel por
    /// página e não resetar campo nenhum ao navegar. Trocar de página NÃO suja e NÃO descarta.
    /// </summary>
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        // Durante o InitializeComponent o IsChecked="True" do primeiro item dispara antes de
        // as páginas existirem.
        if (PaginaIdentidade == null) return;

        PaginaIdentidade.Visibility = Visibilidade(NavIdentidade);
        PaginaConexao.Visibility = Visibilidade(NavConexao);
        PaginaEmail.Visibility = Visibilidade(NavEmail);
        PaginaShadow.Visibility = Visibilidade(NavShadow);
        PaginaFerramentas.Visibility = Visibilidade(NavFerramentas);
        PaginaMemoria.Visibility = Visibilidade(NavMemoria);
        PaginaAvancado.Visibility = Visibilidade(NavAvancado);
        PaginaLogs.Visibility = Visibilidade(NavLogs);

        if (NavLogs.IsChecked == true) AtualizarPastaDeLogs();

        // Recalculado A CADA VISITA, e não uma vez na abertura: a lista de autorizações cresce
        // enquanto a tela está aberta, e a pasta de sessões também.
        if (NavFerramentas.IsChecked == true)
        {
            AtualizarPastasPermitidas();
            AtualizarAutorizacoes();
        }

        if (NavMemoria.IsChecked == true) AtualizarPastaDeMemoria();

        // A página Shadow depende de coisa que muda em OUTRA página: conectar uma caixa
        // acontece em E-mail. Reavaliar ao entrar é o que faz a ajuda parar de dizer "nenhuma
        // caixa conectada" assim que ela passa a existir, sem fechar e reabrir a tela.
        if (NavShadow.IsChecked == true) AtualizarAjudaDoShadow();
    }

    private static Visibility Visibilidade(System.Windows.Controls.Primitives.ToggleButton botao) =>
        botao.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    // ─────────────────────────────────────────────────────────────────────
    // §3.12  Lista de contas de e-mail
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>As contas na tela. Diagnóstico e ensaio.</summary>
    public MailAccountList Contas => _contas;

    private void CarregarContas()
    {
        var vindas = new List<MailAccount>();

        foreach (var gravada in _currentSettings.MailAccounts ?? new List<MailAccountSettings>())
        {
            var conta = new MailAccount
            {
                Address = gravada.Address,
                ImapHost = gravada.ImapHost,
                ImapPort = gravada.ImapPort,
                UseSsl = gravada.UseSsl,
                IsPrimary = gravada.IsPrimary,
                HasPassword = _cofre.Existe(gravada.Address)
            };

            // Status NÃO é gravado (§9 passo 2): dizer "Conectada" na abertura seria afirmar
            // algo que ninguém verificou desde a sessão passada.
            conta.Status = MailAccountStatus.Checking;
            conta.StatusText = TextoDeEstadoEmRepouso(conta.HasPassword, gravada.ImapHost, gravada.ImapPort);

            vindas.Add(conta);
        }

        _contas.Repovoar(vindas);

        Console.WriteLine($"[EMAIL] {_contas.Count} caixa(s) configurada(s), "
                          + $"{_contas.Contas.Count(c => c.HasPassword)} com senha no cofre. "
                          + $"Leitura por IMAP: {(_servicoDeEmail.Disponivel ? "disponível" : "INDISPONÍVEL — " + _servicoDeEmail.MotivoDaIndisponibilidade)}.");
    }

    /// <summary>
    /// O que a segunda linha da conta diz quando nada está acontecendo com ela.
    /// <para>
    /// "Ainda não lida nesta sessão" PROMETE uma leitura que vem. Enquanto não existe serviço
    /// de IMAP, a frase certa é outra: não há ciclo de leitura para rodar. Dizer a primeira nos
    /// dois casos faz o usuário esperar por algo que nunca vai acontecer e, pior, desconfiar da
    /// própria senha — foi exatamente o que aconteceu.
    /// </para>
    /// <para>
    /// A escolha vem da CAPACIDADE do serviço, e não de uma constante: quando o MailKit entrar,
    /// a frase muda sozinha, sem ninguém ter de lembrar de trocar a string.
    /// </para>
    /// </summary>
    private string TextoDeEstadoEmRepouso(bool temSenha, string host, int porta)
    {
        if (!temSenha) return "senha de app ausente — use “alterar senha de app”";

        return _servicoDeEmail.Disponivel
            ? $"{host}:{porta} · ainda não lida nesta sessão"
            : $"{host}:{porta} · {_servicoDeEmail.MotivoDaIndisponibilidade}";
    }

    /// <summary>
    /// Grava a lista de contas AGORA — §7 A15: adicionar, remover e promover não esperam o
    /// "Salvar" do rodapé.
    /// <para>
    /// Grava a partir do que está EM DISCO, e não do objeto em edição. Se usasse o objeto em
    /// edição, acrescentar uma conta persistiria de carona qualquer alteração pendente nas
    /// outras páginas — exatamente o que o "Salvar" desabilitado promete que não aconteceu.
    /// </para>
    /// </summary>
    private void PersistirContas()
    {
        var doDisco = _settingsService.LoadSettings();
        doDisco.MailAccounts = _contas.Contas.Select(ParaGravacao).ToList();
        _settingsService.SaveSettings(doDisco);

        // O objeto em edição acompanha, senão um "Salvar" posterior gravaria a lista antiga.
        _currentSettings.MailAccounts = doDisco.MailAccounts.Select(c => c.Clone()).ToList();
    }

    private static MailAccountSettings ParaGravacao(MailAccount conta) => new()
    {
        Address = conta.Address,
        ImapHost = conta.ImapHost,
        ImapPort = conta.ImapPort,
        UseSsl = conta.UseSsl,
        IsPrimary = conta.IsPrimary
    };

    private void AdicionarConta_Click(object sender, RoutedEventArgs e)
    {
        _contaEmTrocaDeSenha = null;
        AbrirFormularioDeConta(enderecoFixo: null);
    }

    private void AlterarSenhaDaConta_Click(object sender, RoutedEventArgs e)
    {
        if (ContaDoBotao(sender) is not MailAccount conta) return;

        _contaEmTrocaDeSenha = conta;
        AbrirFormularioDeConta(enderecoFixo: conta.Address);
    }

    private void AbrirFormularioDeConta(string? enderecoFixo)
    {
        NovaContaEndereco.Text = enderecoFixo ?? "";

        // Trocando a senha, o endereço é a CHAVE da conta: editável, ele viraria "renomear a
        // conta", que é outra operação e deixaria o blob antigo órfão no cofre.
        NovaContaEndereco.IsReadOnly = enderecoFixo != null;

        NovaContaSenha.Clear();
        EsconderErroDaConta();

        BotaoAdicionarConta.Visibility = Visibility.Collapsed;
        FormularioDeConta.Visibility = Visibility.Visible;

        AtualizarBotaoConectar();
        (enderecoFixo == null ? (System.Windows.Controls.Control)NovaContaEndereco : NovaContaSenha).Focus();
    }

    private void CancelarNovaConta_Click(object sender, RoutedEventArgs e) => FecharFormularioDeConta();

    private void FecharFormularioDeConta()
    {
        NovaContaEndereco.Clear();
        NovaContaEndereco.IsReadOnly = false;
        NovaContaSenha.Clear();
        EsconderErroDaConta();

        _contaEmTrocaDeSenha = null;
        FormularioDeConta.Visibility = Visibility.Collapsed;
        BotaoAdicionarConta.Visibility = Visibility.Visible;
    }

    private void NovaConta_Mudou(object sender, RoutedEventArgs e) => AtualizarBotaoConectar();

    /// <summary>
    /// §3.12 campo 1: a validação de formato é em LostFocus, não a cada tecla — acusar
    /// "inválido" no terceiro caractere de um endereço que ainda está sendo digitado é ruído.
    /// </summary>
    private void NovaContaEndereco_LostFocus(object sender, RoutedEventArgs e)
    {
        string endereco = NovaContaEndereco.Text.Trim();
        if (endereco.Length == 0) { EsconderErroDaConta(); return; }

        if (!ImapHostGuesser.EnderecoParecevalido(endereco))
            MostrarErroDaConta("endereço de e-mail inválido");
        else if (_contaEmTrocaDeSenha == null && _contas.Contem(endereco))
            MostrarErroDaConta("esta conta já está na lista");
        else
            EsconderErroDaConta();
    }

    private void AtualizarBotaoConectar()
    {
        if (BotaoConectarConta == null) return;

        BotaoConectarConta.IsEnabled =
            NovaContaEndereco.Text.Trim().Length > 0 &&
            NovaContaSenha.Password.Length > 0;
    }

    private async void ConectarConta_Click(object sender, RoutedEventArgs e)
    {
        string endereco = NovaContaEndereco.Text.Trim();
        string senha = NovaContaSenha.Password;

        if (endereco.Length == 0 || senha.Length == 0) return;

        if (!ImapHostGuesser.EnderecoParecevalido(endereco))
        {
            MostrarErroDaConta("endereço de e-mail inválido");
            return;
        }

        if (_contaEmTrocaDeSenha == null && _contas.Contem(endereco))
        {
            MostrarErroDaConta("esta conta já está na lista");
            return;
        }

        EsconderErroDaConta();
        BotaoConectarConta.IsEnabled = false;
        BotaoConectarConta.Content = "Conectando…";

        // Rastro no terminal pelo mesmo motivo do pulso do turno: sem ele, "Conectando…" e um
        // ponto âmbar são tudo que se vê, e não dá para saber qual servidor foi tentado nem por
        // que o resultado foi o que foi. A SENHA nunca entra aqui.
        var candidatos = ImapHostGuesser.Candidatos(endereco);
        Console.WriteLine($"[EMAIL] Conectando {endereco} — candidatos: "
                          + string.Join(", ", candidatos.Select(c => $"{c.Host}:{c.Port}")));

        MailLoginResult resultado;
        try
        {
            resultado = await _servicoDeEmail.TestLoginAsync(
                endereco, senha, System.Threading.CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Exceção do cliente NÃO vai para a tela (§3.12): o usuário lê o que pode fazer,
            // não o stack. O detalhe fica no console.
            Console.WriteLine($"[EMAIL] {endereco}: exceção ao testar login — {ex.Message}");
            resultado = new MailLoginResult(
                false, default, "não foi possível entrar. Verifique o e-mail e a senha de app.", false);
        }

        BotaoConectarConta.Content = "Conectar conta";
        AtualizarBotaoConectar();

        if (!resultado.Ok)
        {
            // O formulário FICA aberto e a senha continua no campo — §9 passo 6. Nada é
            // gravado: nem conta, nem senha.
            string motivo = resultado.Erro.Length > 0
                ? resultado.Erro
                : "não foi possível entrar. Verifique o e-mail e a senha de app.";

            Console.WriteLine($"[EMAIL] {endereco}: RECUSADO — {motivo}. Nada gravado.");
            MostrarErroDaConta(motivo);
            return;
        }

        _cofre.Guardar(endereco, senha);

        var alvo = _contaEmTrocaDeSenha;
        if (alvo == null)
        {
            alvo = new MailAccount { Address = endereco };
            var entrada = _contas.Adicionar(alvo);
            if (!entrada.Ok)
            {
                MostrarErroDaConta(entrada.Erro);
                return;
            }
        }

        alvo.ImapHost = resultado.Endpoint.Host;
        alvo.ImapPort = resultado.Endpoint.Port;
        alvo.UseSsl = resultado.Endpoint.UseSsl;
        alvo.HasPassword = true;

        // Verificado = false é o caminho do esqueleto: a conta entra, mas a linha nasce âmbar
        // com "verificação pendente" — nunca verde. §7 A15 pede que só entre conta cujo login
        // passou; enquanto não há IMAP, o desvio fica VISÍVEL na tela em vez de escondido.
        alvo.Status = resultado.Verificado ? MailAccountStatus.Ok : MailAccountStatus.Checking;
        alvo.StatusText = resultado.Verificado
            ? $"{alvo.ImapHost}:{alvo.ImapPort} · conectada agora"
            : TextoDeEstadoEmRepouso(true, alvo.ImapHost, alvo.ImapPort);

        Console.WriteLine($"[EMAIL] {alvo.Address}: senha guardada no cofre, "
                          + $"servidor {alvo.ImapHost}:{alvo.ImapPort} "
                          + $"({(resultado.Verificado ? "login CONFIRMADO" : "login NÃO verificado")}).");

        PersistirContas();
        FecharFormularioDeConta();

        // §6.2.1 de tela-chat: "sucesso -> fecha o modal, a aba passa a mostrar a lista e a
        // PRIMEIRA VARREDURA começa". A senha já está em mão aqui, então não volta ao cofre.
        await VarrerAsync(alvo, senha);
    }

    private void TornarPrincipal_Click(object sender, RoutedEventArgs e)
    {
        if (ContaDoBotao(sender) is not MailAccount conta) return;

        _contas.TornarPrincipal(conta);
        PersistirContas();
    }

    /// <summary>
    /// Esquece até onde já se leu e triou nesta caixa.
    /// <para>
    /// Nasceu de uma necessidade de teste: sem isto, experimentar a triagem depende de chegar
    /// e-mail novo, e a caixa de quem já rodou o digest fica "em dia" o resto do dia. Apagar o
    /// marcador faz a próxima leitura voltar a varrer a janela inteira por data.
    /// </para>
    /// <para>
    /// Não toca na caixa do usuário: o que some é o NOSSO registro de progresso. Nenhuma
    /// mensagem é marcada, movida ou apagada — a leitura sempre foi em EXAMINE.
    /// </para>
    /// </summary>
    private void ResetarLeitura_Click(object sender, RoutedEventArgs e)
    {
        if (ContaDoBotao(sender) is not MailAccount conta) return;

        _estado.Remover(conta.Address);

        conta.Status = MailAccountStatus.Checking;
        conta.StatusText = $"{conta.ImapHost}:{conta.ImapPort} · leitura zerada — a próxima varre tudo";

        Console.WriteLine($"[EMAIL] {conta.Address}: progresso de leitura e triagem apagado; " +
                          "a próxima passada recomeça pela data.");
    }

    private void RemoverConta_Click(object sender, RoutedEventArgs e)
    {
        if (ContaDoBotao(sender) is not MailAccount conta) return;

        if (!_contas.PodeRemover(conta))
        {
            System.Windows.MessageBox.Show(
                "Promova outra conta a principal antes de remover esta.",
                "Conta principal",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        // Apaga uma credencial do disco: confirmação ANTES de executar.
        bool permitido = ConfirmDialog.Perguntar(
            this,
            "Remover esta conta de e-mail?",
            $"A caixa {conta.Address} sai da lista e a senha de app dela é apagada do cofre. "
                + "A conta de e-mail em si não é afetada.",
            dica: "apaga a senha guardada");

        if (!permitido) return;

        var saida = _contas.Remover(conta);
        if (!saida.Ok) return;

        // A senha sai no MESMO comando: conta removida com senha para trás seria credencial
        // órfã em disco, sem nada na interface que a mencionasse.
        _cofre.Remover(conta.Address);

        // O estado sai junto: deixar uidValidity e lastUid para trás faria a mesma caixa,
        // reconectada depois, começar de um UID que ela não tem mais motivo para confiar.
        _estado.Remover(conta.Address);

        Console.WriteLine($"[EMAIL] {conta.Address}: conta removida, senha apagada do cofre e estado descartado.");
        PersistirContas();

        if (ReferenceEquals(_contaEmTrocaDeSenha, conta)) FecharFormularioDeConta();
    }

    /// <summary>
    /// Lê todas as caixas que têm senha no cofre, no máximo três ao mesmo tempo.
    /// </summary>
    private async System.Threading.Tasks.Task VarrerTodasAsync()
    {
        if (!_servicoDeEmail.Disponivel) return;

        var comSenha = _contas.Contas.Where(c => c.HasPassword).ToList();
        if (comSenha.Count == 0) return;

        using var vaga = new System.Threading.SemaphoreSlim(CaixasEmParalelo);

        await System.Threading.Tasks.Task.WhenAll(comSenha.Select(async conta =>
        {
            await vaga.WaitAsync(_cancelamento.Token).ConfigureAwait(true);
            try { await VarrerAsync(conta, senha: null).ConfigureAwait(true); }
            finally { vaga.Release(); }
        })).ConfigureAwait(true);
    }

    /// <summary>
    /// Uma varredura. <paramref name="senha"/> nula manda buscar no cofre — o caminho de
    /// abertura não tem a senha em mão, o de conexão recém-feita tem.
    /// </summary>
    private async System.Threading.Tasks.Task VarrerAsync(MailAccount conta, string? senha)
    {
        if (!_servicoDeEmail.Disponivel) return;

        senha ??= _cofre.Ler(conta.Address);
        if (string.IsNullOrEmpty(senha))
        {
            conta.Status = MailAccountStatus.Error;
            conta.StatusText = "senha de app ausente — use “alterar senha de app”";
            return;
        }

        conta.Status = MailAccountStatus.Checking;
        conta.StatusText = $"{conta.ImapHost}:{conta.ImapPort} · lendo a caixa…";

        var guardado = _estado.Ler(conta.Address);
        uint partida = guardado?.LastUid ?? 0;

        // O "a partir de" impresso aqui é uma INTENÇÃO, não um fato: quem confirma é o selo de
        // validade, e ele só aparece depois de abrir a pasta. A linha de resultado abaixo diz o
        // que de fato aconteceu.
        Console.WriteLine(partida > 0
            ? $"[EMAIL] {conta.Address}: varrendo {conta.ImapHost}:{conta.ImapPort}, "
              + $"tentando partir do UID {partida}."
            : $"[EMAIL] {conta.Address}: varrendo {conta.ImapHost}:{conta.ImapPort}, "
              + $"primeira leitura — últimos {JanelaDeArranqueEmDias} dia(s).");

        MailScanResult r;
        try
        {
            r = await _servicoDeEmail.VarrerAsync(
                conta.Address, senha,
                new ImapEndpoint(conta.ImapHost, conta.ImapPort, conta.UseSsl),
                DateTime.UtcNow.AddDays(-JanelaDeArranqueEmDias),
                guardado,
                _cancelamento.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;   // janela fechou no meio
        }

        if (!r.Ok)
        {
            conta.Status = MailAccountStatus.Error;
            conta.StatusText = r.Erro;
            Console.WriteLine($"[EMAIL] {conta.Address}: varredura sem resultado — {r.Erro}");
            return;
        }

        if (guardado != null && guardado.UidValidity != r.UidValidity)
            Console.WriteLine($"[EMAIL] {conta.Address}: uidValidity mudou "
                              + $"({guardado.UidValidity} -> {r.UidValidity}); recomeçando pela data.");

        // Até onde se leu. Na incremental preserva-se o maior, porque uma busca VAZIA devolve
        // zero e gravar esse zero apagaria o progresso — um fim de semana sem e-mail bastaria
        // para a caixa inteira voltar a parecer novidade na segunda-feira. Na busca por data
        // não há o que preservar: ou é a primeira leitura, ou o selo trocou e o UID guardado é
        // de outra numeração, onde "maior" não quer dizer "mais recente".
        uint ultimoUid = r.Incremental ? Math.Max(partida, r.UltimoUid) : r.UltimoUid;

        _estado.Gravar(conta.Address, new EstadoDaCaixa
        {
            UidValidity = r.UidValidity,
            LastUid = ultimoUid,
            LastReadUtc = DateTime.UtcNow
        });

        conta.Status = MailAccountStatus.Ok;
        conta.LastReadUtc = DateTime.UtcNow;
        conta.StatusText = $"{conta.ImapHost}:{conta.ImapPort} · "
                           + $"{ResumoDaVarredura(r)} · leitura {DateTime.Now:HH:mm}";

        Console.WriteLine($"[EMAIL] {conta.Address}: "
                          + (r.Incremental
                              ? $"busca incremental a partir do UID {partida}"
                              : $"busca por data, últimos {JanelaDeArranqueEmDias} dia(s)")
                          + $" — {r.Mensagens} mensagem(ns), {r.NaoLidas} por ler. "
                          + $"uidValidity={r.UidValidity} últimoUid={ultimoUid}");
    }

    /// <summary>
    /// A frase que descreve o que a varredura OLHOU.
    /// <para>
    /// Duas buscas diferentes não podem sair com a mesma frase. "12 em 3d" depois de uma busca
    /// incremental diria que se conferiu três dias quando se conferiu só o que chegou desde a
    /// última vez — e "0 em 3d" numa caixa cheia seria simplesmente falso.
    /// </para>
    /// </summary>
    private string ResumoDaVarredura(MailScanResult r)
    {
        if (!r.Incremental)
            return $"{r.Mensagens} em {JanelaDeArranqueEmDias}d, {r.NaoLidas} por ler";

        return r.Mensagens == 0
            ? "em dia, nada novo"
            : $"{r.Mensagens} nova(s), {r.NaoLidas} por ler";
    }

    /// <summary>A conta da linha em que o botão clicado vive.</summary>
    private static MailAccount? ContaDoBotao(object sender) =>
        (sender as FrameworkElement)?.DataContext as MailAccount;

    private void MostrarErroDaConta(string mensagem)
    {
        ErroDaContaTexto.Text = mensagem;
        ErroDaConta.Visibility = Visibility.Visible;
    }

    private void EsconderErroDaConta()
    {
        ErroDaContaTexto.Text = "";
        ErroDaConta.Visibility = Visibility.Collapsed;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Estado sujo — §4 "GATILHOS DE IsDirty"
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Mudança de seleção, de texto ou de switch sujam. Foco, hover e rolagem não.
    /// </summary>
    private void Campo_Mudou(object sender, RoutedEventArgs e) => MarcarSujo();

    // ─────────────────────────────────────────────────────────────────────
    // Restaurar padrões
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Devolve aos padrões os campos da página que pediu.
    /// <para>
    /// POR PÁGINA, e não um botão só para a tela: quem quer voltar um número de e-mail atrás
    /// não quer perder de quebra o provedor, o modelo e o personagem. E o padrão vem de um
    /// <c>UserAppSettings</c> recém-criado, que é a definição de padrão que já existe — uma
    /// segunda lista de valores aqui sairia de sincronia na primeira mudança.
    /// </para>
    /// <para>
    /// Restaura os controles, NÃO grava: sair sem salvar continua desfazendo, como em qualquer
    /// outra edição da tela.
    /// </para>
    /// </summary>
    private void Restaurar_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string alvo) return;
        if (!Enum.TryParse<PaginaDeConfiguracoes>(alvo, out var pagina)) return;

        var padrao = new UserAppSettings();

        switch (pagina)
        {
            case PaginaDeConfiguracoes.Identidade:
                if (CharacterComboBox.ItemsSource is System.Collections.IEnumerable itens)
                    foreach (var item in itens)
                        if (string.Equals(item?.ToString(), padrao.ActiveCharacter, StringComparison.Ordinal))
                            CharacterComboBox.SelectedItem = item;
                break;

            case PaginaDeConfiguracoes.Conexao:
                // Keep-alive, teto de contexto e system prompt vieram do Avançado: são
                // parâmetros da CONEXÃO com o modelo, e moravam longe do modelo que configuram.
                SendSystemPromptSwitch.IsChecked = padrao.SendSystemPrompt;
                SemColetaSwitch.IsChecked = padrao.OpenRouterSemColetaDeDados;
                ProvedorFixoComboBox.Text = padrao.OpenRouterProvedorFixo;
                // O perfil de fábrica DO PROVEDOR NA TELA. O provedor e a chave ficam: restaurar
                // não é trocar de provedor nem apagar credencial.
                _perfis[_provedorNaTela] = ProvedoresDeIa.PerfilPadrao(_provedorNaTela);
                MostrarPerfil(_provedorNaTela);
                break;

            case PaginaDeConfiguracoes.Email:
                // As CONTAS ficam de fora. "Restaurar padrões" não é "apagar minhas caixas e
                // as senhas do cofre" — remover conta tem botão próprio, com confirmação.
                MailWindowTextBox.Text = padrao.MailWindowDays.ToString();
                MailTimeoutTextBox.Text = padrao.MailTimeoutSeconds.ToString();
                MailJournalTextBox.Text = padrao.MailJournalDays.ToString();
                MailTriageThinkingSwitch.IsChecked = padrao.MailTriageThinking;
                TriagemProvedorComboBox.SelectedItem = padrao.MailTriageProvider;
                TriagemModeloComboBox.Text = padrao.MailTriageModel;
                break;

            case PaginaDeConfiguracoes.Shadow:
                ShadowAssistantSwitch.IsChecked = padrao.ShadowAssistantEnabled;
                ShadowMailSwitch.IsChecked = padrao.ShadowHandlesMail;
                ShadowMailPreviewTextBox.Text = padrao.ShadowMailPreviewCount.ToString();
                AtualizarAjudaDoShadow();
                break;

            case PaginaDeConfiguracoes.Ferramentas:
                IntelligentToolsSwitch.IsChecked = padrao.EnableIntelligentTools;
                ConfirmDangerousSwitch.IsChecked = padrao.ConfirmDangerousCommands;
                MaxIterationsTextBox.Text = padrao.MaxTurnIterations.ToString();
                // As pastas permitidas voltam ao padrão, que é VAZIO — disco inteiro liberado.
                // Restaurar afrouxa a segurança aqui, então o texto abaixo do campo diz na hora
                // o que passou a valer, em vez de deixar a mudança silenciosa.
                WriteRootsTextBox.Text = padrao.WriteRoots;
                AtualizarPastasPermitidas();
                break;

            case PaginaDeConfiguracoes.Memoria:
                KeepAssistantSpeechSwitch.IsChecked = padrao.KeepAssistantSpeech;
                CompactionTriggerTextBox.Text = ParaPorcento(padrao.CompactionTrigger);
                MemoryFractionTextBox.Text = ParaPorcento(padrao.MemoryFraction);
                MostrarMemoria(padrao);
                break;

            case PaginaDeConfiguracoes.Avancado:
                ThinkingInHistorySwitch.IsChecked = padrao.ThinkingInHistory;
                break;

            case PaginaDeConfiguracoes.Logs:
                VerboseLoggingSwitch.IsChecked = padrao.VerboseConsoleLogging;
                ExecutionLogSwitch.IsChecked = padrao.ExecutionLogging;
                CompactionLogSwitch.IsChecked = padrao.CompactionLogging;
                break;
        }

        MarcarSujo();
    }

    /// <summary>
    /// Diz onde os arquivos de log ficam, e quantos são.
    /// <para>
    /// Uma chave que grava em disco e não diz ONDE obriga o usuário a procurar. Recalculado a
    /// cada visita à página, e não uma vez na abertura: os arquivos nascem enquanto a tela está
    /// aberta.
    /// </para>
    /// </summary>
    private void AtualizarPastaDeLogs()
    {
        string pasta = DirectoryService.LogsDir;

        try
        {
            var arquivos = System.IO.Directory.Exists(pasta)
                ? System.IO.Directory.GetFiles(pasta, "execucao-*.log")
                : Array.Empty<string>();

            long bytes = arquivos.Sum(a => { try { return new System.IO.FileInfo(a).Length; } catch { return 0L; } });

            PastaDeLogsTexto.Text = arquivos.Length == 0
                ? pasta + Environment.NewLine + "(nenhum registro de execução gravado ainda)"
                : pasta + Environment.NewLine
                  + $"{arquivos.Length} arquivo(s), {bytes / 1024.0 / 1024:0.#} MB. "
                  + $"Os {RegistroDeExecucao.ArquivosMantidos} mais recentes ficam; os antigos são apagados.";
        }
        catch (Exception ex)
        {
            PastaDeLogsTexto.Text = pasta + Environment.NewLine
                                    + $"(não consegui ler a pasta: {ex.Message})";
        }
    }


    // ─────────────────────────────────────────────────────────────────────
    // Ferramentas — confinamento de pasta e autorizações da sessão
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Diz, em português, o que a lista de pastas passou a significar.
    /// <para>
    /// O campo é texto livre: uma linha com erro de digitação some da lista sem avisar, e o
    /// usuário sairia da tela achando que confinou a gravação quando não confinou. Mostrar as
    /// pastas que de fato valeram é a única forma de ele perceber.
    /// </para>
    /// </summary>
    private void AtualizarPastasPermitidas()
    {
        var raizes = PastasPermitidas.Analisar(WriteRootsTextBox.Text);

        if (raizes.Count == 0)
        {
            PastasPermitidasTexto.Text =
                "Nenhuma pasta configurada: gravar e editar valem para o disco inteiro.";
            return;
        }

        var faltando = raizes.Where(r => !System.IO.Directory.Exists(r)).ToList();

        PastasPermitidasTexto.Text =
            $"Valendo agora: {string.Join(" | ", raizes)}."
            + (faltando.Count == 0
                ? ""
                : Environment.NewLine
                  + $"Ainda não existe(m) no disco: {string.Join(" | ", faltando)}. "
                  + "Confira se digitou certo — uma pasta errada aqui bloqueia gravações válidas.");
    }

    /// <summary>Reavalia a ajuda a cada tecla: o efeito da linha digitada aparece na hora.</summary>
    private void PastasDeEscrita_Mudou(object sender, TextChangedEventArgs e)
    {
        if (PastasPermitidasTexto == null) return;
        AtualizarPastasPermitidas();
        MarcarSujo();
    }

    /// <summary>
    /// Mostra o que o usuário autorizou nesta sessão.
    /// <para>
    /// A lista vive só em memória e some quando o app fecha — mas enquanto ele está aberto ela é
    /// uma decisão de segurança tomada por clique e depois invisível. Ver é o que permite
    /// desfazer.
    /// </para>
    /// </summary>
    private void AtualizarAutorizacoes()
    {
        var lista = AlwaysAllowSession.Listar();

        if (lista.Count == 0)
        {
            AutorizacoesTexto.Text = "Nada autorizado nesta sessão.";
            LimparAutorizacoes.IsEnabled = false;
            return;
        }

        LimparAutorizacoes.IsEnabled = true;

        AutorizacoesTexto.Text = string.Join(
            Environment.NewLine,
            lista.Select(x =>
            {
                string cmd = x.Cmd.Replace("\r", " ").Replace("\n", " ").Trim();
                if (cmd.Length > 90) cmd = cmd[..90] + "…";
                // Substantivo, e nao o rotulo em gerundio da trilha de acoes: aqui a lista e de
                // coisas AUTORIZADAS, e "[Executando comando] git status" leria como se o
                // comando estivesse rodando agora.
                string tipo = x.Tool switch
                {
                    Ferramentas.Shell => "Comando",
                    Ferramentas.Habilidade => "Habilidade",
                    "materialize_skill" => "Habilidade",
                    _ => x.Tool
                };

                return $"[{tipo}] {cmd}";
            }));
    }

    /// <summary>
    /// Esquece as autorizações da sessão. Vale na hora, sem passar por "Salvar": a lista não é
    /// uma configuração de disco, e deixar um comando autorizado até o próximo Salvar seria
    /// deixá-lo autorizado exatamente enquanto o usuário acha que já revogou.
    /// </summary>
    private void LimparAutorizacoes_Click(object sender, RoutedEventArgs e)
    {
        AlwaysAllowSession.Clear();
        AtualizarAutorizacoes();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Memória — onde a conversa mora
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Onde ficam as sessões, quantas são e quanto ocupam.
    /// <para>
    /// O <c>raw.jsonl</c> de cada sessão é a fonte de verdade da conversa e nunca é apagado por
    /// nós. Dizer isso na tela é o que evita a pergunta "onde foi parar o que eu conversei" e,
    /// principalmente, a suposição errada de que compactar joga algo fora.
    /// </para>
    /// </summary>
    private void AtualizarPastaDeMemoria()
    {
        string pasta = DirectoryService.MemoryDir;

        try
        {
            string sessoes = System.IO.Path.Combine(pasta, "sessions");

            var pastas = System.IO.Directory.Exists(sessoes)
                ? System.IO.Directory.GetDirectories(sessoes)
                : Array.Empty<string>();

            int diarios = pastas.Count(d =>
            {
                try
                {
                    return System.IO.File.Exists(
                        System.IO.Path.Combine(d, RegistroDaCompactacao.NomeDoArquivo));
                }
                catch { return false; }
            });

            long bytes = pastas.Sum(d =>
            {
                try
                {
                    return System.IO.Directory.GetFiles(d, "*", System.IO.SearchOption.AllDirectories)
                        .Sum(a => { try { return new System.IO.FileInfo(a).Length; } catch { return 0L; } });
                }
                catch { return 0L; }
            });

            PastaDeMemoriaTexto.Text = pastas.Length == 0
                ? pasta + Environment.NewLine + "(nenhuma conversa gravada ainda)"
                : pasta + Environment.NewLine
                  + $"{pastas.Length} conversa(s), {bytes / 1024.0 / 1024:0.#} MB. "
                  + "A transcrição bruta de cada uma fica guardada e não é apagada: compactar "
                  + "encurta o que vai para o modelo, não o que está no disco."
                  + Environment.NewLine
                  + (diarios == 0
                      ? "O diário da compactação está desligado — a chave fica na aba Logs."
                      : $"{diarios} conversa(s) com diário da compactação "
                        + $"({RegistroDaCompactacao.NomeDoArquivo}).");
        }
        catch (Exception ex)
        {
            PastaDeMemoriaTexto.Text = pasta + Environment.NewLine
                                       + $"(não consegui ler a pasta: {ex.Message})";
        }
    }

    /// <summary>Abre a pasta no Explorer, como na aba Logs.</summary>
    private void AbrirPastaDeMemoria_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(DirectoryService.MemoryDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = DirectoryService.MemoryDir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CONFIG] não consegui abrir a pasta de memória: {ex.Message}");
        }
    }

    /// <summary>Abre a pasta no Explorer. É o gesto que a pessoa faria em seguida, de qualquer jeito.</summary>
    private void AbrirPastaDeLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(DirectoryService.LogsDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = DirectoryService.LogsDir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CONFIG] não consegui abrir a pasta de logs: {ex.Message}");
        }
    }

    /// <summary>
    /// Simula, grava e abre o arquivo. Gravar e abrir falham por motivos diferentes, e a
    /// mensagem não pode dizer que o arquivo não existe quando só o editor não abriu.
    /// </summary>
    private void ImprimirPrompt_Click(object sender, RoutedEventArgs e)
    {
        RetratoDoPromptTexto.Visibility = Visibility.Visible;

        if (SimularPrimeiroEnvio == null)
        {
            RetratoDoPromptTexto.Text = "Sem conversa ligada a esta janela. Abra as configurações pela janela do chat.";
            return;
        }

        string caminho;
        try
        {
            var agora = DateTime.Now;
            caminho = AIB.Services.Ai.RetratoDoEnvio.Gravar(
                AIB.Services.Ai.RetratoDoEnvio.Renderizar(SimularPrimeiroEnvio(), agora),
                DirectoryService.LogsDir, agora);
        }
        catch (Exception ex)
        {
            RetratoDoPromptTexto.Text = $"Não consegui simular o envio: {ex.Message}";
            return;
        }

        var linhas = new List<string> { caminho };

        // Mudança não salva não entra na simulação. Sem o aviso, quem acabou de trocar o
        // personagem aqui leria a alma do anterior e concluiria que a troca não pegou.
        if (_sujo)
            linhas.Add("Há mudanças não salvas nesta tela: a simulação usou o que está salvo.");

        if (!string.Equals(_currentSettings.AiProvider, "Ollama", StringComparison.Ordinal))
            linhas.Add("O corpo está no formato do Ollama; o seu provedor recebe as mesmas mensagens e ferramentas em outro envelope.");

        RetratoDoPromptTexto.Text = string.Join(Environment.NewLine, linhas);

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = caminho,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CONFIG] simulação gravada, mas não consegui abrir o arquivo: {ex.Message}");
        }
    }

    /// <summary>
    /// Fração para o número que o usuário lê. 0,85 na configuração é "85" na tela: ninguém
    /// pensa em fração de conversa, pensa em porcentagem.
    /// </summary>
    public static string ParaPorcento(double fracao) =>
        Math.Round(fracao * 100).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Lê um número da caixa; texto ilegível mantém o valor que já estava. Zerar em silêncio
    /// seria trocar a configuração do usuário porque ele apagou o campo para redigitar.
    /// </summary>
    private static int Numero(System.Windows.Controls.TextBox caixa, int seDerErrado) =>
        int.TryParse((caixa.Text ?? "").Trim(), out int n) ? n : seDerErrado;

    private static double DePorcento(System.Windows.Controls.TextBox caixa, double seDerErrado) =>
        int.TryParse((caixa.Text ?? "").Trim(), out int n) ? n / 100.0 : seDerErrado;

    /// <summary>Ligar ou desligar o Shadow muda o que a linha da triagem pode prometer.</summary>
    private void ShadowSwitch_Mudou(object sender, RoutedEventArgs e)
    {
        MarcarSujo();
        AtualizarAjudaDoShadow();
    }

    /// <summary>
    /// A linha de ajuda da triagem de e-mail, derivada do estado real.
    /// <para>
    /// Uma frase fixa aqui mentiria em dois dos três casos: com o Shadow desligado ela promete
    /// um trabalho que ninguém vai fazer; sem caixa conectada, promete leitura de uma caixa que
    /// não existe. É o mesmo defeito da linha de estado da conta, que já anunciou uma leitura
    /// que nunca poderia acontecer — a frase tem de ser função do estado, não constante.
    /// </para>
    /// </summary>
    public void AtualizarAjudaDoShadow()
    {
        if (ShadowMailAjuda == null) return;

        bool shadowLigado = ShadowAssistantSwitch.IsChecked == true;
        bool temCaixa = MailAccountList.AlgumaCaixaPronta(
            _currentSettings.MailAccounts, _cofre);

        // Sem Shadow não há onde o aviso aparecer: a chave fica de pé, mas inerte e dizendo
        // por quê.
        ShadowMailSwitch.IsEnabled = shadowLigado;

        ShadowMailAjuda.Text =
            !shadowLigado ? "Ligue o Shadow acima para usar."
            : !temCaixa ? "Nenhuma caixa conectada — conecte uma na página E-mail."
            : TextoDaTriagem;
    }

    /// <summary>
    /// O que a triagem faz HOJE. Enquanto o vigia não existe, a varredura só conta mensagens —
    /// nenhum assunto ou remetente desce do servidor —, e prometer resumo seria vender o que
    /// ainda não há.
    /// </summary>
    public const string TextoDaTriagem =
        "Por ora o Shadow só conta as mensagens; a triagem que resume e prioriza ainda não existe.";

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

    protected override void OnClosed(EventArgs e)
    {
        try { _cancelamento.Cancel(); } catch { }
        _cancelamento.Dispose();
        base.OnClosed(e);
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        // §3.12 — Esc DENTRO do formulário de conta cancela o formulário, e não a janela;
        // Enter conecta, quando o botão está habilitado. Sem isto, quem desiste de acrescentar
        // uma conta fecha a tela de configurações inteira por engano.
        if (FormularioDeConta.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape)
            {
                FecharFormularioDeConta();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter && BotaoConectarConta.IsEnabled)
            {
                ConectarConta_Click(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }
        }

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

    /// <summary>
    /// Troca de provedor: o que está na tela vai para o perfil do anterior, e o perfil do novo
    /// vem para a tela. Voltar traz tudo de volta como estava.
    /// </summary>
    private void ProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_carregando) return;
        if (ProviderComboBox.SelectedItem is not string provedor || provedor == _provedorNaTela) return;

        ColherPerfil(_provedorNaTela);
        _provedorNaTela = provedor;
        MostrarPerfil(provedor);
        MarcarSujo();
    }

    private void UrlTextBox_LostFocus(object sender, RoutedEventArgs e) => _ = RefreshModelsAsync();

    // ─────────────────────────────────────────────────────────────────────
    // Triagem de e-mail — provedor e modelo próprios
    // ─────────────────────────────────────────────────────────────────────

    private void TriagemProvedor_Mudou(object sender, SelectionChangedEventArgs e)
    {
        MarcarSujo();
        AtualizarAvisoDaTriagem();
        EncherModelosDaTriagem();
    }

    /// <summary>
    /// Com a triagem no OpenRouter, trechos dos e-mails saem da máquina. A tela diz isso onde a
    /// escolha é feita, e não num documento.
    /// </summary>
    private void AtualizarAvisoDaTriagem()
    {
        if (TriagemAviso == null) return;

        bool fora = TriagemProvedorComboBox.SelectedItem as string == ProvedoresDeIa.OpenRouter;
        TriagemAviso.Text = fora
            ? "No OpenRouter, remetente, assunto e o começo do corpo de cada e-mail triado são enviados ao OpenRouter e ao modelo escolhido."
            : "Quem lê os e-mails para classificar. No Ollama, nada sai desta máquina.";
        TriagemAviso.Foreground = (System.Windows.Media.Brush)FindResource(fora ? "WarnBrush" : "TextMutedBrush");
    }

    /// <summary>A lista do modelo da triagem vem da mesma fonte do provedor escolhido para ela.</summary>
    private void EncherModelosDaTriagem()
    {
        if (TriagemModeloComboBox == null) return;

        bool antes = _carregando;
        _carregando = true;
        try
        {
            string atual = TriagemModeloComboBox.Text;
            TriagemModeloComboBox.ItemsSource = TriagemProvedorComboBox.SelectedItem as string == ProvedoresDeIa.OpenRouter
                ? _catalogo.Select(m => m.Id).ToList()
                : (ModelComboBox.ItemsSource as IEnumerable<string>)?.ToList();
            TriagemModeloComboBox.Text = atual;
        }
        finally
        {
            _carregando = antes;
        }

        if (TriagemProvedorComboBox.SelectedItem as string == ProvedoresDeIa.OpenRouter && _catalogo.Count == 0)
            _ = CarregarCatalogoAsync();
    }

    /// <summary>
    /// Enche as DUAS listas de modelo — a principal e a do Shadow — com uma consulta só.
    /// <para>
    /// Cada uma recebe a sua PRÓPRIA cópia da lista. Duas ComboBox apontando para a mesma
    /// instância de coleção compartilham a CollectionView padrão do WPF, e com ela a
    /// "currency": mexer numa move a seleção da outra. Copiar é barato e mata a classe inteira
    /// de bug.
    /// </para>
    /// </summary>
    private async System.Threading.Tasks.Task RefreshModelsAsync()
    {
        LoadingProgress.Visibility = Visibility.Visible;
        try
        {
            // Endereço do perfil do Ollama, mesmo com o OpenRouter na tela: a triagem pode estar
            // no Ollama e precisa da lista dele.
            string url = _provedorNaTela == ProvedoresDeIa.Ollama ? UrlTextBox.Text : _perfis[ProvedoresDeIa.Ollama].Url;
            var modelos = await _settingsService.GetOllamaModelsAsync(url);
            if (modelos.Any())
            {
                // O que foi digitado à mão sobrevive à chegada da lista: um modelo que o Ollama
                // ainda não baixou continua sendo uma escolha legítima.
                string atual = ModelComboBox.Text;

                bool antes = _carregando;
                _carregando = true;
                try
                {
                    ModelComboBox.ItemsSource = modelos.ToList();
                    ModelComboBox.Text = atual;
                }
                finally
                {
                    _carregando = antes;
                }

                EncherModelosDaTriagem();
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
        NovaChaveLinha.Visibility = Visibility.Visible;
        NovaChaveBox.Focus();
    }

    private void DebugLink_Click(object sender, RoutedEventArgs e)
    {
        VerboseLoggingSwitch.IsChecked = true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _currentSettings.ActiveCharacter = CharacterComboBox.SelectedItem?.ToString() ?? "Ayano";
        // O perfil na tela, os guardados, e o escolhido vira o ativo. A chave NÃO passa por aqui:
        // foi guardada no cofre pelo "Guardar" da própria linha.
        ColherPerfil(_provedorNaTela);
        foreach (var (nome, perfil) in _perfis) _currentSettings.Perfis[nome] = perfil.Clone();

        string escolhido = ProviderComboBox.SelectedItem as string ?? ProvedoresDeIa.Ollama;
        _currentSettings.Ativar(escolhido, _perfis[escolhido]);

        _currentSettings.MailTriageProvider = TriagemProvedorComboBox.SelectedItem as string ?? ProvedoresDeIa.Ollama;
        _currentSettings.MailTriageModel = (TriagemModeloComboBox.Text ?? "").Trim();

        // O máximo de tokens vem do nível do usuário: é leitura, não preferência.

        _currentSettings.SendSystemPrompt = SendSystemPromptSwitch.IsChecked ?? true;
        _currentSettings.OpenRouterSemColetaDeDados = SemColetaSwitch.IsChecked ?? true;
        _currentSettings.OpenRouterProvedorFixo = (ProvedorFixoComboBox.Text ?? "").Trim();
        _currentSettings.VerboseConsoleLogging = VerboseLoggingSwitch.IsChecked ?? false;

        _currentSettings.ShadowAssistantEnabled = ShadowAssistantSwitch.IsChecked ?? false;
        _currentSettings.ShadowHandlesMail = ShadowMailSwitch.IsChecked ?? false;

        _currentSettings.EnableIntelligentTools = IntelligentToolsSwitch.IsChecked ?? true;
        _currentSettings.ConfirmDangerousCommands = ConfirmDangerousSwitch.IsChecked ?? true;
        _currentSettings.WriteRoots = (WriteRootsTextBox.Text ?? "").Trim();
        _currentSettings.ExecutionLogging = ExecutionLogSwitch.IsChecked ?? false;
        _currentSettings.CompactionLogging = CompactionLogSwitch.IsChecked ?? false;
        _currentSettings.KeepAssistantSpeech = KeepAssistantSpeechSwitch.IsChecked ?? true;
        _currentSettings.ThinkingInHistory = ThinkingInHistorySwitch.IsChecked ?? false;
        _currentSettings.MailTriageThinking = MailTriageThinkingSwitch.IsChecked ?? false;

        _currentSettings.ShadowMailPreviewCount =
            Numero(ShadowMailPreviewTextBox, _currentSettings.ShadowMailPreviewCount);
        _currentSettings.MailWindowDays = Numero(MailWindowTextBox, _currentSettings.MailWindowDays);
        _currentSettings.MailTimeoutSeconds = Numero(MailTimeoutTextBox, _currentSettings.MailTimeoutSeconds);
        _currentSettings.MailJournalDays = Numero(MailJournalTextBox, _currentSettings.MailJournalDays);
        _currentSettings.MaxTurnIterations = Numero(MaxIterationsTextBox, _currentSettings.MaxTurnIterations);
        _currentSettings.CompactionTrigger = DePorcento(CompactionTriggerTextBox, _currentSettings.CompactionTrigger);
        _currentSettings.MemoryFraction = DePorcento(MemoryFractionTextBox, _currentSettings.MemoryFraction);
        _currentSettings.MemoriaComModelo = TagDe(MemoriaQuemEscreveComboBox) != "codigo";
        _currentSettings.TurnosPorCapitulo = int.TryParse(TurnosPorCapituloTextBox.Text, out int turnosPorCapitulo)
            ? turnosPorCapitulo : _currentSettings.TurnosPorCapitulo;
        _currentSettings.CapitulosPorAto = int.TryParse(TagDe(CapitulosPorAtoComboBox), out int porAto) ? porAto : 0;
        _currentSettings.EsconderResultadosDepoisDe = int.TryParse(TagDe(EsconderResultadosComboBox), out int esconder) ? esconder : 0;

        // Saneia ANTES de gravar. O usuário pode digitar 900% e o que for absurdo vira o mais
        // próximo válido — ele perde o exagero, não as configurações inteiras.
        _currentSettings.Sanear();

        _settingsService.SaveSettings(_currentSettings);
        MarcarLimpo();

        var chat = System.Windows.Application.Current.Windows.OfType<ChatWindow>().FirstOrDefault();
        chat?.ApplyCharacterUI();

        // Uma chave que grava e não faz nada até o próximo arranque se lê como quebrada. O
        // Shadow aparece ou some agora, e o visto da bandeja acompanha.
        App.AplicarEstadoDoOrbe();

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
