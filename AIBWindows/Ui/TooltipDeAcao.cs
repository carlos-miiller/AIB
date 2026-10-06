using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace AIB.Ui;

/// <summary>
/// O comportamento do tooltip das ações — §4.6 da spec de chat.
/// <para>
/// A CASCA mora no estilo global de ToolTip (Themes/Controls.xaml). O que fica aqui é o que um
/// estilo de ToolTip não alcança, porque pertence ao DONO: quando abre, quanto dura e de que
/// lado aparece. Sem isto valia o padrão do WPF, que espera meio segundo e FECHA EM 5 SEGUNDOS
/// — no meio da leitura de um caminho longo (D12).
/// </para>
/// </summary>
public static class TooltipDeAcao
{
    /// <summary>
    /// Abre quase na hora, como o mock (transição de 0,12s), e fica aberto enquanto o mouse
    /// estiver em cima. Abre ACIMA do alvo; o WPF inverte para baixo quando não cabe.
    /// </summary>
    public static void Configurar(DependencyObject dono)
    {
        ToolTipService.SetInitialShowDelay(dono, 150);
        ToolTipService.SetBetweenShowDelay(dono, 0);
        ToolTipService.SetShowDuration(dono, int.MaxValue);
        ToolTipService.SetPlacement(dono, PlacementMode.Top);
    }
}
