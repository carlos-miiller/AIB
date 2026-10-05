using System;
using System.Windows;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace AIB.Views;

/// <summary>
/// Põe uma janela de pergunta (confirmação, senha do terminal) logo acima do orbe, e não no
/// centro da tela.
/// <para>
/// No modo orbe o olho do usuário está no canto onde o orbe mora: uma pergunta no meio da tela
/// aparece longe de onde a ação foi pedida, e parece vir de outro programa.
/// </para>
/// </summary>
public static class PertoDoOrbe
{
    /// <summary>Folga entre a janela e o orbe.</summary>
    public const double Folga = 8;

    /// <summary>
    /// O retângulo do orbe na tela, em DIPs, ou null quando ele não está visível. Ligado pelo
    /// App; null nos ensaios.
    /// </summary>
    public static Func<Rect?>? Ancora { get; set; }

    /// <summary>Se há orbe na tela para ancorar.</summary>
    public static bool Ativo => Ancora?.Invoke() is not null;

    /// <summary>
    /// Onde pôr a janela: centrada no orbe, acima dele; sem espaço acima, abaixo. Sempre dentro
    /// da área de trabalho.
    /// </summary>
    /// <param name="sombraEmbaixo">A margem transparente abaixo da casca visível (a sombra). É
    /// a CASCA que tem de ficar a <see cref="Folga"/> do orbe, não a borda da janela.</param>
    public static Point Calcular(Rect orbe, Size janela, Rect area, double sombraEmbaixo = 0)
    {
        double x = orbe.Left + orbe.Width / 2 - janela.Width / 2;
        double y = orbe.Top - Folga - janela.Height + sombraEmbaixo;

        if (y < area.Top) y = orbe.Bottom + Folga;

        x = Math.Max(area.Left, Math.Min(x, area.Right - janela.Width));
        y = Math.Max(area.Top, Math.Min(y, area.Bottom - janela.Height));
        return new Point(x, y);
    }

    /// <summary>A posição em DIP arredondada para o pixel físico mais próximo.</summary>
    public static double NoPixel(double dip, double escala) =>
        escala <= 0 ? Math.Round(dip) : Math.Round(dip * escala) / escala;

    /// <summary>
    /// Ancora a janela ao orbe, se houver orbe na tela; senão ela fica onde já ia ficar. A
    /// posição é refeita quando a janela muda de tamanho (<c>SizeToContent</c> só sabe a altura
    /// depois de medir).
    /// </summary>
    public static void Posicionar(Window janela, double sombraEmbaixo = 0)
    {
        if (!Ativo) return;

        janela.WindowStartupLocation = WindowStartupLocation.Manual;

        void Aplicar()
        {
            var orbe = Ancora?.Invoke();
            if (orbe is null || janela.ActualWidth <= 0) return;

            var p = Calcular(orbe.Value, new Size(janela.ActualWidth, janela.ActualHeight),
                             SystemParameters.WorkArea, sombraEmbaixo);
            // Pixel inteiro: a conta centra a janela no orbe e cai em meio pixel, e uma janela
            // transparente em meio pixel tem o texto todo borrado. A 125% de escala, quase
            // toda posição em DIP cai entre dois pixels.
            double escala = System.Windows.Media.VisualTreeHelper.GetDpi(janela).DpiScaleX;
            janela.Left = NoPixel(p.X, escala);
            janela.Top = NoPixel(p.Y, escala);
        }

        // Antes de medir, fora da tela: sem isto ela pisca no canto (0,0) por um quadro.
        janela.Left = -10000;
        janela.Top = -10000;
        janela.SizeChanged += (_, _) => Aplicar();
        janela.Loaded += (_, _) => Aplicar();
    }
}
