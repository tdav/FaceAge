using FaceAge.Vision;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace FaceAge.Tests;

/// <summary>Сессии ONNX создаются один раз на все тесты класса.</summary>
public sealed class EstimatorFixture : IDisposable
{
    public EstimatorFixture()
    {
        var models = Path.Combine(AppContext.BaseDirectory, "Models");
        var photos = Path.Combine(AppContext.BaseDirectory, "Photos");
        if (!File.Exists(Path.Combine(models, "genderage.onnx")) || !Directory.Exists(photos) || !Directory.EnumerateFiles(photos, "*.jpg").Any())
        {
            // Модель и фото не хранятся в репозитории по условиям их лицензий.
            throw new InvalidOperationException("Нет модели возраста или тестовых фото: запустите fetch-assets.ps1 в корне репозитория.");
        }

        var detector = File.ReadAllBytes(Path.Combine(models, "yunet_320.onnx"));
        var ager = File.ReadAllBytes(Path.Combine(models, "genderage.onnx"));
        this.Estimator = new AgeEstimator(detector, ager);
        this.Raw = new AgeEstimator(detector, ager) { Calibrate = false };
    }

    /// <summary>Как в приложении: с поправкой возраста.</summary>
    public AgeEstimator Estimator { get; }

    /// <summary>Сырая оценка модели.</summary>
    public AgeEstimator Raw { get; }

    /// <summary>
    /// Кадр так, как его отдаёт CameraX: RGBA_8888, фото повёрнуто против часовой стрелки на rotation
    /// (CameraX сообщает поворот по часовой, который делает кадр прямым), у каждой строки — хвостовой отступ.
    /// </summary>
    /// <param name="edit">Порча снимка до «съёмки»: размытие, затемнение, наклон головы.</param>
    public static (byte[] Pixels, int Width, int Height, int RowStride) SensorFrame(string photo, int rotation, Action<IImageProcessingContext>? edit = null)
    {
        using var image = Image.Load<Rgba32>(Path.Combine(AppContext.BaseDirectory, "Photos", photo));
        if (edit is not null)
        {
            image.Mutate(edit);
        }

        // В ImageSharp RotateMode поворачивает по часовой стрелке.
        image.Mutate(x => x.Rotate(rotation switch
        {
            90 => RotateMode.Rotate270,
            180 => RotateMode.Rotate180,
            270 => RotateMode.Rotate90,
            _ => RotateMode.None,
        }));

        // Отступ 64 байта на строку — как у реальных буферов камеры, где rowStride выровнен.
        var rowStride = image.Width * 4 + 64;
        var pixels = new byte[rowStride * image.Height];
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(rows.GetRowSpan(y)).CopyTo(pixels.AsSpan(y * rowStride));
            }
        });

        return (pixels, image.Width, image.Height, rowStride);
    }

    public void Dispose()
    {
        this.Estimator.Dispose();
        this.Raw.Dispose();
    }
}

public sealed class AgeEstimatorTests(EstimatorFixture fixture) : IClassFixture<EstimatorFixture>
{
    private const string MiddleAged = "passport-man-middle-aged.jpg";
    private const string Young = "passport-man-young.jpg";

    // Сырая оценка модели (без поправки) проверяет предобработку: сломанный кроп, порядок каналов или
    // нормализация уводят её с эталона. Поправка проверяется отдельно.

    [Fact]
    public void MiddleAgedMan_FaceFoundAndRawAgeInThirties()
    {
        var (pixels, width, height, stride) = EstimatorFixture.SensorFrame(MiddleAged, 0);

        var reading = fixture.Raw.Analyze(pixels, width, height, stride, 0);

        Assert.Equal(FaceQuality.Good, reading.Quality);
        Assert.True(reading.FaceScore > 0.9f, $"уверенность детектора {reading.FaceScore}");
        // Эталон сырой оценки на этом фото — 34,3 года; допуск покрывает разницу в сборках ONNX Runtime.
        Assert.InRange(reading.Age, 30f, 38f);
    }

