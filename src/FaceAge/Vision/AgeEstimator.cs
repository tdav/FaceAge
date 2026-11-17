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

/// <summary>Модель возраста: от неё зависят размер входа, имя и смысл выхода, нужна ли поправка.</summary>
public enum AgeModel
{
    /// <summary>InsightFace genderage.onnx: вход 96, выход fc1 = [ж, м, возраст/100], с поправкой <see cref="AgeEstimator.Calibration"/>.</summary>
    GenderAge,

    /// <summary>genderage, дообученная на своих фото (tools/train --model small): тот же вход и выход, с поправкой <see cref="AgeEstimator.SmallCalibration"/>.</summary>
    Small,

    /// <summary>MobileNetV3 (tools/train --model large): вход 224, выход age — возраст в годах, с поправкой <see cref="AgeEstimator.LargeCalibration"/>.</summary>
    Large,

    /// <summary>Ансамбль обученных моделей Large (224) + Small (96) с калибровкой и взвешенным усреднением (0,82 / 0,18).</summary>
    Ensemble,
}

/// <summary>
/// Два (в режиме ансамбля — три) ONNX-прохода на кадр: YuNet находит лицо и пять точек (глаза, нос, углы рта),
/// модель возраста оценивает возраст по выровненному по глазам кропу и его зеркальной копии.
/// Буферы выделены один раз. Не потокобезопасен: вызывается только с потока анализатора камеры.
/// </summary>
public sealed class AgeEstimator : IDisposable
{
    private const int DetectorSize = 320;
    private const int SmallSize = 96;
    private const int LargeSize = 224;
    // Резкость и яркость считаются на сетке 96×96 при любом входе модели: на ней подобраны пороги качества.
    private const int LumaSize = 96;
    private const float FaceThreshold = 0.7f;
    private const float EnsembleLargeWeight = 0.82f;
    private const float EnsembleSmallWeight = 0.18f;

    private static readonly int[] Strides = [8, 16, 32];

    private readonly InferenceSession detector;
    private readonly InferenceSession ager;
    private readonly InferenceSession? secondaryAger;
    private readonly float[] detectorInput = new float[3 * DetectorSize * DetectorSize];
    private readonly AgeModel model;
    private readonly int ageSize;
    private readonly float[] ageInput;
    private readonly float[]? secondaryAgeInput;
    private readonly float[] luma = new float[LumaSize * LumaSize];
    private readonly OrtValue detectorValue;
    private readonly OrtValue ageValue;
    private readonly OrtValue? secondaryAgeValue;
    private readonly RunOptions runOptions = new();
    private readonly string[] detectorInputNames = ["input"];
    private readonly string[] detectorOutputNames =
        ["cls_8", "cls_16", "cls_32", "obj_8", "obj_16", "obj_32", "bbox_8", "bbox_16", "bbox_32", "kps_8", "kps_16", "kps_32"];
    private readonly string[] ageInputNames = ["data"];
    private readonly string[] ageOutputNames;
    private readonly string[] secondaryAgeOutputNames = ["fc1"];
    private readonly OrtValue[] detectorInputs;
    private readonly OrtValue[] ageInputs;
    private readonly OrtValue[]? secondaryAgeInputs;

    public AgeEstimator(byte[] detectorModel, byte[] ageModel, AgeModel model = AgeModel.GenderAge, byte[]? secondaryAgeModel = null)
    {
        this.model = model;
        var usesLarge = model is AgeModel.Large or AgeModel.Ensemble;
        this.ageSize = usesLarge ? LargeSize : SmallSize;
        this.ageInput = new float[2 * 3 * this.ageSize * this.ageSize];
        this.ageOutputNames = [usesLarge ? "age" : "fc1"];

        // Поправка устраняет систематическое смещение для каждой модели (исходная Calibration, либо Small/Large по отчётам phone).
        this.Calibrate = true;

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
        this.ageValue = OrtValue.CreateTensorValueFromMemory(this.ageInput, [2, 3, this.ageSize, this.ageSize]);
        this.detectorInputs = [this.detectorValue];
        this.ageInputs = [this.ageValue];

        if (model == AgeModel.Ensemble)
        {
            if (secondaryAgeModel is null)
            {
                throw new ArgumentNullException(nameof(secondaryAgeModel), "Для режима ансамбля требуется вторая модель (Small).");
            }

            this.secondaryAger = new InferenceSession(secondaryAgeModel, options);
            this.secondaryAgeInput = new float[2 * 3 * SmallSize * SmallSize];
            this.secondaryAgeValue = OrtValue.CreateTensorValueFromMemory(this.secondaryAgeInput, [2, 3, SmallSize, SmallSize]);
            this.secondaryAgeInputs = [this.secondaryAgeValue];
        }
    }

