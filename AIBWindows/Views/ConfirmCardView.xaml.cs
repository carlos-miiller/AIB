using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using AIB.Services;
using AIB.Ui;

using UserControl = System.Windows.Controls.UserControl;

namespace AIB.Views;

/// <summary>
/// Card de confirmação de ação destrutiva, dentro da conversa — §5.3 da spec de chat.
/// <para>
/// Substitui a janela modal. A pergunta pertence ao fluxo do turno: aparece na coluna da IA,
/// logo abaixo da ação que a provocou, e o chip correspondente passa a dizer "Aguardando". Uma
/// janela separada tirava a pergunta do contexto em que ela faz sentido.
/// </para>
/// <para>
/// A resposta é sempre NEGATIVA por omissão. Um card que suma sem decisão — turno cancelado,
/// conversa limpa, app encerrando — recusa, nunca autoriza.
/// </para>
/// </summary>
public partial class ConfirmCardView : UserControl
{
    private readonly TaskCompletionSource<(bool Allowed, bool AlwaysAllow)> _resposta =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConfirmCardView()
    {
        InitializeComponent();
    }

    /// <summary>Completa quando o usuário decide, ou quando o card é descartado.</summary>
    public Task<(bool Allowed, bool AlwaysAllow)> Resposta => _resposta.Task;