    [Fact]
    public void YoungMan_EstimatedYoungerThanMiddleAged()
    {
        var young = EstimatorFixture.SensorFrame(Young, 0);
        var youngReading = fixture.Raw.Analyze(young.Pixels, young.Width, young.Height, young.RowStride, 0);
        var middle = EstimatorFixture.SensorFrame(MiddleAged, 0);
        var middleReading = fixture.Raw.Analyze(middle.Pixels, middle.Width, middle.Height, middle.RowStride, 0);

        Assert.True(youngReading.HasFace);
        Assert.InRange(youngReading.Age, 24f, 32f); // эталон 28,8
        // Модель различает возраст, а не выдаёт одно число на любое лицо.
        Assert.True(middleReading.Age - youngReading.Age >= 3f, $"{youngReading.Age} против {middleReading.Age}");
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)] // фронтальная камера телефона в портрете обычно отдаёт 270
    public void RotatedSensorFrame_SameFaceAndAgeAsUpright(int rotation)
    {
        var upright = EstimatorFixture.SensorFrame(MiddleAged, 0);
        var expected = fixture.Estimator.Analyze(upright.Pixels, upright.Width, upright.Height, upright.RowStride, 0);
        var rotated = EstimatorFixture.SensorFrame(MiddleAged, rotation);

        var reading = fixture.Estimator.Analyze(rotated.Pixels, rotated.Width, rotated.Height, rotated.RowStride, rotation);

        // Размер «прямого» кадра и рамка лица — в координатах прямого изображения, как их рисует оверлей.
        Assert.Equal(expected.Frame, reading.Frame);
        Assert.True(reading.HasFace);
        Assert.InRange(reading.Face.X, expected.Face.X - 2, expected.Face.X + 2);
        Assert.InRange(reading.Face.Y, expected.Face.Y - 2, expected.Face.Y + 2);
        Assert.InRange(reading.Face.Width, expected.Face.Width - 2, expected.Face.Width + 2);
        Assert.InRange(reading.Age, expected.Age - 0.5f, expected.Age + 0.5f);
    }

    [Fact]
    public void BlankFrame_NoFace()
    {
        const int Width = 640;
        const int Height = 480;
        var pixels = new byte[Width * 4 * Height];
        Array.Fill(pixels, (byte)200);

        var reading = fixture.Estimator.Analyze(pixels, Width, Height, Width * 4, 270);

        Assert.False(reading.HasFace);
        Assert.Equal(FaceQuality.NoFace, reading.Quality);
        Assert.Equal(new Microsoft.Maui.Graphics.SizeF(Height, Width), reading.Frame);
    }

    [Fact]
    public void TiltedHead_RollMeasuredAndAgeUnchanged()
    {
        var upright = EstimatorFixture.SensorFrame(MiddleAged, 0, x => x.Pad(500, 500, SixLabors.ImageSharp.Color.Gray));
        var expected = fixture.Raw.Analyze(upright.Pixels, upright.Width, upright.Height, upright.RowStride, 0);
        var tilted = EstimatorFixture.SensorFrame(MiddleAged, 0, x => x.Pad(500, 500, SixLabors.ImageSharp.Color.Gray).Rotate(20).BackgroundColor(SixLabors.ImageSharp.Color.Gray));

        var reading = fixture.Raw.Analyze(tilted.Pixels, tilted.Width, tilted.Height, tilted.RowStride, 0);

        // Наклонённое лицо находится и годится для оценки.
        Assert.Equal(FaceQuality.Good, reading.Quality);
        // ponytail: точки глаз YuNet грубые (угол наклона ошибается на ~10°), поэтому выравнивание компенсирует
        // наклон лишь частично: на 7,9 тыс. фото UTKFace с наклоном ±25° оно возвращает ошибку к уровню прямых лиц,
        // но на этом фото наклон 20° всё ещё сдвигает оценку на 3–4 года. Точнее — нужна отдельная модель точек лица.
        Assert.InRange(reading.Age, expected.Age - 5f, expected.Age + 5f);
    }

    [Theory]
    [InlineData("blur", FaceQuality.Blurry)]
    [InlineData("dark", FaceQuality.TooDark)]
    [InlineData("bright", FaceQuality.TooBright)]
    [InlineData("small", FaceQuality.TooSmall)]
    public void SpoiledPhoto_RejectedWithReason(string spoil, FaceQuality expected)
    {
        Action<IImageProcessingContext> edit = spoil switch
        {
            // Размытие и затемнение заметно молодят лицо — такие кадры в оценку идти не должны.
            "blur" => x => x.GaussianBlur(5),
            "dark" => x => x.Brightness(0.3f),
            "bright" => x => x.Brightness(1.6f),
            _ => x => x.Resize(new ResizeOptions { Size = new SixLabors.ImageSharp.Size(140, 0) }),
        };
        var frame = EstimatorFixture.SensorFrame(MiddleAged, 0, edit);

        var reading = fixture.Estimator.Analyze(frame.Pixels, frame.Width, frame.Height, frame.RowStride, 0);

        Assert.Equal(expected, reading.Quality);
    }

    [Fact]
    public void Calibration_IsMonotonicAndMovesAge()
    {
        var knots = AgeEstimator.Calibration;
        for (var k = 1; k < knots.Length; k++)
        {
            Assert.True(knots[k].Raw > knots[k - 1].Raw, $"узлы по сырой оценке должны возрастать: {k}");
            Assert.True(knots[k].Age >= knots[k - 1].Age, $"поправка не должна делать старшего на вид моложе: {k}");
        }

        var (pixels, width, height, stride) = EstimatorFixture.SensorFrame(MiddleAged, 0);
        var raw = fixture.Raw.Analyze(pixels, width, height, stride, 0).Age;
        var calibrated = fixture.Estimator.Analyze(pixels, width, height, stride, 0).Age;

        // Модель на UTKFace завышает возраст в 20–40 лет — поправка снижает оценку.
        Assert.True(calibrated < raw, $"{calibrated} против сырой {raw}");
    }
}
