using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AIB.Services;
using AIB.Services.Mail;

namespace AIB.Views;

/// <summary>
/// O MODO E-MAIL da janela de conversa — tela-chat-v3.html §3.3(d), §3.10 e §3.11.
/// <para>
/// Parte da MESMA <see cref="ChatWindow"/>: é o mesmo <c>Grid.Row 1</c>, o mesmo cabeçalho e,
/// na leitura, a MESMA conversa. Está num arquivo separado porque são seiscentas linhas com um
/// assunto só, e não porque sejam outra tela — dividir por tela, aqui, seria dividir errado.
/// </para>
/// <para>
/// São TRÊS estados, e não dois: a lista (§3.10), a leitura de um e-mail (§3.11) e o convite de
/// quem não conectou caixa nenhuma. Quem decide qual está na tela é <c>AplicarEstadoDoModo</c>,
/// sozinho: visibilidade escrita à mão em cada handler foi o que já deixou a barra de input
/// aparecer no modo errado.
/// </para>
/// <para>
/// REGRA 3 vale aqui inteira: o CORPO de um e-mail nunca chega a este arquivo. O que circula é
/// o veredito da triagem — remetente, assunto, urgência e resumo —, e é só isso que desce
/// para o modelo em §3.11.
/// </para>
/// </summary>
public partial class ChatWindow
{
    /// <summary>
    /// Só para PERGUNTAR se há senha guardada. A conversa nunca lê senha de e-mail: o modo
    /// precisa saber se existe caixa pronta, e essa é toda a pergunta.
    /// </summary>
    private readonly MailVault _cofreDeEmail = new();

    /// <summary>
    /// De onde a lista de §3.10 sai. Quem preenche é o App, que é dono do vigia; a conversa
    /// não conhece o serviço e não precisa conhecer — ela repassa.
    /// </summary>
    public Func<IReadOnlyList<MailSummary>>? FonteDeEmails { get; set; }

    /// <summary>Se existe caixa com senha no cofre — é isto que escolhe entre a lista e o
    /// convite de §3.10.</summary>
    private bool HaCaixaDeEmailPronta() =>
        MailAccountList.AlgumaCaixaPronta(
            _settingsService.LoadSettings().MailAccounts, _cofreDeEmail);

    // ─────────────────────────────────────────────────────────────────────────
    // §3.10  MODO E-MAIL — a caixa de entrada na área central
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Qual conversa está aberta no acordeão. Só uma por vez (§3.10).</summary>
    private MailSummary? _emailAberto;

    /// <summary>
    /// Qual e-mail está em LEITURA (§3.11). <c>null</c> quando se está na lista.
    /// <para>
    /// É o terceiro estado do modo e-mail, e não um modo novo: o switch continua em "E-mail".
    /// </para>
    /// </summary>
    private MailSummary? _emailEmLeitura;

    /// <summary>
    /// Quando presente, este elemento entra no lugar da bolha do usuário no próximo turno.
    /// <para>
    /// Existe por causa de §3.11: o cartão do e-mail É a fala que abre o turno. Um campo e não
    /// um parâmetro porque quem dispara o envio é <c>SendButton_Click</c>, que é handler de
    /// evento e não aceita argumento.
    /// </para>
    /// </summary>
    private Func<FrameworkElement>? _bolhaDoTurno;

    /// <summary>
    /// Relê UMA conversa no servidor e devolve a linha atualizada — o "Recarregar" de §3.11.
    /// <para>
    /// Delegado, como <see cref="FonteDeEmails"/>: a janela não conhece o vigia, e nos testes
    /// de tela não há servidor nenhum para conhecer.
    /// </para>
    /// </summary>
    public Func<MailSummary, CancellationToken, Task<MailSummary?>>? RecarregarEmail { get; set; }

