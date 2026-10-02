using Ssvep.Speller.Core.Devices;

namespace Ssvep.Speller.Core.Logging;

public sealed record CollectionAcquisitionInfo(
    string SourceMode,
    string DeviceName,
    string DeviceAddress,
    int SamplingRateHz,
    int InputChannelCount,
    IReadOnlyList<int> SelectedChannelIndices,
    double ImpedanceThresholdKiloOhms,
    IReadOnlyList<ChannelImpedance> Impedance,
    bool Enable50HzNotch,
    bool EnableHighPass,
    bool EnableLowPass);
