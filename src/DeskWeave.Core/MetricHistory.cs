namespace DeskWeave.Core;

/// <summary>Fixed-size rolling history of percentage samples (oldest first).</summary>
public sealed class MetricHistory(int capacity)
{
    private readonly Queue<double> _samples = new(capacity);

    public int Capacity { get; } = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));

    public IReadOnlyCollection<double> Samples => _samples;

    public void Add(double? value)
    {
        if (value is not { } sample)
        {
            return;
        }

        if (_samples.Count == Capacity)
        {
            _samples.Dequeue();
        }

        _samples.Enqueue(Math.Clamp(sample, 0, 100));
    }
}
