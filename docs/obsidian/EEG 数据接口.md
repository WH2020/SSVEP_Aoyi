# EEG 数据接口

#device #signal #code

## EegSample

```csharp
public sealed record EegSample(
    long SampleIndex,
    DateTimeOffset Timestamp,
    double[] Channels
);
```

`Channels` 是一帧 EEG 样本的多通道值。Synchroni 原始样本可能是 32 通道：采集 CSV 保存全部原始通道，实时算法则通过 `ChannelMontage` 投影为 8 通道。

## IEegStream

```csharp
public interface IEegStream : IAsyncDisposable
{
    int SamplingRateHz { get; }
    int ChannelCount { get; }
    IAsyncEnumerable<EegSample> ReadSamplesAsync(CancellationToken cancellationToken);
}
```

当前实现：

- `SimulatedEegStream`
- `SynchroniBridgeEegStream`
- `BleEegStream` 空壳，用于后续直接 BLE/GATT 接入

## SimulatedEegStream

模拟流按采样率用 `PeriodicTimer` 产出单样本。每个样本来自 `SyntheticSsvepSignal.GenerateWindow(sampleCount: 1)`。

## ChannelMontage

`ChannelMontage` 把输入通道投影为固定 8 通道：

```text
raw channels[32] + selected indices[8] -> projected channels[8]
```

规则：

- 必须选 exactly 8 个通道。
- 通道 index 必须在输入范围内。
- 不能重复。
- 默认选择前 8 个通道。

## EegRingBuffer

`EegRingBuffer` 是固定容量环形缓冲。当前 SDK 模式容量为 `SamplingRateHz * 20`，即 20 秒。分类只读取最新 `625` 个样本。

## 相关

- [[信号窗口与缓冲]]
- [[03 设备与数据流]]
- [[阻抗与就绪门控]]
