# SSVEP 实验设计

第一套 SSVEP 桌面软件的独立入口为 `src/Ssvep.Studio.App`。固定六目标，支持文字、图形、图片内容编辑与方案保存，提供 10–20 电极位置图选通道、手动电极对应表与保存、阻抗参考显示、单次识别选中框和实验记录。原有 `Ssvep.Speller.App` 保留作历史与回归参考。

参数研发分支为 `feature/parameter-research`，软件版本为 **0.3.3**。提供参数方案、连续滤波、样本数驱动识别、时钟映射、独立桥接心跳、校准/独立测试、回放阈值比较、二进制记录和 BrainVision 导出。个人校准按六目标顺序完成四组，每次提示 1 秒、实际反馈 4 秒、等待 0.5 秒。双击项目根目录 **启动参数研发版.cmd**；操作见[研发版使用说明](docs/SSVEP参数研发版使用说明.md)。原版 0.2.4 运行包及入口保留。

0.3.2 修复接收时间抖动造成个人校准一直准备的问题；有界抖动允许继续并标明时钟精度受限，等待和暂停显示具体原因，记录及回放保留时间质量与边界余量。

0.3.3 将校准目标试次改为单次反馈：本轮首次呈现的有效识别结果保持到 4 秒反馈结束，后续窗口不再换框或清框；逐窗计算和记录继续，下一目标重新反馈。

正式研发文档：[产品需求 PRD](docs/SSVEP参数研发版PRD.md)与[架构设计](docs/SSVEP参数研发版架构设计.md)，设计版本 1.4；实施情况和验证边界见[实施记录](docs/SSVEP参数研发版实施记录.md)。`docs/deliverables` 保留设计阶段 Word 稿；[交互原型](docs/prototypes/parameter-research.html)使用演示数据。

直接运行：从 [GitHub Releases](https://github.com/WH2020/SSVEP_Aoyi/releases) 下载 `SSVEP-Studio-win-x64.zip`，完整解压后双击包内的 **启动软件.cmd**，无需安装 .NET 或 Python 开发环境。请保留整个 `SSVEP-Studio` 文件夹。

本地交付目录的 Windows x64 可运行包为 `outputs/SSVEP-Studio/Ssvep.Studio.exe`，项目根目录的 **启动软件.cmd** 指向此目录。Git 仓库保存源码、测试和文档；运行环境及可运行包通过 Releases 分发。

0.2.2 修复 SDK 丢样提示导致实验终止及断开的问题：底部显示本次连接的提示次数和丢样点数，有缺口的窗口自动重新积累，连续数据恢复后继续识别。验证记录位于 `outputs/studio-loss-fix-20261001`。

0.2.3 将阻抗改为参考显示：高阻抗、未知、缺失或过期均不阻止开始/恢复，也不会自动暂停实验。设备连接、有效脑电和通道要求继续校验。验证记录位于 `outputs/studio-impedance-advisory-20261002`。

0.2.4 在实验运行页增加实时帧率，按最近 1 秒的刺激界面绘制统计，约每 200 ms 更新；暂停和结束清空，恢复重新统计。验证记录位于 `outputs/studio-frame-rate-20261002`。

使用流程见 [使用说明](docs/SSVEP实验设计使用说明.md)，模块边界见 [软件架构](docs/SSVEP实验设计软件架构.md)。真实脑电帽、阻抗测量语义和显示刺激时序仍需实机联调；模拟与 SDK 协议测试不能替代硬件验收。

## 源码构建

需要 Windows 和 .NET 8 SDK。连接真实设备还需要 Python 3.11 及 SDK 依赖；模拟模式不需要 Python。安装了 `uv` 时，软件会自动使用源码中固定的 Python 依赖版本；否则先为 PATH 中的 `python` 安装依赖：

```powershell
python -m pip install -r requirements-sdk.txt
```

在项目根目录执行：

```powershell
dotnet build Ssvep.Speller.sln -c Release -m:1
dotnet test tests/Ssvep.Speller.Tests/Ssvep.Speller.Tests.csproj -c Release
dotnet run --project src/Ssvep.Studio.App/Ssvep.Studio.App.csproj
dotnet run --project src/Ssvep.Studio.App/Ssvep.Studio.App.csproj -- --research
dotnet run --project tests/Ssvep.Studio.Smoke/Ssvep.Studio.Smoke.csproj -- outputs/studio-development/smoke-local
dotnet run --project tests/Ssvep.Studio.EditorSmoke/Ssvep.Studio.EditorSmoke.csproj -- outputs/studio-development/editor-local
```

也可以双击 **运行开发版.cmd**；脚本优先使用本地 `.tools/dotnet`，其次使用 PATH 中的 `dotnet`。

SDK 协议测试需要附加 `--sdk <python.exe路径>`，测试脚本仅产生合成数据、不连接硬件。测试、审查与修复记录在 `outputs/studio-development`。

研发集成测试使用 Smoke 项目的 `--research <python.exe路径>`；跨语言 JCS / SciPy 校验及 MNE 读回工具位于 `tools`。运行 `tools/publish-research.ps1` 可利用已有 SDK Python 运行环境发布独立 `outputs/SSVEP-Studio-Research` 包。发布和真实设备测试是不同步骤，生成包不表示已通过真人指标验收。

电极图交互回归使用 Smoke 项目的 `--headmap <python.exe路径>`，覆盖手动映射、重启恢复、选择同步、运行锁定、32 通道子集与阻抗过期。2026-09-26 的验证输出位于 `outputs/studio-electrode-map-20260926`。

## 目录

- `src/Ssvep.Studio.App`：独立 WPF 应用、运行接入、刺激绘制。
- `src/Ssvep.Speller.Core/Studio`：内容方案、会话、取窗、阻抗与记录。
- `src/Ssvep.Speller.Core/Research`：参数/JCS、样本调度、连续滤波、时钟、二进制、回放评估、独立验证和 BrainVision。
- `tests/Ssvep.Speller.Tests`：原有与新增核心回归。
- `tests/Ssvep.Studio.Smoke`：WPF 与采集/识别/记录全流程。
- `tests/Ssvep.Studio.EditorSmoke`：真实控件编辑、图片解码和布局检查。
- `outputs/SSVEP-Studio`：带 .NET 与 Python 运行环境的本地交付目录。

本分支的软件版本为 **0.3.3 参数研发版**。`outputs`、编译缓存、实验记录和本机快捷方式不纳入 Git。工程测试使用合成数据与 SDK 协议夹具；真实设备、个人误决策指标、物理显示锁时及三十分钟实机性能仍需验证。

Synchroni SDK 的 MIT 许可及来源说明保存在 `src/Ssvep.Studio.App/Vendor/Synchroni`。
