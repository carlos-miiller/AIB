using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AIB.Services;

// WinForms entra junto com o WPF em net8.0-windows e traz homonimos.
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace AIB.Ui;

/// <summary>
/// Traduz o nível de urgência de um e-mail em cor, fundo ou rótulo — shadow-assistant.html §4.8.
/// <para>
/// Um conversor com <see cref="Modo"/> em vez de três classes: as três saídas vêm do MESMO
/// mapeamento, e separá-las abriria a porta para a cor da barra e a do selo divergirem — que é
/// exatamente o que a §4.8 não quer, já que as duas são o mesmo sinal lido de dois jeitos.
/// </para>
/// </summary>
public sealed class UrgenciaConverter : IValueConverter
{
    public enum Saida
    {
        /// <summary>Cor sólida: barra lateral, ponto e texto do selo.</summary>
        Cor,

        /// <summary>Fundo do selo — a mesma cor a 13% (Baixa usa branco a 5%).</summary>
        Fundo,

        /// <summary>Máxima / Média / Baixa. A12: o texto acompanha a cor, sempre.</summary>
        Rotulo
    }

    public Saida Modo { get; set; } = Saida.Cor;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var nivel = value is MailUrgency u ? u : MailUrgency.Baixa;

        return Modo switch
        {
            Saida.Rotulo => Rotulo(nivel),
            Saida.Fundo => Pincel(Fundo(nivel)),
            _ => Pincel(Cor(nivel))
        };
    }

    private static string Rotulo(MailUrgency nivel) => nivel switch
    {
        MailUrgency.Maxima => "Máxima",
        MailUrgency.Media => "Média",
        _ => "Baixa"
    };

    private static string Cor(MailUrgency nivel) => nivel switch
    {
        MailUrgency.Maxima => "#FFE5484D",
        MailUrgency.Media => "#FFE8A33D",
        _ => "#FF8B8794"
    };

    /// <summary>
    /// Fundo do selo. Máxima e Média usam a própria cor a 13%; Baixa usa branco a 5% — a
    /// cinza a 13% sumiria contra o card, que já é claro por transparência.
    /// </summary>
    private static string Fundo(MailUrgency nivel) => nivel switch
    {
        MailUrgency.Maxima => "#21E5484D",
        MailUrgency.Media => "#21E8A33D",
        _ => "#0DFFFFFF"
    };

    private static SolidColorBrush Pincel(string hex)
    {
        var pincel = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

        // Congelado: a lista redesenha estes pincéis a cada item, e um Freezable congelado é
        // compartilhado entre threads e não paga notificação de mudança.
        pincel.Freeze();
        return pincel;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException("Mão única: o nível vira aparência, nunca o contrário.");
}
