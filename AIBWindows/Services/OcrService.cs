using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace AIB.Services;

public class OcrService
{
    // Engine único reutilizado: cria uma vez no construtor e usa em todas as chamadas.
    // Antes existiam dois engines (um no construtor não-usado, outro recriado por chamada).
    private readonly OcrEngine? _engine;

    public OcrService()
    {
        // Preferência: pt-BR. Fallback: idiomas do perfil do usuário Windows.
        // Pode resultar em null se nenhum pacote OCR estiver instalado em
        // Configurações → Apps → Recursos opcionais → "OCR" do idioma.
        var ptBr = new Windows.Globalization.Language("pt-BR");
        _engine = OcrEngine.IsLanguageSupported(ptBr)
            ? OcrEngine.TryCreateFromLanguage(ptBr)
            : OcrEngine.TryCreateFromUserProfileLanguages();
    }

    /// <summary>
    /// Captura apenas a tela onde está o cursor do mouse. Recomendado para uso
    /// frequente (Shadow Assistant, contexto rápido) — payload pequeno.
    /// </summary>
    public async Task<string> ExtractTextFromActiveScreenAsync()
    {
        return await Task.Run(async () =>
        {
            try
            {
                var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
                byte[] bytes = CapturePngBytes(screen.Bounds);
                return await ExtractTextFromBytesAsync(bytes);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OCR] Erro ao capturar tela ativa: {ex.Message}");
                return string.Empty;
            }
        });
    }

    /// <summary>
    /// Captura apenas a área de uma janela específica (HWND). Útil para o Shadow Assistant
    /// quando se quer focar no que o usuário está olhando agora, evitando o ruído do desktop
    /// e barras de tarefas. Reduz o payload de OCR em 5-20x vs tela inteira.
    /// </summary>
    public async Task<string> ExtractTextFromWindowAsync(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return string.Empty;

        return await Task.Run(async () =>
        {
            try
            {
                if (!GetWindowRect(hwnd, out RECT rect)) return string.Empty;

                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                // Janela minimizada (-32000,-32000) ou off-screen → ignora
                if (width <= 0 || height <= 0 || rect.Left < -10000 || rect.Top < -10000)
                    return string.Empty;

                var bounds = new System.Drawing.Rectangle(rect.Left, rect.Top, width, height);
                byte[] bytes = CapturePngBytes(bounds);
                return await ExtractTextFromBytesAsync(bytes);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OCR] Erro ao capturar janela {hwnd}: {ex.Message}");
                return string.Empty;
            }
        });
    }

    /// <summary>
    /// Captura TODAS as telas conectadas, rotuladas. Use só quando o agente
    /// realmente precisa de panorama multi-monitor — payload pode estourar tokens.
    /// </summary>
    public async Task<string> ExtractTextFromAllScreensAsync()
    {
        return await Task.Run(async () =>
        {
            var screens = System.Windows.Forms.Screen.AllScreens;
            var sb = new System.Text.StringBuilder();

            for (int i = 0; i < screens.Length; i++)
            {
                var screen = screens[i];
                string label = $"--- TELA {i + 1} {(screen.Primary ? "(Principal)" : "")} ---";
                try
                {
                    byte[] bytes = CapturePngBytes(screen.Bounds);
                    string text = await ExtractTextFromBytesAsync(bytes);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        sb.AppendLine(label);
                        sb.AppendLine(text);
                        sb.AppendLine();
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"{label} (Erro ao capturar: {ex.Message})");
                }
            }
            return sb.ToString();
        });
    }

    private static byte[] CapturePngBytes(System.Drawing.Rectangle bounds)
    {
        using var bitmap = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size);
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return stream.ToArray();
    }

    private async Task<string> ExtractTextFromBytesAsync(byte[] imageBytes)
    {
        if (_engine == null) return "[OCR Engine indisponível: instale o pacote 'OCR' do idioma em Configurações do Windows]";

        try
        {
            using var randomAccessStream = new InMemoryRandomAccessStream();
            await randomAccessStream.WriteAsync(imageBytes.AsBuffer());
            randomAccessStream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
            using var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

            var result = await _engine.RecognizeAsync(softwareBitmap);
            return result?.Text ?? string.Empty;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OCR] Erro interno (ExtractTextFromBytesAsync): {ex.Message}");
            return string.Empty;
        }
    }

    public async Task<string> ExtractTextFromBase64Async(string base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return string.Empty;
        try
        {
            byte[] bytes = Convert.FromBase64String(base64);
            return await ExtractTextFromBytesAsync(bytes);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OCR] Erro ao decodificar Base64: {ex.Message}");
            return $"[Erro ao processar OCR: {ex.Message}]";
        }
    }

    // ── P/Invoke ────────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
}
