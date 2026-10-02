namespace Ssvep.Speller.App;

public sealed class StimulusFrameRenderingEventArgs : EventArgs
{
    public StimulusFrameRenderingEventArgs(
        DateTimeOffset timestamp,
        TimeSpan renderingTime,
        long frameIndex,
        bool hasTimingInterruption = false)
    {
        Timestamp = timestamp;
        RenderingTime = renderingTime;
        FrameIndex = frameIndex;
        HasTimingInterruption = hasTimingInterruption;
    }

    public DateTimeOffset Timestamp { get; }

    public TimeSpan RenderingTime { get; }

    public long FrameIndex { get; }

    public bool HasTimingInterruption { get; }
}
