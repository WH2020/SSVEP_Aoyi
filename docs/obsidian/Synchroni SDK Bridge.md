# Synchroni SDK Bridge

#device #synchroni #bridge

## 为什么需要 Bridge

Synchroni 通讯 SDK 是 Python 侧使用更方便的接口。当前 WPF/C# 应用不直接操作 SDK，而是启动一个 Python 进程：

```text
C# SynchroniBridgeEegStream -> python synchroni_eeg_bridge.py -> Synchroni SDK -> BLE 设备
```

这样 C# 只需要解析稳定的 NDJSON 文本协议。

## C# 启动参数

`SynchroniBridgeEegStream.StartProcess` 会传入：

- `--scan-ms`
- `--package-count`
- `--power-refresh-ms`
- `--device-address`
- `--device-name-prefix`
- 可选滤波：`--filter-50hz`、`--filter-hpf`、`--filter-lpf`

默认配置来自 `SynchroniBridgeOptions.CreateDefault()`。

## stdout

标准输出用于结构化数据：

```json
{"type":"meta","deviceName":"OB6000C","deviceAddress":"...","samplingRateHz":250,"channelCount":32}
{"type":"sample","sampleIndex":42,"timestampUnixMs":1717200000123,"samplingRateHz":250,"channelCount":32,"channels":[...],"lost":false,"impedance":[...]}
```

C# 通过 `SynchroniBridgeSampleParser` 解析。

## stderr

标准错误用于诊断事件：

```json
{"type":"state","state":"DeviceStateEx.Ready"}
{"type":"battery","power":87}
{"type":"error","message":"No Synchroni BLE device found"}
```

C# 通过 `SynchroniBridgeDiagnosticParser` 解析。如果不是 JSON，也会作为 info 消息记录。

## 诊断快照

`SynchroniBridgeDiagnosticsSnapshot` 提供设置页显示所需信息：

- 设备名/地址。
- 采样率和通道数。
- 样本数和丢包数。
- 最新样本 index/time。
- 最新消息。
- 阻抗列表。

## 搜索和连接

- `SynchroniBridgeDeviceScanner` 用 bridge 扫描附近设备。
- UI 选择设备后，`SynchroniBridgeOptions.DeviceAddress` 传给正式 stream。
- Disconnect 会取消读取任务、Dispose stream、杀掉 Python 进程树。

## 常见问题

- 搜不到设备：确认设备开机、靠近电脑、未被其它程序占用。
- 有设备但无样本：看诊断消息和 Python bridge stderr。
- 样本有但无法开始：看 [[阻抗与就绪门控]]。
- 长时间等待 625 samples：看 [[信号窗口与缓冲]] 和通道筛选是否重置。

