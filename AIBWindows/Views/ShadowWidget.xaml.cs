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
            Interval = TimeSpan.FromSeconds(8) // Bolha some após 8 segundos
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
            
            // Fade in da bolha
            var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromSeconds(0.4));
            ThoughtBubble.BeginAnimation(OpacityProperty, fadeIn);

            _hideBubbleTimer.Stop();
            _hideBubbleTimer.Start();
        });
    }

    private void HideSuggestion()
    {
        _hideBubbleTimer.Stop();
        
        var fadeOut = new DoubleAnimation(1.0, 0.0, TimeSpan.FromSeconds(0.4));
        fadeOut.Completed += (s, e) => 
        {
            ThoughtBubble.Visibility = Visibility.Collapsed;
            ThoughtTail.Visibility = Visibility.Collapsed;
        };
        ThoughtBubble.BeginAnimation(OpacityProperty, fadeOut);
    }
}
