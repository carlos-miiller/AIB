using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace AIB.Services;

public static class ScreenshotService
{
    public static string CapturePrimaryScreenAsBase64()
    {
        // 1. Get primary screen bounds
        int screenWidth = System.Windows.Forms.Screen.PrimaryScreen!.Bounds.Width;
        int screenHeight = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height;
        
        using var bitmap = new Bitmap(screenWidth, screenHeight);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.CopyFromScreen(0, 0, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
        }

        // 2. Resize maintaining aspect ratio (equivalent to max_size=(1920, 1080))
        int maxW = 1920, maxH = 1080;
        int newW = bitmap.Width, newH = bitmap.Height;
        if (bitmap.Width > maxW || bitmap.Height > maxH)
        {
            float ratioX = (float)maxW / bitmap.Width;
            float ratioY = (float)maxH / bitmap.Height;
            float ratio = Math.Min(ratioX, ratioY);
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
