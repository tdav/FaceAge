using FaceAge.Camera;
using FaceAge.Vision;

namespace FaceAge;

public partial class MainPage : ContentPage
{
    // Сколько кадров подряд без лица терпим, прежде чем сбросить значение (~0,6 с).
    private const int LostFaceFrames = 15;

    private readonly FaceOverlay overlay = new();
    private readonly Task<AgeEstimator> loading;

    // Медиана годных кадров за 3 с; первый результат — после 10 годных кадров (меньше секунды при 15+ кадрах/с).
    private readonly AgeWindow window = new(TimeSpan.FromSeconds(3), 10);
    private FaceCameraView? camera;
    private int missedFrames;
    private bool permissionDenied;

    public MainPage()
    {
        this.InitializeComponent();
        this.Overlay.Drawable = this.overlay;

        // Модели грузятся сразу, параллельно с запросом разрешения: создание двух ONNX-сессий занимает ~0,2 с.
        this.loading = Task.Run(async () =>
        {
            using var detector = await FileSystem.OpenAppPackageFileAsync("yunet_320.onnx");
            using var ager = await FileSystem.OpenAppPackageFileAsync("genderage.onnx");
            using var detectorBytes = new MemoryStream();
            using var agerBytes = new MemoryStream();
            await detector.CopyToAsync(detectorBytes);
            await ager.CopyToAsync(agerBytes);
            return new AgeEstimator(detectorBytes.ToArray(), agerBytes.ToArray());
        });
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
            this.camera.Estimator = await this.loading;
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
        this.window.Add(reading.Age, TimeSpan.FromMilliseconds(Environment.TickCount64));
        if (this.window.Median() is not { } median)
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
