using Ssvep.Speller.Core.Devices;

namespace Ssvep.Speller.Core.Studio;

/// <summary>Advisory contact assessment for display and recording; it does not gate experiment execution.</summary>
public sealed record StudioImpedanceStatus(bool IsReady, string Message, int? BlockedChannel = null);

public static class StudioImpedanceMonitor
{
    public static StudioImpedanceStatus Evaluate(IReadOnlyList<ChannelImpedance> values,
        DateTimeOffset? receivedAt, IReadOnlyList<int> selectedChannels, DateTimeOffset now)
    {
        if (selectedChannels.Count != 8 || selectedChannels.Distinct().Count() != 8 || selectedChannels.Any(i => i < 0))
            return new(false, "请选择 8 个用于识别的通道");
        if (receivedAt is null || now - receivedAt > StudioRuntimeProfile.MaximumImpedanceAge || receivedAt > now.AddSeconds(1))
            return new(false, "阻抗数据待更新，请检查设备连接");
        foreach (int channel in selectedChannels)
        {
            var value = values.FirstOrDefault(v => v.ChannelIndex == channel);
            if (value is null || value.Quality == ImpedanceQuality.Unknown || !double.IsFinite(value.KiloOhms) || value.KiloOhms <= 0)
                return new(false, $"通道 {channel + 1} 阻抗未知，请检查佩戴", channel);
            if (value.KiloOhms > StudioRuntimeProfile.MaximumImpedanceKiloOhms)
                return new(false, $"请调整通道 {channel + 1} 的电极接触", channel);
        }
        return new(true, "所选通道的阻抗检查通过");
    }
}
