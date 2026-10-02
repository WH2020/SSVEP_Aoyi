using Ssvep.Speller.Core.Devices;

namespace Ssvep.Speller.Tests;

public sealed class StimulusReadinessEvaluatorTests
{
    [Fact]
    public void EvaluateBlocksWhenComputerIsRunningOnBattery()
    {
        SynchroniBridgeDiagnosticsSnapshot snapshot = CreateSnapshot(
            hasMetadata: true,
            sampleCount: 625,
            impedanceOhms: [100_000, 120_000, 110_000, 130_000, 115_000, 125_000, 105_000, 135_000]);

        StimulusReadinessResult result = StimulusReadinessEvaluator.Evaluate(
            snapshot,
            selectedChannelIndices: [0, 1, 2, 3, 4, 5, 6, 7],
            maxSelectedImpedanceKiloOhms: 500,
            isOnAcPower: false);

        Assert.False(result.CanCalculate);
        Assert.Equal(StimulusReadinessState.PowerSaving, result.State);
    }

    [Fact]
    public void EvaluateBlocksWhenLatestSampleIsStale()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch.AddSeconds(10);
        SynchroniBridgeDiagnosticsSnapshot snapshot = CreateSnapshot(
            hasMetadata: true,
            sampleCount: 625,
            impedanceOhms: [100_000, 120_000, 110_000, 130_000, 115_000, 125_000, 105_000, 135_000]) with
        {
            LastSampleAt = now - TimeSpan.FromSeconds(1)
        };

        StimulusReadinessResult result = StimulusReadinessEvaluator.Evaluate(
            snapshot,
            selectedChannelIndices: [0, 1, 2, 3, 4, 5, 6, 7],
            maxSelectedImpedanceKiloOhms: 500,
            now,
            maxSampleAge: TimeSpan.FromMilliseconds(500),
            bufferedSampleCount: 625,
            requiredBufferedSampleCount: 625);

