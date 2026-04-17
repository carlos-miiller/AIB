using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using AIB.Services;

namespace AIB.Views;

public partial class ChatWindow : Window
{
    private readonly OpenAIService _openAIService;
    private readonly OcrService _ocrService;
    private readonly VoiceService _voiceService;
    private bool _includeScreenshot = false;
    private bool _isAnimating = false;
    private bool _isVoiceActive = false;
    private Storyboard? _pulseStoryboard;

    public ChatWindow()
    {
        InitializeComponent();
        _openAIService = new OpenAIService();
        _ocrService = new OcrService();
        _voiceService = new VoiceService();
        
        SetupVoiceService();
        SetupPulseAnimation();
        
        Deactivated += ChatWindow_Deactivated;
    }

    private async void SetupVoiceService()
    {
        try
        {
            await _voiceService.InitializeAsync();
            _voiceService.OnTranscriptionUpdated += VoiceService_OnTranscriptionUpdated;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erro ao inicializar Voz: {ex.Message}");
        }
    }

    private void SetupPulseAnimation()
    {
        _pulseStoryboard = new Storyboard();
        
        var opacityAnim = new DoubleAnimation(0, 0.6, TimeSpan.FromMilliseconds(800)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(opacityAnim, VoicePulse);
        Storyboard.SetTargetProperty(opacityAnim, new PropertyPath(OpacityProperty));
        
        var scaleXAnim = new DoubleAnimation(1, 1.4, TimeSpan.FromMilliseconds(800)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(scaleXAnim, VoicePulse);
        Storyboard.SetTargetProperty(scaleXAnim, new PropertyPath("RenderTransform.ScaleX"));

        var scaleYAnim = new DoubleAnimation(1, 1.4, TimeSpan.FromMilliseconds(800)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(scaleYAnim, VoicePulse);
        Storyboard.SetTargetProperty(scaleYAnim, new PropertyPath("RenderTransform.ScaleY"));

        _pulseStoryboard.Children.Add(opacityAnim);
        _pulseStoryboard.Children.Add(scaleXAnim);
        _pulseStoryboard.Children.Add(scaleYAnim);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        EnableBlur();
    }

    #region Focus & Blur P/Invoke
    [DllImport("user32.dll")]
    internal static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowCompositionAttributeData
    {
        public WindowCompositionAttribute Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    internal enum WindowCompositionAttribute
    {
        WCA_ACCENT_POLICY = 19
    }

    internal enum AccentState
    {
        ACCENT_DISABLED = 0,
        ACCENT_ENABLE_GRADIENT = 1,
        ACCENT_ENABLE_TRANSPARENTGRADIENT = 2,
        ACCENT_ENABLE_BLURBEHIND = 3,
        ACCENT_ENABLE_ACRYLICBLURBEHIND = 4,
        ACCENT_INVALID_STATE = 5
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AccentPolicy
    {
        public AccentState AccentState;
        public int AccentFlags;
        public int GradientColor; 
        public int AnimationId;
    }

    internal void EnableBlur()
    {
        var windowHelper = new WindowInteropHelper(this);

        var accent = new AccentPolicy();
        accent.AccentState = AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND;
        // The 0x01 alpha is the secret to getting blur without the black background glitch
        accent.GradientColor = (0x01 << 24) | (0x000000 & 0xFFFFFF); 

        var accentStructSize = Marshal.SizeOf(accent);
        var accentPtr = Marshal.AllocHGlobal(accentStructSize);
        Marshal.StructureToPtr(accent, accentPtr, false);

        var data = new WindowCompositionAttributeData();
        data.Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY;
        data.SizeOfData = accentStructSize;
        data.Data = accentPtr;

        SetWindowCompositionAttribute(windowHelper.Handle, ref data);
        Marshal.FreeHGlobal(accentPtr);
    }
    #endregion

    public void ToggleWindow()
    {
        if (Opacity > 0 && !_isAnimating)
        {
            HideWindow();
        }
        else if (!_isAnimating)
        {
            ShowWindow();
        }
    }

    private void ShowWindow()
    {
        double screenWidth = System.Windows.SystemParameters.WorkArea.Width;
        double screenHeight = System.Windows.SystemParameters.WorkArea.Height;
        
        this.Width = screenWidth * 0.50; 
        this.Height = (screenHeight * 0.45) + 100;
        
        this.Left = (screenWidth - this.Width) / 2;
        this.Top = screenHeight - this.Height - 50;

        _includeScreenshot = false;
        UpdateCameraButton();

        this.Visibility = Visibility.Visible;

        // Reinforce focus: Force the window to the foreground so Deactivated works later
        var windowHelper = new WindowInteropHelper(this);
        SetForegroundWindow(windowHelper.Handle);

        this.Activate();
        InputBox.Focus();

        // Standard Window Opacity animation as AllowsTransparency=True is back!
        var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
        anim.Completed += (s, e) => _isAnimating = false;
        _isAnimating = true;
        this.BeginAnimation(OpacityProperty, anim);
    }

    private void HideWindow()
    {
        var anim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
        anim.Completed += (s, e) => 
        {
            this.Visibility = Visibility.Hidden;
            _isAnimating = false;
        };
        _isAnimating = true;
        this.BeginAnimation(OpacityProperty, anim);
    }

    private void ChatWindow_Deactivated(object? sender, EventArgs e)
    {
        if (this.Visibility == Visibility.Visible)
        {
            HideWindow();
        }
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape)
        {
            if (Opacity > 0 && !_isAnimating)
            {
                HideWindow();
            }
            e.Handled = true;
        }
    }

    private void InputBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            _ = ProcessMessageAsync();
        }
    }

    private void SendButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ProcessMessageAsync();
    }

    private void ScreenshotButton_Click(object sender, RoutedEventArgs e)
    {
        _includeScreenshot = !_includeScreenshot;
        UpdateCameraButton();
    }

    private void VoiceButton_Click(object sender, RoutedEventArgs e)
    {
        _isVoiceActive = !_isVoiceActive;
        
        if (_isVoiceActive)
        {
            _voiceService.StartListening();
            MicIcon.Text = "🔴";
            _pulseStoryboard?.Begin();
        }
        else
        {
            _voiceService.StopListening();
            MicIcon.Text = "🎙️";
            _pulseStoryboard?.Stop();
            VoicePulse.Opacity = 0;
        }
    }

    private void VoiceService_OnTranscriptionUpdated(object? sender, TranscriptionEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            // Se for final, envia. Se for parcial, mostra no InputBox (ghost text style)
            if (e.IsFinal)
            {
                InputBox.Text = e.Text;
                _ = ProcessMessageAsync();
            }
            else
            {
                // Mostra o texto atual, mas permite que o usuário veja o que está sendo captado
                InputBox.Text = e.Text;
                InputBox.SelectionStart = InputBox.Text.Length;
            }
        });
    }

    private void UpdateCameraButton()
    {
        if (_includeScreenshot)
        {
            CameraIcon.Text = "✓";
            CameraIcon.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 120, 212)); 
            ScreenshotButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(51, 51, 51)); 
        }
        else
        {
            CameraIcon.Text = "📷";
            CameraIcon.ClearValue(System.Windows.Controls.TextBlock.ForegroundProperty);
            ScreenshotButton.ClearValue(System.Windows.Controls.Button.BackgroundProperty);
        }
    }

    private async Task ProcessMessageAsync()
    {
        string text = InputBox.Text.Trim();
        if (string.IsNullOrEmpty(text))
            return;

        InputBox.Clear();
        AddBubble(text, true);

        string? b64Image = null;
        string promptWithContext = text;

        if (_includeScreenshot)
        {
            b64Image = ScreenshotService.CapturePrimaryScreenAsBase64();
            
            // Tenta extrair texto via OCR nativo para ajudar modelos que não veem imagem (ou dar contexto extra)
            string ocrText = await _ocrService.ExtractTextFromBase64Async(b64Image);
            if (!string.IsNullOrWhiteSpace(ocrText))
            {
                promptWithContext += $"\n\n[Texto extraído da tela para contexto]:\n{ocrText}";
            }

            _includeScreenshot = false;
            UpdateCameraButton();
        }

        var aiBubble = AddBubble("⋯", false);

        try
        {
            var stream = _openAIService.SendMessageStreamAsync(promptWithContext, b64Image);
            aiBubble.Text = ""; 
            
            await foreach (var chunk in stream)
            {
                aiBubble.Text += chunk;
                ChatScrollViewer.ScrollToBottom();
            }
        }
        catch (Exception ex)
        {
            aiBubble.Text = $"❌ Erro: {ex.Message}";
        }
    }

    private TextBlock AddBubble(string text, bool isUser)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            Style = (Style)FindResource("BubbleTextStyle")
        };

        var border = new Border
        {
            Style = isUser ? (Style)FindResource("UserBubbleStyle") : (Style)FindResource("AIBubbleStyle"),
            Child = textBlock
        };

        MessagesPanel.Children.Add(border);
        ChatScrollViewer.ScrollToBottom();
        return textBlock;
    }
}
