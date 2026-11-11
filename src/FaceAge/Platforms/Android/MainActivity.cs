using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.View;

namespace FaceAge;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    // Только портрет: анализ и рамка лица считаются для одной ориентации, а поворот экрана пересоздавал бы камеру.
    ScreenOrientation = ScreenOrientation.Portrait,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Превью камеры — от края до края; под панелями оно всегда тёмное, поэтому значки панелей светлые в любой теме.
        var window = this.Window!;
        WindowCompat.SetDecorFitsSystemWindows(window, false);
        var bars = WindowCompat.GetInsetsController(window, window.DecorView)!;
        bars.AppearanceLightStatusBars = false;
        bars.AppearanceLightNavigationBars = false;
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            // Без этого Android подкладывает полупрозрачную полосу под трёхкнопочную навигацию.
            window.NavigationBarContrastEnforced = false;
        }

        // Экран не гаснет, пока человек смотрит в камеру.
        window.AddFlags(Android.Views.WindowManagerFlags.KeepScreenOn);
    }
}
