using Ssvep.Speller.Core.Devices;
using Ssvep.Speller.Core.Signal;

namespace Ssvep.Speller.Tests;

public sealed class SynchroniBridgeParserTests
{
    [Fact]
    public void ParserKeepsDeviceElapsedTimeSeparateFromHostReceiveTime()
    {
        const string json = """
            {"type":"sample","sampleIndex":42,"deviceElapsedMs":168.0,"hostReceivedUnixMs":1717200000123,"samplingRateHz":250,"channelCount":8,"channels":[1,2,3,4,5,6,7,8],"lost":true,"impedance":[11,12,13,14,15,16,17,18]}
            """;

        Assert.True(SynchroniBridgeSampleParser.TryParseFrame(
            json,
            out SynchroniBridgeSampleFrame? frame,
            out SynchroniBridgeMetadata? metadata));

        Assert.Null(metadata);
        Assert.NotNull(frame);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1717200000123), frame.Sample.Timestamp);
        Assert.Equal(168.0, frame.Sample.DeviceElapsedMilliseconds);
        Assert.True(frame.Sample.IsLost);
        Assert.True(frame.IsLost);
    }

    [Fact]
    public void ParserDoesNotInterpretLegacyRelativeMillisecondsAsUnixTime()
    {
        DateTimeOffset before = DateTimeOffset.UtcNow;
        const string json = """
            {"type":"sample","sampleIndex":42,"timestampUnixMs":168,"samplingRateHz":250,"channelCount":8,"channels":[1,2,3,4,5,6,7,8],"lost":false}
            """;

        Assert.True(SynchroniBridgeSampleParser.TryParse(json, out EegSample? sample, out _));

        Assert.NotNull(sample);
        Assert.InRange(sample.Timestamp, before, DateTimeOffset.UtcNow);
        Assert.Equal(168.0, sample.DeviceElapsedMilliseconds);
    }

    [Fact]
    public void ParserAllowsMissingDeviceElapsedTimestampOnLostPackage()
    {
        const string json = """
            {"type":"sample","sampleIndex":42,"deviceElapsedMs":null,"hostReceivedUnixMs":1717200000123,"samplingRateHz":250,"channelCount":8,"channels":[0,0,0,0,0,0,0,0],"lost":true}
            """;

        Assert.True(SynchroniBridgeSampleParser.TryParse(json, out EegSample? sample, out _));
        Assert.NotNull(sample);
        Assert.Null(sample.DeviceElapsedMilliseconds);
        Assert.True(sample.IsLost);
    }

    [Fact]
    public void ParserReadsSampleJsonLineIntoEegSample()
    {
        const string json = """
            {"type":"sample","sampleIndex":42,"timestampUnixMs":1717200000123,"samplingRateHz":250,"channelCount":8,"channels":[1.1,2.2,3.3,4.4,5.5,6.6,7.7,8.8],"lost":false,"impedance":[11,12,13,14,15,16,17,18]}
            """;

        Assert.True(SynchroniBridgeSampleParser.TryParse(json, out EegSample? sample, out SynchroniBridgeMetadata? metadata));

        Assert.Null(metadata);
        Assert.NotNull(sample);
        Assert.Equal(42, sample.SampleIndex);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1717200000123), sample.Timestamp);
        Assert.Equal(8, sample.Channels.Length);
        Assert.Equal(1.1, sample.Channels[0], 6);
        Assert.Equal(8.8, sample.Channels[7], 6);
    }

    [Fact]
    public void ParserReadsMetadataJsonLine()
    {
        const string json = """
            {"type":"meta","deviceName":"Sync-EEG","deviceAddress":"AA:BB","samplingRateHz":250,"channelCount":8}
            """;

        Assert.True(SynchroniBridgeSampleParser.TryParse(json, out EegSample? sample, out SynchroniBridgeMetadata? metadata));

        Assert.Null(sample);
        Assert.NotNull(metadata);
        Assert.Equal("Sync-EEG", metadata.DeviceName);
        Assert.Equal("AA:BB", metadata.DeviceAddress);
        Assert.Equal(250, metadata.SamplingRateHz);
        Assert.Equal(8, metadata.ChannelCount);
    }

    [Fact]
    public void ParserRejectsNonSampleLogLinesAndMalformedSamples()
    {
        Assert.False(SynchroniBridgeSampleParser.TryParse("scan started", out _, out _));
        Assert.False(SynchroniBridgeSampleParser.TryParse("""{"type":"sample","channels":[]}""", out _, out _));
    }
}
