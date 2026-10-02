namespace Ssvep.Speller.Core.Devices;

public sealed record SynchroniBridgeMetadata(
    string DeviceName,
    string DeviceAddress,
    int SamplingRateHz,
    int ChannelCount);
