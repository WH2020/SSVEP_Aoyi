namespace Ssvep.Speller.Core.Signal;

public sealed record EegSample(
    long SampleIndex,
    DateTimeOffset Timestamp,
    double[] Channels,
    double? DeviceElapsedMilliseconds = null,
    bool IsLost = false);
