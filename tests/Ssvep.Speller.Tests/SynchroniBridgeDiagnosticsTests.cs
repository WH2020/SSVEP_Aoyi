using Ssvep.Speller.Core.Devices;

namespace Ssvep.Speller.Tests;

public sealed class SynchroniBridgeDiagnosticsTests
{
    [Theory]
    [InlineData("MSG|LOST SAMPLE|MAC|D8:71:4D:8D:83:5F|TYPE|16|COUNT|10", true)]
    [InlineData("MSG|LOST SAMPLE|MAC|AA:BB|TYPE|16|COUNT|100", true)]
    [InlineData("MSG|LOST SAMPLE|MAC|AA:BB|TYPE|32|COUNT|10", true)]
    [InlineData("MSG|LOST SAMPLE|MAC|AA:BB|TYPE|16|COUNT|0", false)]
    [InlineData("MSG|LOST SAMPLE|MAC|AA:BB|TYPE|16|COUNT|-10", false)]
    [InlineData("MSG|LOST SAMPLE|MAC|AA:BB|TYPE|16|COUNT|bad", false)]
    [InlineData("MSG|LOST SAMPLE|MAC|AA:BB|TYPE|16|COUNT|10|other fault", false)]
    [InlineData("sensor fault: LOST SAMPLE", false)]
    public void OnlyStructuredSdkLossIsRecoverable(string message, bool expected)
    {
        Assert.Equal(expected, SynchroniSampleLoss.TryParse(SynchroniBridgeDiagnosticKind.SensorError, message, out _));
        Assert.False(SynchroniSampleLoss.TryParse(SynchroniBridgeDiagnosticKind.Error, message, out _));
    }

    [Fact]
    public void LossEventsAccumulateOnceWithoutDoubleCountingFillerFrames()
    {
        var diagnostics = new SynchroniBridgeDiagnostics();
        void Report(long count, int type = 16) => diagnostics.RecordEvent(new(
            SynchroniBridgeDiagnosticKind.SensorError, $"MSG|LOST SAMPLE|MAC|AA:BB|TYPE|{type}|COUNT|{count}", DateTimeOffset.UtcNow));
        Report(10);
        for (int i = 0; i < 10; i++)
        {
            diagnostics.RecordSample(new(new(i, DateTimeOffset.UtcNow, new double[8], IsLost: true), true, 250, 8, []));
            Assert.Equal(1, diagnostics.CreateSnapshot().SampleLossEventCount);
            Assert.Equal(10, diagnostics.CreateSnapshot().ReportedLostSampleCount);
        }
        Report(100); // SDK does not emit filler frames for large gaps.
        Report(10); // Identical message is a new loss event, not a duplicate snapshot.
        Report(50, 32); // Other sensor streams do not enter EEG counts.
        diagnostics.RecordEvent(new(SynchroniBridgeDiagnosticKind.Battery, "87%", DateTimeOffset.UtcNow));
        var snapshot = diagnostics.CreateSnapshot();
        Assert.Equal(3, snapshot.SampleLossEventCount);
        Assert.Equal(120, snapshot.ReportedLostSampleCount);
        Assert.Equal(10, snapshot.LostSampleCount);
    }

    [Fact]
    public void DiagnosticParserReadsErrorStateAndBatteryLines()
    {
        Assert.True(SynchroniBridgeDiagnosticParser.TryParse(
            """{"type":"error","message":"No Synchroni BLE device found"}""",
            out SynchroniBridgeDiagnosticEvent? error));
        Assert.NotNull(error);
        Assert.Equal(SynchroniBridgeDiagnosticKind.Error, error.Kind);
        Assert.Equal("No Synchroni BLE device found", error.Message);

        Assert.True(SynchroniBridgeDiagnosticParser.TryParse(
            """{"type":"state","state":"DeviceStateEx.Ready"}""",
            out SynchroniBridgeDiagnosticEvent? state));
        Assert.NotNull(state);
        Assert.Equal(SynchroniBridgeDiagnosticKind.State, state.Kind);
        Assert.Equal("DeviceStateEx.Ready", state.Message);

        Assert.True(SynchroniBridgeDiagnosticParser.TryParse(
            """{"type":"battery","power":87}""",
            out SynchroniBridgeDiagnosticEvent? battery));
        Assert.NotNull(battery);
        Assert.Equal(SynchroniBridgeDiagnosticKind.Battery, battery.Kind);
        Assert.Equal("87%", battery.Message);
    }

    [Fact]
    public void SampleParserPreservesLostFlagForDiagnostics()
    {
        const string json = """
            {"type":"sample","sampleIndex":9,"timestampUnixMs":1717200000000,"samplingRateHz":250,"channelCount":8,"channels":[1,2,3,4,5,6,7,8],"lost":true}
            """;

        Assert.True(SynchroniBridgeSampleParser.TryParseFrame(json, out SynchroniBridgeSampleFrame? frame, out _));

        Assert.NotNull(frame);
        Assert.True(frame.IsLost);
        Assert.Equal(9, frame.Sample.SampleIndex);
        Assert.Equal(8, frame.Sample.Channels.Length);
    }

    [Fact]
    public void DiagnosticsSnapshotAggregatesMetadataEventsSamplesAndLostSamples()
    {
        var diagnostics = new SynchroniBridgeDiagnostics();

        diagnostics.RecordMetadata(new SynchroniBridgeMetadata("Sync-EEG", "AA:BB", 250, 8));
        diagnostics.RecordEvent(new SynchroniBridgeDiagnosticEvent(
            SynchroniBridgeDiagnosticKind.State,
            "Ready",
            DateTimeOffset.UnixEpoch));
        diagnostics.RecordSample(new SynchroniBridgeSampleFrame(
            new(1, DateTimeOffset.UnixEpoch, [1, 2, 3, 4, 5, 6, 7, 8]),
            IsLost: false,
            SamplingRateHz: 250,
            ChannelCount: 8,
            ImpedanceOhms: []));
        diagnostics.RecordSample(new SynchroniBridgeSampleFrame(
            new(2, DateTimeOffset.UnixEpoch, [1, 2, 3, 4, 5, 6, 7, 8]),
            IsLost: true,
            SamplingRateHz: 250,
            ChannelCount: 8,
            ImpedanceOhms: []));

        SynchroniBridgeDiagnosticsSnapshot snapshot = diagnostics.CreateSnapshot();

        Assert.Equal("Sync-EEG", snapshot.DeviceName);
        Assert.Equal("AA:BB", snapshot.DeviceAddress);
        Assert.Equal(250, snapshot.SamplingRateHz);
        Assert.Equal(8, snapshot.ChannelCount);
        Assert.Equal(2, snapshot.SampleCount);
        Assert.Equal(1, snapshot.LostSampleCount);
        Assert.Equal(2, snapshot.LastSampleIndex);
        Assert.Equal("Ready", snapshot.LastMessage);
    }
}
