using System.Drawing.Imaging;
using System.Runtime.InteropServices;

/// <summary>Расшифровка и запись JPEG средствами GDI+ (Windows): отдельные пакеты для картинок не нужны.</summary>
public static class GdiCodec
{
    private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders().First(x => x.FormatID == ImageFormat.Jpeg.Guid);

    /// <summary>Фото → пиксели R,G,B,A построчно (шаг строки = width * 4); false — файл не открылся. Подходит под PhotoDecoder.</summary>
    public static bool Decode(byte[] photo, out byte[] rgba, out int width, out int height)
    {
        try
        {
            using var stream = new MemoryStream(photo);
            using var source = new Bitmap(stream);
            width = source.Width;
            height = source.Height;
            rgba = new byte[width * height * 4];

            var data = source.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[width * 4];
                for (var y = 0; y < height; y++)
                {
                    Marshal.Copy(data.Scan0 + (y * data.Stride), row, 0, row.Length);
                    for (var x = 0; x < width; x++)
                    {
                        // В памяти GDI+ порядок B,G,R,A.
                        var to = ((y * width) + x) * 4;
                        rgba[to] = row[(x * 4) + 2];
                        rgba[to + 1] = row[(x * 4) + 1];
                        rgba[to + 2] = row[x * 4];
                        rgba[to + 3] = row[(x * 4) + 3];
                    }
                }
            }
            finally
            {
                source.UnlockBits(data);
            }

            return true;
        }
        catch (Exception)
        {
            // Любая ошибка открытия (не картинка, повреждён файл, нехватка памяти GDI+) — фото пропускается.
            rgba = [];
            width = 0;
            height = 0;
            return false;
        }
    }

    /// <summary>RGB 256×256×3 → JPEG с качеством 95.</summary>
    public static byte[] EncodeJpeg(byte[] rgb)
    {
        const int Size = 256;
        using var bitmap = new Bitmap(Size, Size, PixelFormat.Format24bppRgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, Size, Size), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[Size * 3];
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    // В памяти GDI+ порядок B,G,R.
                    var from = ((y * Size) + x) * 3;
                    row[x * 3] = rgb[from + 2];
                    row[(x * 3) + 1] = rgb[from + 1];
                    row[(x * 3) + 2] = rgb[from];
                }

                Marshal.Copy(row, 0, data.Scan0 + (y * data.Stride), row.Length);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 95L);
        using var output = new MemoryStream();
        bitmap.Save(output, JpegCodec, parameters);
        return output.ToArray();
    }
}
