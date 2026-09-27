namespace AntecCaseDisplay.Dashboard;

/// <summary>
/// Fixed-capacity ring buffer of samples; NaN marks a gap (reading missing).
/// </summary>
public sealed class SampleBuffer
{
    private readonly double[] _samples;
    private int _start;

    public SampleBuffer(int capacity)
    {
        _samples = new double[Math.Max(2, capacity)];
    }

    public int Capacity => _samples.Length;
    public int Count { get; private set; }

    /// <summary>Oldest sample is index 0.</summary>
    public double this[int index] => _samples[(_start + index) % _samples.Length];

    public void Add(double value)
    {
        if (Count < _samples.Length)
        {
            _samples[(_start + Count) % _samples.Length] = value;
            Count++;
        }
        else
        {
            _samples[_start] = value;
            _start = (_start + 1) % _samples.Length;
        }
    }
}
