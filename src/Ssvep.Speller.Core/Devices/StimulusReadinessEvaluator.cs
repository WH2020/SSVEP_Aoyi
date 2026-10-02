namespace Ssvep.Speller.Core.Devices;

public enum StimulusReadinessState
{
    Ready,
    PowerSaving,
    NotConnected,
    NoSamples,
    StaleSamples,
    InsufficientBuffer,
    InvalidSignalWindow,
    InvalidMontage,
    MissingImpedance,
    ImpedanceTooHigh
}

public sealed record StimulusReadinessResult(
    bool CanCalculate,
    StimulusReadinessState State,
    string Message,
    int? BlockedChannelIndex = null,
    double? BlockedChannelImpedanceKiloOhms = null);

public static class StimulusReadinessEvaluator
{
    private const int RequiredSelectedChannelCount = 8;

    public static StimulusReadinessResult Evaluate(
        SynchroniBridgeDiagnosticsSnapshot snapshot,
        IReadOnlyList<int> selectedChannelIndices,
        double maxSelectedImpedanceKiloOhms,
        DateTimeOffset? now = null,
        TimeSpan? maxSampleAge = null,
        int? bufferedSampleCount = null,
        int requiredBufferedSampleCount = 0,
        bool? isLatestWindowUsable = null,
        bool isOnAcPower = true)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(selectedChannelIndices);
        if (maxSelectedImpedanceKiloOhms <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSelectedImpedanceKiloOhms), "阻抗阈值必须大于 0。");
        }

        if (!isOnAcPower)
        {
            return new StimulusReadinessResult(
                CanCalculate: false,
                StimulusReadinessState.PowerSaving,
                "请连接电脑电源后再开始刺激，电池节能会降低显示刷新稳定性。");
        }

        if (selectedChannelIndices.Count != RequiredSelectedChannelCount ||
            selectedChannelIndices.Distinct().Count() != RequiredSelectedChannelCount)
        {
            return new StimulusReadinessResult(
                CanCalculate: false,
                StimulusReadinessState.InvalidMontage,
                $"请选择 {RequiredSelectedChannelCount} 个不重复的通道。");
        }

        if (!snapshot.HasMetadata || snapshot.ChannelCount < RequiredSelectedChannelCount)
        {
            return new StimulusReadinessResult(
                CanCalculate: false,
                StimulusReadinessState.NotConnected,
                "请连接至少 8 通道的 Synchroni 脑电设备。");
        }

        if (snapshot.SampleCount <= 0 || snapshot.LastSampleIndex is null)
        {
            return new StimulusReadinessResult(
                CanCalculate: false,
                StimulusReadinessState.NoSamples,
                "正在等待实时脑电样本。");
        }

        TimeSpan allowedSampleAge = maxSampleAge ?? TimeSpan.FromMilliseconds(500);
        if (now is not null &&
            (snapshot.LastSampleAt is null || now.Value - snapshot.LastSampleAt.Value > allowedSampleAge))
        {
            return new StimulusReadinessResult(
                CanCalculate: false,
                StimulusReadinessState.StaleSamples,
                $"最新脑电样本已超过 {allowedSampleAge.TotalMilliseconds:0} 毫秒未更新。");
        }

        if (requiredBufferedSampleCount > 0 && bufferedSampleCount.GetValueOrDefault() < requiredBufferedSampleCount)
        {
            return new StimulusReadinessResult(
                CanCalculate: false,
                StimulusReadinessState.InsufficientBuffer,
                $"正在缓冲脑电样本：{bufferedSampleCount.GetValueOrDefault()} / {requiredBufferedSampleCount}。");
        }

        if (isLatestWindowUsable == false)
        {
            return new StimulusReadinessResult(
                CanCalculate: false,
                StimulusReadinessState.InvalidSignalWindow,
                "最新脑电窗口包含丢包或样本断号。");
        }

        foreach (int channelIndex in selectedChannelIndices)
        {
            if (channelIndex < 0 || channelIndex >= snapshot.ChannelCount)
            {
                return new StimulusReadinessResult(
                    CanCalculate: false,
                    StimulusReadinessState.InvalidMontage,
                    $"通道 {channelIndex + 1} 不在设备通道范围内。",
                    channelIndex);
            }

            ChannelImpedance? channel = snapshot.Impedance.FirstOrDefault(item => item.ChannelIndex == channelIndex);
            if (channel is null || channel.Quality == ImpedanceQuality.Unknown || channel.KiloOhms <= 0)
            {
                return new StimulusReadinessResult(
                    CanCalculate: false,
                    StimulusReadinessState.MissingImpedance,
                    $"正在等待通道 {channelIndex + 1} 的阻抗数据。",
                    channelIndex);
            }

            if (channel.KiloOhms > maxSelectedImpedanceKiloOhms)
            {
                return new StimulusReadinessResult(
                    CanCalculate: false,
                    StimulusReadinessState.ImpedanceTooHigh,
                    $"通道 {channelIndex + 1} 阻抗 {channel.KiloOhms:0} 千欧超过阈值 {maxSelectedImpedanceKiloOhms:0} 千欧。",
                    channelIndex,
                    channel.KiloOhms);
            }
        }

        return new StimulusReadinessResult(
            CanCalculate: true,
            StimulusReadinessState.Ready,
            "设备数据与所选 8 通道阻抗已就绪。");
    }
}
