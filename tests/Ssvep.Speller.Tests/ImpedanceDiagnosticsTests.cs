using Ssvep.Speller.Core.Devices;

namespace Ssvep.Speller.Tests;

public sealed class ImpedanceDiagnosticsTests
{
    [Fact]
    public void SampleParserReadsImpedanceArray()
    {
        const string json = """
            {"type":"sample","sampleIndex":3,"timestampUnixMs":1717200000000,"samplingRateHz":250,"channelCount":4,"channels":[1,2,3,4],"lost":false,"impedance":[100000,600000,1200000,0]}
            """;

        Assert.True(SynchroniBridgeSampleParser.TryParseFrame(json, out SynchroniBridgeSampleFrame? frame, out _));

        Assert.NotNull(frame);
        Assert.Equal([100000, 600000, 1200000, 0], frame.ImpedanceOhms);
    }

    [Fact]
    public void ImpedanceQualityUsesSdkDemoThresholds()
    {
        Assert.Equal(ImpedanceQuality.Good, ChannelImpedance.FromOhms(0, 100_000).Quality);
        Assert.Equal(ImpedanceQuality.Warning, ChannelImpedance.FromOhms(1, 600_000).Quality);
        Assert.Equal(ImpedanceQuality.Bad, ChannelImpedance.FromOhms(2, 1_200_000).Quality);
        Assert.Equal(ImpedanceQuality.Unknown, ChannelImpedance.FromOhms(3, 0).Quality);
    }

    [Fact]
    public void DiagnosticsSnapshotIncludesLatestImpedanceValues()
    {
        var diagnostics = new SynchroniBridgeDiagnostics();
        diagnostics.RecordSample(new SynchroniBridgeSampleFrame(
            new(10, DateTimeOffset.UnixEpoch, [1, 2, 3, 4]),
            IsLost: false,
            SamplingRateHz: 250,
            ChannelCount: 4,
            ImpedanceOhms: [100_000, 600_000, 1_200_000, 0]));

        SynchroniBridgeDiagnosticsSnapshot snapshot = diagnostics.CreateSnapshot();

        Assert.Equal(4, snapshot.Impedance.Count);
        Assert.Equal(100.0, snapshot.Impedance[0].KiloOhms, 3);
        Assert.Equal(ImpedanceQuality.Good, snapshot.Impedance[0].Quality);
        Assert.Equal(ImpedanceQuality.Warning, snapshot.Impedance[1].Quality);
        Assert.Equal(ImpedanceQuality.Bad, snapshot.Impedance[2].Quality);
        Assert.Equal(ImpedanceQuality.Unknown, snapshot.Impedance[3].Quality);
    }
}
