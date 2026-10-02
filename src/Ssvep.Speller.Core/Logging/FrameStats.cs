namespace Ssvep.Speller.Core.Logging;

public sealed record FrameStats(
    double AverageFps,
    int DroppedFrames,
    double MedianFrameIntervalMs = 0,
    double P95FrameIntervalMs = 0,
    double P99FrameIntervalMs = 0,
    double StandardDeviationMs = 0,
    long PresentedFrames = 0,
    double ExpectedRefreshRateHz = 0,
    int TimingInterruptions = 0);
