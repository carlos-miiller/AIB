using System.Windows;
using System.Windows.Media;

// WinForms entra junto com o WPF em net8.0-windows e traz homonimos.
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace AIB.Ui;

/// <summary>
/// Um pincel de <c>Themes/Tokens.xaml</c> para quem desenha por CÓDIGO — conversores e
/// elementos montados fora do XAML.
/// <para>
/// Existe porque esses lugares repetiam o hex do token (<c>#E5484D</c> no lugar de
/// <c>DangerBrush</c>, <c>#6F6A7A</c> no de <c>TextMutedBrush</c>): uma segunda cópia da cor que
/// não acompanha a primeira quando o tema muda. Lendo o recurso, a cor mora num lugar só.
/// </para>
/// <para>
/// O hex continua aqui, mas como RESERVA: sem <see cref="Application.Current"/> (ensaio que não
/// sobe o App), fora da thread dele ou sem o dicionário carregado, o desenho sai com a mesma cor de antes em vez de
/// sair sem cor. Por isso o hex passado tem de ser o MESMO do token — não é um segundo tema.
/// </para>
/// </summary>
public static class PincelDoTema
{
    public static SolidColorBrush De(string chave, string hexDeReserva)
    {
        try
        {
            // Só da thread dona do App. De outra, o pincel do dicionário (se não estiver
            // congelado) lançaria no primeiro acesso à cor — e quem chama é um conversor, que
            // não tem como tratar isso. A reserva cobre.
            var app = Application.Current;

            if (app != null && app.Dispatcher.CheckAccess()
                && app.TryFindResource(chave) is SolidColorBrush doTema)
            {
                // Congelado para poder ser compartilhado como os de reserva; o do dicionário
                // pode não vir assim, e aí vai uma cópia com a mesma cor.
                return doTema.IsFrozen ? doTema : Congelado(doTema.Color);
            }
        }
        catch
        {
            // Dicionário quebrado não pode apagar a cor da tela.
        }

        return Congelado((Color)ColorConverter.ConvertFromString(hexDeReserva));
    }

    private static SolidColorBrush Congelado(Color cor)
    {
        var pincel = new SolidColorBrush(cor);
        pincel.Freeze();
        return pincel;
    }
}
