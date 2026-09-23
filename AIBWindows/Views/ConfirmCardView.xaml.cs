using System;
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
            Ferramentas.Navegador => "Clicar nesta página?",
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
            Ferramentas.Navegador =>
                "A ação acontece no site, com o seu login. O AIB não desfaz o que o site fizer.",
            _ => "Esta ação altera o seu sistema e não pode ser desfeita pelo AIB."
        };

        FerramentaText.Text = ferramenta;
        AlvoText.Text = alvo;
        AlvoText.ToolTip = alvo;
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

        _resposta.TrySetResult((permitido, permitido && SempreCheck.IsChecked == true));
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
