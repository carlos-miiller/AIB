using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AIB.Ui;

/// <summary>
/// Metade da altura, como <see cref="CornerRadius"/>. É o que faz o morph do orbe
/// (shadow-assistant.html §6) funcionar sem animar <c>CornerRadius</c>.
/// <para>
/// WPF puro não tem <c>CornerRadiusAnimation</c>. O caminho usual é
/// <c>ObjectAnimationUsingKeyFrames</c> com quadros discretos, e o resultado é um canto que
/// muda aos saltos no meio de uma animação que deveria ser contínua.
/// </para>
/// <para>
/// Não é preciso: TODO raio da spec é exatamente metade da altura do elemento — orbe 56/2=28,
/// barra 52/2=26, e as cascas internas 53/2=26,5 e 49/2=24,5. Ligando o raio ao
/// <c>ActualHeight</c>, ele acompanha de graça a <c>DoubleAnimation</c> de Height, quadro a
/// quadro, sem nenhuma animação própria.
/// </para>
/// </summary>
public sealed class AlturaParaRaioConverter : IValueConverter
{
    /// <summary>
    /// O maior raio: o do orbe. A barra cresce com o texto de várias linhas, e metade de uma
    /// altura de 120 daria uma cápsula que come os cantos do texto.
    /// </summary>
    public const double RaioMaximo = 28;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double altura = value is double d ? d : 0;

        // ActualHeight é 0 até o primeiro layout. Raio negativo lança; zero é o canto vivo, que
        // é o que o elemento tem antes de existir de fato.
        if (double.IsNaN(altura) || double.IsInfinity(altura) || altura <= 0)
            return new CornerRadius(0);

        return new CornerRadius(Math.Min(altura / 2, RaioMaximo));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException("Mão única: o raio sai da altura, nunca o contrário.");
}
