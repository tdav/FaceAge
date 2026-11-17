using FaceAge.Camera;
using FaceAge.Vision;

namespace FaceAge;

public partial class MainPage : ContentPage
{
    // Сколько кадров подряд без лица терпим, прежде чем сбросить значение (~0,6 с).
    private const int LostFaceFrames = 15;

    // Модели на выбор. Ошибка — MAE 13–50 лет в режиме phone из report.md обучения (для исходной — «с поправкой»).
    private static readonly (string Id, string Title, string File, string? SecondaryFile, AgeModel Kind, string Error)[] Models =
    [
        ("v2-ensemble", "Ансамбль large + small", "age-v2-large.onnx", "age-v2-small.onnx", AgeModel.Ensemble, "около 1,9 года"),
        ("v2-large", "Обученная large", "age-v2-large.onnx", null, AgeModel.Large, "около 2,0 лет"),
        ("v2-small", "Обученная small", "age-v2-small.onnx", null, AgeModel.Small, "около 3,2 года"),
        ("genderage", "Исходная InsightFace", "genderage.onnx", null, AgeModel.GenderAge, "около 7 лет"),
    ];

    // ponytail: загруженные модели не освобождаются — поток камеры может держать прежнюю, а их всего четыре (~25 МБ).
    private readonly Dictionary<string, Task<AgeEstimator>> estimators = [];
    private readonly FaceOverlay overlay = new();
    private Task<byte[]>? detectorBytesTask;
    private string modelId;

    // Взвешенное окно годных кадров за 3 с; первый результат — после 10 годных кадров (меньше секунды при 15+ кадрах/с).
    private readonly AgeWindow window = new(TimeSpan.FromSeconds(3), 10);
    private FaceCameraView? camera;
    private int missedFrames;
    private bool permissionDenied;

    public MainPage()
    {
        this.InitializeComponent();
        this.Overlay.Drawable = this.overlay;

        // Выбранная модель грузится сразу, параллельно с запросом разрешения камеры.
        var saved = Preferences.Get("model", Models[0].Id);
        var model = Models.FirstOrDefault(m => m.Id == saved);
        model = model.Id is null ? Models[0] : model;
        this.modelId = model.Id;
        this.ModelLabel.Text = $"{model.Title} · ошибка {model.Error} ›";
        _ = this.LoadModel(this.modelId);
    }

    private static async Task<byte[]> LoadAssetBytesAsync(string fileName)
    {
        using var stream = await FileSystem.OpenAppPackageFileAsync(fileName);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return memory.ToArray();
    }

    private Task<byte[]> GetDetectorBytesAsync()
    {
        if (this.detectorBytesTask is null || this.detectorBytesTask.IsFaulted)
        {
            this.detectorBytesTask = Task.Run(() => LoadAssetBytesAsync("yunet_320.onnx"));
        }

        return this.detectorBytesTask;
    }

    /// <summary>Загружает модель один раз; повторный выбор берёт уже созданную сессию.</summary>
    private Task<AgeEstimator> LoadModel(string id)
    {
        if (!this.estimators.TryGetValue(id, out var task) || task.IsFaulted)
        {
            var model = Models.First(m => m.Id == id);
            var detectorTask = this.GetDetectorBytesAsync();
            task = Task.Run(async () =>
            {
                var primaryTask = LoadAssetBytesAsync(model.File);
                var secondaryBytes = model.SecondaryFile is not null
                    ? await LoadAssetBytesAsync(model.SecondaryFile)
                    : null;
                var detectorBytes = await detectorTask;
                var primaryBytes = await primaryTask;
                return new AgeEstimator(detectorBytes, primaryBytes, model.Kind, secondaryBytes)
                {
                    SkipAgeOnBadQuality = true,
                };
            });
            this.estimators[id] = task;
        }

        return task;
    }

