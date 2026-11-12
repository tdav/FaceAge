using FaceAge.Vision;

namespace FaceAge.Export;

/// <summary>Результат вырезания: найдено ли лицо, пиксели Size×Size×3 (R,G,B) и возраст по модели телефона.</summary>
public readonly record struct CropResult(bool Found, byte[] Rgb, float Pred);

/// <summary>
/// Вырезает лицо для обучения. Геометрия как в AgeEstimator.Analyze: квадрат вокруг центра рамки YuNet,
/// повёрнутый по линии глаз. Отличия: сторона × CropScale (запас для сдвигов при обучении),
/// за краем фото продолжаются крайние пиксели, уменьшение — с усреднением.
/// </summary>
public sealed class FaceCropper : IDisposable
{
    public const int Size = 256;
    public const float CropScale = 1.75f;
    public const float ModelCropScale = 1.5f;

    private readonly AgeEstimator estimator;

    public FaceCropper(byte[] detectorModel, byte[] ageModel)
    {
        this.estimator = new AgeEstimator(detectorModel, ageModel) { Calibrate = false };
    }

    /// <summary>rgba — пиксели R,G,B,A построчно с шагом rowStride.</summary>
    public CropResult Cut(ReadOnlySpan<byte> rgba, int width, int height, int rowStride)
    {
        var reading = this.estimator.Analyze(rgba, width, height, rowStride, 0);
        if (!reading.HasFace)
        {
            return new CropResult(false, [], 0);
        }

        var cx = reading.Face.X + reading.Face.Width / 2f;
        var cy = reading.Face.Y + reading.Face.Height / 2f;
        var step = MathF.Max(reading.Face.Width, reading.Face.Height) * CropScale / Size;
        var angle = reading.Roll * MathF.PI / 180f;
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);
        var n = Math.Max(1, (int)MathF.Ceiling(step));
        var samples = n * n;
        var rgb = new byte[Size * Size * 3];

        for (var b = 0; b < Size; b++)
        {
            for (var a = 0; a < Size; a++)
            {
                float r = 0, g = 0, bl = 0;
                for (var j = 0; j < n; j++)
                {
                    for (var i = 0; i < n; i++)
                    {
                        var ox = (a + (i + 0.5f) / n - Size / 2f) * step;
                        var oy = (b + (j + 0.5f) / n - Size / 2f) * step;
                        var u = Math.Clamp(cx + ox * cos - oy * sin - 0.5f, 0f, width - 1);
                        var v = Math.Clamp(cy + ox * sin + oy * cos - 0.5f, 0f, height - 1);
                        var u0 = (int)MathF.Floor(u);
                        var v0 = (int)MathF.Floor(v);
                        var fu = u - u0;
                        var fv = v - v0;
                        for (var k = 0; k < 4; k++)
                        {
                            var cu = Math.Min(u0 + (k & 1), width - 1);
                            var cv = Math.Min(v0 + (k >> 1), height - 1);
                            var weight = ((k & 1) == 0 ? 1 - fu : fu) * ((k >> 1) == 0 ? 1 - fv : fv);
                            var p = cv * rowStride + cu * 4;
                            r += rgba[p] * weight;
                            g += rgba[p + 1] * weight;
                            bl += rgba[p + 2] * weight;
                        }
                    }
                }

                var o = (b * Size + a) * 3;
                rgb[o] = (byte)Math.Clamp(MathF.Round(r / samples), 0, 255);
                rgb[o + 1] = (byte)Math.Clamp(MathF.Round(g / samples), 0, 255);
                rgb[o + 2] = (byte)Math.Clamp(MathF.Round(bl / samples), 0, 255);
            }
        }

        return new CropResult(true, rgb, reading.Age);
    }

    public void Dispose() => this.estimator.Dispose();
}
