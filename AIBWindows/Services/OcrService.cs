using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace AIB.Services;

public class OcrService
{
    private readonly OcrEngine _engine;

    public OcrService()
    {
        // Tenta inicializar com o idioma do sistema, ou Português se disponível
        if (OcrEngine.IsLanguageSupported(new Windows.Globalization.Language("pt-BR")))
        {
             _engine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("pt-BR"));
        }
        else
        {
            _engine = OcrEngine.TryCreateFromUserProfileLanguages();
        }
    }

    public async Task<string> ExtractTextFromAllScreensAsync()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        var sb = new System.Text.StringBuilder();

        for (int i = 0; i < screens.Length; i++)
        {
            var screen = screens[i];
            string screenLabel = $"--- TELA {i + 1} {(screen.Primary ? "(Principal)" : "")} ---";
            
            try
            {
                using var bitmap = new System.Drawing.Bitmap(screen.Bounds.Width, screen.Bounds.Height);
                using (var g = System.Drawing.Graphics.FromImage(bitmap))
                {
                    g.CopyFromScreen(screen.Bounds.X, screen.Bounds.Y, 0, 0, screen.Bounds.Size);
                }

                using var stream = new MemoryStream();
                bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                byte[] bytes = stream.ToArray();

                string text = await ExtractTextFromBytesAsync(bytes);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    sb.AppendLine(screenLabel);
                    sb.AppendLine(text);
                    sb.AppendLine();
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"{screenLabel} (Erro ao capturar: {ex.Message})");
            }
        }

        return sb.ToString();
    }

    private async Task<string> ExtractTextFromBytesAsync(byte[] imageBytes)
    {
        using var stream = new MemoryStream(imageBytes);
        using var randomStream = stream.AsRandomAccessStream();
        
        var decoder = await BitmapDecoder.CreateAsync(randomStream);
        using var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine == null) return "OCR Engine not available.";

        var result = await engine.RecognizeAsync(softwareBitmap);
        return result?.Text ?? string.Empty;
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
            Console.WriteLine($"Erro no OCR: {ex.Message}");
            return $"[Erro ao processar OCR: {ex.Message}]";
        }
    }
}
