using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FaceAge.Export;
using Oracle.ManagedDataAccess.Client;

/// <summary>Окно выгрузки лиц из Oracle. Раскладка — в MainForm.Designer.cs.</summary>
public sealed partial class MainForm : Form
{
    private const string DefaultSql =
        "SELECT r.tv_photo, p.tv_datebirth, p.tv_dateenter\r\n" +
        "FROM citizen.tv_person p\r\n" +
        "  JOIN citizen.tv_personphoto ph ON ph.tv_personguid = p.tv_guid AND ph.tv_personid = p.tv_id\r\n" +
        "  JOIN media.tv_photo r ON ph.tv_photoid = r.tv_id\r\n" +
        "WHERE p.tv_recordstatus = 1\r\n" +
        "  AND p.tv_personstatus = 1\r\n" +
        "  AND p.tv_sex = TO_NUMBER(:sex)\r\n" +
        "  AND ph.tv_table = 58\r\n" +
        "  AND FLOOR(MONTHS_BETWEEN(p.tv_dateenter, p.tv_datebirth) / 12) = :age\r\n" +
        "  AND ROWNUM <= 2000";

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FaceExport", "settings.json");

    // Строка подключения и пароль из appsettings.json; пустые, если файла или строки нет.
    private readonly string connectionString = "";
    private readonly string configuredPassword = "";

    private CancellationTokenSource? cancel;
    private Task? work;
    private bool closeAfterWork;