    /// <summary>Preenche o card a partir do contexto que o portão montou.</summary>
    public void Preencher(CommandConfirmationContext contexto)
    {
        string ferramenta = contexto.Tool ?? "";
        string alvo = contexto.Command ?? "";

        _ferramenta = ferramenta;
        _alvo = alvo;
        _bloqueado = contexto.DenylistHit;

        // CRIAR e SOBRESCREVER são decisões diferentes. O card dizia "o conteúdo atual será
        // substituído" também para um arquivo que ainda não existe — e um aviso que erra no caso
        // comum ensina a não ler o aviso. Quem sabe qual dos dois é o WriteFileTool, que olhou o
        // disco e escreveu no começo do comando.
        bool criar = ferramenta == Ferramentas.Gravar
                     && alvo.StartsWith("CRIAR ", StringComparison.Ordinal);
        bool manual = ferramenta == Ferramentas.Habilidade
                      && alvo.StartsWith("LER MANUAL ", StringComparison.Ordinal);

        TituloText.Text = ferramenta switch
        {
            Ferramentas.Gravar => criar ? "Criar este arquivo?" : "Sobrescrever este arquivo?",
            Ferramentas.Editar => "Editar este arquivo?",
            Ferramentas.Shell => "Executar este comando?",
            Ferramentas.Habilidade => manual ? "Ler o manual desta habilidade?" : "Executar esta habilidade?",
            Ferramentas.Arquivos when alvo.StartsWith("APAGAR ", StringComparison.Ordinal) => "Mandar para a Lixeira?",
            Ferramentas.Arquivos => "Mexer nestes arquivos?",
            Ferramentas.Navegador when alvo.StartsWith("ABRIR SITE NOVO ", StringComparison.Ordinal) => "Abrir este site?",
            Ferramentas.Navegador when alvo.StartsWith("DIGITAR ", StringComparison.Ordinal) => "Digitar nesta página?",
            Ferramentas.Navegador when alvo.StartsWith("ANOTAR ", StringComparison.Ordinal) => "Guardar esta anotação?",
            Ferramentas.Navegador => "Clicar nesta página?",
            Ferramentas.Lembrar => "Guardar isto na memória?",
            Ferramentas.Lembrete when alvo.StartsWith("CANCELAR ", StringComparison.Ordinal) => "Cancelar este lembrete?",
            Ferramentas.Lembrete => "Agendar este lembrete?",
            _ => "Autorizar esta ação?"
        };

        ConsequenciaText.Text = ferramenta switch
        {
            Ferramentas.Gravar when criar =>
                "Um arquivo novo será criado com o conteúdo abaixo.",
            Ferramentas.Gravar =>
                "O conteúdo atual do arquivo será substituído. Não é possível desfazer pelo AIB.",
            Ferramentas.Editar =>
                "O trecho da linha \"-\" será trocado pelo da linha \"+\". O resto do arquivo não "
                + "muda. Não é possível desfazer pelo AIB.",
            Ferramentas.Shell =>
                "O comando roda no seu PowerShell, com as suas permissões. O AIB não desfaz o "
                + "que ele fizer.",
            Ferramentas.Habilidade when manual =>
                "Nada será executado: o texto do SKILL.md vai para o modelo ler.",
            Ferramentas.Habilidade =>
                "O script da habilidade roda na sua máquina, com as suas permissões. O AIB não "
                + "desfaz o que ele fizer.",
            Ferramentas.Arquivos when alvo.StartsWith("APAGAR ", StringComparison.Ordinal) =>
                "Vai para a Lixeira do Windows: dá para restaurar de lá.",
            Ferramentas.Arquivos =>
                "Nada existente é sobrescrito: se o destino já existir, a ação é recusada.",
            Ferramentas.Navegador when alvo.StartsWith("ABRIR SITE NOVO ", StringComparison.Ordinal) =>
                "O Edge do AIB abre este endereço, com o login que você tiver feito nele.",
            Ferramentas.Navegador when alvo.StartsWith("ANOTAR ", StringComparison.Ordinal) =>
                "A AIB vai ler isto sempre que abrir esta página, em qualquer conversa. Fica em "
                + "~/.AIB/navegador/notas, e dá para editar ou apagar à mão.",
            Ferramentas.Navegador =>
                "A ação acontece no site, com o seu login. O AIB não desfaz o que o site fizer.",
            Ferramentas.Lembrar =>
                "Fica valendo em todas as próximas conversas. Dá para rever e apagar em "
                + "Configurações > Memória.",
            Ferramentas.Lembrete when alvo.StartsWith("CANCELAR ", StringComparison.Ordinal) =>
                "O lembrete sai da lista e não avisa mais.",
            Ferramentas.Lembrete =>
                "Na hora marcada, a fala acima chega pelo orbe ou pela bandeja.",
            _ => "Esta ação altera o seu sistema e não pode ser desfeita pelo AIB."
        };

        FerramentaText.Text = ferramenta;
        AlvoText.Text = alvo;
        IconeAlvo.Data = ToolIcons.De(ferramenta);

        // A prévia que o WriteFileTool e o EditFileTool montavam e nenhuma view lia: o usuário
        // autorizava uma gravação vendo só o caminho. Autorizar sem ver o que muda é autorizar
        // no escuro.
        if (!string.IsNullOrWhiteSpace(contexto.ScriptBody))
        {
            PreviaText.Text = contexto.ScriptBody;
            PreviaBorder.Visibility = Visibility.Visible;
        }

        if (contexto.DenylistHit && !string.IsNullOrWhiteSpace(contexto.DenylistReason))
        {
            MotivoText.Text = "Motivo do bloqueio: " + contexto.DenylistReason;
            MotivoText.Visibility = Visibility.Visible;
        }

        if (contexto.ConteudoDeEmailNoContexto)
            AvisoEmailText.Visibility = Visibility.Visible;

        if (!string.IsNullOrWhiteSpace(contexto.Aviso))
        {
            AvisoPastaText.Text = contexto.Aviso;
            AvisoPastaText.Visibility = Visibility.Visible;
        }

        // "Sempre permitir" só faz sentido para comando (casado pelo texto exato na sessão) e para
        // o navegador (casado pelo site). Para as demais ferramentas seria uma autorização vaga.
        // Some quando o portão não o respeitaria: texto de terceiros no contexto, ou operação que
        // pergunta toda vez (SemSempre).
        bool shell = ferramenta == Ferramentas.Shell && !contexto.ConteudoDeEmailNoContexto;
        bool navegador = ferramenta == Ferramentas.Navegador && !contexto.SemSempre
                         && (!contexto.ConteudoDeEmailNoContexto
                             || (contexto.SempreApesarDeTerceiros && !contexto.EmailNoContexto));
        SempreCheck.Visibility = shell || navegador ? Visibility.Visible : Visibility.Collapsed;
        if (navegador) SempreCheck.Content = "Sempre permitir neste site (até fechar o AIB)";

        if (navegador && contexto.SempreSegurando)
        {
            _segurar = true;
            SempreCheck.Content = RotuloDeSegurar;
            SempreCheck.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (SempreCheck.IsChecked == true) return; // desmarcar é um clique só
                e.Handled = true;
                SempreCheck.CaptureMouse();
                ApertouSempre();
            };
            SempreCheck.PreviewMouseLeftButtonUp += (_, _) => SoltouSempre();
            SempreCheck.LostMouseCapture += (_, _) => SoltouSempre();

            // Espaço marcaria na hora, sem os 5 s.
            SempreCheck.PreviewKeyDown += (_, e) =>
            {
                if (SempreCheck.IsChecked != true) e.Handled = true;
            };
        }

        // No navegador, o texto de terceiros é quase sempre a própria página: o aviso só entra se
        // houver e-mail. Repetir em todo clique ensinaria a não ler o aviso.
        if (ferramenta == Ferramentas.Navegador && !contexto.EmailNoContexto)
            AvisoEmailText.Visibility = Visibility.Collapsed;

