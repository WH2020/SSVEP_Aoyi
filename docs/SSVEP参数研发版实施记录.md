# 参数研发版 0.3.2 实施记录

更新 2026-10-09 · 分支 `feature/parameter-research` · 原版对照 `v0.2.4 / 911bd19`

本次完成第一套 SSVEP 软件的研发功能和工程验证，提供独立 Windows x64 包及直接运行入口。自动化验证使用合成数据；0.3.2 排查时只读检查了用户真实设备记录中的时钟与状态事件，未将其作为真人识别率、物理相位同步或三十分钟实机性能验收。

## 0.3.2 校准准备修复

用户记录中已有连续脑电与识别结果，但样本时钟 P95 残差约 25–31 毫秒，被原拟合器标为 `Degraded`。旧校准入口要求样本与呈现时钟同时为 `Estimated`，导致始终没有试次标记。另一个问题是首次提示之前暂停时，界面仍显示笼统等待消息。

新增 `CalibrationClockPolicy` 检查映射的观测量、新鲜度、速率与残差；成熟且有界抖动的映射可以启动普通校准，同时保留原时钟质量并标记 `EstimatedWithJitter`。无效映射仍阻止试次开始，并显示具体原因。暂停、数据窗口重新积累或映射不可用时清除提示和反馈；受影响的未完成试次按原规则补采，已完成反馈后的等待保留试次。

标签记录两个模型和按残差计算的边界余量，算法回放与在线反馈评估采用相同边界约束。BrainVision 把精度受限标签保留在 `events.tsv`，明确提示未将其写为 `.vmrk` 精确事件。拟合器的原始质量门限、样本驱动节拍、四组 1 / 4 / 0.5 秒流程不变。历史记录不补造缺失的校准标签。

165 项核心测试通过。新增 WPF 集成夹具在连续样本上注入 34 毫秒正弦接收抖动，通过真实批次接收与时钟拟合复现原门槛；验证准备期间暂停、两次完整提示/反馈/等待、真实 T2 反馈、带余量回放和 BrainVision 附属事件。测试不占用真实设备。复现命令：

```powershell
dotnet run --project tests/Ssvep.Studio.Smoke -c Release -- outputs/calibration-032/readiness-verified --calibration-readiness
```

四组共 24 次真实 WPF 绘制校准，以及暂停恢复、等待保留已完成试次、走神补采和取消回归通过，结果位于 `outputs/calibration-032/full`。新抖动场景导出的两段、8 通道、3744 个样本通过 MNE 1.10.2 独立读回；精度受限事件留在附属表，未写入 `.vmrk`，最大幅值误差约 `7.63e-13 V`。本次没有重新做真人效果或物理刺激锁时验证。

独立运行包为 `outputs/SSVEP-Studio-Research`。通过项目根目录入口实测启动 0.3.2、传递研发模式参数并正常关闭，测试数据目录隔离，结果保存在 `outputs/calibration-032/entry/result.json`。随包 5801 个文件的 SHA256 校验通过。

## 0.3.1 个人校准修订

普通个人校准改为四组目标 1→6，共 24 次。每次黄色提示框 1 秒、真实反馈 4 秒、等待 0.5 秒；提示和等待不闪烁、不显示识别框。反馈只显示完整落入本次观察区间的窗口结果，仍可真实出现误选或拒识。提示、反馈和等待分别记录绘制时间、样本映射及阶段标记。补采保留原目标与组号，等待期间暂停不会丢弃已经完成的试次。

普通校准不再追加三分钟无意图；无意图指标仍显示缺少数据，不将其当作零误触发。独立测试继续保留六目标各十次和十一分钟无意图。新版可从原有“启动参数研发版.cmd”进入。

本次已通过 156 项核心测试，并完整运行 24 次真实 WPF 绘制校准及暂停、恢复、走神补采、取消两个桌面场景。合成 EEG 固定为 T2，提示 T1 时确认实际显示 T2，避免把提示当成识别结果。24 次记录中的提示和反馈时长约为 1.017、4.017 秒，等待约为 0.500 秒；差值来自显示帧量化，不是物理光输出测量。