    // Настройки подобраны замером на UTKFace (tools/AgeEval); меняются только для замера.

    /// <summary>Сторона кропа возраста в долях большей стороны рамки YuNet.</summary>
    public float CropScale { get; init; } = 1.5f;

    /// <summary>Поворачивать кроп так, чтобы глаза стояли на одной горизонтали.</summary>
    public bool AlignByEyes { get; init; } = true;

    /// <summary>Усреднять возраст с зеркальной копией кропа.</summary>
    public bool UseFlip { get; init; } = true;

    /// <summary>Применять поправку систематического смещения к сырой оценке модели.</summary>
    public bool Calibrate { get; init; }

    /// <summary>Пропускать запуск нейросети возраста, если кадр уже отбракован по качеству.</summary>
    public bool SkipAgeOnBadQuality { get; init; }

    /// <summary>
    /// Поправка систематической ошибки исходной модели genderage: узлы «сырая оценка → медиана настоящего возраста»,
    /// между узлами — линейно, за краями — сдвиг крайнего узла. Подобрана tools/AgeEval/fit_calibration.py
    /// на 22 642 годных фото UTKFace (1–99 лет). На отложенной половине: 13–40 лет MAE 8,5 → 5,7, все возрасты 10,2 → 8,5.
    /// </summary>
    public static readonly (float Raw, float Age)[] Calibration =
    [
        (8.1f, 2.0f), (24.0f, 22.0f), (26.7f, 25.0f), (29.0f, 26.0f), (31.3f, 26.0f), (34.0f, 28.0f),
        (37.3f, 30.0f), (41.3f, 34.0f), (47.2f, 39.0f), (55.3f, 47.0f), (65.3f, 55.0f), (75.4f, 66.0f),
    ];

    /// <summary>Поправка остаточного смещения модели Large по отчёту обучения (режим phone).</summary>
    public static readonly (float Raw, float Age)[] LargeCalibration =
    [
        (14.72f, 14.50f), (17.35f, 17.00f), (20.25f, 19.50f), (23.02f, 23.00f), (27.97f, 27.50f),
        (32.80f, 32.50f), (36.35f, 36.50f), (40.80f, 40.50f), (44.92f, 45.00f), (48.94f, 49.00f),
    ];