        // O foco nasce em "Recusar". Enter sem ler o card não pode executar nada.
        Loaded += (_, _) => RecusarButton.Focus();
    }

    /// <summary>
    /// Registra a decisão e trava os botões.
    /// <para>
    /// Travar não é enfeite: entre o clique e a saída do card da conversa existe um intervalo,
    /// e um segundo clique nesse intervalo chegaria a um card já respondido. O
    /// <see cref="TaskCompletionSource{TResult}.TrySetResult"/> já ignoraria o segundo, mas
    /// deixar o botão vivo depois de decidido dá a impressão de que a decisão ainda está em
    /// aberto.
    /// </para>
    /// <para>
    /// Quem tira o card da tela é a janela de chat, assim que a resposta chega. Aqui só se
    /// responde.
    /// </para>
    /// </summary>
    private void Encerrar(bool permitido)
    {
        PermitirButton.IsEnabled = false;
        RecusarButton.IsEnabled = false;
        SempreCheck.IsEnabled = false;
        _relogio?.Stop();

        _resposta.TrySetResult((permitido, permitido && SempreCheck.IsChecked == true));
    }

    // ── "Sempre" que se marca segurando ───────────────────────────────────

    /// <summary>Quanto tempo o clique tem de ficar na caixa para ela marcar.</summary>
    public static readonly TimeSpan EsperaDoSempre = TimeSpan.FromSeconds(5);

    private const string RotuloDeSegurar = "Sempre permitir este botão neste site (segure 5 s)";

    private bool _segurar;
    private DateTime _apertou;
    private System.Windows.Threading.DispatcherTimer? _relogio;

    /// <summary>O clique desceu na caixa: começa a contagem.</summary>
    public void ApertouSempre()
    {
        if (!_segurar || SempreCheck.IsChecked == true || !SempreCheck.IsEnabled) return;

        _apertou = DateTime.UtcNow;
        _relogio ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _relogio.Tick -= Batida;
        _relogio.Tick += Batida;
        _relogio.Start();
        SegurandoSempre(TimeSpan.Zero);
    }

    private void Batida(object? sender, EventArgs e) => SegurandoSempre(DateTime.UtcNow - _apertou);

    /// <summary>
    /// O clique continua na caixa há <paramref name="decorrido"/>: mostra quanto falta e, aos
    /// 5 s, marca.
    /// </summary>
    public void SegurandoSempre(TimeSpan decorrido)
    {
        if (!_segurar || SempreCheck.IsChecked == true) return;

        if (decorrido >= EsperaDoSempre)
        {
            _relogio?.Stop();
            SempreCheck.IsChecked = true;
            SempreCheck.Content = "Sempre permitir este botão neste site (até fechar o AIB)";
            if (SempreCheck.IsMouseCaptured) SempreCheck.ReleaseMouseCapture();
            return;
        }

        int falta = (int)Math.Ceiling((EsperaDoSempre - decorrido).TotalSeconds);
        SempreCheck.Content = $"Continue segurando... {falta}";
    }

    /// <summary>O clique saiu antes da hora: nada marca, e a contagem recomeça do zero.</summary>
    public void SoltouSempre()
    {
        if (!_segurar) return;

        _relogio?.Stop();
        if (SempreCheck.IsMouseCaptured) SempreCheck.ReleaseMouseCapture();
        if (SempreCheck.IsChecked != true) SempreCheck.Content = RotuloDeSegurar;
    }

    // ── A versão flutuante (perto do orbe) ─────────────────────────────────

    private string _ferramenta = "";
    private string _alvo = "";
    private bool _bloqueado;

    /// <summary>O que a pill da janela flutuante diz, e em que cor.</summary>
    public enum Risco { Destrutivo, Cuidado, Leitura }

    /// <summary>
    /// O risco de uma ação, para a janela flutuante. VERMELHO só para o que apaga ou
    /// sobrescreve sem volta; LARANJA para o que executa ou dá para desfazer; LILÁS para o que
    /// só guarda ou lê.
    /// <para>
    /// Pedido do usuário (16/10/2026): o card dizia "Ação destrutiva" em vermelho para tudo,
    /// até um Get-ChildItem — um alarme que toca sempre ensina a não ouvir o alarme. Só nas
    /// janelas flutuantes: o card dentro da conversa continua como está, por decisão dele.
    /// </para>
    /// </summary>
    public static (Risco Risco, string Rotulo) RiscoDe(string ferramenta, string alvo, bool bloqueado)
    {
        bool comeca(string prefixo) => alvo.StartsWith(prefixo, StringComparison.Ordinal);

        if (bloqueado) return (Risco.Destrutivo, "Ação destrutiva");

        return ferramenta switch
        {
            Ferramentas.Gravar when comeca("CRIAR ") => (Risco.Cuidado, "Cria um arquivo"),
            Ferramentas.Gravar => (Risco.Destrutivo, "Ação destrutiva"),
            Ferramentas.Editar => (Risco.Destrutivo, "Ação destrutiva"),
            Ferramentas.Shell => (Risco.Cuidado, "Executa na sua máquina"),
            Ferramentas.Habilidade when comeca("LER MANUAL ") => (Risco.Leitura, "Só leitura"),
            Ferramentas.Habilidade => (Risco.Cuidado, "Executa na sua máquina"),
            Ferramentas.Arquivos when comeca("APAGAR ") => (Risco.Cuidado, "Dá para restaurar"),
            Ferramentas.Arquivos => (Risco.Cuidado, "Mexe em arquivos"),
            Ferramentas.Navegador when comeca("ABRIR SITE NOVO ") => (Risco.Cuidado, "Abre um site"),
            Ferramentas.Navegador when comeca("ANOTAR ") => (Risco.Leitura, "Guarda uma nota"),
            Ferramentas.Navegador => (Risco.Cuidado, "Age no site, com seu login"),
            Ferramentas.Lembrar => (Risco.Leitura, "Guarda na memória"),
            Ferramentas.Lembrete => (Risco.Cuidado, "Agenda uma fala"),
            _ => (Risco.Destrutivo, "Ação destrutiva")
        };
    }

    /// <summary>O risco pintado — para o ensaio conferir sem comparar pincel.</summary>
    public Risco Pintado { get; private set; } = Risco.Destrutivo;

    /// <summary>
    /// Prepara o card para a janela flutuante: sem a casca de bolha (a janela já tem a dela),
    /// pill e "Permitir" na cor do risco, e o antes e depois do edit com as linhas coloridas.
    /// </summary>
    public void ParaFlutuante()
    {
        Casca.Background = System.Windows.Media.Brushes.Transparent;
        Casca.BorderThickness = new Thickness(0);
        Casca.Padding = new Thickness(0);
        Casca.Effect = null;

        var (risco, rotulo) = RiscoDe(_ferramenta, _alvo, _bloqueado);
        Pintado = risco;
        PillTexto.Text = rotulo;

        var (texto, fundo, borda, botao) = risco switch
        {
            Risco.Cuidado => ("WarnBrush", "WarnFill10Brush", "WarnBorderBrush", "WarnFilledButton"),
            Risco.Leitura => ("AccentLilacBrush", "AccentFill12Brush", "AccentBorderStrongBrush", "AccentFilledButton"),
            _ => ("DangerTextBrush", "DangerFill14Brush", "DangerEdge32Brush", "DangerFilledButton")
        };

        Pill.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, fundo);
        Pill.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, borda);
        PillTexto.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, texto);
        PillIcone.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, texto);
        PermitirButton.SetResourceReference(StyleProperty, botao);
        FerramentaText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, texto);

        if (_ferramenta == Ferramentas.Editar && PreviaBorder.Visibility == Visibility.Visible)
            ColorirDiferenca(PreviaText);
    }

    /// <summary>
    /// As linhas "-" com fundo vermelho e as "+" com fundo verde. Só o fundo: a fonte e os
    /// sinais ficam, e a captura em tons de cinza continua legível pelo - e pelo +.
    /// </summary>
    private void ColorirDiferenca(System.Windows.Controls.TextBlock alvo)
    {
        string texto = alvo.Text ?? "";
        alvo.Inlines.Clear();

        var linhas = texto.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        for (int i = 0; i < linhas.Length; i++)
        {
            var run = new System.Windows.Documents.Run(linhas[i]);

            if (linhas[i].StartsWith("-", StringComparison.Ordinal))
                run.SetResourceReference(System.Windows.Documents.TextElement.BackgroundProperty, "DangerFill12Brush");
            else if (linhas[i].StartsWith("+", StringComparison.Ordinal))
                run.SetResourceReference(System.Windows.Documents.TextElement.BackgroundProperty, "SuccessFill10Brush");

            alvo.Inlines.Add(run);
            if (i < linhas.Length - 1) alvo.Inlines.Add(new System.Windows.Documents.LineBreak());
        }
    }

    private void Permitir_Click(object sender, RoutedEventArgs e) => Encerrar(true);

    private void Recusar_Click(object sender, RoutedEventArgs e) => Encerrar(false);

    /// <summary>
    /// Descarta a pergunta sem decisão — cancelamento do turno, limpeza da conversa, encerramento.
    /// Devolve recusa: nada executa sem alguém autorizar.
    /// </summary>
    public void Descartar()
    {
        if (_resposta.Task.IsCompleted) return;

        DicaText.Text = "cancelado";
        PermitirButton.IsEnabled = false;
        RecusarButton.IsEnabled = false;
        SempreCheck.IsEnabled = false;

        _resposta.TrySetResult((false, false));
    }
}
