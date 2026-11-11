namespace FaceAge;

public partial class App : Application
{
    public App()
    {
        this.InitializeComponent();
        this.ApplyTheme();
        this.RequestedThemeChanged += (_, _) => this.ApplyTheme();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var page = new MainPage();
        var window = new Window(page);

        // Возврат в приложение: акцент Material You мог смениться вместе с обоями (события об этом нет),
        // а разрешение на камеру — выдано в настройках системы.
        window.Resumed += async (_, _) =>
        {
            this.ApplyTheme();
            await page.StartCameraAsync(askPermission: false);
        };

        return window;
    }

    /// <summary>Раскладывает токены под текущую тему; на Android 12+ акцент берётся из Material You.</summary>
    private void ApplyTheme()
    {
        var dark = this.RequestedTheme == AppTheme.Dark;

        this.Resources["Label"] = Color.FromArgb(dark ? "#FFFFFFFF" : "#FF000000");
        this.Resources["SecondaryLabel"] = Color.FromArgb(dark ? "#99EBEBF5" : "#993C3C43");
        this.Resources["TertiaryLabel"] = Color.FromArgb(dark ? "#4CEBEBF5" : "#4C3C3C43");
        this.Resources["GlassOpaque"] = Color.FromArgb(dark ? "#FF2C2C2E" : "#FFF9F9F9");
        this.Resources["GlassEdge"] = Color.FromArgb(dark ? "#80000000" : "#0F000000");
        this.Resources["QuaternarySystemFill"] = Color.FromArgb(dark ? "#2E767680" : "#14747480");

        // Запасной оранжевый — с чёрным текстом (белый дал бы 2,3:1); на Material You тон 40/60 — с белым.
        var tint = Color.FromArgb(dark ? "#FFFF9230" : "#FFFF8D28");
        var onTint = Colors.Black;
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            var id = dark ? Android.Resource.Color.SystemAccent1400 : Android.Resource.Color.SystemAccent1600;
            tint = Color.FromInt(Android.App.Application.Context.GetColor(id));
            onTint = Colors.White;
        }

        this.Resources["Tint"] = tint;
        this.Resources["OnTint"] = onTint;
    }
}