    /// <summary>
    /// Troca o conteúdo da área central.
    /// <para>
    /// A barra de input e o rodapé de contexto somem junto: no modo e-mail a área central é só
    /// a caixa de entrada, de ponta a ponta. Não se fala com a IA a partir daqui — para
    /// perguntar sobre a caixa, volta-se ao Chat.
    /// </para>
    /// <para>
    /// A janela NÃO muda de tamanho: os dois são irmãos na MESMA linha da grade, e só a
    /// Visibility troca (§8 A2).
    /// </para>
    /// </summary>
    private void Modo_Checked(object sender, RoutedEventArgs e)
    {
        // Durante o InitializeComponent o IsChecked="True" do ModoChat dispara antes de os
        // elementos existirem.
        if (CaixaDeEntrada == null) return;

        bool email = ModoEmail.IsChecked == true;

        // Voltar ao Chat encerra a leitura: o caminho "Caixa de entrada › assunto" não faz
        // sentido fora do modo e-mail, e deixá-lo armado faria o próximo clique em "E-mail"
        // cair numa leitura que o usuário já tinha abandonado.
        if (!email) _emailEmLeitura = null;

        AplicarEstadoDoModo();

        if (email) MontarCaixaDeEntrada();
    }

    /// <summary>
    /// Põe na tela o estado atual dos três: CHAT, LISTA de e-mails ou LEITURA de um e-mail.
    /// <para>
    /// Um lugar só, e derivado de <see cref="_emailEmLeitura"/> e do switch: as visibilidades
    /// escritas à mão em cada handler foi o que já deixou a barra de input aparecer no modo
    /// errado. Aqui não há como dois caminhos discordarem.
    /// </para>
    /// </summary>
    private void AplicarEstadoDoModo()
    {
        if (CaixaDeEntrada == null) return;

        bool email = ModoEmail.IsChecked == true;
        bool leitura = email && _emailEmLeitura != null;
        bool lista = email && !leitura;

        CabecalhoDoEmail.Visibility = email ? Visibility.Visible : Visibility.Collapsed;
        CaixaDeEntrada.Visibility = lista ? Visibility.Visible : Visibility.Collapsed;

        // Na leitura a conversa é a MESMA do chat: as bolhas de §3.5, o mesmo rolo, o mesmo
        // histórico. Um segundo painel de mensagens seria uma segunda cópia de toda a máquina
        // de balões, cadeia de ações e confirmação — e a primeira a divergir.
        ChatScrollViewer.Visibility = lista ? Visibility.Collapsed : Visibility.Visible;

        // No CHAT os 24px de topo afastam a primeira bolha da divisória do header. Na LEITURA
        // quem faz esse afastamento é o cabeçalho, e os dois somados empurravam o cartão quase
        // cinquenta pixels para baixo — numa janela de 520 isso é um décimo da altura.
        ChatScrollViewer.Padding = leitura
            ? new Thickness(26, 4, 26, 24)
            : new Thickness(26, 24, 26, 24);

        // A barra de input volta na LEITURA — é o único ponto do modo e-mail em que ela
        // aparece (§3.11).
        BarraDeInput.Visibility = lista ? Visibility.Collapsed : Visibility.Visible;

        // O rodapé de contexto sai da LEITURA porque descreve a conversa inteira, e aqui a
        // atenção é de um e-mail só. Mas sai COLAPSANDO SÓ O CONTEÚDO, não a linha: a barra de
        // input não tem margem de baixo própria — quem sempre deu o chão dela foi este rodapé.
        // Collapsed aqui fazia o input encostar na borda arredondada da janela e aparecer
        // cortado. Hidden guarda o lugar, e o input fica exatamente onde fica no chat.
        //
        // Na LISTA ele pode colapsar de verdade: não há input embaixo para sustentar, e a lista
        // ganha a linha inteira.
        RodapeDeContexto.Visibility = lista
            ? Visibility.Collapsed
            : leitura ? Visibility.Hidden : Visibility.Visible;

        // As duas caras do cabeçalho.
        TituloDaCaixa.Visibility = lista ? Visibility.Visible : Visibility.Collapsed;
        MedidasDaCaixa.Visibility = lista ? Visibility.Visible : Visibility.Collapsed;
        EngrenagemDoEmail.Visibility = lista ? Visibility.Visible : Visibility.Collapsed;
        CaminhoDaLeitura.Visibility = leitura ? Visibility.Visible : Visibility.Collapsed;
        AcoesDaLeitura.Visibility = leitura ? Visibility.Visible : Visibility.Collapsed;

        // O convite de conversa vazia é do CHAT. Deixá-lo visível por cima da caixa de entrada
        // anunciaria "nenhuma conversa ainda" em cima de uma lista cheia de e-mails.
        if (email) EmptyState.Visibility = Visibility.Collapsed;
        else AtualizarEstadoVazio();

        AplicarPlaceholder();
    }