同时通过 10 个研发集成场景与 19 项窗口/DPI/布局检查，结果分别位于 `outputs/calibration-031/research`、`outputs/calibration-031/layout`。长时桌面测试以不激活测试窗口的方式运行，避免测试工具抢焦点触发正常的失焦暂停逻辑。

复现：

```powershell
dotnet test tests/Ssvep.Speller.Tests/Ssvep.Speller.Tests.csproj -c Release
dotnet run --project tests/Ssvep.Studio.Smoke -c Release -- outputs/calibration-031/full-verified --calibration
```

本地结果与三阶段截图保存在 `outputs/calibration-031/full-verified`，`timing.json` 保存逐试次时长。

## 已实现

| 能力 | 主要实现位置 |
| --- | --- |
| 受限参数校验、原版只读对照、修订及默认、JCS 指纹 | `Core/Research/ResearchProfile.cs`、`CanonicalJson.cs`、`ProfileRepository.cs`、`App/ResearchPanel.cs` |
| 不可变运行配置、显示尺寸/缩放锁定、准备失败回退 | `RunContext.cs`、`App/StudioRuntime.cs`、`MainWindow.xaml.cs` |
| 625/1250 点窗口，50/100 点步长，缺口重新预热 | `SampleWindowScheduler.cs`、`ResearchPipeline.cs` |
| 连续 Butterworth SOS、可选陷波、稳定白化 CCA、归一化分和单次拒识 | `StreamingFilterBank.cs`、`ResearchPipeline.cs` |
| WPF RenderingTime 相位，实际绘制标签，FPS/P95/长帧 | `App/StudioStimulusView.cs`、`StudioRuntime.cs` |
| Python 批次时钟、独立 TCP 心跳、四时间戳对时、Huber 样本/呈现映射 | `Bridges/synchroni_eeg_bridge.py`、`Devices/SynchroniBridgeEegStream.cs`、`Research/ClockMapping.cs` |
| 后台二进制原始数据、CRC32C、索引重建、残缺文件恢复 | `Studio/StudioRecorder.cs`、`Research/BinarySampleStore.cs` |
| 校准、排除补采、独立测试、回放、35 组阈值搜索 | `App/CalibrationController.cs`、`ReplayPanel.cs`、`Research/ReplayRunner.cs`、`ParameterSearch.cs` |
| 首选指标、区间、混淆矩阵、在线绘制统计、本地独立验证证据 | `RecordedFeedbackEvaluator.cs`、`ValidationRepository.cs` |
| 流式 BrainVision 与独立 MNE 读回工具 | `BrainVisionExporter.cs`、`tools/verify_brainvision.py` |

上表 Core 为 `src/Ssvep.Speller.Core`，App 为 `src/Ssvep.Studio.App`。在线与回放共用处理链；窗口由采样推进，UI 刷新不取窗。分类任务数量有界，过载明确暂停并记录取消窗口。没有三次确认、外设命令或文字累计输出。

丢样告警只更新计数并重置受影响窗口，仍保持连接；阻抗高、未知或过期不阻止开始、恢复或继续实验。明确断连、进程退出和通信失败按独立原因结束不完整会话。

## 0.3.0 基线验证结果（2026-10-02）

自动化使用合成信号和 SDK 协议夹具。可复现输出保存在本地 `outputs/research-implementation`，不纳入 Git。

| 检查 | 结果与覆盖 |
| --- | --- |
| 核心单元测试 | 154 项通过；参数边界、旧版精确得分、250/500 Hz、时钟漂移/离群点、数据恢复、标签统计和验证隔离 |
| 研发桌面集成 | 10 个场景通过；Preparing 失败后重试、UI 阻塞期间样本调度、在线/回放窗口对齐、四类桥接状态、500 Hz 32 通道、真实 WPF 校准提示、导出缺口/事件、回放/扫描/保存候选的真实控件操作 |
| 既有运行回归 | 20 个场景通过；停止写盘排空、格式变化、迟到结果、启动失败释放设备占用、丢样保持连接、阻抗仅供参考 |
| 启动与布局 | 19 项检查通过；八种屏幕/DPI 边界、小视口编辑/运行、研发导航/校准/诊断均在视口内；已目视检查参数及运行截图 |
| C# / Python JCS | 4 组向量同规范文本、同 SHA256，包含浮点边界、负零、Unicode 排序及组合字符 |
| SciPy 独立滤波比较 | 250/500 Hz 共 8 组子带、每组 4097 个频率点；最大复数频响差约 `1.94e-12` |
| MNE 1.10.2 | 500 Hz 32 通道全部样本读回；另验 250 Hz、Unicode/逗号事件、缺失/非有限/丢样分段。单位、顺序、样本数与事件位置一致，最大幅值差约 `1.53e-12 V` |

