using Ssvep.Speller.Core.Signal;

namespace Ssvep.Speller.Core.Research;

public sealed record SampleBatch(long BatchId, string ClockEpoch, int SamplingRateHz,
    IReadOnlyList<EegSample> Samples, double SourceReceivedSeconds, double HostReceivedSeconds,
    string SourceClock = "host-stopwatch-seconds", string? DomainModelId = null,
    double? MappedHostSeconds = null, string DeviceTimeSource = "derived-from-sample-index");

public enum BridgeHealthKind { Starting, Healthy, BridgeUnresponsive, DataStalled, DeviceDisconnected, BridgeExited, BridgeIpcFailure }
public sealed record BridgeHeartbeat(int ProtocolVersion, string BridgeInstanceId, long Sequence, double PythonSeconds,
    double? LastCallbackSeconds, long? LastSampleIndex, string SdkState, double StateChangedSeconds);
public sealed record BridgeHealthSnapshot(BridgeHealthKind Kind, string Message, double? HeartbeatAge, double? SampleAge, string SdkState);

public static class BridgeHealthPolicy
{
    public static BridgeHealthSnapshot Evaluate(double now, double started, bool exited, bool controlClosed,
        double? heartbeat, double? sample, string sdkState, bool heartbeatRequired = true)
    {
        double? ha=heartbeat is double h ? Math.Max(0,now-h) : null, sa=sample is double s ? Math.Max(0,now-s) : null;
        if(exited) return new(BridgeHealthKind.BridgeExited,"桥接进程已退出",ha,sa,"unknown");
        if(controlClosed) return new(BridgeHealthKind.BridgeIpcFailure,"桥接控制通道已关闭",ha,sa,"unknown");
        if(sdkState is "disconnected" or "DeviceStateEx.Disconnected") return new(BridgeHealthKind.DeviceDisconnected,"设备明确报告断连",ha,sa,sdkState);
        if(heartbeatRequired && (ha > 3 || heartbeat is null && now-started > 15)) return new(BridgeHealthKind.BridgeUnresponsive,"桥接无响应，设备状态未知",ha,sa,"unknown/stale");
        if(sample is null) return new(BridgeHealthKind.Starting,"等待设备数据",ha,sa,sdkState);
        if(sa > .5) return new(BridgeHealthKind.DataStalled,"设备无有效新数据",ha,sa,sdkState);
        return new(BridgeHealthKind.Healthy,"采集正常",ha,sa,sdkState);
    }
}