        Assert.False(result.CanCalculate);
        Assert.Equal(StimulusReadinessState.StaleSamples, result.State);
    }

    [Fact]
    public void EvaluateBlocksUntilClassificationWindowIsBuffered()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch.AddSeconds(10);
        SynchroniBridgeDiagnosticsSnapshot snapshot = CreateSnapshot(
            hasMetadata: true,
            sampleCount: 200,
            impedanceOhms: [100_000, 120_000, 110_000, 130_000, 115_000, 125_000, 105_000, 135_000]) with
        {
            LastSampleAt = now
        };

        StimulusReadinessResult result = StimulusReadinessEvaluator.Evaluate(
            snapshot,
            selectedChannelIndices: [0, 1, 2, 3, 4, 5, 6, 7],
            maxSelectedImpedanceKiloOhms: 500,
            now,
            maxSampleAge: TimeSpan.FromMilliseconds(500),
            bufferedSampleCount: 200,
            requiredBufferedSampleCount: 625);

        Assert.False(result.CanCalculate);
        Assert.Equal(StimulusReadinessState.InsufficientBuffer, result.State);
    }

    [Fact]
    public void EvaluateBlocksWhenLatestWindowContainsDataLoss()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch.AddSeconds(10);
        SynchroniBridgeDiagnosticsSnapshot snapshot = CreateSnapshot(
            hasMetadata: true,
            sampleCount: 625,
            impedanceOhms: [100_000, 120_000, 110_000, 130_000, 115_000, 125_000, 105_000, 135_000]) with
        {
            LastSampleAt = now
        };

        StimulusReadinessResult result = StimulusReadinessEvaluator.Evaluate(
            snapshot,
            selectedChannelIndices: [0, 1, 2, 3, 4, 5, 6, 7],
            maxSelectedImpedanceKiloOhms: 500,
            now,
            maxSampleAge: TimeSpan.FromMilliseconds(500),
            bufferedSampleCount: 625,
            requiredBufferedSampleCount: 625,
            isLatestWindowUsable: false);

        Assert.False(result.CanCalculate);
        Assert.Equal(StimulusReadinessState.InvalidSignalWindow, result.State);
    }

    [Fact]
    public void EvaluateBlocksWhenDeviceHasNoSamples()
    {
        SynchroniBridgeDiagnosticsSnapshot snapshot = CreateSnapshot(
            hasMetadata: true,
            sampleCount: 0,
            impedanceOhms: [100_000, 120_000, 110_000, 130_000, 115_000, 125_000, 105_000, 135_000]);

        StimulusReadinessResult result = StimulusReadinessEvaluator.Evaluate(
            snapshot,
            selectedChannelIndices: [0, 1, 2, 3, 4, 5, 6, 7],
            maxSelectedImpedanceKiloOhms: 500);

        Assert.False(result.CanCalculate);
        Assert.Equal(StimulusReadinessState.NoSamples, result.State);
    }

    [Fact]
    public void EvaluateAllowsCalculationWhenFirstEightChannelsHaveLowImpedance()
    {
        SynchroniBridgeDiagnosticsSnapshot snapshot = CreateSnapshot(
            hasMetadata: true,
            sampleCount: 42,
            impedanceOhms: [100_000, 120_000, 110_000, 130_000, 115_000, 125_000, 105_000, 135_000]);

        StimulusReadinessResult result = StimulusReadinessEvaluator.Evaluate(
            snapshot,
            selectedChannelIndices: [0, 1, 2, 3, 4, 5, 6, 7],
            maxSelectedImpedanceKiloOhms: 500);

        Assert.True(result.CanCalculate);
        Assert.Equal(StimulusReadinessState.Ready, result.State);
    }

    [Fact]
    public void EvaluateBlocksWhenAnyFirstEightChannelExceedsImpedanceThreshold()
    {
        SynchroniBridgeDiagnosticsSnapshot snapshot = CreateSnapshot(
            hasMetadata: true,
            sampleCount: 42,
            impedanceOhms: [100_000, 120_000, 610_000, 130_000, 115_000, 125_000, 105_000, 135_000]);

        StimulusReadinessResult result = StimulusReadinessEvaluator.Evaluate(
            snapshot,
            selectedChannelIndices: [0, 1, 2, 3, 4, 5, 6, 7],
            maxSelectedImpedanceKiloOhms: 500);

        Assert.False(result.CanCalculate);
        Assert.Equal(StimulusReadinessState.ImpedanceTooHigh, result.State);
        Assert.Equal(2, result.BlockedChannelIndex);
    }

    [Fact]
    public void EvaluateAllowsCalculationWhenSelectedChannelsHaveLowImpedance()
    {
        SynchroniBridgeDiagnosticsSnapshot snapshot = CreateSnapshot(
            hasMetadata: true,
            sampleCount: 42,
            impedanceOhms:
            [
                1_200_000, 1_200_000, 1_200_000, 1_200_000,
                1_200_000, 1_200_000, 1_200_000, 1_200_000,
                100_000, 120_000, 110_000, 130_000,
                115_000, 125_000, 105_000, 135_000
            ]);

        StimulusReadinessResult result = StimulusReadinessEvaluator.Evaluate(
            snapshot,
            selectedChannelIndices: [8, 9, 10, 11, 12, 13, 14, 15],
            maxSelectedImpedanceKiloOhms: 500);

        Assert.True(result.CanCalculate);
        Assert.Equal(StimulusReadinessState.Ready, result.State);
    }

    [Fact]
    public void EvaluateBlocksWhenSelectedChannelExceedsImpedanceThreshold()
    {
        SynchroniBridgeDiagnosticsSnapshot snapshot = CreateSnapshot(
            hasMetadata: true,
            sampleCount: 42,
            impedanceOhms:
            [
                100_000, 120_000, 110_000, 130_000,
                115_000, 125_000, 105_000, 135_000,
                100_000, 120_000, 610_000, 130_000,
                115_000, 125_000, 105_000, 135_000
            ]);

        StimulusReadinessResult result = StimulusReadinessEvaluator.Evaluate(
            snapshot,
            selectedChannelIndices: [8, 9, 10, 11, 12, 13, 14, 15],
            maxSelectedImpedanceKiloOhms: 500);

        Assert.False(result.CanCalculate);
        Assert.Equal(StimulusReadinessState.ImpedanceTooHigh, result.State);
        Assert.Equal(10, result.BlockedChannelIndex);
    }

    private static SynchroniBridgeDiagnosticsSnapshot CreateSnapshot(
        bool hasMetadata,
        long sampleCount,
        IReadOnlyList<int> impedanceOhms)
    {
        IReadOnlyList<ChannelImpedance> impedance = impedanceOhms
            .Select((ohms, index) => ChannelImpedance.FromOhms(index, ohms))
            .ToArray();

        return new SynchroniBridgeDiagnosticsSnapshot(
            DeviceName: hasMetadata ? "OB6000C" : string.Empty,
            DeviceAddress: hasMetadata ? "AA:BB" : string.Empty,
            SamplingRateHz: 250,
            ChannelCount: impedanceOhms.Count,
            SampleCount: sampleCount,
            LostSampleCount: 0,
            LastSampleIndex: sampleCount > 0 ? sampleCount - 1 : null,
            LastMessage: "-",
            LastMessageKind: SynchroniBridgeDiagnosticKind.Info,
            LastSampleAt: sampleCount > 0 ? DateTimeOffset.UnixEpoch : null,
            LastEventAt: DateTimeOffset.UnixEpoch,
            HasMetadata: hasMetadata,
            Impedance: impedance);
    }
}
