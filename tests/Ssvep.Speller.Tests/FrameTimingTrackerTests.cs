using Ssvep.Speller.Core.Logging;

namespace Ssvep.Speller.Tests;

public sealed class FrameTimingTrackerTests
{
    [Fact]
    public void TrackerCountsMissedRefreshIntervalsAndResetsPerSession()
    {
        var tracker = new FrameTimingTracker(expectedRefreshRateHz: 60, historyCapacity: 240);
        tracker.RecordInterval(TimeSpan.FromMilliseconds(16.6));
        tracker.RecordInterval(TimeSpan.FromMilliseconds(16.8));
        tracker.RecordInterval(TimeSpan.FromMilliseconds(50.0));

        FrameStats stats = tracker.CreateSnapshot();

        Assert.Equal(2, stats.DroppedFrames);
        Assert.Equal(3, stats.PresentedFrames);
        Assert.InRange(stats.AverageFps, 35, 37);
        Assert.InRange(stats.P95FrameIntervalMs, 49, 51);

        tracker.Reset(expectedRefreshRateHz: 60);
        FrameStats reset = tracker.CreateSnapshot();
        Assert.Equal(0, reset.DroppedFrames);
        Assert.Equal(0, reset.PresentedFrames);
        Assert.Equal(0, reset.AverageFps);
    }

    [Fact]
    public void TrackerRecordsLongPauseAsInterruptionWithoutInflatingDroppedFrames()
    {
        var tracker = new FrameTimingTracker(expectedRefreshRateHz: 60, historyCapacity: 240);

        Assert.True(tracker.RecordInterval(TimeSpan.FromMilliseconds(16.7)));
        Assert.False(tracker.RecordInterval(TimeSpan.FromSeconds(5)));

        FrameStats stats = tracker.CreateSnapshot();

        Assert.Equal(0, stats.DroppedFrames);
        Assert.Equal(1, stats.TimingInterruptions);
        Assert.InRange(stats.AverageFps, 59, 61);
    }
}