    /// <summary>
    /// "Fale com a KAI sobre este e-mail..." durante a leitura; o de sempre fora dela.
    /// </summary>
    private void AplicarPlaceholder()
    {
        if (InputPlaceholder == null) return;

        string nome = NomeDaInteligencia();

        InputPlaceholder.Text = _emailEmLeitura == null
            ? $"Fale com {nome}..."
            : $"Fale com {nome} sobre este e-mail...";
    }

    /// <summary>
    /// Monta a lista de conversas da caixa. Recalculada a cada entrada no modo: a triagem roda
    /// de vinte em vinte minutos e a lista muda com a janela aberta.
    /// </summary>
    private void MontarCaixaDeEntrada()
    {
        bool configurado = HaCaixaDeEmailPronta();

        ConviteDeEmailCentral.Visibility = configurado ? Visibility.Collapsed : Visibility.Visible;
        RoloDaCaixa.Visibility = configurado ? Visibility.Visible : Visibility.Collapsed;
        MedidasDaCaixa.Visibility = configurado ? Visibility.Visible : Visibility.Collapsed;

        ListaDaCaixa.Children.Clear();
        if (!configurado) return;

        var conversas = FonteDeEmails?.Invoke() ?? Array.Empty<MailSummary>();
        var agora = DateTime.Now;

        int urgentes = conversas.Count(c => c.Urgency == MailUrgency.Maxima);
        int tokens = TokensDaCaixa(conversas);

        MedidasDaCaixa.Text =
            $"{conversas.Count} e-mail(s) · {urgentes} urgente(s) · {tokens:N0} tokens";

        // Urgente em danger quando HÁ urgente, e apagado quando não há: uma contagem em
        // vermelho dizendo zero treina a pessoa a ignorar o vermelho.
        MedidasDaCaixa.Foreground = urgentes > 0
            ? (System.Windows.Media.Brush)FindResource("DangerTextBrush")
            : (System.Windows.Media.Brush)FindResource("TextMutedBrush");

        string nomeDaIA = ChatTitleText?.Text ?? "a IA";

        foreach (var conversa in conversas)
        {
            var item = new MailListItem
            {
                DataContext = conversa,
                CornerRadius = new CornerRadius(12),
                RealceLilas = true,
                EscalaDeJanela = true,
                MostrarMetadados = true,
                Aberto = ReferenceEquals(conversa, _emailAberto),
                NomeDaInteligencia = NomeDaInteligencia(),
                RotuloDoCliente = RotuloDoCliente(conversa),
                Margin = new Thickness(0, 0, 0, 8)
            };

            item.PreencherMetadados(conversa, agora);

            var alvo = conversa;

            // Um item aberto por vez: abrir o segundo fecha o primeiro. Dois corpos abertos
            // empurrariam a lista para fora da tela e desfariam o ganho do acordeão.
            item.PediuAlternar += (_, _) =>
            {
                _emailAberto = ReferenceEquals(alvo, _emailAberto) ? null : alvo;
                MontarCaixaDeEntrada();
            };

            item.PediuAbrirNoCliente += (_, _) => AbrirEmailNoCliente(alvo);

            item.PediuAbrirComIA += (_, _) => AbrirEmailNoChat(alvo);

            ListaDaCaixa.Children.Add(item);
        }
    }

    /// <summary>
    /// Quanto a caixa pesaria no prompt.
    /// <para>
    /// MEDIDO aqui, e não lido de <c>ContextTokens</c>: o vigia monta a linha antes de existir
    /// contador, e o campo chega zero. Uma terceira medida escrita "0 tokens" ao lado de quatro
    /// e-mails com resumo é pior que medida nenhuma — ela ensina a ignorar a linha.
    /// </para>
    /// <para>
    /// Assunto + resumo, que é o que de fato desceria: o corpo não entra em prompt nenhum.
    /// </para>
    /// </summary>
    private int TokensDaCaixa(IReadOnlyList<MailSummary> conversas)
    {
        int total = 0;

        foreach (var c in conversas)
            total += c.ContextTokens > 0
                ? c.ContextTokens
                : _contadorDaCaixa.CountText(c.Name + " " + c.Description);

        return total;
    }

    private readonly TokenCounter _contadorDaCaixa = new();

