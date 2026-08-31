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

        TituloText.Text = ferramenta switch
        {
            "write_file" => "Gravar neste arquivo?",
            "run_command" => "Executar este comando?",
            _ => "Autorizar esta ação?"
        };

        ConsequenciaText.Text = ferramenta switch
        {
            "write_file" =>
                "O conteúdo atual do arquivo será substituído. Não é possível desfazer pelo AIB.",
            "run_command" =>
                "O comando roda no seu PowerShell, com as suas permissões. O AIB não desfaz o "
                + "que ele fizer.",
            _ => "Esta ação altera o seu sistema e não pode ser desfeita pelo AIB."
        };

        FerramentaText.Text = ferramenta;
        AlvoText.Text = alvo;
        AlvoText.ToolTip = alvo;
        IconeAlvo.Data = ToolIcons.De(ferramenta);

        if (contexto.DenylistHit && !string.IsNullOrWhiteSpace(contexto.DenylistReason))
        {
            MotivoText.Text = "Motivo do bloqueio: " + contexto.DenylistReason;
            MotivoText.Visibility = Visibility.Visible;
        }

        // "Sempre permitir" só faz sentido para comando: ele é casado pelo texto exato do
        // comando na sessão. Para as demais ferramentas seria uma autorização vaga.
        SempreCheck.Visibility = ferramenta == "run_command" ? Visibility.Visible : Visibility.Collapsed;

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
