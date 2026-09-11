using System;
using System.Globalization;
using WBinding = System.Windows.Data.Binding;
using IValueConverter = System.Windows.Data.IValueConverter;

namespace AIB.Ui;

/// <summary>
/// Texto em caixa alta para os rótulos de seção — "RESUMO DA KAI".
/// <para>
/// No conversor e não no dado: o nome da personalidade é o mesmo que aparece no header em caixa
/// normal, e gravá-lo maiúsculo na fonte obrigaria a desfazer do outro lado.
/// </para>
/// </summary>
public sealed class MaiusculaConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value as string ?? "").ToUpperInvariant();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        WBinding.DoNothing;
}
