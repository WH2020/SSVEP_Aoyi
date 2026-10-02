using Ssvep.Speller.Core.Classification;
using Ssvep.Speller.Core.Devices;
using Ssvep.Speller.Core.Signal;
using Ssvep.Speller.Core.Studio;

namespace Ssvep.Speller.Tests;

public sealed class StudioRuntimeTests
{
    private static ClassificationResult Result(string id = "T2") =>
        new(id, .8, "T1", .1, new Dictionary<string, double> { [id] = .8 });

    [Fact]
    public void FirstResultSelectsAndRepeatKeepsOnlyOneTarget()
    {
        var session = new StudioSessionController();
        long run = session.Start();
        Assert.True(session.AcceptResult(run, 624, Result()));
        Assert.Equal("T2", session.SelectedTargetId);
        Assert.True(session.AcceptResult(run, 687, Result()));
        Assert.Equal("T2", session.SelectedTargetId);
        session.AcceptResult(run, 750, Result("T3"));
        Assert.Equal("T3", session.SelectedTargetId);
    }

    [Fact]
    public void PauseResumeIgnoresOldResultAndNeedsNewRun()
    {
        var session = new StudioSessionController();
        long oldRun = session.Start();
        session.AcceptResult(oldRun, 624, Result());
        session.Pause("paused");
        Assert.Null(session.SelectedTargetId);
        Assert.False(session.AcceptResult(oldRun, 687, Result()));
        long nextRun = session.Start();
        Assert.False(session.AcceptResult(oldRun, 750, Result()));
        Assert.Null(session.SelectedTargetId);
        Assert.True(session.AcceptResult(nextRun, 1400, Result("T4")));
        session.Stop();
        Assert.Null(session.SelectedTargetId);
        Assert.False(session.AcceptResult(nextRun, 1500, Result()));
    }

    [Fact]
    public void InvalidResultClearsBorderAndOutOfOrderCannotReplaceNewSelection()
    {
        var session = new StudioSessionController();
        long run = session.Start();
        session.AcceptResult(run, 700, Result());
        Assert.False(session.AcceptResult(run, 650, Result("T3")));
        Assert.Equal("T2", session.SelectedTargetId);
        Assert.False(session.AcceptResult(run, 800, Result("T99")));
        Assert.Null(session.SelectedTargetId);
        session.AcceptResult(run, 900, Result());
        Assert.False(session.AcceptResult(run, 1000, Result() with { BestScore = double.NaN }));
        Assert.Null(session.SelectedTargetId);
    }

    [Theory]
    [InlineData(250, 625)]
    [InlineData(500, 1250)]
    public void WindowUsesActualRateAndExcludesPreStartSamples(int rate, int count)
    {
        var buffer = new StudioWindowBuffer(rate, 8, afterSampleIndex: 100);
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < count + 100; i++)
            buffer.Append(new EegSample(i, now, Enumerable.Repeat(Math.Sin(i * .1), 8).ToArray()));
        Assert.False(buffer.TryRead(now, out _));
        buffer.Append(new EegSample(count + 100, now, Enumerable.Repeat(.3, 8).ToArray()));
        Assert.True(buffer.TryRead(now, out var window));
        Assert.Equal(101, window!.FirstSampleIndex);
        Assert.Equal(count, window.Samples.GetLength(0));
        Assert.False(buffer.TryRead(now.AddSeconds(2), out _));
    }

    [Fact]
    public void DataGapInvalidatesOldResultWithoutPausing()
    {
        var session = new StudioSessionController();
        long oldRun = session.Start();
        session.AcceptResult(oldRun, 624, Result());
        session.InvalidateWindow();
        Assert.Equal(StudioSessionState.Running, session.State);
        Assert.Null(session.SelectedTargetId);
        Assert.False(session.AcceptResult(oldRun, 700, Result()));
        Assert.True(session.AcceptResult(session.RunGeneration, 1400, Result()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LostFramesAndIndexGapsRequireFreshContinuousWindowThenRecover(bool filled)
    {
        var buffer = new StudioWindowBuffer(250, 8, -1);
        var now = DateTimeOffset.UtcNow;
        void Add(int index, bool lost = false) => buffer.Append(new(index, now, Enumerable.Repeat(.2, 8).ToArray(), IsLost: lost));
        for (int i = 0; i < 625; i++) Add(i);
        Assert.True(buffer.TryRead(now, out _));
        long generation = buffer.Generation;
        if (filled) for (int i = 625; i < 635; i++) Add(i, true);
        Add(635);
        Assert.True(buffer.Generation > generation);
        Assert.Equal(1, buffer.Count);
        Assert.False(buffer.TryRead(now, out _));
        for (int i = 636; i < 1259; i++) Add(i);
        Assert.False(buffer.TryRead(now, out _));
        Add(1259);
        Assert.True(buffer.TryRead(now, out var window));
        Assert.Equal(635, window!.FirstSampleIndex);
        Assert.True(window.IsUsable);
    }

    [Fact]
    public void NonFiniteContinuousWindowIsStillRejected()
    {
        var buffer = new StudioWindowBuffer(250, 8, -1);
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < 625; i++)
            buffer.Append(new(i, now, Enumerable.Repeat(i == 300 ? double.NaN : .2, 8).ToArray()));
        Assert.Equal(625, buffer.Count);
        Assert.False(buffer.TryRead(now, out _));
    }

    [Fact]
    public void LostAndNonFiniteSamplesCannotBeClassified()
    {
        var buffer = new StudioWindowBuffer(250, 8, -1);
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < 625; i++)
            buffer.Append(new EegSample(i, now, Enumerable.Repeat(i == 300 ? double.NaN : .2, 8).ToArray(), IsLost: i == 400));
        Assert.False(buffer.TryRead(now, out _));
    }

    [Fact]
    public void ImpedanceMissingStaleAndHighValuesAreReported()
    {
        var now = DateTimeOffset.UtcNow;
        var good = Enumerable.Range(0, 8).Select(i => ChannelImpedance.FromOhms(i, 100_000)).ToArray();
        int[] channels = Enumerable.Range(0, 8).ToArray();
        Assert.True(StudioImpedanceMonitor.Evaluate(good, now, channels, now).IsReady);
        Assert.False(StudioImpedanceMonitor.Evaluate(good, now.AddSeconds(-10), channels, now).IsReady);
        Assert.False(StudioImpedanceMonitor.Evaluate(good, null, channels, now).IsReady);
        good[3] = ChannelImpedance.FromOhms(3, 0);
        Assert.Equal(3, StudioImpedanceMonitor.Evaluate(good, now, channels, now).BlockedChannel);
        good[3] = ChannelImpedance.FromOhms(3, 600_000);
        Assert.False(StudioImpedanceMonitor.Evaluate(good, now, channels, now).IsReady);
    }
}
