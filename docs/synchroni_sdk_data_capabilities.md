# Synchroni 脑电帽 SDK 数据能力清单

## 1. 文档范围

本文基于当前仓库中的以下实现整理：

- 本地 Synchroni Python SDK：`synchroni-sensor-sdk` 1.0.0。
- SDK 本地源码版本：`b11ff17fcf3c226a7feb6dfb0bd05665f16d014d`。
- Python bridge：`src/Ssvep.Speller.App/Bridges/synchroni_eeg_bridge.py`。
- C# bridge、诊断、日志和 UI 代码。

本文区分三个层次：

1. **SDK 可提供**：本地 SDK 源码中已经定义的数据或回调。
2. **当前 bridge 已输出**：Python bridge 已经传给 C# 的数据。
3. **当前软件已使用或保存**：WPF 应用实际显示、参与算法或写入日志的数据。

不同设备型号由固件 feature map 决定支持哪些传感器。因此，SDK 中存在某种数据类型，不等于当前脑电帽硬件一定能返回该数据；需要连接实机后读取 `DeviceInfo` 或做数据回调验证。

## 2. 结论摘要

当前软件已经完整接入的主要数据是：

- 多通道 EEG 波形，SDK 注释单位为 `uV`。
- EEG 采样率和原始通道数。
- 每个样本的采样序号、设备侧相对时间、电脑接收时间。
- 丢包标记和应用侧丢包统计。
- 各 EEG 通道阻抗，单位为 `Ohm`，应用中换算为 `kOhm`。
- 设备名称、蓝牙地址。
- 设备连接状态、电量和错误信息，但目前只作为“最新诊断消息”处理。

SDK 还定义了但当前软件没有接入的数据包括：

- EEG 原始 ADC 数值和采样分辨率。
- 通道饱和度。
- 型号、硬件版本、固件版本、MTU、蓝牙 RSSI。
- EMG、ECG、加速度、陀螺仪、呼吸和部分设备的角度百分比。
- 设备支持的数据类型和完整通道能力表。

## 3. 数据能力总览

| 数据类别 | SDK 可提供 | bridge 已输出 | 软件当前用途 | 状态 |
| --- | --- | --- | --- | --- |
| EEG 通道值 | 是，`Sample.data`，单位 `uV` | `channels[]` | FBCCA 分类、数据采集 | 已接入 |
| EEG 原始 ADC 值 | 是，`Sample.rawData` | 否 | 无 | 未接入 |
| EEG 采样率 | 是，`EegSampleRate` / `SensorData.sampleRate` | `samplingRateHz` | 分类参数、诊断、日志元数据 | 已接入 |
| EEG 通道数 | 是，`EegChannelCount` / `channelCount` | `channelCount` | 通道筛选、诊断、日志元数据 | 已接入 |
| 采样序号 | 是，`Sample.sampleIndex` | `sampleIndex` | 连续性检查、标记对齐、日志 | 已接入 |
| 设备相对时间 | 是，`Sample.timeStampInMs` | `deviceElapsedMs` | 样本日志 | 已接入 |
| 电脑接收时间 | bridge 本地生成 | `hostReceivedUnixMs` | 样本时间、日志、数据新鲜度检查 | 已接入 |
| 丢包标记 | 是，`Sample.isLost` | `lost` | 窗口有效性检查、统计、日志 | 已接入 |
| 通道阻抗 | 是，`Sample.impedance`，单位 `Ohm` | `impedance[]` | 佩戴质量显示、刺激就绪门控 | 已接入 |
| 通道饱和度 | 是，`Sample.saturation`，单位 `%` | 否 | 无 | 未接入 |
| 电池电量 | 是，`0-100`，`-1` 表示无效 | `battery.power` 诊断事件 | 最新诊断消息 | 部分接入 |
| 连接状态 | 是，六种 `DeviceStateEx` 状态 | `state.state` 诊断事件 | 最新诊断消息 | 部分接入 |
| 设备名 | 是 | `deviceName` | 搜索、连接、诊断、采集元数据 | 已接入 |
| 蓝牙地址 | 是 | `deviceAddress` | 搜索、指定连接、诊断、采集元数据 | 已接入 |
| 蓝牙 RSSI | 是，`BLEDevice.RSSI` | 否 | 无 | 未接入 |
| 型号/硬件/固件版本 | 是，`DeviceInfo` | 否 | 无 | 未接入 |
| MTU | 是，`DeviceInfo.MTUSize` | 否 | 无 | 未接入 |
| EMG | SDK 定义，设备需支持 | 关闭 | 无 | 未接入 |
| ECG | SDK 定义，设备需支持 | 关闭 | 无 | 未接入 |
| 加速度 | SDK 定义，单位 `g`，设备需支持 | 关闭 | 无 | 未接入 |
| 陀螺仪 | SDK 定义，单位 `degree/s`，设备需支持 | 关闭 | 无 | 未接入 |
| 呼吸 | SDK 定义，单位 `uV`，设备需支持 | 关闭 | 无 | 未接入 |
| 角度百分比 | 部分设备定义，`0%-100%` | 关闭 | 无 | 未接入 |

