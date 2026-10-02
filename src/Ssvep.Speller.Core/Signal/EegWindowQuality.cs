namespace Ssvep.Speller.Core.Signal;

public sealed record EegWindowQuality(
    long FirstSampleIndex,
    long LastSampleIndex,
    bool ContainsLostSamples,
    bool IsContiguous)
{
    public bool IsUsable => !ContainsLostSamples && IsContiguous;
}
