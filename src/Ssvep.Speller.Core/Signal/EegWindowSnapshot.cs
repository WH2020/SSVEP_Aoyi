namespace Ssvep.Speller.Core.Signal;

public sealed record EegWindowSnapshot(
    double[,] Samples,
    long FirstSampleIndex,
    long LastSampleIndex,
    DateTimeOffset LastTimestamp,
    bool ContainsLostSamples,
    bool IsContiguous)
{
    public bool IsUsable => !ContainsLostSamples && IsContiguous;
}
