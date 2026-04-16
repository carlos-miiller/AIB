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

    public async Task<string> ExtractTextFromBase64Async(string base64Image)
    {
        if (string.IsNullOrWhiteSpace(base64Image)) return string.Empty;

        try
        {
            byte[] bytes = Convert.FromBase64String(base64Image);
            using var ms = new MemoryStream(bytes);
            using var randomStream = ms.AsRandomAccessStream();

            var decoder = await BitmapDecoder.CreateAsync(randomStream);
            using var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

            var result = await _engine.RecognizeAsync(softwareBitmap);
            return result.Text;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erro no OCR: {ex.Message}");
            return $"[Erro ao processar OCR: {ex.Message}]";
        }
    }
}
