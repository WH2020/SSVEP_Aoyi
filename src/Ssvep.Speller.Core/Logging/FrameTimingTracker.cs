namespace Ssvep.Speller.Core.Logging;

public sealed class FrameTimingTracker
{
    private readonly int historyCapacity;
    private readonly TimeSpan maximumContinuousInterval;
    private readonly Queue<double> intervals = new();
    private double expectedRefreshRateHz;
    private int droppedFrames;
    private int timingInterruptions;
    private long presentedFrames;

    public FrameTimingTracker(
        double expectedRefreshRateHz,
        int historyCapacity = 240,
        TimeSpan? maximumContinuousInterval = null)
    {
        if (historyCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(historyCapacity));
        }

        this.historyCapacity = historyCapacity;
        this.maximumContinuousInterval = maximumContinuousInterval ?? TimeSpan.FromMilliseconds(250);
        if (this.maximumContinuousInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumContinuousInterval));
        }

        Reset(expectedRefreshRateHz);
    }

    public void Reset(double expectedRefreshRateHz)
    {
        if (expectedRefreshRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRefreshRateHz));
        }

        this.expectedRefreshRateHz = expectedRefreshRateHz;
        intervals.Clear();
        droppedFrames = 0;
        timingInterruptions = 0;
        presentedFrames = 0;
    }

    public bool RecordInterval(TimeSpan interval)
    {
        double intervalMs = interval.TotalMilliseconds;
        if (intervalMs <= 0)
        {
            return true;
        }

        presentedFrames++;
        if (interval > maximumContinuousInterval)
        {
            timingInterruptions++;
            return false;
        }

        intervals.Enqueue(intervalMs);
        if (intervals.Count > historyCapacity)
        {
            intervals.Dequeue();
        }

        double expectedIntervalMs = 1000.0 / expectedRefreshRateHz;
        int elapsedRefreshIntervals = Math.Max(1, (int)Math.Round(intervalMs / expectedIntervalMs));
        droppedFrames += Math.Max(0, elapsedRefreshIntervals - 1);
        return true;
    }

    public FrameStats CreateSnapshot()
    {
        if (intervals.Count == 0)
        {
            return new FrameStats(
                0,
                droppedFrames,
                PresentedFrames: presentedFrames,
                ExpectedRefreshRateHz: expectedRefreshRateHz,
                TimingInterruptions: timingInterruptions);
        }

        double[] ordered = intervals.Order().ToArray();
        double averageInterval = ordered.Average();
        double variance = ordered.Select(value => Math.Pow(value - averageInterval, 2)).Average();
        return new FrameStats(
            AverageFps: 1000.0 / averageInterval,
            DroppedFrames: droppedFrames,
            MedianFrameIntervalMs: Quantile(ordered, 0.50),
            P95FrameIntervalMs: Quantile(ordered, 0.95),
            P99FrameIntervalMs: Quantile(ordered, 0.99),
            StandardDeviationMs: Math.Sqrt(variance),
            PresentedFrames: presentedFrames,
            ExpectedRefreshRateHz: expectedRefreshRateHz,
            TimingInterruptions: timingInterruptions);
    }

    private static double Quantile(IReadOnlyList<double> ordered, double probability)
    {
        int index = Math.Clamp((int)Math.Ceiling(probability * ordered.Count) - 1, 0, ordered.Count - 1);
        return ordered[index];
    }
}
