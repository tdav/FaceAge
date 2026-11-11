using FaceAge.Vision;

namespace FaceAge.Tests;

public sealed class AgeWindowTests
{
    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void NoResultUntilMinSamples()
    {
        var window = new AgeWindow(At(3), 3);
        window.Add(30, At(0));
        window.Add(31, At(0.1));

        Assert.Null(window.Median());

        window.Add(32, At(0.2));
        Assert.Equal(31f, window.Median());
    }

    [Fact]
    public void Median_IgnoresSingleOutlier()
    {
        var window = new AgeWindow(At(3), 5);
        foreach (var (age, i) in new[] { 34f, 35f, 80f, 33f, 34f }.Select((a, i) => (a, i)))
        {
            window.Add(age, At(i * 0.1));
        }

        // Среднее дало бы 43,2; медиана не замечает выброс 80.
        Assert.Equal(34f, window.Median());
    }

    [Fact]
    public void Median_EvenCount_AveragesMiddlePair()
    {
        var window = new AgeWindow(At(3), 4);
        window.Add(30, At(0));
        window.Add(40, At(0.1));
        window.Add(32, At(0.2));
        window.Add(34, At(0.3));

        Assert.Equal(33f, window.Median());
    }

    [Fact]
    public void OldSamples_LeaveWindow()
    {
        var window = new AgeWindow(At(3), 2);
        window.Add(60, At(0));
        window.Add(60, At(1));
        window.Add(30, At(4.5));
        window.Add(30, At(4.6));

        // Оценки старше 3 с (в моменты 0 и 1) ушли из окна.
        Assert.Equal(2, window.Count);
        Assert.Equal(30f, window.Median());
    }
}
