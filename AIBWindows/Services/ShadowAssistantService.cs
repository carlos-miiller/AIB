using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace AIB.Services;

/// <summary>
/// Shadow Assistant: observa qual janela está sob o cursor. Quando o usuário "dwellsˮ
/// (mouse parado) sobre uma janela por >= DWELL_MS, extrai texto APENAS daquela janela
/// (via UI Automation) e pede uma sugestão ao LLM.
///
/// Histórico das mudanças:
///  v1: OCR de tela inteira a cada 3s → 7k+ tokens, hardware-pesado
///  v2: OCR só da janela sob hover ≥ 3s → 2-3k tokens, mas ainda capturava UI chrome
///      (abas, favoritos, URL do navegador) misturado com conteúdo
///  v3 (atual): UI Automation só da janela sob hover ≥ 3s → 0.5-1.5k tokens, texto
///      estruturado, sem chrome de UI, ~10x mais leve em CPU/RAM. Apps que não
///      expõem UIA (jogos, viewers antigos) simplesmente não geram sugestão.
/// </summary>
public class ShadowAssistantService
{
    private const int POLL_MS = 400;          // Frequência de leitura da posição do cursor
    private const int DWELL_MS = 3000;        // Tempo parado para considerar "dwellˮ
    private const int MOVE_THRESHOLD_PX = 6;  // Tolerância para considerar "parado"

    private readonly DispatcherTimer _pollTimer;
    private readonly OpenAIService _openAIService;
    private readonly SettingsService _settingsService;

    // Estado de dwell
    private System.Drawing.Point _lastCursor;
    private DateTime _stillSince = DateTime.MinValue;
    // Identidade do último contexto processado:
    //   - Chave = título da janela (ou "hwnd:{N}" se sem título)
    //   - Trocou de aba no Chrome -> título muda -> chave nova -> processa
    //   - Ficou parado na mesma aba -> mesma chave -> NÃO reprocessa (mesmo após muito tempo)
    //   - Para "refresh" forçado, basta mover o mouse para outra janela e voltar.
    private string _lastProcessedKey = "";
    // Último texto extraído via UIA. Usado para evitar chamar o LLM quando o conteúdo
    // não mudou (ex: título muda por causa de notificação `(99+)` mas página é a mesma).
    private string _lastExtractedText = "";
    private bool _processing;

    // Filtros: HWNDs do próprio AIB que NUNCA devem ser processados
    private readonly System.Collections.Generic.HashSet<IntPtr> _ownHwnds = new();

    public bool IsActive { get; private set; }
    public event Action<string>? OnSuggestionReceived;