    /// <summary>
    /// O nome da personalidade ativa, para "Abrir com &lt;NOME&gt;".
    /// <para>
    /// Do MESMO campo que preenche o header — nunca a string "Kai" em hard-code, que é o que já
    /// deixou o nome cravado em meia dúzia de lugares.
    /// </para>
    /// </summary>
    private string NomeDaInteligencia()
    {
        string nome = (ChatTitleText?.Text ?? "").Trim();
        return nome.Length == 0 ? "a IA" : nome;
    }

    /// <summary>
    /// O rótulo do botão secundário segue o PROVEDOR da conta. Desconhecido vira "Abrir no
    /// cliente": prometer Gmail numa caixa que não é Gmail seria mentir sobre para onde o
    /// clique leva.
    /// </summary>
    private static string RotuloDoCliente(MailSummary conversa)
    {
        string conta = (conversa?.Account ?? "").ToLowerInvariant();

        if (conta.Contains("gmail") || conta.Contains("googlemail")) return "Abrir no Gmail";
        if (conta.Contains("outlook") || conta.Contains("hotmail") || conta.Contains("live"))
            return "Abrir no Outlook";

        return "Abrir no cliente";
    }

    /// <summary>Sai do app pelo mesmo caminho das outras telas — ver MailListItem.</summary>
    private static void AbrirEmailNoCliente(MailSummary conversa) =>
        MailListItem.AbrirNoNavegador(conversa?.Url);

    // ──────────────────────────────────────────────────────────────────────────────
    // §3.11  LEITURA DO E-MAIL COM A IA
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Traz um e-mail para dentro da conversa — o destino de "Abrir com &lt;NOME&gt;".
    /// <para>
    /// NÃO troca o switch: continua-se em "E-mail". O que sai é a LISTA; o cabeçalho fica, com
    /// o título virado caminho.
    /// </para>
    /// <para>
    /// O que desce para o modelo é o VEREDITO — remetente, assunto, urgência e resumo — e
    /// nunca o corpo. O corpo vive em memória durante uma triagem e morre lá; pô-lo aqui o
    /// gravaria no <c>raw.jsonl</c>, que é disco, e o resumidor de capítulos leria e-mail alheio
    /// semanas depois. É a regra 3, e ela não tem exceção nesta tela.
    /// </para>
    /// <para>
    /// A conversa é a MESMA do chat, de propósito: o turno é um turno de verdade, entra no
    /// histórico, conta para a compactação e pode chamar ferramenta. Uma conversa paralela
    /// exigiria segunda sessão, segundo <c>raw.jsonl</c> e segunda memória.
    /// </para>
    /// </summary>
    /// <summary>
    /// Põe a tela no estado de LEITURA. Só a tela: nada é enviado ao modelo daqui.
    /// <para>
    /// Separado de <see cref="AbrirEmailNoChat"/> porque o estado da tela e o turno têm custos
    /// bem diferentes — um é instantâneo, o outro são minutos nesta máquina — e porque um
    /// ensaio de tela não pode depender de um modelo responder.
    /// </para>
    /// </summary>
    public void EntrarNaLeitura(MailSummary alvo)
    {
        if (alvo == null) return;

        _emailEmLeitura = alvo;
        _emailAberto = null;

        AssuntoDaLeitura.Text = alvo.Name;
        AssuntoDaLeitura.ToolTip = alvo.Name;
        BotaoAbrirNoClienteDaLeitura.Content = RotuloDoCliente(alvo);

        AplicarEstadoDoModo();
    }

    private void AbrirEmailNoChat(MailSummary alvo)
    {
        if (alvo == null) return;

        EntrarNaLeitura(alvo);

        // O CARTÃO é a fala que abre o turno: ele entra no lugar da bolha do usuário.
        var cartao = alvo;
        _bolhaDoTurno = () => CartaoDoEmail(cartao);

        // Enquanto um turno roda o primário é PARAR, e enquanto se grava ele é ENCERRAR A
        // ESCUTA: nos dois casos o clique cancelaria algo em curso em vez de enviar — e quem
        // clicou em "Abrir com" não pediu isso. O cartão entra sozinho e o campo espera.
        if (_isSending || _voiceListening)
        {
            _bolhaDoTurno = null;
            MessagesPanel.Children.Add(CartaoDoEmail(cartao));
            AtualizarEstadoVazio();
            ChatScrollViewer.ScrollToEnd();
            InputBox.Focus();
            return;
        }

        InputBox.Text = EnquadramentoDoEmail(alvo);
        SendButton_Click(this, new RoutedEventArgs());
    }

