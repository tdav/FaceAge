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

        this.Estimator = new AgeEstimator(
            File.ReadAllBytes(Path.Combine(models, "ultraface_rfb320.onnx")),
            File.ReadAllBytes(Path.Combine(models, "genderage.onnx")));
    }

    public AgeEstimator Estimator { get; }

    /// <summary>
    /// Кадр так, как его отдаёт CameraX: RGBA_8888, фото повёрнуто против часовой стрелки на rotation
    /// (CameraX сообщает поворот по часовой, который делает кадр прямым), у каждой строки — хвостовой отступ.
    /// </summary>
    public static (byte[] Pixels, int Width, int Height, int RowStride) SensorFrame(string photo, int rotation)
    {
        using var image = Image.Load<Rgba32>(Path.Combine(AppContext.BaseDirectory, "Photos", photo));

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

    public void Dispose() => this.Estimator.Dispose();
}

public sealed class AgeEstimatorTests(EstimatorFixture fixture) : IClassFixture<EstimatorFixture>
{
    private const string MiddleAged = "passport-man-middle-aged.jpg";
    private const string Young = "passport-man-young.jpg";

    [Fact]
    public void MiddleAgedMan_FaceFoundAndAgeInThirties()
    {
        var (pixels, width, height, stride) = EstimatorFixture.SensorFrame(MiddleAged, 0);

        var reading = fixture.Estimator.Analyze(pixels, width, height, stride, 0);

        Assert.True(reading.HasFace);
        Assert.True(reading.FaceScore > 0.9f, $"уверенность детектора {reading.FaceScore}");
        // Эталон на этом фото — 34,1 года; допуск покрывает разницу в сборках ONNX Runtime, но не сломанную предобработку.
        Assert.InRange(reading.Age, 30f, 38f);
    }

    [Fact]
    public void YoungMan_EstimatedYoungerThanMiddleAged()
    {
        var young = EstimatorFixture.SensorFrame(Young, 0);
        var youngReading = fixture.Estimator.Analyze(young.Pixels, young.Width, young.Height, young.RowStride, 0);
        var middle = EstimatorFixture.SensorFrame(MiddleAged, 0);
        var middleReading = fixture.Estimator.Analyze(middle.Pixels, middle.Width, middle.Height, middle.RowStride, 0);

        Assert.True(youngReading.HasFace);
        Assert.InRange(youngReading.Age, 24f, 32f); // эталон 28,3
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
        Assert.Equal(new Microsoft.Maui.Graphics.SizeF(Height, Width), reading.Frame);
    }
}
