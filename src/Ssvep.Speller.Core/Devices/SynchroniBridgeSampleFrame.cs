using Ssvep.Speller.Core.Signal;

namespace Ssvep.Speller.Core.Devices;

public sealed record SynchroniBridgeSampleFrame(
    EegSample Sample,
    bool IsLost,
    int SamplingRateHz,
    int ChannelCount,
    IReadOnlyList<int> ImpedanceOhms);
