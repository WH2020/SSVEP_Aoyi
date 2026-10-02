namespace Ssvep.Speller.Core.Devices;

public sealed record SynchroniBridgeDiagnosticsSnapshot(
    string DeviceName,
    string DeviceAddress,
    int SamplingRateHz,
    int ChannelCount,
    long SampleCount,
    long LostSampleCount,
    long? LastSampleIndex,
    string LastMessage,
    SynchroniBridgeDiagnosticKind LastMessageKind,
    DateTimeOffset? LastSampleAt,
    DateTimeOffset? LastEventAt,
    bool HasMetadata,
    IReadOnlyList<ChannelImpedance> Impedance)
{
    /// <summary>Host receive time of the last payload that actually contained impedance, not device measurement time.</summary>
    public DateTimeOffset? ImpedanceReceivedAt { get; init; }
    /// <summary>Number of EEG loss diagnostics received during this bridge connection.</summary>
    public long SampleLossEventCount { get; init; }
    /// <summary>Sum of SDK EEG loss COUNT values, including gaps without zero-filled frames.</summary>
    public long ReportedLostSampleCount { get; init; }
}
