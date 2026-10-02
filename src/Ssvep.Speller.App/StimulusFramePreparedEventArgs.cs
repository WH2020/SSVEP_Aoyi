namespace Ssvep.Speller.App;

public sealed class StimulusFramePreparedEventArgs : EventArgs
{
    public StimulusFramePreparedEventArgs(
        DateTimeOffset timestamp,
        TimeSpan renderingTime,
        long frameIndex,
        double frameIntervalMilliseconds,
        double stimulusElapsedSeconds,
        bool isFlickering,
        bool photodiodePatchIsBright)
    {
        Timestamp = timestamp;
        RenderingTime = renderingTime;
        FrameIndex = frameIndex;
        FrameIntervalMilliseconds = frameIntervalMilliseconds;
        StimulusElapsedSeconds = stimulusElapsedSeconds;
        IsFlickering = isFlickering;
        PhotodiodePatchIsBright = photodiodePatchIsBright;
    }

    public DateTimeOffset Timestamp { get; }

    public TimeSpan RenderingTime { get; }

    public long FrameIndex { get; }

    public double FrameIntervalMilliseconds { get; }

    public double StimulusElapsedSeconds { get; }

    public bool IsFlickering { get; }

    public bool PhotodiodePatchIsBright { get; }
}
