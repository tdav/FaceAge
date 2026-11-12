// Замер точности AgeEstimator на фото с известным возрастом.
// Использование: dotnet run --project tools/AgeEval -- <папки через ;> [наклон в градусах] [raw.csv]
// Файлы — вида <номер>_<возраст>.jpg. С третьим аргументом замеряется только настройка по умолчанию
// без поправки возраста, и сырые оценки пишутся в CSV для подбора поправки (fit_calibration.py).
// Режим выгрузки: AgeEval export <папки через ;> <папка экспорта> [лимит] [холст] — кропы для обучения (FaceExport.Core).
// Режимы с базой SQLite: AgeEval make-db <папки через ;> <файл.db> [лимит]; AgeEval export-db <файл.db> <папка экспорта> <от> <до> <пол через ;>.
// Холст по умолчанию 400 (серое поле, как в замере); 0 — фото подаётся как есть. Модель возраста — FACEAGE_AGE_MODEL.
// Каждое фото кладётся на серое поле 400×400 (в UTKFace лицо занимает весь кадр — детектору нужен контекст)
// и, если задан наклон, поворачивается на случайный угол ±наклон: так проверяется выравнивание по глазам.
using FaceAge.Export;
using FaceAge.Vision;
using Microsoft.Data.Sqlite;
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

if (args[0] == "make-db")
{
    // Тестовая база SQLite: фото UTKFace как есть, пол по чётности номера файла, плюс четыре «плохие» строки пары 26/M.
    var dbShot = new DateOnly(2020, 7, 1);
    var dbLimit = args.Length > 3 ? int.Parse(args[3]) : int.MaxValue;
    var dbFiles = args[1].Split(';')
        .SelectMany(folder => Directory.GetFiles(folder, "*.jpg"))
        .OrderBy(file => file, StringComparer.Ordinal)
        .Take(dbLimit)
        .ToArray();

    File.Delete(args[2]);
    using var dbConnection = new SqliteConnection($"Data Source={args[2]};Pooling=False");
    dbConnection.Open();
    using var dbCreate = dbConnection.CreateCommand();
    dbCreate.CommandText = "CREATE TABLE persons(photo BLOB, birth_date TEXT, shoot_date TEXT, sex TEXT, age INTEGER)";
    dbCreate.ExecuteNonQuery();

    using var dbTransaction = dbConnection.BeginTransaction();
    using var dbInsert = dbConnection.CreateCommand();
    dbInsert.Transaction = dbTransaction;
    dbInsert.CommandText = "INSERT INTO persons(photo, birth_date, shoot_date, sex, age) VALUES ($photo, $birth, $shoot, $sex, $age)";
    var pPhoto = dbInsert.Parameters.Add("$photo", SqliteType.Blob);
    var pBirth = dbInsert.Parameters.Add("$birth", SqliteType.Text);
    var pShoot = dbInsert.Parameters.Add("$shoot", SqliteType.Text);
    var pSex = dbInsert.Parameters.Add("$sex", SqliteType.Text);
    var pAge = dbInsert.Parameters.Add("$age", SqliteType.Integer);

    void Insert(object photo, DateTime birth, DateTime shoot, string sex, int age)
    {
        pPhoto.Value = photo;
        pBirth.Value = birth.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        pShoot.Value = shoot.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        pSex.Value = sex;
        pAge.Value = age;
        dbInsert.ExecuteNonQuery();
    }

    var dbShootDate = dbShot.ToDateTime(TimeOnly.MinValue);
    foreach (var file in dbFiles)
    {
        var parts = Path.GetFileNameWithoutExtension(file).Split('_');
        var age = int.Parse(parts[1]);
        Insert(File.ReadAllBytes(file), dbShootDate.AddYears(-age).AddDays(-182), dbShootDate, int.Parse(parts[0]) % 2 == 0 ? "M" : "F", age);
    }

    var goodBytes = File.ReadAllBytes(dbFiles[0]);
    var goodBirth = dbShootDate.AddYears(-26).AddDays(-182);
    using var grayImage = new Image<Rgb24>(200, 200, new Rgb24(128, 128, 128));
    using var grayStream = new MemoryStream();
    grayImage.SaveAsJpeg(grayStream);

    Insert(DBNull.Value, goodBirth, dbShootDate, "M", 26);
    Insert(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, goodBirth, dbShootDate, "M", 26);
    Insert(goodBytes, dbShootDate, goodBirth, "M", 26);
    Insert(grayStream.ToArray(), goodBirth, dbShootDate, "M", 26);
    dbTransaction.Commit();
    Console.WriteLine($"База {args[2]}: {dbFiles.Length} фото и 4 плохие строки");
    return;
}

if (args[0] == "export-db")
{
    // AgeEval export-db <файл.db> <папка экспорта> <от> <до> <значения пола через ;>
    using var dbConnection = new SqliteConnection($"Data Source={args[1]};Mode=ReadOnly;Pooling=False");
    dbConnection.Open();
    using var cancel = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cancel.Cancel();
    };

    var dbEncoder = new JpegEncoder { Quality = 95 };
    PhotoDecoder decodePhoto = (byte[] photo, out byte[] rgba, out int width, out int height) =>
    {
        // Как в режиме export: фото на сером поле 400×400, иначе детектору не хватает контекста.
        try
        {
            using var image = Image.Load<Rgba32>(photo);
            using var field = new Image<Rgba32>(400, 400, new Rgba32(128, 128, 128));
            field.Mutate(x => x.DrawImage(image, new SixLabors.ImageSharp.Point((400 - image.Width) / 2, (400 - image.Height) / 2), 1f));
            rgba = new byte[400 * 400 * 4];
            field.CopyPixelDataTo(rgba);
            width = 400;
            height = 400;
            return true;
        }
        catch (Exception e) when (e is UnknownImageFormatException or InvalidImageContentException)
        {
            rgba = [];
            width = 0;
            height = 0;
            return false;
        }
    };

    using var cropper = new FaceCropper(detector, ager);
    using var folder = new ExportFolder(args[2], "yunet_320.onnx");
    var run = new ExportRun(cropper, folder, decodePhoto, rgb =>
    {
        using var image = Image.LoadPixelData<Rgb24>(rgb, FaceCropper.Size, FaceCropper.Size);
        using var stream = new MemoryStream();
        image.SaveAsJpeg(stream, dbEncoder);
        return stream.ToArray();
    });

    try
    {
        run.Run(
            dbConnection,
            "SELECT photo, birth_date, shoot_date FROM persons WHERE sex = :sex AND age = :age",
            int.Parse(args[3]), int.Parse(args[4]), args[5].Split(';'),
            null,
            new SyncProgress<ExportProgress>(p =>
            {
                if (p.Line is not null)
                {
                    Console.WriteLine(p.Line);
                }
            }),
            cancel.Token);
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("Остановлено");
        Environment.ExitCode = 130;
    }
    catch (InvalidOperationException e)
    {
        Console.WriteLine(e.Message);
        Environment.ExitCode = 1;
    }

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

// Progress<T> шлёт отчёты через пул потоков и теряет их при выходе; здесь они выводятся сразу.
sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