## 4. 当前 EEG 样本包含什么

### 4.1 SDK 原始对象

SDK 的 `SensorData` 表示一批多通道数据，主要字段包括：

| 字段 | 含义 |
| --- | --- |
| `deviceMac` | 数据所属设备的蓝牙地址 |
| `dataType` | EEG、ECG、EMG、ACC、GYRO 等数据类型 |
| `sampleRate` | 采样率 |
| `channelCount` | 通道数 |
| `packageSampleCount` | 当前包包含的样本数量 |
| `channelSamples` | `[通道][样本]` 二维数据 |
| `resolutionBits` | ADC/传输分辨率 |
| `channelMask` | 启用通道掩码 |
| `K` | 原始值转换为物理量的比例系数 |

每个 `Sample` 主要包含：

| 字段 | 含义 | 单位/范围 |
| --- | --- | --- |
| `rawData` | 转换前的原始采样值 | ADC count，具体范围取决于分辨率 |
| `data` | 使用 `rawData * K` 得到的物理量 | EEG/ECG/呼吸为 `uV` |
| `impedance` | 对应电极阻抗 | `Ohm` |
| `saturation` | 通道饱和度 | `%`，`0-100` |
| `sampleIndex` | 连续采样序号 | 整数 |
| `timeStampInMs` | 由采样序号和采样率计算的设备流相对时间 | `ms` |
| `channelIndex` | 原始通道编号 | 从 0 开始 |
| `isLost` | 该样本是否由丢包补位生成 | 布尔值 |

### 4.2 当前 bridge 输出

当前 bridge 只处理 `DataType.NTF_EEG`，将每个时间点输出成一行 NDJSON：

```json
{
  "type": "sample",
  "sampleIndex": 42,
  "deviceElapsedMs": 168,
  "hostReceivedUnixMs": 1717200000123,
  "samplingRateHz": 250,
  "channelCount": 32,
  "channels": [1.2, 2.3],
  "lost": false,
  "impedance": [100000, 120000]
}
```

字段说明：

| bridge 字段 | 来源 | 说明 |
| --- | --- | --- |
| `sampleIndex` | 第一个有效通道的 `Sample.sampleIndex` | 当前时间点的采样序号 |
| `deviceElapsedMs` | `Sample.timeStampInMs` | 设备数据流相对时间，不是 Unix 时间 |
| `hostReceivedUnixMs` | Python 收到 SDK 数据包时的系统时间 | 同一 SDK 数据包内的多个样本可能共享此时间 |
| `samplingRateHz` | `SensorData.sampleRate` | 实时 EEG 采样率 |
| `channelCount` | `len(channelSamples)` | 实际回调中的通道数 |
| `channels` | 每通道 `Sample.data` | 已换算的 EEG 值，单位 `uV` |
| `lost` | 所有通道 `Sample.isLost` 的或运算 | 任一通道丢失即将整帧标为丢失 |
| `impedance` | 每通道 `Sample.impedance` | 单位 `Ohm` |

bridge 当前没有输出 `rawData`、`saturation`、`channelIndex`、`resolutionBits`、`channelMask`、`K` 和包序号等字段。

## 5. 设备和连接数据

### 5.1 设备搜索

SDK 扫描结果 `BLEDevice` 包含：

- `Name`：设备名称。
- `Address`：蓝牙地址。
- `RSSI`：扫描时信号强度。

当前设备搜索 bridge 只输出名称和地址，RSSI 被丢弃。因此软件不能显示设备距离或蓝牙信号质量。

### 5.2 设备信息

SDK 的 `DeviceInfo` 可以提供：

