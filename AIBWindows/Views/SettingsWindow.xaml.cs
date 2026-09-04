using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AIB.Services;
using AIB.Services.Mail;

namespace AIB.Views;

/// <summary>Qual página abrir — §3.10, "rota direta".</summary>
public enum PaginaDeConfiguracoes
{
    Identidade,
    Conexao,
    Email,
    Shadow,
    Avancado
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

            UrlTextBox.Text = _currentSettings.ApiUrl;
            ModelComboBox.Text = _currentSettings.ModelName;

            int userLevel = LevelService.GetLevel(_currentSettings.MessageCount);
            MaxHistoryTextBox.Text = LevelService.GetMaxTokensForLevel(userLevel).ToString();

            SendSystemPromptSwitch.IsChecked = _currentSettings.SendSystemPrompt;
            VerboseLoggingSwitch.IsChecked = _currentSettings.VerboseConsoleLogging;

            ShadowAssistantSwitch.IsChecked = _currentSettings.ShadowAssistantEnabled;
            ShadowMailSwitch.IsChecked = _currentSettings.ShadowHandlesMail;
            AtualizarAjudaDoShadow();

            IntelligentToolsSwitch.IsChecked = _currentSettings.EnableIntelligentTools;
            ConfirmDangerousSwitch.IsChecked = _currentSettings.ConfirmDangerousCommands;

            ShadowModelTextBox.Text = _currentSettings.ShadowModelName;
            ShadowMailPreviewTextBox.Text = _currentSettings.ShadowMailPreviewCount.ToString();
            MailWindowTextBox.Text = _currentSettings.MailWindowDays.ToString();
            MailTimeoutTextBox.Text = _currentSettings.MailTimeoutSeconds.ToString();
            MaxIterationsTextBox.Text = _currentSettings.MaxTurnIterations.ToString();
            CompactionTriggerTextBox.Text = ParaPorcento(_currentSettings.CompactionTrigger);
            MemoryFractionTextBox.Text = ParaPorcento(_currentSettings.MemoryFraction);

            SelecionarKeepAlive(_currentSettings.KeepAlive);
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
            PaginaDeConfiguracoes.Avancado => NavAvancado,
            _ => NavIdentidade
        };

        botao.IsChecked = true;
    }

    /// <summary>Página visível agora. Diagnóstico e ensaio.</summary>
    public PaginaDeConfiguracoes PaginaAtiva =>
        NavConexao.IsChecked == true ? PaginaDeConfiguracoes.Conexao :
        NavEmail.IsChecked == true ? PaginaDeConfiguracoes.Email :
        NavShadow.IsChecked == true ? PaginaDeConfiguracoes.Shadow :
        NavAvancado.IsChecked == true ? PaginaDeConfiguracoes.Avancado :
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
        PaginaAvancado.Visibility = Visibilidade(NavAvancado);

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
                // O PROVEDOR e a CHAVE ficam de fora. O padrão do provedor é vazio, que não é
                // uma preferência: é o sinal de "ainda não passou pelo primeiro arranque", e
                // restaurá-lo deixaria o programa sem saber com quem falar. A chave é do
                // FirstRunWindow e nem editável aqui é.
                UrlTextBox.Text = padrao.ApiUrl;
                ModelComboBox.Text = padrao.ModelName;
                ShadowModelTextBox.Text = padrao.ShadowModelName;
                break;

            case PaginaDeConfiguracoes.Email:
                // As CONTAS ficam de fora. "Restaurar padrões" não é "apagar minhas caixas e
                // as senhas do cofre" — remover conta tem botão próprio, com confirmação.
                MailWindowTextBox.Text = padrao.MailWindowDays.ToString();
                MailTimeoutTextBox.Text = padrao.MailTimeoutSeconds.ToString();
                break;

            case PaginaDeConfiguracoes.Shadow:
                ShadowAssistantSwitch.IsChecked = padrao.ShadowAssistantEnabled;
                ShadowMailSwitch.IsChecked = padrao.ShadowHandlesMail;
                ShadowMailPreviewTextBox.Text = padrao.ShadowMailPreviewCount.ToString();
                AtualizarAjudaDoShadow();
                break;

            case PaginaDeConfiguracoes.Avancado:
                SelecionarKeepAlive(padrao.KeepAlive);
                SendSystemPromptSwitch.IsChecked = padrao.SendSystemPrompt;
                VerboseLoggingSwitch.IsChecked = padrao.VerboseConsoleLogging;
                IntelligentToolsSwitch.IsChecked = padrao.EnableIntelligentTools;
                ConfirmDangerousSwitch.IsChecked = padrao.ConfirmDangerousCommands;
                MaxIterationsTextBox.Text = padrao.MaxTurnIterations.ToString();
                CompactionTriggerTextBox.Text = ParaPorcento(padrao.CompactionTrigger);
                MemoryFractionTextBox.Text = ParaPorcento(padrao.MemoryFraction);
                break;
        }

        MarcarSujo();
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

        _currentSettings.ShadowAssistantEnabled = ShadowAssistantSwitch.IsChecked ?? false;
        _currentSettings.ShadowHandlesMail = ShadowMailSwitch.IsChecked ?? false;

        _currentSettings.EnableIntelligentTools = IntelligentToolsSwitch.IsChecked ?? true;
        _currentSettings.ConfirmDangerousCommands = ConfirmDangerousSwitch.IsChecked ?? true;

        _currentSettings.ShadowModelName = ShadowModelTextBox.Text.Trim();
        _currentSettings.ShadowMailPreviewCount =
            Numero(ShadowMailPreviewTextBox, _currentSettings.ShadowMailPreviewCount);
        _currentSettings.MailWindowDays = Numero(MailWindowTextBox, _currentSettings.MailWindowDays);
        _currentSettings.MailTimeoutSeconds = Numero(MailTimeoutTextBox, _currentSettings.MailTimeoutSeconds);
        _currentSettings.MaxTurnIterations = Numero(MaxIterationsTextBox, _currentSettings.MaxTurnIterations);
        _currentSettings.CompactionTrigger = DePorcento(CompactionTriggerTextBox, _currentSettings.CompactionTrigger);
        _currentSettings.MemoryFraction = DePorcento(MemoryFractionTextBox, _currentSettings.MemoryFraction);

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
