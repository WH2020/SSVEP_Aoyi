namespace Ssvep.Speller.Core.Devices;

public sealed record SynchroniDiscoveredDevice(string Name, string Address)
{
    public string DisplayName => $"{Name} ({Address})";
}