- 设备名称、型号、硬件版本、固件版本。
- EMG、EEG、ECG、加速度、陀螺仪、呼吸和角度数据的通道数。
- 对应数据类型的采样率。
- BLE MTU 大小。

当前 bridge 的 `meta` 消息只保留：

```json
{
  "type": "meta",
  "deviceName": "OB6000C",
  "deviceAddress": "...",
  "samplingRateHz": 250,
  "channelCount": 32
}
```

也就是说，应用目前只知道 EEG 相关能力，不知道设备型号、版本和其它传感器能力。

### 5.3 状态、电量和错误

SDK 可提供以下异步信息：

- 设备状态：`Disconnected`、`Connecting`、`Connected`、`Ready`、`Disconnecting`、`Invalid`。
- 电池电量：`0-100`，`-1` 为无效值。
- 传感器错误回调。

bridge 会把这些信息写到 stderr：

```json
{"type":"state","state":"DeviceStateEx.Ready"}
{"type":"battery","power":87}
{"type":"sensor_error","message":"..."}
```

C# 可以解析这些事件，但诊断快照目前只保留一条 `LastMessage`。因此新状态或错误会覆盖之前的电量信息，电量也没有独立字段、历史记录或低电量告警。

## 6. SDK 支持但当前关闭的数据类型

bridge 连接设备时明确执行：

```python
sensor.setParam("NTF_EEG", "ON")
sensor.setParam("NTF_EMG", "OFF")
sensor.setParam("NTF_ECG", "OFF")
sensor.setParam("NTF_IMU", "OFF")
sensor.setParam("NTF_BRTH", "OFF")
```

因此当前软件只接收 EEG。其它类型的能力如下：

| 类型 | SDK 数据对象 | SDK 文档单位 | 备注 |
| --- | --- | --- | --- |
| EMG | `NTF_EMG` | SDK 当前 README 未明确标注 | SDK 源码默认按 8 通道、500 Hz 初始化；实际以设备为准 |
| ECG | `NTF_ECG` | `uV` | SDK 源码按 1 通道初始化；实际以设备为准 |
| 加速度 | `NTF_ACC` | `g` | `NTF_IMU` 会拆分为 ACC 和 GYRO 两个回调对象 |
| 陀螺仪 | `NTF_GYRO` | `degree/s` | 与加速度共享 IMU 配置和采样率 |
| 呼吸 | `NTF_BRTH` | `uV` | SDK 源码按 1 通道初始化；实际以设备为准 |
| 角度百分比 | `NTF_MAG_ANGLE_DATA` | `%` | 源码注明部分 NeuCir 设备使用，范围 `0-100%` |
| ADS | `NTF_ADS` | 无单位 | SDK 有枚举和 feature flag，但当前高层回调路径没有完整接入证据 |

这些数据能否由“目前这顶脑电帽”实际提供，不能只依据 SDK 枚举判断。建议连接目标设备后记录完整 `DeviceInfo` 和 feature map，再形成硬件型号级能力表。

## 7. 可配置的数据处理

SDK 支持以下设备侧或 SDK 侧参数：

| 参数 | 含义 | 当前软件 |
| --- | --- | --- |
| `FILTER_50HZ` | 50 Hz 陷波 | 可通过 bridge 参数启用 |
| `FILTER_60HZ` | 60 Hz 陷波 | SDK 支持，bridge 未暴露 |
| `FILTER_HPF` | 0.5 Hz 高通 | 可通过 bridge 参数启用 |
| `FILTER_LPF` | 80 Hz 低通 | 可通过 bridge 参数启用 |
| `packageSampleCount` | 每次回调期望聚合的样本数 | 当前默认 10 |
| `powerRefreshInterval` | 电量回调刷新间隔 | 当前默认 60000 ms |
| `DEBUG_BLE_DATA_PATH` | SDK 原始 BLE 调试 CSV 路径 | 当前未使用 |

注意：滤波开关是采集配置，不是额外的数据字段。当前采集 `session.json` 会记录 50 Hz、高通、低通三个开关，但不会记录 60 Hz 陷波。

## 8. 当前软件实际显示和保存什么

### 8.1 设置页实时显示

- 设备名称和地址。
- EEG 采样率和原始通道数。
- 已收到样本数和丢包帧数。
- 环形缓冲区中可用于分类的样本数。
- 最新样本序号和电脑接收时间。
- 最新状态、电量或错误消息。
- 各原始通道的最新阻抗及质量分级。

### 8.2 分类链路

