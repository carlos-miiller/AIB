using System;
using System.Windows;
using System.Windows.Controls;

// Size e Rect existem em System.Drawing e em System.Windows; o WPF usa os de System.Windows.
using Size = System.Windows.Size;
using Rect = System.Windows.Rect;

namespace AIB.Ui;

/// <summary>
/// Faz o filho ocupar a largura do CONTEÚDO, e não a largura disponível.
/// <para>
/// Existe por causa da bolha da IA. O texto do usuário é um <see cref="System.Windows.Controls.TextBlock"/>,
/// que mede o próprio conteúdo e encolhe; o texto da IA é um <c>MarkdownViewer</c>, cujo miolo
/// é um <see cref="FlowDocumentScrollViewer"/>. Documento de fluxo é feito para PAGINAR: ele
/// aceita toda a largura oferecida e distribui o texto nela. O resultado era "Kai online. Olá."
/// dentro de uma bolha de 74% da lista, com um vão enorme à direita.
/// </para>
/// <para>
/// A correção é medir duas vezes. Na primeira, com largura infinita, o filho responde a largura
/// natural do texto sem quebra nenhuma. Na segunda, já com a largura escolhida — o menor entre
/// a natural e a disponível — para a ALTURA sair com a quebra de linha real. Sem essa segunda
/// passada, um texto longo reportaria a altura de uma linha só e ficaria cortado.
/// </para>
/// </summary>
public sealed class ShrinkWrap : Decorator
{
    /// <summary>
    /// Quanto o conteúdo quer de largura, quando o próprio filho não sabe responder.
    /// <para>
    /// Um <see cref="FlowDocumentScrollViewer"/> devolve como largura desejada exatamente a
    /// largura que lhe foi oferecida — inclusive medido com infinito. Para ele, medir o filho
    /// não descobre nada, e quem monta a bolha passa aqui uma medição feita por fora, sobre o
    /// próprio documento.
    /// </para>
    /// </summary>
    public Func<double>? MedirNatural { get; set; }

    protected override Size MeasureOverride(Size constraint)
    {
        var filho = Child;
        if (filho == null) return new Size(0, 0);

        double natural;

        if (MedirNatural != null)
        {
            natural = MedirNatural();

            // A medição por fora cobre o texto, não a moldura de quem o desenha: borda,
            // padding e a barra de rolagem do visualizador entram por cima.
            filho.Measure(new Size(double.PositiveInfinity, constraint.Height));
        }
        else
        {
            // 1ª passada: quanto o conteúdo quer, se ninguém o obrigar a quebrar.
            filho.Measure(new Size(double.PositiveInfinity, constraint.Height));
            natural = filho.DesiredSize.Width;
        }

        // Um filho que devolve infinito ou NaN não sabe se medir sozinho; nesse caso vale a
        // largura oferecida, que é o comportamento de antes.
        if (double.IsNaN(natural) || double.IsInfinity(natural)) natural = constraint.Width;

        double largura = double.IsInfinity(constraint.Width)
            ? natural
            : Math.Min(natural, constraint.Width);

        // Degenerado: nem o conteúdo nem o pai souberam dizer uma largura. Zerar aqui faria a
        // bolha sumir sem erro nenhum; medir o filho normalmente devolve o comportamento de
        // antes, que é grande demais mas legível.
        if (double.IsNaN(largura) || double.IsInfinity(largura) || largura < 0)
        {
            filho.Measure(constraint);
            return filho.DesiredSize;
        }

        // 2ª passada: a altura precisa refletir a quebra na largura que vai valer de fato.
        filho.Measure(new Size(largura, constraint.Height));

        return new Size(largura, filho.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(arrangeSize));
        return arrangeSize;
    }
}