上述数量不等于全部 PRD 验收完成。测试命令及脚本如下：

```powershell
dotnet test tests/Ssvep.Speller.Tests/Ssvep.Speller.Tests.csproj -c Release
dotnet run --project tests/Ssvep.Studio.Smoke -c Release -- outputs/check/research --research <python.exe>
dotnet run --project tests/Ssvep.Studio.Smoke -c Release -- outputs/check/runtime --regressions <python.exe>
dotnet run --project tests/Ssvep.Studio.Smoke -c Release -- outputs/check/layout --window-size
python tools/verify_research_reference.py outputs/check/research/reference-artifacts.json
python tools/verify_brainvision.py <source-session> <brainvision-export>
```

## 与设计的具体对应及边界

采用 C# 流式 BrainVision 写入器替代设计阶段拟用的 pybv，桌面导出无新增科学计算 Python 依赖。MNE/SciPy/rfc8785 只用于独立核验，版本固定在 `tools/requirements-research-qa.txt`。SDK 原始单位依据随包 `sensor_data.py` 的 µV 合约记录，未做外部幅度标定。

时钟回归输出偏移、漂移、MAD/P95、观测范围、质量和模型版本；原始批次与四时间戳观测保留，manifest 保存各时钟段的最终模型。模型是因果的软件估计，固定蓝牙/设备延迟和显示延迟未知。样本计数回退、桥接实例或采集格式改变时，结束旧会话并要求重新连接，防止索引重用污染标签。非线性时钟漂移会降低质量，本版不把它自动解释为硬件同步。

默认研究滤波需要预热后才进入完整窗口。暂停恢复、缺口和丢样重置均清除之前状态。候选改变刺激频率时强制重新采集；软件处理参数可以在同一份原始数据上回放。旧 NDJSON 可回放得分，但没有新版本时钟或标签时不会补造正确率。

报告中的算法指标仅使用完全位于标签观察区间的窗口；原始在线绘制指标单列来源方案。独立测试记录不得参与阈值搜索，且只能给实际采集时冻结的相同参数登记验证。测试达到点估计目标也不表示置信区间已足够窄，更不代表人群普适效果。

仍需在用户的实际设备和显示器上完成：SDK 硬件滤波读回、真实位置映射、计时/相位外部测量、参与者独立测试、目标切换实测，以及开启诊断和记录后的三十分钟持续运行和性能对照。TRCA/eCCA、脑控小车、机械手和插件宿主不属于此版本实现。

## 二进制协议摘要

`samples.bin` 为小端：8 字节 `SSVEPB01`，Int32 JSON 头长度及 UTF-8 头。后续块为 UInt32 `0x314B4C42`、Int32 body 长度、UInt32 CRC32C，以及 body。body 包含 Int32 元数据长度、UTF-8 元数据，再按采样点依次写入 Int64 UTC ticks、Float64 设备时间（NaN 表示未知）、1 字节丢样标记、UInt64 通道有效位图、各通道 Float64 原始值。索引由块首索引加行偏移恢复。

每块至多一秒、单一连续采样段和统一格式。`.idx` 为 `SSVEPI01` 后每条 32 字节：Int64 块序号、Int64 文件偏移、Int32 文件长度、Int64 首样本索引、Int32 样本数。索引可从完整有效块重建；损坏块与未提交尾块会明确提示，不静默填充。采样索引为零基，BrainVision 事件位置为段内一基。
