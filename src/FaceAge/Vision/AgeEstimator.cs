using Microsoft.ML.OnnxRuntime;

namespace FaceAge.Vision;

/// <summary>Результат анализа кадра: лицо в координатах повёрнутого («прямого») кадра и оценка возраста.</summary>
public readonly record struct AgeReading(bool HasFace, float Age, float FaceScore, RectF Face, SizeF Frame, double Milliseconds);

/// <summary>
/// Два ONNX-прохода на кадр: UltraFace RFB-320 находит лицо, InsightFace genderage оценивает возраст.
/// Буферы входов выделены один раз — на кадр не создаётся ни одного массива.
/// Не потокобезопасен: вызывается только с потока анализатора камеры.
/// </summary>
public sealed class AgeEstimator : IDisposable
{
    private const int DetectorWidth = 320;
    private const int DetectorHeight = 240;
    private const int AgeSize = 96;

    // Порог уверенности детектора (как в демо UltraFace).
    private const float FaceThreshold = 0.7f;

    private readonly InferenceSession detector;
    private readonly InferenceSession ager;
    private readonly float[] detectorInput = new float[3 * DetectorHeight * DetectorWidth];
    private readonly float[] ageInput = new float[3 * AgeSize * AgeSize];
    private readonly OrtValue detectorValue;
    private readonly OrtValue ageValue;
    private readonly RunOptions runOptions = new();
    private readonly string[] detectorInputNames = ["input"];
    private readonly string[] detectorOutputNames = ["scores", "boxes"];
    private readonly string[] ageInputNames = ["data"];
    private readonly string[] ageOutputNames = ["fc1"];
    private readonly OrtValue[] detectorInputs;
    private readonly OrtValue[] ageInputs;

    public AgeEstimator(byte[] detectorModel, byte[] ageModel)
    {
        // Модели крошечные (по ~1,3 МБ): два потока на сессию быстрее, чем все ядра, — меньше синхронизации
        // и телефон не греется; большие ядра остаются превью и UI.
        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = 2,
            InterOpNumThreads = 1,
        };