    /// <summary>Поправка остаточного смещения модели Small по отчёту обучения (режим phone).</summary>
    public static readonly (float Raw, float Age)[] SmallCalibration =
    [
        (15.74f, 14.50f), (18.49f, 17.00f), (21.17f, 19.50f), (23.65f, 23.00f), (28.53f, 27.50f),
        (33.37f, 32.50f), (36.54f, 36.50f), (40.52f, 40.50f), (44.14f, 45.00f), (47.64f, 49.00f),
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

        Span<int> uCoords = stackalloc int[DetectorSize];
        for (var dx = 0; dx < DetectorSize; dx++)
        {
            uCoords[dx] = (int)MathF.Floor((dx + 0.5f - padX) / scale);
        }

        var uStep = nu * rowStride + mu * 4;
        for (var dy = 0; dy < DetectorSize; dy++)
        {
            var v = (int)MathF.Floor((dy + 0.5f - padY) / scale);
            var rowOffset = dy * DetectorSize;
            if (v < 0 || v >= frameHeight)
            {
                this.detectorInput.AsSpan(rowOffset, DetectorSize).Clear();
                this.detectorInput.AsSpan(Plane + rowOffset, DetectorSize).Clear();
                this.detectorInput.AsSpan(2 * Plane + rowOffset, DetectorSize).Clear();
                continue;
            }

            var nvBase = (nv * v + n0) * rowStride + (mv * v + m0) * 4;
            for (var dx = 0; dx < DetectorSize; dx++)
            {
                var u = uCoords[dx];
                var i = rowOffset + dx;
                if (u < 0 || u >= frameWidth)
                {
                    this.detectorInput[i] = 0f;
                    this.detectorInput[Plane + i] = 0f;
                    this.detectorInput[2 * Plane + i] = 0f;
                    continue;
                }

                var p = nvBase + u * uStep;
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

        // 2. Возраст: квадрат CropScale × большей стороны рамки вокруг её центра, S×S, билинейно,
        //    повёрнутый на roll — глаза в кропе горизонтальны. Вторая половина батча — зеркальная копия.
        //    Для обученных моделей (не GenderAge) за краем кадра продолжаются крайние пиксели (как в обучении)
        //    и при сжатии (step > 1.35) применяется суперсэмплирование 2×2 против ступенек.
        var angle = this.AlignByEyes ? roll : 0f;
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);
        var size = this.ageSize;
        var step = MathF.Max(boxWidth, boxHeight) * this.CropScale / size;
        var agePlane = size * size;
        var mirror = 3 * agePlane;
        var edgeClamp = this.model != AgeModel.GenderAge;
        var supersample = edgeClamp && step > 1.35f;

        for (var b = 0; b < size; b++)
        {
            var oy = (b + 0.5f - size / 2f) * step;
            for (var a = 0; a < size; a++)
            {
                var ox = (a + 0.5f - size / 2f) * step;
                float r = 0, g = 0, bl = 0;

                if (supersample)
                {
                    var quarterStep = 0.25f * step;
                    for (var sy = -1; sy <= 1; sy += 2)
                    {
                        var soy = oy + sy * quarterStep;
                        for (var sx = -1; sx <= 1; sx += 2)
                        {
                            var sox = ox + sx * quarterStep;
                            var u = boxCenterX + sox * cos - soy * sin - 0.5f;
                            var v = boxCenterY + sox * sin + soy * cos - 0.5f;
                            AccumulateBilinear(rgba, frameWidth, frameHeight, rowStride, mu, mv, m0, nu, nv, n0, u, v, true, ref r, ref g, ref bl);
                        }
                    }

                    r *= 0.25f;
                    g *= 0.25f;
                    bl *= 0.25f;
                }
                else
                {
                    var u = boxCenterX + ox * cos - oy * sin - 0.5f;
                    var v = boxCenterY + ox * sin + oy * cos - 0.5f;
                    AccumulateBilinear(rgba, frameWidth, frameHeight, rowStride, mu, mv, m0, nu, nv, n0, u, v, edgeClamp, ref r, ref g, ref bl);
                }

                // Все модели сами вычитают среднее и масштабируют — вход 0…255, RGB.
                var i = b * size + a;
                var mirrored = b * size + (size - 1 - a);
                this.ageInput[i] = r;
                this.ageInput[agePlane + i] = g;
                this.ageInput[2 * agePlane + i] = bl;
                this.ageInput[mirror + mirrored] = r;
                this.ageInput[mirror + agePlane + mirrored] = g;
                this.ageInput[mirror + 2 * agePlane + mirrored] = bl;
            }
        }

        // Яркость на сетке 96×96: при входе 96 — те же пиксели, при 224 — ближайшие, чтобы пороги резкости не съехали.
        // В режиме ансамбля одновременно заполняем буфер 96×96 для малой модели усреднением 2×2 из буфера 224×224.
        var lumaSum = 0f;
        var fillSecondary = this.secondaryAgeInput is not null;
        const int SmallPlane = SmallSize * SmallSize;
        const int SmallMirror = 3 * SmallPlane;

        for (var b = 0; b < LumaSize; b++)
        {
            var row = (2 * b + 1) * size / (2 * LumaSize) * size;
            var secRow0 = fillSecondary ? Math.Clamp(((2 * b + 1) * size - LumaSize) / (2 * LumaSize), 0, size - 2) * size : 0;
            var secRow1 = secRow0 + size;
            for (var a = 0; a < LumaSize; a++)
            {
                var i = row + (2 * a + 1) * size / (2 * LumaSize);
                var y = 0.299f * this.ageInput[i] + 0.587f * this.ageInput[agePlane + i] + 0.114f * this.ageInput[2 * agePlane + i];
                this.luma[b * LumaSize + a] = y;
                lumaSum += y;

                if (fillSecondary)
                {
                    var secCol0 = Math.Clamp(((2 * a + 1) * size - LumaSize) / (2 * LumaSize), 0, size - 2);
                    var p00 = secRow0 + secCol0;
                    var p01 = p00 + 1;
                    var p10 = secRow1 + secCol0;
                    var p11 = p10 + 1;

                    var rAvg = 0.25f * (this.ageInput[p00] + this.ageInput[p01] + this.ageInput[p10] + this.ageInput[p11]);
                    var gAvg = 0.25f * (this.ageInput[agePlane + p00] + this.ageInput[agePlane + p01] + this.ageInput[agePlane + p10] + this.ageInput[agePlane + p11]);
                    var bAvg = 0.25f * (this.ageInput[2 * agePlane + p00] + this.ageInput[2 * agePlane + p01] + this.ageInput[2 * agePlane + p10] + this.ageInput[2 * agePlane + p11]);

                    var si = b * SmallSize + a;
                    var smirrored = b * SmallSize + (SmallSize - 1 - a);
                    this.secondaryAgeInput![si] = rAvg;
                    this.secondaryAgeInput[SmallPlane + si] = gAvg;
                    this.secondaryAgeInput[2 * SmallPlane + si] = bAvg;
                    this.secondaryAgeInput[SmallMirror + smirrored] = rAvg;
                    this.secondaryAgeInput[SmallMirror + SmallPlane + smirrored] = gAvg;
                    this.secondaryAgeInput[SmallMirror + 2 * SmallPlane + smirrored] = bAvg;
                }
            }
        }

        // Резкость: дисперсия лапласиана яркости по внутренней области кропа.
        var lapSum = 0f;
        var lapSquares = 0f;
        for (var b = 1; b < LumaSize - 1; b++)
        {
            for (var a = 1; a < LumaSize - 1; a++)
            {
                var i = b * LumaSize + a;
                var lap = this.luma[i - 1] + this.luma[i + 1] + this.luma[i - LumaSize] + this.luma[i + LumaSize] - 4 * this.luma[i];
                lapSum += lap;
                lapSquares += lap * lap;
            }
        }

        const int Inner = (LumaSize - 2) * (LumaSize - 2);
        var sharpness = lapSquares / Inner - (lapSum / Inner) * (lapSum / Inner);
        var brightness = lumaSum / (LumaSize * LumaSize);

        var quality = MathF.Max(boxWidth, boxHeight) < this.MinFaceSize ? FaceQuality.TooSmall
            : MathF.Abs(yaw) > this.MaxYaw ? FaceQuality.Turned
            : sharpness < this.MinSharpness ? FaceQuality.Blurry
            : brightness < this.MinBrightness ? FaceQuality.TooDark
            : brightness > this.MaxBrightness ? FaceQuality.TooBright
            : FaceQuality.Good;

        if (this.SkipAgeOnBadQuality && quality != FaceQuality.Good)
        {
            return new AgeReading(
                quality, 0f, bestScore, face, frame, roll * 180f / MathF.PI, yaw, sharpness, brightness,
                System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        using var aged = this.ager.Run(this.runOptions, this.ageInputNames, this.ageInputs, this.ageOutputNames);

        // GenderAge и Small: fc1 = [женщина, мужчина, возраст / 100] на каждый элемент батча; Large/Ensemble: age — годы.
        var output = aged[0].GetTensorDataAsSpan<float>();
        float age;
        if (this.model == AgeModel.Ensemble)
        {
            var largeRaw = this.UseFlip ? (output[0] + output[1]) / 2f : output[0];
            var largeAge = this.Calibrate ? ApplyCalibration(largeRaw, LargeCalibration) : largeRaw;

            using var secAged = this.secondaryAger!.Run(this.runOptions, this.ageInputNames, this.secondaryAgeInputs!, this.secondaryAgeOutputNames);
            var secOutput = secAged[0].GetTensorDataAsSpan<float>();
            var smallRaw = (this.UseFlip ? (secOutput[2] + secOutput[5]) / 2f : secOutput[2]) * 100f;
            var smallAge = this.Calibrate ? ApplyCalibration(smallRaw, SmallCalibration) : smallRaw;

            age = EnsembleLargeWeight * largeAge + EnsembleSmallWeight * smallAge;
        }
        else
        {
            var rawAge = this.model == AgeModel.Large
                ? (this.UseFlip ? (output[0] + output[1]) / 2f : output[0])
                : (this.UseFlip ? (output[2] + output[5]) / 2f : output[2]) * 100f;
            if (this.Calibrate)
            {
                var knots = this.model switch
                {
                    AgeModel.Large => LargeCalibration,
                    AgeModel.Small => SmallCalibration,
                    _ => Calibration,
                };
                age = ApplyCalibration(rawAge, knots);
            }
            else
            {
                age = rawAge;
            }
        }

        return new AgeReading(
            quality, age, bestScore, face, frame, roll * 180f / MathF.PI, yaw, sharpness, brightness,
            System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static void AccumulateBilinear(
        ReadOnlySpan<byte> rgba,
        int frameWidth,
        int frameHeight,
        int rowStride,
        int mu,
        int mv,
        int m0,
        int nu,
        int nv,
        int n0,
        float u,
        float v,
        bool edgeClamp,
        ref float r,
        ref float g,
        ref float bl)
    {
        var u0 = (int)MathF.Floor(u);
        var v0 = (int)MathF.Floor(v);
        var fu = u - u0;
        var fv = v - v0;
        for (var k = 0; k < 4; k++)
        {
            var cu = u0 + (k & 1);
            var cv = v0 + (k >> 1);
            if (edgeClamp)
            {
                cu = Math.Clamp(cu, 0, frameWidth - 1);
                cv = Math.Clamp(cv, 0, frameHeight - 1);
            }
            else if (cu < 0 || cv < 0 || cu >= frameWidth || cv >= frameHeight)
            {
                continue;
            }

            var weight = ((k & 1) == 0 ? 1 - fu : fu) * ((k >> 1) == 0 ? 1 - fv : fv);
            var p = (nu * cu + nv * cv + n0) * rowStride + (mu * cu + mv * cv + m0) * 4;
            r += rgba[p] * weight;
            g += rgba[p + 1] * weight;
            bl += rgba[p + 2] * weight;
        }
    }

    private static float ApplyCalibration(float age, (float Raw, float Age)[] knots)
    {
        var k = 1;
        while (k < knots.Length - 1 && age > knots[k].Raw)
        {
            k++;
        }

        return age < knots[0].Raw ? age + knots[0].Age - knots[0].Raw
            : age > knots[^1].Raw ? age + knots[^1].Age - knots[^1].Raw
            : knots[k - 1].Age + (age - knots[k - 1].Raw) * (knots[k].Age - knots[k - 1].Age) / (knots[k].Raw - knots[k - 1].Raw);
    }

    public void Dispose()
    {
        this.secondaryAgeValue?.Dispose();
        this.secondaryAger?.Dispose();
        this.detectorValue.Dispose();
        this.ageValue.Dispose();
        this.runOptions.Dispose();
        this.detector.Dispose();
        this.ager.Dispose();
    }
}
