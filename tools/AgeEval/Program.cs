// Замер точности AgeEstimator на фото с известным возрастом.
// Использование: dotnet run --project tools/AgeEval -- <папки через ;> [наклон в градусах] [raw.csv]
// Файлы — вида <номер>_<возраст>.jpg. С третьим аргументом замеряется только настройка по умолчанию
// без поправки возраста, и сырые оценки пишутся в CSV для подбора поправки (fit_calibration.py).
// Режим выгрузки: AgeEval export <папки через ;> <папка экспорта> [лимит] [холст] — кропы для обучения (FaceExport.Core).
// Холст по умолчанию 400 (серое поле, как в замере); 0 — фото подаётся как есть. Модель возраста — FACEAGE_AGE_MODEL.
// Каждое фото кладётся на серое поле 400×400 (в UTKFace лицо занимает весь кадр — детектору нужен контекст)
// и, если задан наклон, поворачивается на случайный угол ±наклон: так проверяется выравнивание по глазам.
using FaceAge.Export;
using FaceAge.Vision;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

var models = Path.Combine(AppContext.BaseDirectory, "Models");
var detector = File.ReadAllBytes(Path.Combine(models, "yunet_320.onnx"));
var ager = File.ReadAllBytes(Environment.GetEnvironmentVariable("FACEAGE_AGE_MODEL") ?? Path.Combine(models, "genderage.onnx"));

if (args[0] == "export")
{
    var exportShot = new DateOnly(2020, 7, 1);
    var exportLimit = args.Length > 3 ? int.Parse(args[3]) : int.MaxValue;
    var exportCanvas = args.Length > 4 ? int.Parse(args[4]) : 400;
    var exportFiles = args[1].Split(';')
        .SelectMany(folder => Directory.GetFiles(folder, "*.jpg"))
        .Select(file => (File: file, Age: int.Parse(Path.GetFileNameWithoutExtension(file).Split('_')[1])))
        .OrderBy(x => x.Age).ThenBy(x => x.File, StringComparer.Ordinal)
        .Take(exportLimit)
        .ToArray();

    using var cropper = new FaceCropper(detector, ager);
    using var exportFolder = new ExportFolder(args[2], "yunet_320.onnx");
    var exportSaved = 0;
    var exportSkipped = 0;
    var exportClock = System.Diagnostics.Stopwatch.StartNew();
    var exportProcessed = 0;
    var jpegEncoder = new JpegEncoder { Quality = 95 };

    foreach (var group in exportFiles.GroupBy(x => x.Age))
    {
        if (exportFolder.Completed.Contains((group.Key, "U")))
        {
            continue;
        }

        var groupSaved = 0;
        var groupSkipped = 0;
        foreach (var (file, age) in group)
        {
            exportProcessed++;
            using var photo = Image.Load<Rgba32>(file);
            var width = exportCanvas > 0 ? exportCanvas : photo.Width;
            var height = exportCanvas > 0 ? exportCanvas : photo.Height;
            var rgba = new byte[width * height * 4];
            if (exportCanvas > 0)
            {
                using var field = new Image<Rgba32>(width, height, new Rgba32(128, 128, 128));
                field.Mutate(x => x.DrawImage(photo, new SixLabors.ImageSharp.Point((width - photo.Width) / 2, (height - photo.Height) / 2), 1f));
                field.CopyPixelDataTo(rgba);
            }
            else
            {
                photo.CopyPixelDataTo(rgba);
            }

            var crop = cropper.Cut(rgba, width, height, width * 4);
            if (!crop.Found)
            {
                groupSkipped++;
                continue;
            }

            using var cropImage = Image.LoadPixelData<Rgb24>(crop.Rgb, FaceCropper.Size, FaceCropper.Size);
            using var jpeg = new MemoryStream();
            cropImage.SaveAsJpeg(jpeg, jpegEncoder);
            exportFolder.Add(jpeg.ToArray(), exportShot.AddYears(-age).AddDays(-182), exportShot, "U", crop.Pred);
            groupSaved++;
        }

        exportFolder.CompletePair(group.Key, "U", group.Count(), groupSaved, groupSkipped);
        exportSaved += groupSaved;
        exportSkipped += groupSkipped;
    }

    Console.WriteLine($"Сохранено: {exportSaved}, пропущено: {exportSkipped}, откачено при открытии (Dropped): {exportFolder.Dropped}");
    Console.WriteLine($"Обработано фото: {exportProcessed}, время на фото: {(exportProcessed == 0 ? 0 : exportClock.Elapsed.TotalMilliseconds / exportProcessed):0.0} мс");
    return;
}

var maxRoll = args.Length > 1 ? float.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 0f;
var csvPath = args.Length > 2 ? args[2] : null;

var configs = csvPath is not null
    ? [("по умолчанию, без поправки", new AgeEstimator(detector, ager) { Calibrate = false })]
    : new (string Name, AgeEstimator Estimator)[]
    {
        ("кроп 1.5, без выравнивания, без зеркала", new AgeEstimator(detector, ager) { AlignByEyes = false, UseFlip = false, Calibrate = false }),
        ("+ зеркало", new AgeEstimator(detector, ager) { AlignByEyes = false, Calibrate = false }),
        ("+ выравнивание по глазам", new AgeEstimator(detector, ager) { UseFlip = false, Calibrate = false }),
        ("+ зеркало + выравнивание", new AgeEstimator(detector, ager) { Calibrate = false }),
        ("+ поправка возраста (по умолчанию)", new AgeEstimator(detector, ager)),
    };
