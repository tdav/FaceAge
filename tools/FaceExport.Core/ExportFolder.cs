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

        // Завершённые пары и число сохранённых по ним фото.
        var saved = 0;
        var progressPath = Path.Combine(path, "progress.jsonl");
        if (File.Exists(progressPath))
        {
            // Строка без завершающего \n — оборванная: пара не завершена, даже если строка разбирается как JSON.
            var progressText = File.ReadAllText(progressPath, Utf8);
            var complete = progressText[..(progressText.LastIndexOf('\n') + 1)];
            if (complete.Length != progressText.Length)
            {
                File.WriteAllText(progressPath, complete, Utf8);
            }

            foreach (var line in complete.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    using var pair = JsonDocument.Parse(line);
                    this.completed.Add((pair.RootElement.GetProperty("age").GetInt32(), pair.RootElement.GetProperty("sex").GetString()!));
                    saved += pair.RootElement.GetProperty("saved").GetInt32();
                }
                catch (JsonException)
                {
                    // Оборванная последняя строка: пара не завершена.
                }
            }
        }

        // Откат: в metadata.jsonl остаются первые saved строк, файлы лишних удаляются.
        var metadataPath = Path.Combine(path, "metadata.jsonl");
        if (File.Exists(metadataPath))
        {
            var lines = File.ReadAllLines(metadataPath, Utf8);
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

            this.Dropped = Math.Max(0, lines.Length - saved);
            if (this.Dropped > 0)
            {
                File.WriteAllText(metadataPath, string.Concat(lines.Take(saved).Select(x => x + "\n")), Utf8);
            }
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
