namespace Ssvep.Speller.Core.Signal;

public sealed record EegSampleMetadata(
    long SampleIndex,
    DateTimeOffset Timestamp,
    double? DeviceElapsedMilliseconds,
    bool IsLost);