    /// <summary>
    /// Disparado quando o cursor muda de tela. O int é o índice em
    /// <see cref="System.Windows.Forms.Screen.AllScreens"/>. UI usa para destacar
    /// qual widget está "ativo" (opacidade 0.7) e mostrar a bolha de sugestão
    /// na tela certa.
    /// </summary>
    public event Action<int>? OnActiveScreenChanged;
    private int _lastActiveScreen = -1;

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
        _lastProcessedKey = "";
        _lastExtractedText = "";
        _lastActiveScreen = -1; // força disparo inicial do OnActiveScreenChanged
        _pollTimer.Start();
        Console.WriteLine("[SHADOW] Serviço INICIADO (dwell-mode).");
    }

    /// <summary>
    /// Retorna o índice da tela onde o cursor está agora (ou 0 se algo der errado).
    /// Útil para a UI sincronizar widgets quando o Shadow é (re)ativado.
    /// </summary>
    public static int GetCurrentScreenIndex()
    {
        try
        {
            var pt = System.Windows.Forms.Cursor.Position;
            var target = System.Windows.Forms.Screen.FromPoint(pt);
            var all = System.Windows.Forms.Screen.AllScreens;
            for (int i = 0; i < all.Length; i++)
                if (all[i].DeviceName == target.DeviceName) return i;
        }
        catch { }
        return 0;
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
        var current = System.Windows.Forms.Cursor.Position;

        // Detecção de mudança de tela acontece SEMPRE (independente de processing/dwell).
        // É barato e a UI precisa reagir em tempo real para acender o widget certo.
        int currentScreen = GetScreenIndexAt(current);
        if (currentScreen != _lastActiveScreen)
        {
            _lastActiveScreen = currentScreen;
            try { OnActiveScreenChanged?.Invoke(currentScreen); }
            catch (Exception ex) { Console.WriteLine($"[SHADOW] erro em OnActiveScreenChanged: {ex.Message}"); }
        }

        if (_processing) return; // Uma sugestão em curso — não enfileira outra

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

        // Identidade baseada em TÍTULO, não em HWND. Razão: trocar de aba no Chrome
        // mantém o mesmo HWND mas muda o título — comportamento que o usuário espera
        // que dispare nova sugestão.
        string title = GetWindowTitle(hwnd);
        string key = string.IsNullOrWhiteSpace(title) ? $"hwnd:{hwnd}" : title;

        // Só processa se o contexto MUDOU desde a última sugestão.
        // Ficar parado na mesma tela não dispara nova análise (evita spam de sugestões).
        // Para forçar refresh, basta passar o mouse por outra janela e voltar.
        if (key == _lastProcessedKey) return;

        _lastProcessedKey = key;
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
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [SHADOW] Dwell em '{windowTitle}' — extraindo via UIA...");

        // UI Automation: mais barato e limpo que OCR. Retorna null se a janela
        // não expõe acessibilidade útil (jogos, alguns viewers). Nesse caso,
        // o Shadow simplesmente não comenta nessa janela.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? text = await WindowTextExtractor.ExtractTextAsync(hwnd);
        sw.Stop();

        if (string.IsNullOrWhiteSpace(text))
        {
            Console.WriteLine($"[SHADOW] UIA não retornou texto útil para '{windowTitle}' ({sw.ElapsedMilliseconds}ms). Ignorando.");
            return;
        }

        Console.WriteLine($"[SHADOW] UIA OK ({text.Length} chars em {sw.ElapsedMilliseconds}ms).");

        // Defesa contra reprocessamento desnecessário: se o texto extraído for IDÊNTICO
        // ao último, não chama o LLM. Cobre o caso de título mudar sem mudança real de
        // conteúdo (notificações `(99+)`, contadores), abas diferentes com mesmo conteúdo,
        // etc. Pagamos só o custo barato do UIA (~100-300ms) e economizamos a chamada
        // LLM (10-30s).
        if (text == _lastExtractedText)
        {
            Console.WriteLine($"[SHADOW] Conteúdo idêntico ao último processado. Pulando LLM.");
            return;
        }
        _lastExtractedText = text;

        // Cap antes do LLM. UIA tende a ser mais denso/limpo que OCR, então
        // baixamos o cap de 3000 para 1500 chars — sugestões mais rápidas.
        const int MAX_CHARS = 1500;
        if (text.Length > MAX_CHARS)
        {
            text = text.Substring(0, MAX_CHARS) + "\n[...texto truncado pelo Shadow...]";
        }

        var settings = _settingsService.LoadSettings();
        string userPrompt =
            $"[JANELA EM FOCO]: {windowTitle}\n\n" +
            $"[TEXTO EXTRAÍDO DA JANELA]:\n{text}";

        Console.WriteLine($"[SHADOW] Enviando para LLM ({text.Length} chars)...");
        string llm = await _openAIService.AskStatelessAsync(SYSTEM_PROMPT, userPrompt, settings.ShadowModelName);
        Console.WriteLine($"[SHADOW] Resposta: {llm}");

        if (string.IsNullOrWhiteSpace(llm)) return;
        string trimmed = llm.Trim();
        if (trimmed.Equals("NOTHING", StringComparison.OrdinalIgnoreCase)) return;
        if (trimmed.StartsWith("[ERROR]", StringComparison.OrdinalIgnoreCase)) return;

        // Registra no histórico para a aba "Shadow Ast" da sidebar exibir
        ShadowHistoryService.Add(windowTitle, trimmed);

        OnSuggestionReceived?.Invoke(trimmed);
    }

    // ── Helpers de tela ─────────────────────────────────────────────────────

    private static int GetScreenIndexAt(System.Drawing.Point pt)
    {
        try
        {
            var target = System.Windows.Forms.Screen.FromPoint(pt);
            var all = System.Windows.Forms.Screen.AllScreens;
            for (int i = 0; i < all.Length; i++)
                if (all[i].DeviceName == target.DeviceName) return i;
        }
        catch { }
        return 0;
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