    private async void OnModelTapped(object? sender, TappedEventArgs e)
    {
        var titles = Models.Select(m => m.Title).ToArray();
        var picked = await this.DisplayActionSheetAsync("Модель возраста", "Отмена", null, titles);
        var model = Models.FirstOrDefault(m => m.Title == picked);
        if (model.Id is null || model.Id == this.modelId)
        {
            return;
        }

        this.modelId = model.Id;
        Preferences.Set("model", model.Id);
        this.ModelLabel.Text = $"{model.Title} · ошибка {model.Error} ›";

        // Оценки прежней модели в медиану новой не смешиваются.
        this.window.Clear();
        this.AgeLabel.Text = "—";
        this.AgeUnitLabel.Text = string.Empty;
        if (this.camera is null)
        {
            return;
        }

        this.StatusLabel.Text = "Загрузка модели…";
        try
        {
            var estimator = await this.LoadModel(model.Id);

            // Пока грузилась, могли выбрать другую.
            if (this.modelId == model.Id)
            {
                this.camera.Estimator = estimator;
                this.StatusLabel.Text = "Посмотрите в камеру";
            }
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("FaceAge", ex.ToString());
            this.StatusLabel.Text = "Не удалось загрузить модель";
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await this.StartCameraAsync(askPermission: true);
    }

    /// <summary>Запускает камеру, если она ещё не запущена и есть разрешение.</summary>
    /// <param name="askPermission">Спросить разрешение системным диалогом; без него — только проверить (возврат из настроек).</param>
    public async Task StartCameraAsync(bool askPermission)
    {
        if (this.camera is not null)
        {
            return;
        }

        var status = await Permissions.CheckStatusAsync<Permissions.Camera>();
        if (status != PermissionStatus.Granted && askPermission)
        {
            status = await Permissions.RequestAsync<Permissions.Camera>();
        }

        if (status != PermissionStatus.Granted)
        {
            this.permissionDenied = true;
            this.StatusLabel.Text = "Нет доступа к камере";
            this.ActionButton.Text = "Разрешить";
            this.ActionButton.IsEnabled = true;
            return;
        }

        this.permissionDenied = false;
        this.camera = new FaceCameraView();
        this.camera.Reading += this.OnReading;
        this.Root.Children.Insert(0, this.camera);
        this.ActionButton.Text = "Пауза";
        this.StatusLabel.Text = "Загрузка модели…";

        try
        {
            this.camera.Estimator = await this.LoadModel(this.modelId);
            this.StatusLabel.Text = "Посмотрите в камеру";
            this.ActionButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("FaceAge", ex.ToString());
            this.StatusLabel.Text = "Не удалось загрузить модель";
        }
    }

    private async void OnActionClicked(object? sender, EventArgs e)
    {
        if (this.permissionDenied)
        {
            // Повторный запрос; если система больше не показывает диалог («Не спрашивать») — в настройки приложения.
            var status = await Permissions.RequestAsync<Permissions.Camera>();
            if (status == PermissionStatus.Granted)
            {
                await this.StartCameraAsync(askPermission: false);
            }
            else
            {
                AppInfo.ShowSettingsUI();
            }

            return;
        }

        if (this.camera is null)
        {
            return;
        }

        this.camera.IsPaused = !this.camera.IsPaused;
        this.ActionButton.Text = this.camera.IsPaused ? "Продолжить" : "Пауза";
        this.StatusLabel.Text = this.camera.IsPaused ? "Пауза" : "Посмотрите в камеру";
    }

    private void OnReading(object? sender, AgeReading reading)
    {
        // Кадр, проанализированный до нажатия «Пауза», не должен менять замороженное значение.
        if (this.camera is not { IsPaused: false })
        {
            return;
        }

        if (!reading.HasFace)
        {
            if (++this.missedFrames == LostFaceFrames)
            {
                this.window.Clear();
                this.overlay.Face = null;
                this.AgeLabel.Text = "—";
                this.AgeUnitLabel.Text = string.Empty;
                SemanticProperties.SetDescription(this.AgeLabel, "Возраст пока не определён");
                this.StatusLabel.Text = "Лицо не найдено";
                this.Overlay.Invalidate();
            }

            return;
        }

        this.missedFrames = 0;
        this.overlay.Face = reading.Face;
        this.overlay.Frame = reading.Frame;

        // Плохой кадр в оценку не идёт: рамка серая, в строке состояния — что исправить. Прежнее значение остаётся.
        if (reading.Quality != FaceQuality.Good)
        {
            this.overlay.Stroke = Colors.Gray;
            this.Overlay.Invalidate();
            this.StatusLabel.Text = reading.Quality switch
            {
                FaceQuality.TooSmall => "Подойдите ближе",
                FaceQuality.Turned => "Смотрите прямо в камеру",
                FaceQuality.Blurry => "Держите телефон неподвижно",
                FaceQuality.TooDark => "Слишком темно",
                _ => "Слишком яркий свет",
            };
            return;
        }

        this.overlay.Stroke = (Color)Application.Current!.Resources["Tint"];
        this.Overlay.Invalidate();
        var sharpnessFactor = Math.Clamp((reading.Sharpness - 150f) / 250f, 0f, 1f);
        var yawFactor = Math.Clamp(1f - MathF.Abs(reading.Yaw) / 0.35f, 0f, 1f);
        var weight = 1f + 0.6f * sharpnessFactor + 0.4f * yawFactor;
        this.window.Add(reading.Age, TimeSpan.FromMilliseconds(Environment.TickCount64), weight);
        if (this.window.WeightedEstimate() is not { } median)
        {
            this.StatusLabel.Text = $"Измерение… {this.window.Count * 100 / this.window.MinSamples}%";
            return;
        }

        var years = Math.Max(1, (int)MathF.Round(median));
        var unit = (years % 10, years % 100) switch
        {
            (1, not 11) => "год",
            (2 or 3 or 4, not (12 or 13 or 14)) => "года",
            _ => "лет",
        };

        this.AgeLabel.Text = years.ToString();
        this.AgeUnitLabel.Text = unit;
        SemanticProperties.SetDescription(this.AgeLabel, $"Возраст около {years} {unit}");
#if DEBUG
        this.StatusLabel.Text = $"Лицо найдено · {reading.Milliseconds:0} мс";
#else
        this.StatusLabel.Text = "Лицо найдено";
#endif
    }

    /// <summary>Рамка лица поверх превью.</summary>
    private sealed class FaceOverlay : IDrawable
    {
        public RectF? Face { get; set; }

        public SizeF Frame { get; set; }

        public Color Stroke { get; set; } = Colors.White;

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            if (this.Face is not { } face || this.Frame.Width <= 0)
            {
                return;
            }

            // PreviewView (FILL_CENTER) масштабирует кадр до заполнения и обрезает лишнее по центру;
            // превью фронтальной камеры зеркальное, кадр анализа — нет, поэтому X отражается.
            var scale = MathF.Max(dirtyRect.Width / this.Frame.Width, dirtyRect.Height / this.Frame.Height);
            var offsetX = (dirtyRect.Width - this.Frame.Width * scale) / 2f;
            var offsetY = (dirtyRect.Height - this.Frame.Height * scale) / 2f;
            var x = dirtyRect.Width - (offsetX + face.Right * scale);
            var y = offsetY + face.Top * scale;

            canvas.StrokeColor = this.Stroke;
            canvas.StrokeSize = 3;
            canvas.DrawRoundedRectangle(x, y, face.Width * scale, face.Height * scale, 26);
        }
    }
}