    /// <summary>
    /// O texto que o modelo recebe ao abrir um e-mail. Só o que a triagem já apurou.
    /// <para>
    /// Diz em voz alta que o corpo não está aqui: sem isso o modelo responde como se tivesse
    /// lido a mensagem inteira, e inventa cláusula, anexo e prazo que ninguém escreveu.
    /// </para>
    /// </summary>
    public static string EnquadramentoDoEmail(MailSummary alvo)
    {
        var linhas = new List<string>
        {
            $"E-mail em contexto — {alvo.Name}"
        };

        if (!string.IsNullOrWhiteSpace(alvo.De)) linhas.Add($"De: {alvo.De}");

        if (alvo.LastMessageAt != default)
            linhas.Add($"Quando: {alvo.LastMessageAt:dd/MM/yyyy, HH:mm}");

        linhas.Add($"Urgência da triagem: {AIB.Ui.UrgenciaConverter.RotuloDe(alvo.Urgency)}");

        if (!string.IsNullOrWhiteSpace(alvo.Description))
            linhas.Add($"Resumo da triagem: {alvo.Description}");

        linhas.Add("");
        linhas.Add("Não tenho o corpo da mensagem aqui — só este resumo, e ele é o único que vai "
                   + "ficar gravado. Diga o que dá para fazer a partir daqui e o que você "
                   + "precisaria que eu abrisse para ter certeza.");

        return string.Join("\n", linhas);
    }

    /// <summary>
    /// O cartão de e-mail em contexto — barra de urgência, remetente, hora e o resumo inteiro.
    /// <para>
    /// Não é bolha e não tem autor: é contexto. Por isso ocupa a coluna inteira, sem os 74% de
    /// MaxWidth de §3.5 — uma bolha alinhada à direita diria que o usuário digitou aquilo.
    /// </para>
    /// </summary>
    private FrameworkElement CartaoDoEmail(MailSummary alvo)
    {
        var conteudo = new StackPanel();

        conteudo.Children.Add(new TextBlock
        {
            Text = "E-MAIL EM CONTEXTO",
            FontSize = 9.5,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 4),
            Foreground = (System.Windows.Media.Brush)FindResource("AccentLilacBrush")
        });

        var remetente = new TextBlock
        {
            Text = MailListItem.Remetente(alvo, DateTime.Now),
            FontSize = 10.5,
            Margin = new Thickness(0, 0, 0, 6),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush")
        };

        conteudo.Children.Add(remetente);

