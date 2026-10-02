using Ssvep.Speller.Core.Signal;

namespace Ssvep.Speller.Core.Studio;

public sealed class StudioWindowBuffer
{
    private readonly object gate = new();
    private EegRingBuffer buffer;
    private readonly long afterSampleIndex;
    private long? lastSampleIndex;
    private long generation;
    public int RequiredSamples { get; }
    public int Count { get { lock (gate) return buffer.Count; } }
    public long Generation { get { lock (gate) return generation; } }

    public StudioWindowBuffer(int samplingRateHz, int channelCount, long afterSampleIndex)
    {
        RequiredSamples = StudioRuntimeProfile.WindowSamples(samplingRateHz);
        buffer = new EegRingBuffer(channelCount, RequiredSamples * 2);
        this.afterSampleIndex = afterSampleIndex;
    }

    public void Append(EegSample sample)
    {
        if (sample.SampleIndex <= afterSampleIndex) return;
        lock (gate)
        {
            if (sample.IsLost || lastSampleIndex is not null && sample.SampleIndex != lastSampleIndex + 1)
            {
                // SDK fills small gaps with IsLost frames; large gaps only advance the index.
                // Both restart accumulation without requiring the user to resume the experiment.
                if (buffer.Count > 0) buffer = new EegRingBuffer(buffer.ChannelCount, RequiredSamples * 2);
                generation++;
            }
            lastSampleIndex = sample.SampleIndex;
            if (!sample.IsLost) buffer.Append(sample);
        }
    }

    public bool TryRead(DateTimeOffset now, out EegWindowSnapshot? window)
    {
        lock (gate)
        {
            if (!buffer.TryReadLatestWindowSnapshot(RequiredSamples, out window) || window is null)
                return false;
            if (!window.IsUsable || now - window.LastTimestamp > StudioRuntimeProfile.MaximumSampleAge
                || window.LastTimestamp > now.AddSeconds(1)
                || window.Samples.Cast<double>().Any(v => !double.IsFinite(v)))
            {
                window = null;
                return false;
            }
            return true;
        }
    }
}
