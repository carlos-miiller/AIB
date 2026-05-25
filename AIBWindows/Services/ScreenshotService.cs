using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace AIB.Services;

public static class ScreenshotService
{
    public static string CaptureAllScreensAsBase64()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        int left = screens.Min(s => s.Bounds.X);
        int top = screens.Min(s => s.Bounds.Y);
        int right = screens.Max(s => s.Bounds.Right);
        int bottom = screens.Max(s => s.Bounds.Bottom);
        int width = right - left;
        int height = bottom - top;

        using var bitmap = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bitmap))
        {
            foreach (var screen in screens)
            {
                // Desenha cada monitor na sua posição relativa correta na imagem final
                g.CopyFromScreen(screen.Bounds.X, screen.Bounds.Y, 
                                screen.Bounds.X - left, screen.Bounds.Y - top, 
                                screen.Bounds.Size, CopyPixelOperation.SourceCopy);
            }
        }

        // Resizing logic (mantemos o aumento de resolução para panoramas)
        int maxW = 3840, maxH = 2160; 
        int newW = bitmap.Width, newH = bitmap.Height;
        if (bitmap.Width > maxW || bitmap.Height > maxH)
        {
            float ratio = Math.Min((float)maxW / bitmap.Width, (float)maxH / bitmap.Height);
            newW = (int)(bitmap.Width * ratio);
            newH = (int)(bitmap.Height * ratio);
        }

        using var resized = new Bitmap(newW, newH);
        using (var g = Graphics.FromImage(resized))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(bitmap, 0, 0, newW, newH);
        }

        // 3. Convert to jpeg and base64
        using var ms = new MemoryStream();
        // Setup jpeg quality
        var jpegCodec = GetEncoder(ImageFormat.Jpeg);
        var encoderParams = new EncoderParameters(1);
        encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
        
        resized.Save(ms, jpegCodec!, encoderParams);
        return Convert.ToBase64String(ms.ToArray());
    }

    private static ImageCodecInfo? GetEncoder(ImageFormat format)
    {
        var codecs = ImageCodecInfo.GetImageDecoders();
        foreach (var codec in codecs)
        {
            if (codec.FormatID == format.Guid)
            {
                return codec;
            }
        }
        return null;
    }
}