    public MainForm()
    {
        this.InitializeComponent();

        this.sql.Text = DefaultSql;
        try
        {
            if (File.Exists(SettingsPath))
            {
                var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SettingsPath));
                if (saved is not null)
                {
                    this.server.Text = saved.GetValueOrDefault("server", "");
                    this.user.Text = saved.GetValueOrDefault("user", "");
                    this.sql.Text = saved.GetValueOrDefault("sql", DefaultSql);
                    this.ageFrom.Value = Math.Clamp(int.Parse(saved.GetValueOrDefault("ageFrom", "10"), CultureInfo.InvariantCulture), 0, 120);
                    this.ageTo.Value = Math.Clamp(int.Parse(saved.GetValueOrDefault("ageTo", "55"), CultureInfo.InvariantCulture), 0, 120);
                    this.sexes.Text = saved.GetValueOrDefault("sexes", "1;2");
                    this.folder.Text = saved.GetValueOrDefault("folder", "");
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or FormatException or OverflowException or UnauthorizedAccessException)
        {
            // Повреждённые настройки не мешают запуску: остаются значения по умолчанию.
            this.log.Text = "Настройки не прочитаны: " + e.Message;
        }

        // Строка подключения ConnectionStrings:Oracle из файла рядом с программой заполняет сервер, пользователя и пароль.
        // appsettings.local.json важнее appsettings.json: он не попадает в git, в нём можно держать пароль.
        try
        {
            var localPath = Path.Combine(AppContext.BaseDirectory, "appsettings.local.json");
            var appPath = File.Exists(localPath) ? localPath : Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(appPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(appPath));
                if (document.RootElement.TryGetProperty("ConnectionStrings", out var strings)
                    && strings.TryGetProperty("Oracle", out var value)
                    && value.GetString() is { Length: > 0 } text)
                {
                    var parsed = new OracleConnectionStringBuilder(text);
                    this.connectionString = text;
                    this.configuredPassword = parsed.Password ?? "";
                    this.server.Text = parsed.DataSource;
                    this.user.Text = parsed.UserID;
                    this.password.Text = this.configuredPassword;
                    this.log.AppendText("Подключение взято из " + Path.GetFileName(appPath) + Environment.NewLine);
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            // Текст ошибки не выводится: в нём может оказаться кусок строки подключения с паролем.
            this.log.AppendText("Строка подключения ConnectionStrings:Oracle в appsettings.json не прочитана (" + e.GetType().Name + "): проверьте формат" + Environment.NewLine);
        }

        this.browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = this.folder.Text, UseDescriptionForTitle = true, Description = "Папка экспорта" };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                this.folder.Text = dialog.SelectedPath;
            }
        };
        this.stop.Click += (_, _) =>
        {
            this.stop.Enabled = false;
            this.cancel?.Cancel();
            this.log.AppendText("Остановка: ждём конца текущего фото" + Environment.NewLine);
        };
        this.start.Click += this.OnStart;
        this.FormClosing += this.OnClosing;
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (this.work is { IsCompleted: false })
        {
            // Закрытие во время работы: отменяем и закрываем окно, когда поток закончит текущее фото.
            e.Cancel = true;
            this.closeAfterWork = true;
            this.stop.Enabled = false;
            this.cancel?.Cancel();
            this.Text = "FaceExport — останавливаем, ждём конца текущего фото";
        }
    }

    private async void OnStart(object? sender, EventArgs e)
    {
        var serverText = this.server.Text.Trim();
        var userText = this.user.Text.Trim();
        var passwordText = this.password.Text;
        var folderText = this.folder.Text.Trim();
        var sqlText = this.sql.Text;
        var from = (int)this.ageFrom.Value;
        var to = (int)this.ageTo.Value;
        var sexList = this.sexes.Text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        string? problem = null;
        if (serverText.Length == 0)
        {
            problem = "Поле «Сервер Oracle» не заполнено";
        }
        else if (userText.Length == 0)
        {
            problem = "Поле «Пользователь» не заполнено";
        }
        else if (passwordText.Length == 0)
        {
            problem = "Поле «Пароль» не заполнено";
        }
        else if (folderText.Length == 0)
        {
            problem = "Поле «Папка экспорта» не заполнено";
        }
        else if (!sqlText.Contains(":age", StringComparison.OrdinalIgnoreCase) || !sqlText.Contains(":sex", StringComparison.OrdinalIgnoreCase))
        {
            problem = "В поле «SQL-запрос» должны быть параметры :age и :sex";
        }
        else if (from < 0 || to > 120 || from > to)
        {
            problem = "Поля «Возраст от» и «до»: нужно 0 ≤ от ≤ до ≤ 120";
        }
        else if (sexList.Length == 0)
        {
            problem = "Поле «значения :sex» не заполнено: укажите хотя бы одно значение, например 1;2";
        }

        if (problem is not null)
        {
            MessageBox.Show(this, problem, "FaceExport", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var detectorPath = Path.Combine(AppContext.BaseDirectory, "Models", "yunet_320.onnx");
        var agePath = Path.Combine(AppContext.BaseDirectory, "Models", "genderage.onnx");
        foreach (var model in new[] { detectorPath, agePath })
        {
            if (!File.Exists(model))
            {
                MessageBox.Show(this, "Не найден файл модели: " + Path.GetFileName(model) + " (папка Models рядом с программой)", "FaceExport", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
        }

        try
        {
            // Пароль в настройки не попадает.
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["server"] = serverText,
                ["user"] = userText,
                ["sql"] = sqlText,
                ["ageFrom"] = from.ToString(CultureInfo.InvariantCulture),
                ["ageTo"] = to.ToString(CultureInfo.InvariantCulture),
                ["sexes"] = this.sexes.Text,
                ["folder"] = folderText,
            }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            this.log.AppendText("Настройки не сохранены: " + ex.Message + Environment.NewLine);
        }

        Control[] inputs = [this.server, this.user, this.password, this.sql, this.ageFrom, this.ageTo, this.sexes, this.folder, this.browse, this.start];
        foreach (var control in inputs)
        {
            control.Enabled = false;
        }

        this.stop.Enabled = true;
        this.cancel = new CancellationTokenSource();
        var token = this.cancel.Token;
        var clock = Stopwatch.StartNew();
        var pairsDone = 0;
        this.bar.Value = 0;
        this.log.AppendText($"Старт: возраст {from}–{to}, пол {string.Join(", ", sexList)}" + Environment.NewLine);

        // Progress создан в потоке окна, поэтому обработчик выполняется там же.
        ExportProgress last = default;
        var progress = new Progress<ExportProgress>(p =>
        {
            last = p;
            this.current.Text = $"Возраст {p.Age}, пол {p.Sex}: получено {p.Returned.ToString("N0", Ru)}, сохранено {p.Saved.ToString("N0", Ru)}";
            this.pairNumber.Text = $"пара {p.PairIndex} из {p.PairCount}";
            this.bar.Maximum = p.PairCount;
            this.bar.Value = Math.Min(p.Line is null ? p.PairIndex - 1 : p.PairIndex, p.PairCount);
            if (p.Line is not null)
            {
                this.log.AppendText(p.Line + Environment.NewLine);
                if (p.Returned > 0)
                {
                    pairsDone++;
                }
            }

            var seconds = Math.Max(clock.Elapsed.TotalSeconds, 1);
            var left = "—";
            if (pairsDone > 0)
            {
                var rest = TimeSpan.FromSeconds(seconds / pairsDone * (p.PairCount - p.PairIndex));
                left = rest.TotalHours >= 1 ? $"{(int)rest.TotalHours} ч {rest.Minutes} мин" : $"{rest.Minutes} мин {rest.Seconds} с";
            }

            this.counters.Text = $"Сохранено {p.TotalSaved.ToString("N0", Ru)} │ Пропущено {p.TotalSkipped.ToString("N0", Ru)} │ " +
                                 $"Скорость {((p.TotalSaved + p.TotalSkipped) / seconds).ToString("0.0", Ru)} фото/с │ Осталось {left} (оценка)";
        });

        this.work = Task.Run(() =>
        {
            // Остальные ключи строки из appsettings.json сохраняются; поля окна важнее её значений.
            var connectionBuilder = new OracleConnectionStringBuilder(this.connectionString) { DataSource = serverText, UserID = userText, Password = passwordText };
            if (this.connectionString.Length == 0)
            {
                connectionBuilder.ConnectionTimeout = 15;
            }

            using var connection = new OracleConnection(connectionBuilder.ConnectionString);
            connection.Open();
            using var cropper = new FaceCropper(File.ReadAllBytes(detectorPath), File.ReadAllBytes(agePath));
            using var exportFolder = new ExportFolder(folderText, "yunet_320.onnx");
            if (exportFolder.Dropped > 0)
            {
                this.BeginInvoke(() => this.log.AppendText($"Удалено {exportFolder.Dropped} строк незавершённой пары" + Environment.NewLine));
            }

            var run = new ExportRun(cropper, exportFolder, GdiCodec.Decode, GdiCodec.EncodeJpeg);
            run.Run(
                connection, sqlText, from, to, sexList,
                command => ((OracleCommand)command).BindByName = true,
                progress,
                token);
        });

        string outcome;
        var failed = false;
        try
        {
            await this.work;
            outcome = $"Экспорт завершён: сохранено {last.TotalSaved.ToString("N0", Ru)}, пропущено {last.TotalSkipped.ToString("N0", Ru)}";
        }
        catch (OperationCanceledException)
        {
            outcome = "Остановлено. При следующем запуске незаконченная пара будет выгружена заново";
        }
        catch (Exception ex)
        {
            failed = true;
            outcome = passwordText.Length == 0 ? ex.Message : ex.Message.Replace(passwordText, "***");
        }

        // Введённый вручную пароль нужен только на время работы; пароль из appsettings.json возвращается в поле.
        this.password.Text = this.configuredPassword;
        this.log.AppendText(outcome + Environment.NewLine);
        this.cancel.Dispose();
        this.cancel = null;
        foreach (var control in inputs)
        {
            control.Enabled = true;
        }

        this.stop.Enabled = false;
        this.Text = "FaceExport — экспорт лиц для обучения";
        if (this.closeAfterWork)
        {
            this.Close();
        }
        else if (failed)
        {
            MessageBox.Show(this, outcome, "FaceExport: ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
