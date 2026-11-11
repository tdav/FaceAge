namespace FaceAge.Vision;

/// <summary>
/// Скользящее окно оценок годных кадров: показываемый возраст — медиана за последние <paramref name="span"/>.
/// Медиана, а не среднее: единичный промах (моргание, смаз) не сдвигает результат.
/// </summary>
public sealed class AgeWindow(TimeSpan span, int minSamples)
{
    private readonly Queue<(TimeSpan At, float Age)> samples = new();

    /// <summary>Сколько оценок сейчас в окне.</summary>
    public int Count => this.samples.Count;

    /// <summary>Сколько оценок нужно для первого результата.</summary>
    public int MinSamples => minSamples;

    public void Add(float age, TimeSpan now)
    {
        this.samples.Enqueue((now, age));
        while (this.samples.Peek().At < now - span)
        {
            this.samples.Dequeue();
        }
    }

    /// <summary>Медиана окна или null, пока оценок меньше <see cref="MinSamples"/>.</summary>
    public float? Median()
    {
        if (this.samples.Count < minSamples)
        {
            return null;
        }

        var ages = this.samples.Select(x => x.Age).Order().ToArray();
        var middle = ages.Length / 2;
        return ages.Length % 2 == 1 ? ages[middle] : (ages[middle - 1] + ages[middle]) / 2f;
    }

    public void Clear() => this.samples.Clear();
}