- 原始设备可能返回 8、32 或其它数量的 EEG 通道。
- 用户必须选择 8 个通道。
- `ChannelMontage` 将原始通道投影成固定 8 通道。
- 算法读取最新 625 个样本；250 Hz 时对应 2.5 秒窗口。
- 含丢包或采样序号不连续的窗口会被判为不可用。
- 阻抗只参与就绪门控，不作为 FBCCA 输入特征。

### 8.3 数据采集日志

`samples.csv` 当前保存设备返回的全部原始 EEG 通道。表头根据设备元数据中的原始通道数动态生成，例如 32 通道设备为：

```text
sample_index,host_received_timestamp,device_elapsed_ms,is_lost,ch1,ch2,...,ch32
```

其中 `ch1-chN` 与设备返回的原始通道顺序一致。用户选择的 8 个通道只进入实时分类缓冲，不影响原始数据保存。

`session.json` 的 acquisition 区域保存：

- 数据源模式。
- 设备名称和地址。
- EEG 采样率、原始通道数。
- 选择的 8 个原始通道索引。
- 阻抗阈值和开始采集时的阻抗快照。
- 50 Hz 陷波、高通、低通开关。

当前没有落盘：

- 每个样本对应的阻抗和饱和度历史。
- 电量、状态和错误事件历史。
- RSSI、型号、硬件版本、固件版本、MTU。
- EEG 原始 ADC 值、分辨率、通道掩码和转换系数。
- ECG、EMG、IMU、呼吸等非 EEG 数据。

## 9. 时间戳和同步限制

目前有两种时间：

- `deviceElapsedMs`：SDK 使用 `sampleIndex * sampleInterval` 计算的相对时间，用于表示数据流内部时间轴。
- `hostReceivedUnixMs`：Python 收到整个 SDK 回调包时记录的电脑系统时间。

当前没有得到设备硬件时钟的绝对时间。由于一个回调包内的多个样本共用同一个 `hostReceivedUnixMs`，它不能代表每个样本准确到达电脑的时刻。实验对齐应优先使用连续的 `sampleIndex` 和刺激渲染标记，不能把电脑接收时间当作严格的硬件同步时间。

此外，SDK 当前用整数毫秒间隔 `1000 // sampleRate` 生成 `timeStampInMs`。250 Hz EEG 的间隔正好是 4 ms，不受影响；对于不能整除 1000 的采样率，该相对时间可能出现量化误差和累计漂移。

## 10. 建议的接入优先级

### P0：保证实验数据可追溯

- 在 `meta` 中增加型号、硬件版本、固件版本和 SDK 版本。
- 在采集日志中记录电量、设备状态、错误事件历史。
- 明确记录 EEG 数值单位为 `uV`。
- 记录 bridge 和 SDK 的代码版本。

### P1：提升数据质量诊断

- 输出并保存 `saturation`，用于识别前端饱和和运动伪迹。
- 保存周期性阻抗历史，而不只保存开始时快照。
- 扫描结果增加 RSSI，用于排查蓝牙链路质量。
- 将电量从“最新消息”提升为独立诊断字段。

### P2：支持高级研究

- 在离线工具中根据 `selectedChannelIndices` 复现在线 montage，或重新选择其它 8 通道。
- 可选保存 `rawData`、分辨率、`K` 和通道掩码。
- 按设备能力开放 IMU，用于运动伪迹检测。
- 视研究需求开放 ECG、EMG 或呼吸数据，并建立独立时间轴和日志格式。

## 11. 关键源码位置

- SDK 数据模型：`.tools/synchroni-sensor-sdk/sensor/sensor_data.py`
- SDK 设备信息：`.tools/synchroni-sensor-sdk/sensor/sensor_device.py`
- SDK 数据解析与单位换算：`.tools/synchroni-sensor-sdk/sensor/sensor_data_context.py`
- SDK 使用说明：`.tools/synchroni-sensor-sdk/README.md`
- 当前 Python bridge：`src/Ssvep.Speller.App/Bridges/synchroni_eeg_bridge.py`
- C# 样本解析：`src/Ssvep.Speller.Core/Devices/SynchroniBridgeSampleParser.cs`
- C# 诊断聚合：`src/Ssvep.Speller.Core/Devices/SynchroniBridgeDiagnostics.cs`
- 采集日志：`src/Ssvep.Speller.Core/Logging/DataCollectionLogWriter.cs`