var Default = configs.Length - 1;
var csv = csvPath is null ? null : new StreamWriter(csvPath);
csv?.WriteLine("file,age,raw,quality");

var errors = configs.Select(_ => new List<(int Age, float Error)>()).ToArray();
var defaultReadings = new List<(int Age, AgeReading Reading)>();
var random = new Random(42);
const int Canvas = 400;
var pixels = new byte[Canvas * Canvas * 4];
var files = args[0].Split(';').SelectMany(folder => Directory.GetFiles(folder, "*.jpg")).ToArray();

foreach (var file in files)
{
    var age = int.Parse(Path.GetFileNameWithoutExtension(file).Split('_')[1]);
    using var face = Image.Load<Rgba32>(file);
    var roll = maxRoll > 0 ? (float)(random.NextDouble() * 2 - 1) * maxRoll : 0f;
    if (roll != 0)
    {
        face.Mutate(x => x.Rotate(roll));
    }

    using var canvas = new Image<Rgba32>(Canvas, Canvas, new Rgba32(128, 128, 128));
    canvas.Mutate(x => x.DrawImage(face, new SixLabors.ImageSharp.Point((Canvas - face.Width) / 2, (Canvas - face.Height) / 2), 1f));
    canvas.CopyPixelDataTo(pixels);

    for (var c = 0; c < configs.Length; c++)
    {
        var reading = configs[c].Estimator.Analyze(pixels, Canvas, Canvas, Canvas * 4, 0);
        if (reading.HasFace)
        {
            errors[c].Add((age, reading.Age - age));
        }

        if (c == Default)
        {
            defaultReadings.Add((age, reading));
            if (reading.HasFace)
            {
                csv?.WriteLine(FormattableString.Invariant($"{Path.GetFileName(file)},{age},{reading.Age:0.000},{reading.Quality}"));
            }
        }
    }
}

csv?.Dispose();

Console.WriteLine($"Фото: {files.Length}, наклон ±{maxRoll}°");
Console.WriteLine($"{"Настройка",-42} {"найдено",8} {"MAE",6} {"смещ.",6} {"≤3 года",8}");
for (var c = 0; c < configs.Length; c++)
{
    var e = errors[c];
    Console.WriteLine($"{configs[c].Name,-42} {e.Count,8} {e.Average(x => MathF.Abs(x.Error)),6:0.00} {e.Average(x => x.Error),6:+0.00;-0.00} {e.Count(x => MathF.Abs(x.Error) <= 3) * 100.0 / e.Count,7:0}%");
}

// Ошибка по возрастным группам (настройка по умолчанию): где модель систематически ошибается.
Console.WriteLine();
Console.WriteLine("По возрасту (по умолчанию): группа  фото  MAE  смещение");
foreach (var group in errors[Default].GroupBy(x => Math.Min(x.Age / 10, 7)).OrderBy(g => g.Key))
{
    var label = group.Key == 7 ? "70+" : $"{group.Key * 10}–{group.Key * 10 + 9}";
    Console.WriteLine($"  {label,-6} {group.Count(),5} {group.Average(x => MathF.Abs(x.Error)),5:0.00} {group.Average(x => x.Error),+6:+0.00;-0.00}");
}

// Фильтр качества: сколько кадров он отбрасывает и насколько точнее оставшиеся.
Console.WriteLine();
var found = defaultReadings.Where(x => x.Reading.HasFace).ToList();
var good = found.Where(x => x.Reading.Quality == FaceQuality.Good).ToList();
Console.WriteLine($"Фильтр качества: годных {good.Count} из {found.Count}; MAE годных {good.Average(x => MathF.Abs(x.Reading.Age - x.Age)):0.00}, отброшенных {found.Except(good).DefaultIfEmpty().Average(x => x.Reading.HasFace ? MathF.Abs(x.Reading.Age - x.Age) : 0):0.00}");
foreach (var reason in found.GroupBy(x => x.Reading.Quality).OrderByDescending(g => g.Count()))
{
    Console.WriteLine($"  {reason.Key,-10} {reason.Count(),5}  MAE {reason.Average(x => MathF.Abs(x.Reading.Age - x.Age)):0.00}");
}

// Распределения для калибровки порогов.
float Percentile(IEnumerable<float> values, double p)
{
    var sorted = values.OrderBy(x => x).ToArray();
    return sorted[(int)Math.Clamp(p * (sorted.Length - 1), 0, sorted.Length - 1)];
}

Console.WriteLine();
Console.WriteLine($"Резкость  p5 {Percentile(found.Select(x => x.Reading.Sharpness), 0.05):0.0}  p50 {Percentile(found.Select(x => x.Reading.Sharpness), 0.5):0.0}");
Console.WriteLine($"Яркость   p5 {Percentile(found.Select(x => x.Reading.Brightness), 0.05):0}  p95 {Percentile(found.Select(x => x.Reading.Brightness), 0.95):0}");
Console.WriteLine($"|Yaw|     p50 {Percentile(found.Select(x => MathF.Abs(x.Reading.Yaw)), 0.5):0.000}  p95 {Percentile(found.Select(x => MathF.Abs(x.Reading.Yaw)), 0.95):0.000}");
Console.WriteLine($"Время на кадр (по умолчанию): {found.Average(x => x.Reading.Milliseconds):0.0} мс");
