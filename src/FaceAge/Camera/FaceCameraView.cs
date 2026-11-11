using AndroidX.Camera.Core;
using AndroidX.Camera.Core.ResolutionSelector;
using AndroidX.Camera.Lifecycle;
using AndroidX.Camera.View;
using AndroidX.Core.Content;
using AndroidX.Lifecycle;
using FaceAge.Vision;
using Java.Util.Concurrent;
using Microsoft.Maui.Handlers;

namespace FaceAge.Camera;

/// <summary>Превью фронтальной камеры; каждый проанализированный кадр приходит событием <see cref="Reading"/> на главном потоке.</summary>
public sealed class FaceCameraView : View
{
    public event EventHandler<AgeReading>? Reading;

    /// <summary>Модели загружены — анализатор начинает работу с ближайшего кадра.</summary>
    public AgeEstimator? Estimator { get; set; }

    /// <summary>Пауза: превью идёт, кадры не анализируются, последнее значение остаётся на экране.</summary>
    public bool IsPaused { get; set; }

    internal void RaiseReading(AgeReading reading) => this.Reading?.Invoke(this, reading);
}

/// <summary>
/// CameraX: Preview в PreviewView и ImageAnalysis (RGBA_8888, только последний кадр) на отдельном потоке.
/// Привязка к жизненному циклу активити: при сворачивании камера останавливается сама.
/// </summary>
public sealed class FaceCameraViewHandler() : ViewHandler<FaceCameraView, PreviewView>(ViewMapper)
{
    private IExecutorService? executor;
    private ProcessCameraProvider? provider;

    protected override PreviewView CreatePlatformView() => new(this.Context);

    protected override void ConnectHandler(PreviewView platformView)
    {
        base.ConnectHandler(platformView);

        this.executor = Executors.NewSingleThreadExecutor();
        var future = ProcessCameraProvider.GetInstance(this.Context);
        future.AddListener(new Java.Lang.Runnable(() =>
        {
            if (this.executor is null)
            {
                return; // Обработчик отключили раньше, чем камера была готова.
            }

            this.provider = (ProcessCameraProvider)future.Get()!;

            // 4:3 у превью и анализа — одно и то же поле зрения сенсора, поэтому рамку лица
            // можно наложить на превью простым масштабом. 640×480 хватает детектору 320×240 с запасом.
            var previewSelector = new ResolutionSelector.Builder()
                .SetAspectRatioStrategy(AspectRatioStrategy.Ratio43FallbackAutoStrategy!)!
                .Build();
            var analysisSelector = new ResolutionSelector.Builder()
                .SetAspectRatioStrategy(AspectRatioStrategy.Ratio43FallbackAutoStrategy!)!
                .SetResolutionStrategy(new ResolutionStrategy(new Android.Util.Size(640, 480), ResolutionStrategy.FallbackRuleClosestLowerThenHigher))!
                .Build();

            var preview = new Preview.Builder().SetResolutionSelector(previewSelector)!.Build()!;
            preview.SetSurfaceProvider(ContextCompat.GetMainExecutor(this.Context), platformView.SurfaceProvider);

            var analysis = new ImageAnalysis.Builder()
                .SetResolutionSelector(analysisSelector)!
                .SetBackpressureStrategy(ImageAnalysis.StrategyKeepOnlyLatest)!
                .SetOutputImageFormat(ImageAnalysis.OutputImageFormatRgba8888)!
                .Build()!;
            analysis.SetAnalyzer(this.executor, new FaceAnalyzer(this.VirtualView));

            this.provider.UnbindAll();
            this.provider.BindToLifecycle((ILifecycleOwner)this.Context, CameraSelector.DefaultFrontCamera!, preview, analysis);
        }), ContextCompat.GetMainExecutor(this.Context));
    }

    protected override void DisconnectHandler(PreviewView platformView)
    {
        this.provider?.UnbindAll();
        this.provider = null;
        this.executor?.Shutdown();
        this.executor = null;
        base.DisconnectHandler(platformView);
    }

    /// <summary>Анализатор кадров: работает на потоке executor, результат отправляет на главный поток.</summary>
    private sealed class FaceAnalyzer(FaceCameraView view) : Java.Lang.Object, ImageAnalysis.IAnalyzer
    {
        public unsafe void Analyze(IImageProxy? image)
        {
            if (image is null)
            {
                return;
            }

            try
            {
                if (view.IsPaused || view.Estimator is not { } estimator)
                {
                    return;
                }

                // RGBA_8888 — одна плоскость. Читаем direct ByteBuffer по адресу: копирование ~1,2 МБ
                // через JNI на каждый кадр стоило бы больше, чем сам детектор.
                var plane = image.GetPlanes()![0];
                var address = plane.Buffer!.GetDirectBufferAddress();
                var pixels = new ReadOnlySpan<byte>((void*)address, plane.RowStride * image.Height);

                var reading = estimator.Analyze(pixels, image.Width, image.Height, plane.RowStride, image.ImageInfo!.RotationDegrees);
                MainThread.BeginInvokeOnMainThread(() => view.RaiseReading(reading));
            }
            catch (Exception ex)
            {
                // Сбой одного кадра не должен ронять приложение: следующий кадр придёт через ~30 мс.
                Android.Util.Log.Error("FaceAge", ex.ToString());
            }
            finally
            {
                // Без Close CameraX не отдаст следующий кадр.
                image.Close();
            }
        }
    }
}
