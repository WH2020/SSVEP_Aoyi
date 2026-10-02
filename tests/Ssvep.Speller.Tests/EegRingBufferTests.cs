using Ssvep.Speller.Core.Signal;

namespace Ssvep.Speller.Tests;

public sealed class EegRingBufferTests
{
    [Fact]
    public void WindowSnapshotReportsLostAndNonContiguousSamples()
    {
        var buffer = new EegRingBuffer(channelCount: 2, capacitySamples: 10);
        buffer.Append(new EegSample(10, DateTimeOffset.UnixEpoch, [1, 2]));
        buffer.Append(new EegSample(11, DateTimeOffset.UnixEpoch.AddMilliseconds(4), [3, 4], IsLost: true));
        buffer.Append(new EegSample(13, DateTimeOffset.UnixEpoch.AddMilliseconds(8), [5, 6]));

        Assert.True(buffer.TryReadLatestWindowSnapshot(3, out EegWindowSnapshot? snapshot));

        Assert.NotNull(snapshot);
        Assert.Equal(10, snapshot.FirstSampleIndex);
        Assert.Equal(13, snapshot.LastSampleIndex);
        Assert.True(snapshot.ContainsLostSamples);
        Assert.False(snapshot.IsContiguous);
        Assert.False(snapshot.IsUsable);
        Assert.True(buffer.TryGetLatestWindowQuality(3, out EegWindowQuality quality));
        Assert.False(quality.IsUsable);
    }

    [Fact]
    public void LatestSampleMetadataMatchesMostRecentAppend()
    {
        var buffer = new EegRingBuffer(channelCount: 2, capacitySamples: 10);
        DateTimeOffset timestamp = DateTimeOffset.UnixEpoch.AddSeconds(2);
        buffer.Append(new EegSample(25, timestamp, [1, 2], DeviceElapsedMilliseconds: 100, IsLost: false));

        Assert.True(buffer.TryGetLatestSampleMetadata(out EegSampleMetadata metadata));
        Assert.Equal(25, metadata.SampleIndex);
        Assert.Equal(timestamp, metadata.Timestamp);
        Assert.False(metadata.IsLost);
    }

    [Fact]
    public void TryReadLatestWindowReturnsNewestSamplesInChronologicalOrder()
    {
        var buffer = new EegRingBuffer(channelCount: 2, capacitySamples: 10);

        for (int i = 0; i < 12; i++)
        {
            buffer.Append(new EegSample(i, DateTimeOffset.UnixEpoch.AddMilliseconds(i), [i, 100 + i]));
        }

        Assert.True(buffer.TryReadLatestWindow(sampleCount: 5, out double[,] window));
        Assert.Equal(5, window.GetLength(0));
        Assert.Equal(2, window.GetLength(1));
        Assert.Equal(7.0, window[0, 0]);
        Assert.Equal(11.0, window[4, 0]);
        Assert.Equal(107.0, window[0, 1]);
        Assert.Equal(111.0, window[4, 1]);
    }

    [Fact]
    public void TryReadLatestWindowRejectsRequestsBeforeEnoughSamplesArrive()
    {
        var buffer = new EegRingBuffer(channelCount: 8, capacitySamples: 625);
        buffer.Append(new EegSample(0, DateTimeOffset.UnixEpoch, [0, 0, 0, 0, 0, 0, 0, 0]));

        Assert.False(buffer.TryReadLatestWindow(sampleCount: 2, out _));
    }
}