        conteudo.Children.Add(new TextBlock
        {
            Text = alvo.Description ?? "",
            FontSize = 12,
            LineHeight = 12 * 1.55,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)FindResource("TextBodyBrush")
        });

        var barra = new System.Windows.Shapes.Rectangle
        {
            Width = 3,
            RadiusX = 2,
            RadiusY = 2,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Stretch,
            Fill = AIB.Ui.UrgenciaConverter.CorDe(alvo.Urgency)
        };

        var grade = new Grid();
        grade.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grade.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(barra, 0);
        Grid.SetColumn(conteudo, 1);
        grade.Children.Add(barra);
        grade.Children.Add(conteudo);

        return new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(13, 11, 13, 11),
            Margin = new Thickness(0, 0, 0, 14),
            Background = (System.Windows.Media.Brush)FindResource("AccentFill07Brush"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("AccentEdge28Brush"),
            BorderThickness = new Thickness(1),
            Child = grade
        };
    }

    private void VoltarParaCaixa_Click(object sender, RoutedEventArgs e)
    {
        _emailEmLeitura = null;
        AplicarEstadoDoModo();
        MontarCaixaDeEntrada();
    }

    private void AbrirNoClienteDaLeitura_Click(object sender, RoutedEventArgs e)
    {
        if (_emailEmLeitura != null) AbrirEmailNoCliente(_emailEmLeitura);
    }

    /// <summary>Uma releitura por vez — o botão fica desabilitado enquanto roda.</summary>
    private bool _recarregandoEmail;

    /// <summary>
    /// Relê o e-mail no servidor e refaz o resumo (§3.11).
    /// <para>
    /// CUSTA UMA CHAMADA AO MODELO, e nesta máquina isso é minutos. Por isso o botão
    /// desabilita e o ícone gira: sem sinal, o segundo clique vira a segunda releitura.
    /// </para>
    /// <para>
    /// A LISTA não é reescrita daqui. Ela vem do vigia, e forçar a linha nova nela faria a
    /// tela discordar do que a próxima passada vai mostrar. O que muda é o cartão desta
    /// leitura — que é exatamente o que o usuário pediu ao clicar.
    /// </para>
    /// </summary>
    private async void RecarregarEmail_Click(object sender, RoutedEventArgs e)
    {
        if (_recarregandoEmail || _emailEmLeitura == null) return;

        if (RecarregarEmail == null)
        {
            MostrarFaixa("Não dá para reler: nenhuma caixa conectada.");
            return;
        }

        var alvo = _emailEmLeitura;

        _recarregandoEmail = true;
        BotaoRecarregarEmail.IsEnabled = false;
        GirarSetaDeRecarregar(true);

        try
        {
            var atualizado = await RecarregarEmail(alvo, CancellationToken.None);

            // Sai da leitura no meio da releitura: o resultado é de outro e-mail, e escrevê-lo
            // no cartão que está na tela trocaria um resumo pelo outro.
            if (atualizado == null || !ReferenceEquals(alvo, _emailEmLeitura))
            {
                if (atualizado == null) MostrarFaixa("Não consegui reler este e-mail.");
                return;
            }

            _emailEmLeitura = atualizado;
            AssuntoDaLeitura.Text = atualizado.Name;
            AssuntoDaLeitura.ToolTip = atualizado.Name;

            MessagesPanel.Children.Add(CartaoDoEmail(atualizado));
            AtualizarEstadoVazio();
            ChatScrollViewer.ScrollToEnd();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EMAIL] recarregar falhou — {ex.GetType().Name}: {ex.Message}");
            MostrarFaixa("Não consegui reler este e-mail.");
        }
        finally
        {
            _recarregandoEmail = false;
            BotaoRecarregarEmail.IsEnabled = true;
            GirarSetaDeRecarregar(false);
        }
    }

    /// <summary>O mesmo giro do spinner de §4.2: 360° em 0,9s, linear, para sempre.</summary>
    private void GirarSetaDeRecarregar(bool ligado)
    {
        if (GiroDoRecarregar == null) return;

        if (!ligado)
        {
            GiroDoRecarregar.BeginAnimation(
                System.Windows.Media.RotateTransform.AngleProperty, null);
            GiroDoRecarregar.Angle = 0;
            return;
        }

        var giro = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = new Duration(TimeSpan.FromSeconds(0.9)),
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
        };

        GiroDoRecarregar.BeginAnimation(
            System.Windows.Media.RotateTransform.AngleProperty, giro);
    }

    private void EngrenagemDoEmail_Click(object sender, RoutedEventArgs e) =>
        AbrirConfiguracoesDeEmail();

    /// <summary>
    /// A engrenagem da linha de título e o botão do convite, que são o MESMO caminho. §6.2.1
    /// mandava abrir o modal de §6.5, mas a
    /// página de e-mail da tela de configurações passou a fazer o mesmo e mais — várias caixas,
    /// troca de senha, remoção, teste de conexão. Um modal agora seria uma segunda porta para a
    /// mesma sala, com sua própria cópia do cofre e da validação.
    /// </summary>
    private void AbrirConfiguracoesDeEmail()
    {
        // O painel é Topmost. Sem baixá-lo, o diálogo modal abre ATRÁS dele e a tela parece
        // travada: o clique não responde e não há nada visível explicando por quê.
        bool painelNoTopo = _painel?.Topmost ?? false;
        if (_painel != null) _painel.Topmost = false;

        this.Deactivated -= Window_Deactivated;
        var janela = new SettingsWindow(_settingsService, PaginaDeConfiguracoes.Email)
        {
            Owner = this
        };
        janela.ShowDialog();
        this.Deactivated += Window_Deactivated;

        if (_painel != null) _painel.Topmost = painelNoTopo;

        ApplyShadowAssistantSetting();
    }
}
