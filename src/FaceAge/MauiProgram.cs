using FaceAge.Camera;
using Microsoft.Maui.Handlers;

namespace FaceAge;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                // Inter (OFL) — Android-замена SF Pro по дизайн-системе; Light — для крупной цифры возраста.
                fonts.AddFont("Inter-Light.ttf", "InterLight");
                fonts.AddFont("Inter-Regular.ttf", "InterRegular");
                fonts.AddFont("Inter-SemiBold.ttf", "InterSemiBold");
            })
            .ConfigureMauiHandlers(handlers => handlers.AddHandler<FaceCameraView, FaceCameraViewHandler>());

        // Табличные цифры: у меняющегося числа не прыгает ширина. FontFeatures в MAUI нет — ставим в TextView.
        LabelHandler.Mapper.AppendToMapping("TabularDigits", (handler, label) =>
        {
            if (label is Label { StyleId: "Tabular" })
            {
                handler.PlatformView.FontFeatureSettings = "'tnum'";
            }
        });

        return builder.Build();
    }
}
