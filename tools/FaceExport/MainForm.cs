using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FaceAge.Export;
using Oracle.ManagedDataAccess.Client;

/// <summary>Окно выгрузки лиц из Oracle. Строится кодом; макет — раздел 5 описания проекта.</summary>
public sealed class MainForm : Form
{
    private const string DefaultSql =
        "SELECT photo, birth_date, shoot_date\r\n" +
        "FROM (\r\n" +
        "  SELECT p.photo, p.birth_date, p.shoot_date\r\n" +
        "  FROM persons p\r\n" +
        "  WHERE p.sex = :sex\r\n" +
        "    AND FLOOR(MONTHS_BETWEEN(p.shoot_date, p.birth_date) / 12) = :age\r\n" +
        "  ORDER BY DBMS_RANDOM.VALUE\r\n" +
        ")\r\n" +
        "WHERE ROWNUM <= 2000";

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FaceExport", "settings.json");

    private readonly TextBox server = new() { Dock = DockStyle.Fill, Name = "server" };
    private readonly TextBox user = new() { Dock = DockStyle.Fill, Name = "user" };
    private readonly TextBox password = new() { Dock = DockStyle.Fill, Name = "password", UseSystemPasswordChar = true };
    private readonly TextBox sql = new()
    {
        Dock = DockStyle.Fill, Name = "sql", Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Both, WordWrap = false,
        Font = new Font(FontFamily.GenericMonospace, 9.5f), Height = 110,
    };
    private readonly NumericUpDown ageFrom = new() { Name = "ageFrom", Minimum = 0, Maximum = 120, Value = 10, Width = 60 };
    private readonly NumericUpDown ageTo = new() { Name = "ageTo", Minimum = 0, Maximum = 120, Value = 55, Width = 60 };
    private readonly TextBox sexes = new() { Name = "sexes", Text = "M;F", Width = 90 };
    private readonly TextBox folder = new() { Dock = DockStyle.Fill, Name = "folder" };
    private readonly Button browse = new() { Name = "browse", Text = "Обзор", Dock = DockStyle.Fill };
    private readonly Label current = new() { AutoSize = true, Text = "Ожидание запуска" };
    private readonly Label pairNumber = new() { AutoSize = true, Anchor = AnchorStyles.Right };
    private readonly ProgressBar bar = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 1, Height = 22 };
    private readonly Label counters = new() { AutoSize = true, Text = "Сохранено 0 │ Пропущено 0 │ Скорость 0 фото/с │ Осталось —" };
    private readonly TextBox log = new() { Dock = DockStyle.Fill, Name = "log", Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly Button stop = new() { Name = "stop", Text = "Стоп", Enabled = false, Width = 90 };
    private readonly Button start = new() { Name = "start", Text = "Старт", Width = 90 };

    private CancellationTokenSource? cancel;
    private Task? work;
    private bool closeAfterWork;

    public MainForm()
    {
        this.Text = "FaceExport — экспорт лиц для обучения";
        this.ClientSize = new Size(820, 640);
        this.MinimumSize = new Size(700, 560);
        this.Font = new Font("Segoe UI", 9f);

        var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, Padding = new Padding(10, 10, 10, 4) };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));

        var ageRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        ageRow.Controls.AddRange([
            new Label { Text = "от", AutoSize = true, Margin = new Padding(0, 6, 3, 0) }, this.ageFrom,
            new Label { Text = "до", AutoSize = true, Margin = new Padding(10, 6, 3, 0) }, this.ageTo,
            new Label { Text = "значения :sex", AutoSize = true, Margin = new Padding(16, 6, 3, 0) }, this.sexes]);

        fields.Controls.Add(new Label { Text = "Сервер Oracle", AutoSize = true, Margin = new Padding(0, 6, 0, 0) }, 0, 0);
        fields.Controls.Add(this.server, 1, 0);
        fields.SetColumnSpan(this.server, 3);
        fields.Controls.Add(new Label { Text = "Пользователь, пароль", AutoSize = true, Margin = new Padding(0, 6, 0, 0) }, 0, 1);
        fields.Controls.Add(this.user, 1, 1);
        fields.Controls.Add(this.password, 2, 1);
        fields.SetColumnSpan(this.password, 2);
        fields.Controls.Add(new Label { Text = "SQL-запрос", AutoSize = true, Margin = new Padding(0, 6, 0, 0) }, 0, 2);
        fields.Controls.Add(this.sql, 1, 2);
        fields.SetColumnSpan(this.sql, 3);
        fields.Controls.Add(new Label { Text = "Возраст и пол", AutoSize = true, Margin = new Padding(0, 6, 0, 0) }, 0, 3);
        fields.Controls.Add(ageRow, 1, 3);
        fields.SetColumnSpan(ageRow, 3);
        fields.Controls.Add(new Label { Text = "Папка экспорта", AutoSize = true, Margin = new Padding(0, 6, 0, 0) }, 0, 4);
        fields.Controls.Add(this.folder, 1, 4);
        fields.SetColumnSpan(this.folder, 2);
        fields.Controls.Add(this.browse, 3, 4);

        var head = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        head.Controls.Add(this.current, 0, 0);
        head.Controls.Add(this.pairNumber, 1, 0);

        var state = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(10, 6, 10, 4) };
        state.Controls.Add(head, 0, 0);
        state.Controls.Add(this.bar, 0, 1);
        state.Controls.Add(this.counters, 0, 2);

        var logPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 0, 10, 6) };
        logPanel.Controls.Add(this.log);

        // Справа налево: первым добавленный «Старт» оказывается крайним справа, «Стоп» — левее.
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(10, 6, 10, 10),
        };
        buttons.Controls.Add(this.start);
        buttons.Controls.Add(this.stop);

        // Dock раскладывается с последнего элемента к первому: заполняющая панель добавляется первой.
        this.Controls.Add(logPanel);
        this.Controls.Add(state);
        this.Controls.Add(fields);
        this.Controls.Add(buttons);

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
                    this.sexes.Text = saved.GetValueOrDefault("sexes", "M;F");
                    this.folder.Text = saved.GetValueOrDefault("folder", "");
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or FormatException or OverflowException or UnauthorizedAccessException)
        {
            // Повреждённые настройки не мешают запуску: остаются значения по умолчанию.
            this.log.Text = "Настройки не прочитаны: " + e.Message;
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
            problem = "Поле «значения :sex» не заполнено: укажите хотя бы одно значение, например M;F";
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
            using var connection = new OracleConnection(
                new OracleConnectionStringBuilder { DataSource = serverText, UserID = userText, Password = passwordText, ConnectionTimeout = 15 }.ConnectionString);
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

        // Пароль нужен только на время работы: следующий запуск требует ввести его заново.
        this.password.Clear();
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
