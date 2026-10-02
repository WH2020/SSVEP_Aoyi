# FBCCA

#algorithm #fbcca #ssvep

## 它是什么

FBCCA 可以理解为“多子带/多谐波版本的 CCA”。它对每个候选目标计算多个子带分数，并用权重累加，通常比单次 CCA 对谐波和噪声更稳。

## 当前实现方式

`FbccaClassifier` 默认 `harmonicCount = 3`。对每个目标：

```csharp
for (int subBand = 1; subBand <= harmonicCount; subBand++)
{
    int harmonicsInBand = harmonicCount - subBand + 1;
    reference = Generate(target.FrequencyHz * subBand, ..., harmonicsInBand, phase);
    weight = Math.Pow(subBand, -1.25) + 0.25;
    rho = CcaMath.CanonicalCorrelation(window, reference);
    score += weight * rho * rho;
}
```

最终 `score` 越大，表示 EEG 窗口越像该目标频率。

## 当前默认参数

| 参数 | 值 |
| --- | ---: |
| harmonicCount | `3` |
| 子带权重 | `subBand^-1.25 + 0.25` |
| 窗口 | `625 x 8` |
| 采样率 | `250 Hz` |

## 为什么它适合 V1

- 不需要训练数据。
- 可以通过合成信号做单元测试。
- 接口和 CCA 相同，便于保留基线。
- 对基波和谐波响应都有利用。

## 输出如何进入决策

FBCCA 只给出每个目标的分数，不直接输出字符。输出必须经过：

1. [[反馈模式]]：决定用真实分类还是假反馈目标。
2. [[混合异步决策]]：连续窗口、阈值、冷却。
3. `TextComposer` 或采集 feedback 高亮。

## 相关代码

- [FbccaClassifier.cs](../../src/Ssvep.Speller.Core/Classification/FbccaClassifier.cs)
- [ClassifierTests.cs](../../tests/Ssvep.Speller.Tests/ClassifierTests.cs)

