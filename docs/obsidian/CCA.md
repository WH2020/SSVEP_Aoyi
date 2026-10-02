# CCA

#algorithm #cca #ssvep

## 它解决什么问题

CCA 用来判断 EEG 窗口和某个候选频率的参考信号有多相关。对每个目标频率都算一次相关分数，分数最高的目标就是分类 best。

## 输入

- EEG 窗口：`double[,] window`，形状为 `sampleCount x channelCount`。
- 目标列表：每个 `StimulusTarget` 提供 `FrequencyHz` 和 `PhaseRadians`。
- 采样率：通常是 `250 Hz`。

## 参考信号

`ReferenceSignalGenerator.Generate` 为频率 `f` 生成：

```text
sin(2πft), cos(2πft),
sin(2π2ft), cos(2π2ft),
...
```

列数为 `harmonicCount * 2`。

## 当前实现

`CcaClassifier.Classify` 对每个 target：

1. 生成参考矩阵。
2. 调 `CcaMath.CanonicalCorrelation(window, reference)`。
3. 把分数写入 `scores[target.Id]`。
4. 排序得到 best 和 second。

输出是 `ClassificationResult`：

```csharp
BestTargetId
BestScore
SecondTargetId
SecondScore
Scores
Margin = BestScore - SecondScore
```

## 局限

- 没有滤波器组，抗噪声不如 FBCCA。
- 没有个体模板，因此对电极位置、眨眼、肌电和屏幕刷新比较敏感。
- 多目标且频率接近时 margin 可能小。

## 相关代码

- [CcaClassifier.cs](../../src/Ssvep.Speller.Core/Classification/CcaClassifier.cs)
- [CcaMath.cs](../../src/Ssvep.Speller.Core/Classification/CcaMath.cs)
- [ReferenceSignalGenerator.cs](../../src/Ssvep.Speller.Core/Classification/ReferenceSignalGenerator.cs)

