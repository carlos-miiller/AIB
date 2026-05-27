using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace AIB.Services;

/// <summary>
/// Shadow Assistant: observa qual janela está sob o cursor. Quando o usuário "dwellsˮ
/// (mouse parado) sobre uma janela por >= DWELL_MS, captura OCR APENAS daquela janela
/// e pede uma sugestão ao LLM.
///
/// Por que mudou de "tela inteira a cada 3sˮ para "janela sob hover ≥ 3sˮ:
///  - Payload OCR cai de ~7k+ tokens (tela inteira em multi-monitor) para 500-2k.
///  - Sinal de intenção forte: mouse parado por 3s = usuário lendo/pensando ali.
///  - Sem polling cego: só dispara LLM quando há dwelling em janela NOVA.
/// </summary>
public class ShadowAssistantService
{
    private const int POLL_MS = 400;        // Frequência de leitura da posição do cursor
    private const int DWELL_MS = 3000;      // Tempo parado para considerar "dwellˮ
    private const int MOVE_THRESHOLD_PX = 6; // Tolerância para considerar "parado"

    private readonly DispatcherTimer _pollTimer;
    private readonly OcrService _ocrService;
    private readonly OpenAIService _openAIService;
    private readonly SettingsService _settingsService;

    // Estado de dwell
    private System.Drawing.Point _lastCursor;
    private DateTime _stillSince = DateTime.MinValue;
    private IntPtr _lastProcessedHwnd = IntPtr.Zero;
    private bool _processing;

    // Filtros: HWNDs do próprio AIB que NUNCA devem ser processados
    private readonly System.Collections.Generic.HashSet<IntPtr> _ownHwnds = new();

    public bool IsActive { get; private set; }
    public event Action<string>? OnSuggestionReceived;

    private const string SYSTEM_PROMPT =
        "Você é o Shadow Assistant. O texto abaixo é o que o usuário está olhando AGORA (OCR " +
        "de uma janela específica, capturado porque ele parou o mouse ali por 3 segundos). " +
        "Identifique o que pode ser útil: explicação curta, sugestão de código, próximo passo, " +
        "correção de erro visível, etc. Seja DIRETO, máximo 2-3 linhas. " +
        "Se realmente não der para extrair nada acionável, retorne apenas 'NOTHING'. " +
        "Não cumprimente, não diga o que você é.";

    public ShadowAssistantService(OpenAIService openAIService, SettingsService settingsService)
    {
        _openAIService = openAIService;
        _settingsService = settingsService;
        _ocrService = new OcrService();

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(POLL_MS) };
        _pollTimer.Tick += PollTimer_Tick;
    }

    /// <summary>
    /// Registra HWNDs que pertencem ao próprio AIB para que nunca sejam capturados
    /// (evita auto-OCR recursivo).
    /// </summary>
    public void RegisterOwnWindow(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero) _ownHwnds.Add(hwnd);
    }

    public void Start()
    {
        if (IsActive) return;
        IsActive = true;
        _lastCursor = System.Windows.Forms.Cursor.Position;
        _stillSince = DateTime.Now;
        _lastProcessedHwnd = IntPtr.Zero;
        _pollTimer.Start();
        Console.WriteLine("[SHADOW] Serviço INICIADO (dwell-mode).");
    }

    public void Stop()
    {
        if (!IsActive) return;
        IsActive = false;
        _pollTimer.Stop();
        Console.WriteLine("[SHADOW] Serviço PAUSADO.");
    }

    private async void PollTimer_Tick(object? sender, EventArgs e)
    {
        if (_processing) return; // Uma sugestão em curso — não enfileira outra

        var current = System.Windows.Forms.Cursor.Position;
        int dx = current.X - _lastCursor.X;
        int dy = current.Y - _lastCursor.Y;
        bool moved = (dx * dx + dy * dy) > (MOVE_THRESHOLD_PX * MOVE_THRESHOLD_PX);

        if (moved)
        {
            _lastCursor = current;
            _stillSince = DateTime.Now;
            return;
        }

        // Parado: já bateu o dwell?
        if ((DateTime.Now - _stillSince).TotalMilliseconds < DWELL_MS) return;

        IntPtr hwnd = GetTopLevelWindowAt(current);
        if (hwnd == IntPtr.Zero) return;
        if (_ownHwnds.Contains(hwnd)) return;
        if (IsShellOrDesktop(hwnd)) return;
        if (hwnd == _lastProcessedHwnd) return; // Mesma janela que já comentamos, espera mudar

        _lastProcessedHwnd = hwnd;
        _processing = true;
        try
        {
            await ProcessHoveredWindow(hwnd);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SHADOW] Erro: {ex.Message}");
        }
        finally
        {
            _processing = false;
        }
    }

    private async Task ProcessHoveredWindow(IntPtr hwnd)
    {
        string windowTitle = GetWindowTitle(hwnd);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [SHADOW] Dwell em '{windowTitle}' — extraindo OCR...");

        string ocrText = await _ocrService.ExtractTextFromWindowAsync(hwnd);
        if (string.IsNullOrWhiteSpace(ocrText) || ocrText.Length < 20)
        {
            Console.WriteLine("[SHADOW] OCR vazio ou insignificante. Ignorando.");
            return;
        }

        // Limita o payload: o problema dos 7k tokens. Cortamos com folga em ~3000 chars.
        const int MAX_CHARS = 3000;
        if (ocrText.Length > MAX_CHARS)
        {
            ocrText = ocrText.Substring(0, MAX_CHARS) + "\n[...texto truncado pelo Shadow...]";
        }

        var settings = _settingsService.LoadSettings();
        string userPrompt =
            $"[JANELA EM FOCO]: {windowTitle}\n\n" +
            $"[OCR DESTA JANELA]:\n{ocrText}";

        Console.WriteLine($"[SHADOW] Enviando para LLM ({ocrText.Length} chars OCR)...");
        string llm = await _openAIService.AskStatelessAsync(SYSTEM_PROMPT, userPrompt, settings.ShadowModelName);
        Console.WriteLine($"[SHADOW] Resposta: {llm}");

        if (string.IsNullOrWhiteSpace(llm)) return;
        string trimmed = llm.Trim();
        if (trimmed.Equals("NOTHING", StringComparison.OrdinalIgnoreCase)) return;
        if (trimmed.StartsWith("[ERROR]", StringComparison.OrdinalIgnoreCase)) return;

        OnSuggestionReceived?.Invoke(trimmed);
    }

    // ── Detecção de janela top-level sob o cursor ───────────────────────────

    private static IntPtr GetTopLevelWindowAt(System.Drawing.Point pt)
    {
        IntPtr child = WindowFromPoint(new POINT { x = pt.X, y = pt.Y });
        if (child == IntPtr.Zero) return IntPtr.Zero;
        return GetAncestor(child, GA_ROOT);
    }

    private static bool IsShellOrDesktop(IntPtr hwnd)
    {
        var sb = new StringBuilder(128);
        GetClassName(hwnd, sb, sb.Capacity);
        string cls = sb.ToString();
        return cls == "Shell_TrayWnd"      // Barra de tarefas
            || cls == "Progman"            // Desktop (Program Manager)
            || cls == "WorkerW"            // Layer do desktop com wallpaper
            || cls == "Shell_SecondaryTrayWnd"; // Taskbar no segundo monitor
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        return GetWindowText(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "[sem título]";
    }

    // ── P/Invoke ────────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    private const uint GA_ROOT = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT pt);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
}
