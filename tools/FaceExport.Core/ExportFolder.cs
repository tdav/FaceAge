using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FaceAge.Export;

/// <summary>
/// Папка экспорта (формат — раздел 4 описания): export.json, metadata.jsonl, progress.jsonl, images\возраст\guid.jpg.
/// Источник истины — metadata.jsonl; при открытии незавершённая пара откатывается.
/// </summary>
public sealed class ExportFolder : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly string path;
    private readonly FileStream metadata;
    private readonly FileStream progress;
    private readonly HashSet<(int Age, string Sex)> completed = [];

    public ExportFolder(string path, string detector)
    {
        this.path = path;
        Directory.CreateDirectory(Path.Combine(path, "images"));

        var settingsPath = Path.Combine(path, "export.json");
        if (!File.Exists(settingsPath))
        {
            File.WriteAllText(
                settingsPath,
                "{\"version\":1,\"size\":256,\"cropScale\":1.75,\"modelCropScale\":1.5,\"border\":\"replicate\",\"jpegQuality\":95,\"detector\":"
                + JsonSerializer.Serialize(detector) + "}\n",
                Utf8);
        }
        else
        {
            using var json = JsonDocument.Parse(File.ReadAllText(settingsPath));
            (string Name, string Expected)[] expected =
            [
                ("version", "1"), ("size", "256"), ("cropScale", "1.75"), ("modelCropScale", "1.5"), ("border", "\"replicate\""),
            ];
            foreach (var (name, value) in expected)
            {
                var actual = json.RootElement.TryGetProperty(name, out var element) ? element.GetRawText() : "(нет)";
                if (actual != value)
                {
                    throw new InvalidOperationException($"Папка создана с другими параметрами экспорта: {name} = {actual}, ожидалось {value}");
                }
            }
        }

        // Временные файлы прошлого сбоя при переписывании не нужны: исходные файлы остались целыми.
        var progressPath = Path.Combine(path, "progress.jsonl");
        var metadataPath = Path.Combine(path, "metadata.jsonl");
        File.Delete(progressPath + ".tmp");
        File.Delete(metadataPath + ".tmp");

        // Сначала всё читается и проверяется, и только потом файлы меняются: повреждённая папка остаётся нетронутой.
        var saved = 0;
        string? progressRewrite = null;
        if (File.Exists(progressPath))
        {
            // Строка без завершающего \n — оборванная: пара не завершена, даже если строка разбирается как JSON.
            var progressText = File.ReadAllText(progressPath, Utf8);
            var complete = progressText[..(progressText.LastIndexOf('\n') + 1)];
            if (complete.Length != progressText.Length)
            {
                progressRewrite = complete;
            }

            var progressLines = complete.Split('\n');
            for (var number = 1; number < progressLines.Length; number++)
            {
                try
                {
                    using var pair = JsonDocument.Parse(progressLines[number - 1]);
                    var age = pair.RootElement.GetProperty("age").GetInt32();
                    var sex = pair.RootElement.GetProperty("sex").GetString()!;
                    saved += pair.RootElement.GetProperty("saved").GetInt32();
                    this.completed.Add((age, sex));
                }
                catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentNullException)
                {
                    throw new InvalidOperationException($"Папка повреждена: строка {number} файла progress.jsonl не читается. Выберите новую папку", e);
                }
            }
        }

        // Откат: в metadata.jsonl остаются первые saved строк, файлы лишних удаляются.
        string[] lines = [];
        if (File.Exists(metadataPath))
        {
            lines = File.ReadAllLines(metadataPath, Utf8);
        }

        if (lines.Length < saved)
        {
            throw new InvalidOperationException($"Папка повреждена: в metadata.jsonl {lines.Length} строк, а в progress.jsonl записано {saved}. Выберите новую папку");
        }

        if (progressRewrite is not null)
        {
            var tmp = progressPath + ".tmp";
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            {
                stream.Write(Utf8.GetBytes(progressRewrite));
                stream.Flush(true);
            }

            File.Move(tmp, progressPath, true);
        }

        foreach (var extra in lines.Skip(saved))
        {
            try
            {
                using var row = JsonDocument.Parse(extra);
                File.Delete(Path.Combine(path, "images", row.RootElement.GetProperty("file").GetString()!.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch (JsonException)
            {
                // Оборванная строка: файла у неё нет.
            }
        }

        this.Dropped = lines.Length - saved;
        if (this.Dropped > 0)
        {
            var tmp = metadataPath + ".tmp";
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            {
                stream.Write(Utf8.GetBytes(string.Concat(lines.Take(saved).Select(x => x + "\n"))));
                stream.Flush(true);
            }

            File.Move(tmp, metadataPath, true);
        }

        this.metadata = new FileStream(metadataPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        this.progress = new FileStream(progressPath, FileMode.Append, FileAccess.Write, FileShare.Read);
    }

    public IReadOnlySet<(int Age, string Sex)> Completed => this.completed;

    /// <summary>Строк незавершённой пары, удалённых при открытии.</summary>
    public int Dropped { get; }

    public void Add(byte[] jpeg, DateOnly birth, DateOnly shot, string sex, float pred)
    {
        var years = shot.Year - birth.Year - (shot < birth.AddYears(shot.Year - birth.Year) ? 1 : 0);
        var name = Guid.NewGuid().ToString();
        var folder = Path.Combine(this.path, "images", years.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, name + ".jpg"), jpeg);

        var row = FormattableString.Invariant(
            $"{{\"file\":\"{years}/{name}.jpg\",\"birth\":\"{birth:yyyy-MM-dd}\",\"shot\":\"{shot:yyyy-MM-dd}\",\"sex\":{JsonSerializer.Serialize(sex)},\"pred\":{pred:0.0}}}\n");
        this.metadata.Write(Utf8.GetBytes(row));
        this.metadata.Flush(true);
    }

    public void CompletePair(int age, string sex, int returned, int saved, int skipped)
    {
        var row = FormattableString.Invariant(
            $"{{\"age\":{age},\"sex\":{JsonSerializer.Serialize(sex)},\"returned\":{returned},\"saved\":{saved},\"skipped\":{skipped}}}\n");
        this.progress.Write(Utf8.GetBytes(row));
        this.progress.Flush(true);
        this.completed.Add((age, sex));
    }

    public void Dispose()
    {
        this.metadata.Dispose();
        this.progress.Dispose();
    }
}
