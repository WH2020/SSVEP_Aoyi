namespace Ssvep.Speller.Core.Devices;

public enum ImpedanceQuality
{
    Unknown,
    Good,
    Warning,
    Bad
}

public sealed record ChannelImpedance(
    int ChannelIndex,
    int Ohms,
    double KiloOhms,
    ImpedanceQuality Quality)
{
    public static ChannelImpedance FromOhms(int channelIndex, int ohms)
    {
        if (channelIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channelIndex), "Channel index cannot be negative.");
        }

        if (ohms <= 0)
        {
            return new ChannelImpedance(channelIndex, ohms, 0, ImpedanceQuality.Unknown);
        }

        double kiloOhms = ohms / 1000.0;
        ImpedanceQuality quality = kiloOhms switch
        {
            <= 500 => ImpedanceQuality.Good,
            <= 999 => ImpedanceQuality.Warning,
            _ => ImpedanceQuality.Bad
        };

        return new ChannelImpedance(channelIndex, ohms, kiloOhms, quality);
    }
}
