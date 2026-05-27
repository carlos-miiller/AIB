using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace AIB.Views;

public partial class ShadowWidget : Window
{
    private readonly DispatcherTimer _hideBubbleTimer;

    public ShadowWidget()
    {
        InitializeComponent();
        
        _hideBubbleTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30) // Bolha some após 30 segundos (ou clique no X)
        };
        _hideBubbleTimer.Tick += (s, e) => HideSuggestion();
    }

    public void ShowSuggestion(string suggestion)
    {
        Dispatcher.Invoke(() =>
        {
            SuggestionText.Text = suggestion;
            ThoughtBubble.Visibility = Visibility.Visible;
            ThoughtTail.Visibility = Visibility.Visible;

            // Fade in sincronizado nos dois elementos (sem isso, a cauda piscava
            // em opacity 1 enquanto a bolha animava de 0 para 1).
            var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromSeconds(0.4));
            ThoughtBubble.BeginAnimation(OpacityProperty, fadeIn);
            ThoughtTail.BeginAnimation(OpacityProperty, fadeIn);

            _hideBubbleTimer.Stop();
            _hideBubbleTimer.Start();
        });
    }

    private void HideSuggestion()
    {
        _hideBubbleTimer.Stop();

        // Fade out sincronizado. Sem isso a cauda ficava visível por 0.4s sozinha.
        var fadeOut = new DoubleAnimation(1.0, 0.0, TimeSpan.FromSeconds(0.4));
        fadeOut.Completed += (s, e) =>
        {
            ThoughtBubble.Visibility = Visibility.Collapsed;
            ThoughtTail.Visibility = Visibility.Collapsed;
        };
        ThoughtBubble.BeginAnimation(OpacityProperty, fadeOut);
        ThoughtTail.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void CloseSuggestionButton_Click(object sender, RoutedEventArgs e)
    {
        HideSuggestion();
    }

    /// <summary>
    /// Alterna entre estado "ativo" (tela do cursor, opacidade 0.9) e
    /// "inativo" (demais telas, opacidade 0.15). Aplica só ao ícone do olho —
    /// o balão tem opacidade independente.
    /// </summary>
    public void SetActiveState(bool isActive)
    {
        Dispatcher.Invoke(() =>
        {
            double target = isActive ? 0.9 : 0.15;
            var anim = new DoubleAnimation(target, TimeSpan.FromSeconds(0.25));
            EyeBorder.BeginAnimation(OpacityProperty, anim);
        });
    }
}
