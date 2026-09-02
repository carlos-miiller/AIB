using System.Windows;

// O projeto tem ImplicitUsings com System.Drawing no escopo (herdado do WinForms que
// entra junto com o WPF em net8.0-windows). Sem este alias, Point e ambiguo.
using Point = System.Windows.Point;

namespace AIB.Services;

/// <summary>
/// Onde o orbe do Shadow Assistant fica na tela — shadow-assistant.html §1.
/// <para>
/// Regra: monitor primário, centralizado na horizontal, base a 45px ACIMA da barra de tarefas.
/// </para>
/// <para>
/// A conta é pura de propósito. Posicionamento de janela é a parte que a spec marca como "leia
/// inteira antes de codar", e é onde mora a armadilha A1 — usar
/// <c>SystemParameters.PrimaryScreenHeight</c> em vez de <c>WorkArea</c> erra com a barra de
/// tarefas em cima, à esquerda, oculta, ou com DPI diferente de 100%. Separando o cálculo da
/// janela, dá para exercitar todas essas configurações num ensaio, sem precisar de barra de
/// tarefas de verdade.
/// </para>
/// </summary>
public static class ScreenAnchorService
{
    /// <summary>Folga entre a base do orbe e a barra de tarefas — §2 gapAboveTaskbar.</summary>
    public const double FolgaAcimaDaBarra = 45;

    /// <summary>
    /// Canto superior esquerdo da janela.
    /// </summary>
    /// <param name="areaDeTrabalho">
    /// <c>SystemParameters.WorkArea</c> — já exclui a barra de tarefas, em qualquer borda, e
    /// respeita barras de terceiros. Nunca a altura da tela.
    /// </param>
    /// <param name="largura">Largura da janela (fixa; ver §1 ancoragem e A8).</param>
    /// <param name="altura">Altura atual da janela, incluindo a margem da sombra.</param>
    /// <param name="margemInferior">
    /// Espaço vazio entre a base da janela e a base do desenho. Existe porque a
    /// <c>DropShadowEffect</c> precisa de área para ser desenhada e o WPF recorta o que passar
    /// da janela. Sem descontá-la, a folga de 45px seria medida a partir do fim da sombra —
    /// e o orbe subiria na tela por uma quantidade que ninguém pediu.
    /// </param>
    /// <param name="folga">Folga acima da barra de tarefas.</param>
    public static Point Calcular(
        Rect areaDeTrabalho,
        double largura,
        double altura,
        double margemInferior = 0,
        double folga = FolgaAcimaDaBarra)
    {
        double x = areaDeTrabalho.Left + (areaDeTrabalho.Width - largura) / 2;

        // A base do DESENHO — e não a da janela — é o que fica a 45px da barra.
        double y = areaDeTrabalho.Bottom - folga - altura + margemInferior;

        return new Point(x, y);
    }
}
