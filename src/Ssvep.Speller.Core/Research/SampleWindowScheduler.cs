using Ssvep.Speller.Core.Signal;
using Ssvep.Speller.Core.Studio;

namespace Ssvep.Speller.Core.Research;

public sealed record ScheduledWindow(long Ordinal, long Continuity, long Anchor, int WarmupSamples, EegWindowSnapshot Window);

/// <summary>Pure sample-index scheduling, shared verbatim by live acquisition and replay.</summary>
public sealed class SampleWindowScheduler
{
    public const string Version = "sample-grid-v1";
    private readonly int channels;
    private readonly int warmup;
    private EegRingBuffer buffer;
    private long? previous;
    private long? anchor;
    private long ordinal;
    private int warmupRemaining;
    private int untilNext;
    public int WindowSamples { get; }
    public int StepSamples { get; }
    public long Continuity { get; private set; }
    public long? Anchor => anchor;
    public int Count => buffer.Count;
    public bool WarmingUp => warmupRemaining > 0;

    public SampleWindowScheduler(int rate, int channels, int warmupSamples = 0)
    {
        WindowSamples = StudioRuntimeProfile.WindowSamples(rate); StepSamples = rate / 5;
        this.channels = channels; warmup = warmupSamples;
        if (channels < 1 || warmup < 0) throw new ArgumentOutOfRangeException(nameof(channels));
        buffer = new(channels, WindowSamples); Reset();
    }

    public void Reset()
    {
        Continuity++; previous = null; anchor = null; ordinal = 0;
        warmupRemaining = warmup; untilNext = WindowSamples; buffer = new(channels, WindowSamples);
    }

    public ScheduledWindow? Push(EegSample sample)
    {
        bool invalid = sample.IsLost || sample.Channels.Length != channels || sample.Channels.Any(v => !double.IsFinite(v));
        if (invalid || previous is long prior && sample.SampleIndex != prior + 1) Reset();
        previous = sample.SampleIndex;
        if (invalid) return null;
        if (warmupRemaining > 0) { warmupRemaining--; return null; }
        anchor ??= sample.SampleIndex;
        buffer.Append(sample);
        if (--untilNext > 0) return null;
        untilNext = StepSamples;
        if (!buffer.TryReadLatestWindowSnapshot(WindowSamples, out var window) || window is null || !window.IsUsable)
            throw new InvalidOperationException("样本调度器生成了不连续窗口。");
        return new(ordinal++, Continuity, anchor.Value, warmup, window);
    }
}
