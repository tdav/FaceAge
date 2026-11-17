namespace FaceAge.Vision;

/// <summary>
/// Скользящее окно оценок годных кадров за последние <paramref name="span"/>:
/// объединяет взвешенную по качеству кадра медиану и усечённое среднее центрального ядра.
/// </summary>
public sealed class AgeWindow(TimeSpan span, int minSamples)
{
    private readonly Queue<(TimeSpan At, float Age, float Weight)> samples = new();

    /// <summary>Сколько оценок сейчас в окне.</summary>
    public int Count => this.samples.Count;

    /// <summary>Сколько оценок нужно для первого результата.</summary>
    public int MinSamples => minSamples;

    public void Add(float age, TimeSpan now) => this.Add(age, now, 1f);

    public void Add(float age, TimeSpan now, float weight)
    {
        var safeWeight = float.IsFinite(weight) && weight > 0.01f ? weight : 0.01f;
        this.samples.Enqueue((now, age, safeWeight));
        while (this.samples.Count > 0 && this.samples.Peek().At < now - span)
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

        var ages = new float[this.samples.Count];
        var index = 0;
        foreach (var sample in this.samples)
        {
            ages[index++] = sample.Age;
        }

        Array.Sort(ages);
        var middle = ages.Length / 2;
        return ages.Length % 2 == 1 ? ages[middle] : (ages[middle - 1] + ages[middle]) / 2f;
    }

    /// <summary>
    /// Взвешенная оценка окна (50% взвешенная медиана + 50% взвешенное усечённое среднее центральных 60% оценок)
    /// или null, пока оценок меньше <see cref="MinSamples"/>.
    /// </summary>
    public float? WeightedEstimate()
    {
        var count = this.samples.Count;
        if (count < minSamples)
        {
            return null;
        }

        var items = new (float Age, float Weight)[count];
        var index = 0;
        var totalWeight = 0f;
        foreach (var sample in this.samples)
        {
            items[index++] = (sample.Age, sample.Weight);
            totalWeight += sample.Weight;
        }

        Array.Sort(items, static (left, right) => left.Age.CompareTo(right.Age));

        var halfWeight = totalWeight * 0.5f;
        var cumulative = 0f;
        var weightedMedian = items[^1].Age;
        for (var i = 0; i < count; i++)
        {
            cumulative += items[i].Weight;
            if (cumulative >= halfWeight)
            {
                weightedMedian = cumulative == halfWeight && i + 1 < count
                    ? (items[i].Age + items[i + 1].Age) * 0.5f
                    : items[i].Age;
                break;
            }
        }

        var trim = count >= 5 ? count / 5 : 0;
        var coreSum = 0f;
        var coreWeight = 0f;
        for (var i = trim; i < count - trim; i++)
        {
            coreSum += items[i].Age * items[i].Weight;
            coreWeight += items[i].Weight;
        }

        var trimmedMean = coreSum / coreWeight;
        return 0.5f * weightedMedian + 0.5f * trimmedMean;
    }

    public void Clear() => this.samples.Clear();
}
