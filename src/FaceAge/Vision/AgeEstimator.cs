using Microsoft.ML.OnnxRuntime;

namespace FaceAge.Vision;

/// <summary>Пригодность кадра для оценки возраста; всё, кроме <see cref="Good"/>, — подсказка человеку.</summary>
public enum FaceQuality
{
    Good,
    NoFace,
    TooSmall,
    Turned,
    Blurry,
    TooDark,
    TooBright,
}

/// <summary>
/// Результат анализа кадра. Координаты — в «прямом» (повёрнутом) кадре.
/// Возраст считается и для плохого кадра: решение, учитывать ли его, принимает вызывающий по <see cref="Quality"/>.
/// </summary>
public readonly record struct AgeReading(
    FaceQuality Quality,
    float Age,
    float FaceScore,
    RectF Face,
    SizeF Frame,
    float Roll,
    float Yaw,
    float Sharpness,
    float Brightness,
    double Milliseconds)
{
    public bool HasFace => this.Quality != FaceQuality.NoFace;
}

/// <summary>
/// Два ONNX-прохода на кадр: YuNet находит лицо и пять точек (глаза, нос, углы рта),
/// InsightFace genderage оценивает возраст по выровненному по глазам кропу и его зеркальной копии.
/// Буферы выделены один раз. Не потокобезопасен: вызывается только с потока анализатора камеры.
/// </summary>
public sealed class AgeEstimator : IDisposable
{
    private const int DetectorSize = 320;
    private const int AgeSize = 96;
    private const float FaceThreshold = 0.7f;

    private static readonly int[] Strides = [8, 16, 32];

    private readonly InferenceSession detector;
    private readonly InferenceSession ager;
    private readonly float[] detectorInput = new float[3 * DetectorSize * DetectorSize];
    private readonly float[] ageInput = new float[2 * 3 * AgeSize * AgeSize];
    private readonly float[] luma = new float[AgeSize * AgeSize];
    private readonly OrtValue detectorValue;
    private readonly OrtValue ageValue;
    private readonly RunOptions runOptions = new();
    private readonly string[] detectorInputNames = ["input"];
    private readonly string[] detectorOutputNames =
        ["cls_8", "cls_16", "cls_32", "obj_8", "obj_16", "obj_32", "bbox_8", "bbox_16", "bbox_32", "kps_8", "kps_16", "kps_32"];
    private readonly string[] ageInputNames = ["data"];
    private readonly string[] ageOutputNames = ["fc1"];
    private readonly OrtValue[] detectorInputs;
    private readonly OrtValue[] ageInputs;

    public AgeEstimator(byte[] detectorModel, byte[] ageModel)
    {
        // Модели крошечные: два потока на сессию быстрее всех ядер — меньше синхронизации, телефон не греется.
        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = 2,
            InterOpNumThreads = 1,

            // Объявленные в моделях формы выходов не совпадают с фактическими (у genderage батч 1, а мы подаём
            // кадр и его зеркало; у YuNet вход уменьшен до 320). Это предупреждения, а не ошибки, но
            // ONNX Runtime пишет их на каждом кадре — в logcat это десятки строк в секунду.
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };

        this.detector = new InferenceSession(detectorModel, options);
        this.ager = new InferenceSession(ageModel, options);
        this.detectorValue = OrtValue.CreateTensorValueFromMemory(this.detectorInput, [1, 3, DetectorSize, DetectorSize]);
        this.ageValue = OrtValue.CreateTensorValueFromMemory(this.ageInput, [2, 3, AgeSize, AgeSize]);
        this.detectorInputs = [this.detectorValue];
        this.ageInputs = [this.ageValue];
    }

    // Настройки подобраны замером на UTKFace (tools/AgeEval); меняются только для замера.

    /// <summary>Сторона кропа возраста в долях большей стороны рамки YuNet.</summary>
    public float CropScale { get; init; } = 1.5f;

    /// <summary>Поворачивать кроп так, чтобы глаза стояли на одной горизонтали.</summary>
    public bool AlignByEyes { get; init; } = true;

    /// <summary>Усреднять возраст с зеркальной копией кропа.</summary>
    public bool UseFlip { get; init; } = true;

    /// <summary>Применять поправку <see cref="Calibration"/> к сырой оценке модели.</summary>
    public bool Calibrate { get; init; } = true;

    /// <summary>
    /// Поправка систематической ошибки модели: узлы «сырая оценка → медиана настоящего возраста»,
    /// между узлами — линейно, за краями — сдвиг крайнего узла. Подобрана tools/AgeEval/fit_calibration.py
    /// на 22 642 годных фото UTKFace (1–99 лет). На отложенной половине: 13–40 лет MAE 8,5 → 5,7, все возрасты 10,2 → 8,5.
    /// </summary>
    public static readonly (float Raw, float Age)[] Calibration =
    [
        (8.1f, 2.0f), (24.0f, 22.0f), (26.7f, 25.0f), (29.0f, 26.0f), (31.3f, 26.0f), (34.0f, 28.0f),
        (37.3f, 30.0f), (41.3f, 34.0f), (47.2f, 39.0f), (55.3f, 47.0f), (65.3f, 55.0f), (75.4f, 66.0f),
    ];

    // ponytail: пороги качества — калибровочные ручки. Заданы по UTKFace и размытым/затемнённым тестовым фото;
    // у реальной камеры шум завышает резкость, поэтому MinSharpness стоит проверить на устройстве.

    /// <summary>Минимальная сторона лица в пикселях кадра.</summary>
    public float MinFaceSize { get; init; } = 80f;

    /// <summary>
    /// Допустимый поворот головы: смещение носа от середины глаз в долях расстояния между глазами.
    /// На UTKFace поворот до 0,2 ошибку не увеличивал — порог отсекает только явный поворот.
    /// </summary>
    public float MaxYaw { get; init; } = 0.35f;

    /// <summary>
    /// Минимальная резкость: дисперсия лапласиана яркости кропа 96×96. Размытие молодит лицо на 2–3 года
    /// (гауссово σ=1 на тестовых фото), у 95 % фото UTKFace резкость выше 269.
    /// </summary>
    public float MinSharpness { get; init; } = 150f;

    /// <summary>
    /// Затемнение тоже молодит: при средней яркости кропа 85–92 тестовые фото теряли 1–2,5 года, при 50–55 — до 4.
    /// Строже 90 не ставим: в обычной комнате лицо на камере телефона темнее, чем на студийном фото.
    /// </summary>
    public float MinBrightness { get; init; } = 90f;

    public float MaxBrightness { get; init; } = 210f;

    /// <summary>
    /// Анализирует кадр RGBA_8888 от CameraX.
    /// </summary>
    /// <param name="rgba">Пиксели кадра, строка за строкой с шагом <paramref name="rowStride"/>.</param>
    /// <param name="rotation">Поворот по часовой стрелке, который делает кадр прямым (ImageInfo.RotationDegrees).</param>
    public AgeReading Analyze(ReadOnlySpan<byte> rgba, int width, int height, int rowStride, int rotation)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        // «Прямой» кадр: при 90° и 270° ширина и высота меняются местами.
        var swap = rotation is 90 or 270;
        var frameWidth = swap ? height : width;
        var frameHeight = swap ? width : height;
        var frame = new SizeF(frameWidth, frameHeight);

        // Пиксель прямого кадра (u, v) лежит в исходном кадре в (mu·u + mv·v + m0, nu·u + nv·v + n0):
        // поворот считается один раз, а не ветвлением на каждый пиксель.
        var (mu, mv, m0, nu, nv, n0) = rotation switch
        {
            90 => (0, 1, 0, -1, 0, height - 1),
            180 => (-1, 0, width - 1, 0, -1, height - 1),
            270 => (0, -1, width - 1, 1, 0, 0),
            _ => (1, 0, 0, 0, 1, 0),
        };

        // 1. YuNet: прямой кадр вписывается в 320×320 с чёрными полями, лицо не сплющивается.
        //    Вход — BGR 0…255 без нормализации, как blobFromImage в OpenCV.
        var scale = MathF.Min((float)DetectorSize / frameWidth, (float)DetectorSize / frameHeight);
        var padX = (DetectorSize - frameWidth * scale) / 2f;
        var padY = (DetectorSize - frameHeight * scale) / 2f;
        const int Plane = DetectorSize * DetectorSize;

        for (var dy = 0; dy < DetectorSize; dy++)
        {
            var v = (int)MathF.Floor((dy + 0.5f - padY) / scale);
            for (var dx = 0; dx < DetectorSize; dx++)
            {
                var u = (int)MathF.Floor((dx + 0.5f - padX) / scale);
                var i = dy * DetectorSize + dx;
                if (u < 0 || v < 0 || u >= frameWidth || v >= frameHeight)
                {
                    this.detectorInput[i] = 0f;
                    this.detectorInput[Plane + i] = 0f;
                    this.detectorInput[2 * Plane + i] = 0f;
                    continue;
                }

                var p = (nu * u + nv * v + n0) * rowStride + (mu * u + mv * v + m0) * 4;
                this.detectorInput[i] = rgba[p + 2];
                this.detectorInput[Plane + i] = rgba[p + 1];
                this.detectorInput[2 * Plane + i] = rgba[p];
            }
        }

        using var detected = this.detector.Run(this.runOptions, this.detectorInputNames, this.detectorInputs, this.detectorOutputNames);

        // Расшифровка YuNet (как FaceDetectorYN в OpenCV): на каждой ячейке сетки шагом 8/16/32 —
        // уверенность sqrt(cls·obj), смещение центра и логарифм размера рамки, смещения пяти точек.
        // ponytail: одно лицо — ячейка с максимальной уверенностью, без NMS. Для нескольких лиц нужен NMS.
        var bestScore = FaceThreshold;
        var bestLevel = -1;
        var bestIndex = -1;
        for (var level = 0; level < Strides.Length; level++)
        {
            var cls = detected[level].GetTensorDataAsSpan<float>();
            var obj = detected[3 + level].GetTensorDataAsSpan<float>();
            for (var k = 0; k < cls.Length; k++)
            {
                var score = MathF.Sqrt(Math.Clamp(cls[k], 0f, 1f) * Math.Clamp(obj[k], 0f, 1f));
                if (score > bestScore)
                {
                    bestScore = score;
                    bestLevel = level;
                    bestIndex = k;
                }
            }
        }

        if (bestLevel < 0)
        {
            return new AgeReading(FaceQuality.NoFace, 0, 0, default, frame, 0, 0, 0, 0, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        var stride = Strides[bestLevel];
        var columns = DetectorSize / stride;
        var cellX = bestIndex % columns;
        var cellY = bestIndex / columns;
        var box = detected[6 + bestLevel].GetTensorDataAsSpan<float>().Slice(bestIndex * 4, 4);
        var kps = detected[9 + bestLevel].GetTensorDataAsSpan<float>().Slice(bestIndex * 10, 10);

        // Из координат входа детектора — в координаты прямого кадра (снимаем поля и масштаб).
        var boxCenterX = ((cellX + box[0]) * stride - padX) / scale;
        var boxCenterY = ((cellY + box[1]) * stride - padY) / scale;
        var boxWidth = MathF.Exp(box[2]) * stride / scale;
        var boxHeight = MathF.Exp(box[3]) * stride / scale;
        var face = new RectF(boxCenterX - boxWidth / 2, boxCenterY - boxHeight / 2, boxWidth, boxHeight);

        var eyeAX = ((kps[0] + cellX) * stride - padX) / scale;
        var eyeAY = ((kps[1] + cellY) * stride - padY) / scale;
        var eyeBX = ((kps[2] + cellX) * stride - padX) / scale;
        var eyeBY = ((kps[3] + cellY) * stride - padY) / scale;
        var noseX = ((kps[4] + cellX) * stride - padX) / scale;
        var noseY = ((kps[5] + cellY) * stride - padY) / scale;

        // Линия глаз слева направо по кадру: наклон головы (roll) и единичный вектор вдоль неё.
        if (eyeBX < eyeAX)
        {
            (eyeAX, eyeAY, eyeBX, eyeBY) = (eyeBX, eyeBY, eyeAX, eyeAY);
        }

        var eyeDx = eyeBX - eyeAX;
        var eyeDy = eyeBY - eyeAY;
        var eyeDistance = MathF.Max(1f, MathF.Sqrt(eyeDx * eyeDx + eyeDy * eyeDy));
        var roll = MathF.Atan2(eyeDy, eyeDx);

        // Поворот головы влево-вправо (yaw): нос смещается от середины глаз вдоль их линии.
        var yaw = ((noseX - (eyeAX + eyeBX) / 2f) * eyeDx + (noseY - (eyeAY + eyeBY) / 2f) * eyeDy) / (eyeDistance * eyeDistance);

        // 2. Возраст: квадрат CropScale × большей стороны рамки вокруг её центра, 96×96, билинейно,
        //    повёрнутый на roll — глаза в кропе горизонтальны. Вторая половина батча — зеркальная копия.
        var angle = this.AlignByEyes ? roll : 0f;
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);
        var step = MathF.Max(boxWidth, boxHeight) * this.CropScale / AgeSize;
        const int AgePlane = AgeSize * AgeSize;
        const int Mirror = 3 * AgePlane;
        var lumaSum = 0f;

        for (var b = 0; b < AgeSize; b++)
        {
            var oy = (b + 0.5f - AgeSize / 2f) * step;
            for (var a = 0; a < AgeSize; a++)
            {
                var ox = (a + 0.5f - AgeSize / 2f) * step;
                var u = boxCenterX + ox * cos - oy * sin - 0.5f;
                var v = boxCenterY + ox * sin + oy * cos - 0.5f;
                float r = 0, g = 0, bl = 0;

                // Четыре соседа билинейной выборки; за краем кадра — чёрный, как borderValue=0 у warpAffine.
                var u0 = (int)MathF.Floor(u);
                var v0 = (int)MathF.Floor(v);
                var fu = u - u0;
                var fv = v - v0;
                for (var k = 0; k < 4; k++)
                {
                    var cu = u0 + (k & 1);
                    var cv = v0 + (k >> 1);
                    if (cu < 0 || cv < 0 || cu >= frameWidth || cv >= frameHeight)
                    {
                        continue;
                    }

                    var weight = ((k & 1) == 0 ? 1 - fu : fu) * ((k >> 1) == 0 ? 1 - fv : fv);
                    var p = (nu * cu + nv * cv + n0) * rowStride + (mu * cu + mv * cv + m0) * 4;
                    r += rgba[p] * weight;
                    g += rgba[p + 1] * weight;
                    bl += rgba[p + 2] * weight;
                }

                // genderage сам вычитает среднее и масштабирует — вход 0…255, RGB.
                var i = b * AgeSize + a;
                var mirrored = b * AgeSize + (AgeSize - 1 - a);
                this.ageInput[i] = r;
                this.ageInput[AgePlane + i] = g;
                this.ageInput[2 * AgePlane + i] = bl;
                this.ageInput[Mirror + mirrored] = r;
                this.ageInput[Mirror + AgePlane + mirrored] = g;
                this.ageInput[Mirror + 2 * AgePlane + mirrored] = bl;

                var y = 0.299f * r + 0.587f * g + 0.114f * bl;
                this.luma[i] = y;
                lumaSum += y;
            }
        }

        // Резкость: дисперсия лапласиана яркости по внутренней области кропа.
        var lapSum = 0f;
        var lapSquares = 0f;
        for (var b = 1; b < AgeSize - 1; b++)
        {
            for (var a = 1; a < AgeSize - 1; a++)
            {
                var i = b * AgeSize + a;
                var lap = this.luma[i - 1] + this.luma[i + 1] + this.luma[i - AgeSize] + this.luma[i + AgeSize] - 4 * this.luma[i];
                lapSum += lap;
                lapSquares += lap * lap;
            }
        }

        const int Inner = (AgeSize - 2) * (AgeSize - 2);
        var sharpness = lapSquares / Inner - (lapSum / Inner) * (lapSum / Inner);
        var brightness = lumaSum / AgePlane;

        using var aged = this.ager.Run(this.runOptions, this.ageInputNames, this.ageInputs, this.ageOutputNames);

        // fc1 = [женщина, мужчина, возраст / 100] на каждый элемент батча.
        var output = aged[0].GetTensorDataAsSpan<float>();
        var age = (this.UseFlip ? (output[2] + output[5]) / 2f : output[2]) * 100f;
        if (this.Calibrate)
        {
            var knots = Calibration;
            var k = 1;
            while (k < knots.Length - 1 && age > knots[k].Raw)
            {
                k++;
            }

            age = age < knots[0].Raw ? age + knots[0].Age - knots[0].Raw
                : age > knots[^1].Raw ? age + knots[^1].Age - knots[^1].Raw
                : knots[k - 1].Age + (age - knots[k - 1].Raw) * (knots[k].Age - knots[k - 1].Age) / (knots[k].Raw - knots[k - 1].Raw);
        }

        var quality = MathF.Max(boxWidth, boxHeight) < this.MinFaceSize ? FaceQuality.TooSmall
            : MathF.Abs(yaw) > this.MaxYaw ? FaceQuality.Turned
            : sharpness < this.MinSharpness ? FaceQuality.Blurry
            : brightness < this.MinBrightness ? FaceQuality.TooDark
            : brightness > this.MaxBrightness ? FaceQuality.TooBright
            : FaceQuality.Good;

        return new AgeReading(
            quality, age, bestScore, face, frame, roll * 180f / MathF.PI, yaw, sharpness, brightness,
            System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    public void Dispose()
    {
        this.detectorValue.Dispose();
        this.ageValue.Dispose();
        this.runOptions.Dispose();
        this.detector.Dispose();
        this.ager.Dispose();
    }
}