        this.detector = new InferenceSession(detectorModel, options);
        this.ager = new InferenceSession(ageModel, options);
        this.detectorValue = OrtValue.CreateTensorValueFromMemory(this.detectorInput, [1, 3, DetectorHeight, DetectorWidth]);
        this.ageValue = OrtValue.CreateTensorValueFromMemory(this.ageInput, [1, 3, AgeSize, AgeSize]);
        this.detectorInputs = [this.detectorValue];
        this.ageInputs = [this.ageValue];
    }

    /// <summary>
    /// Анализирует кадр RGBA_8888 от CameraX.
    /// </summary>
    /// <param name="rgba">Пиксели кадра, строка за строкой с шагом <paramref name="rowStride"/>.</param>
    /// <param name="rotation">Поворот по часовой стрелке, который делает кадр прямым (ImageInfo.RotationDegrees).</param>
    public AgeReading Analyze(ReadOnlySpan<byte> rgba, int width, int height, int rowStride, int rotation)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        // Размер «прямого» кадра: при 90° и 270° ширина и высота меняются местами.
        var swap = rotation is 90 or 270;
        float frameWidth = swap ? height : width;
        float frameHeight = swap ? width : height;
        var frame = new SizeF(frameWidth, frameHeight);

        // 1. Детектор: «прямой» кадр вписывается в 320×240 с полями (letterbox), чтобы лицо не сплющилось:
        //    портретный кадр фронтальной камеры в альбомный вход без полей дал бы сжатие почти вдвое.
        var scale = MathF.Min(DetectorWidth / frameWidth, DetectorHeight / frameHeight);
        var padX = (DetectorWidth - frameWidth * scale) / 2f;
        var padY = (DetectorHeight - frameHeight * scale) / 2f;
        const int Plane = DetectorWidth * DetectorHeight;

        for (var dy = 0; dy < DetectorHeight; dy++)
        {
            var v = (dy + 0.5f - padY) / scale;
            for (var dx = 0; dx < DetectorWidth; dx++)
            {
                var u = (dx + 0.5f - padX) / scale;
                var i = dy * DetectorWidth + dx;
                if (u < 0 || v < 0 || u >= frameWidth || v >= frameHeight)
                {
                    // Поле letterbox — серый 127, после нормализации ровно 0.
                    this.detectorInput[i] = 0f;
                    this.detectorInput[Plane + i] = 0f;
                    this.detectorInput[2 * Plane + i] = 0f;
                    continue;
                }

                // Ближайший пиксель исходного (неповёрнутого) кадра; поворот по часовой стрелке.
                var (sx, sy) = rotation switch
                {
                    90 => ((int)v, height - 1 - (int)u),
                    180 => (width - 1 - (int)u, height - 1 - (int)v),
                    270 => (width - 1 - (int)v, (int)u),
                    _ => ((int)u, (int)v),
                };
                var p = sy * rowStride + sx * 4;

                // Нормализация UltraFace: (x − 127) / 128, порядок каналов RGB, раскладка CHW.
                this.detectorInput[i] = (rgba[p] - 127f) / 128f;
                this.detectorInput[Plane + i] = (rgba[p + 1] - 127f) / 128f;
                this.detectorInput[2 * Plane + i] = (rgba[p + 2] - 127f) / 128f;
            }
        }

        using var detected = this.detector.Run(this.runOptions, this.detectorInputNames, this.detectorInputs, this.detectorOutputNames);
        var scores = detected[0].GetTensorDataAsSpan<float>();
        var boxes = detected[1].GetTensorDataAsSpan<float>();

        // ponytail: одно лицо — бокс с максимальной уверенностью, без NMS. Для нескольких лиц нужен NMS по IoU 0.3.
        var best = -1;
        var bestScore = FaceThreshold;
        for (var a = 0; a < scores.Length / 2; a++)
        {
            if (scores[a * 2 + 1] > bestScore)
            {
                bestScore = scores[a * 2 + 1];
                best = a;
            }
        }

        if (best < 0)
        {
            return new AgeReading(false, 0, 0, default, frame, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        // Бокс нормирован к входу детектора — переводим в координаты «прямого» кадра, снимая поля.
        var x1 = Math.Clamp((boxes[best * 4] * DetectorWidth - padX) / scale, 0, frameWidth);
        var y1 = Math.Clamp((boxes[best * 4 + 1] * DetectorHeight - padY) / scale, 0, frameHeight);
        var x2 = Math.Clamp((boxes[best * 4 + 2] * DetectorWidth - padX) / scale, 0, frameWidth);
        var y2 = Math.Clamp((boxes[best * 4 + 3] * DetectorHeight - padY) / scale, 0, frameHeight);
        var face = new RectF(x1, y1, x2 - x1, y2 - y1);

        // 2. Возраст: квадрат со стороной 1,5 × большей стороны бокса вокруг его центра, 96×96 —
        //    тот же кроп, что у InsightFace (attribute.py), с билинейной выборкой, как cv2.warpAffine.
        var centerX = face.Center.X;
        var centerY = face.Center.Y;
        var step = MathF.Max(face.Width, face.Height) * 1.5f / AgeSize;
        const int AgePlane = AgeSize * AgeSize;

        for (var b = 0; b < AgeSize; b++)
        {
            var v = centerY + (b + 0.5f - AgeSize / 2f) * step - 0.5f;
            for (var a = 0; a < AgeSize; a++)
            {
                var u = centerX + (a + 0.5f - AgeSize / 2f) * step - 0.5f;
                var i = b * AgeSize + a;
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
                    var (sx, sy) = rotation switch
                    {
                        90 => (cv, height - 1 - cu),
                        180 => (width - 1 - cu, height - 1 - cv),
                        270 => (width - 1 - cv, cu),
                        _ => (cu, cv),
                    };
                    var p = sy * rowStride + sx * 4;
                    r += rgba[p] * weight;
                    g += rgba[p + 1] * weight;
                    bl += rgba[p + 2] * weight;
                }

                // genderage сам вычитает среднее и масштабирует (Sub/Mul в начале графа) — вход 0…255, RGB.
                this.ageInput[i] = r;
                this.ageInput[AgePlane + i] = g;
                this.ageInput[2 * AgePlane + i] = bl;
            }
        }

        using var aged = this.ager.Run(this.runOptions, this.ageInputNames, this.ageInputs, this.ageOutputNames);

        // fc1 = [женщина, мужчина, возраст / 100].
        var age = aged[0].GetTensorDataAsSpan<float>()[2] * 100f;

        return new AgeReading(true, age, bestScore, face, frame, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
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
