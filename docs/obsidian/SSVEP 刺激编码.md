# SSVEP 刺激编码

#ssvep #stimulus #algorithm

## 目标类型

每个刺激目标是一个 `StimulusTarget`：

```csharp
public sealed record StimulusTarget(
    string Id,
    string Label,
    double FrequencyHz,
    double PhaseRadians,
    TargetAction Action);
```

`TargetAction` 决定提交后对文本做什么：

- `Digit(char)`
- `Letter(char)`
- `Space`
- `Delete`
- `Pause`
- `Clear`

## 当前视觉布局

```text
1 2 3 4 5 6 7 8 9 0
Q W E R T Y U I O P
 A S D F G H J K L
   Z X C V B N M
SPACE DEL PAUSE CLEAR
```

注意：视觉布局顺序和频率分配顺序不是同一个概念。

## 频率分配顺序

`StimulusLayout.FrequencyOrder` 当前为：

```text
1 2 3 4 T 5 6 7 O 8
9 0 D Q W E J R Y U
X I P A N S F G H K
L Z C V B M SPACE DEL PAUSE CLEAR
```

频率公式：

```text
FrequencyHz = 8.0 + index * 0.2
```

因此：

- 第 1 个目标 `1`：`8.0 Hz`
- 第 40 个目标 `CLEAR`：`15.8 Hz`

## 相位

当前所有目标 `PhaseRadians = 0.0`。代码结构已经预留相位字段，后续可以做频率 + 相位联合编码，但当前版本未使用相位差。

## 为什么频率顺序不等于键盘顺序

频率顺序可以被视为编码表。视觉键盘顺序服务于用户寻找目标；频率顺序服务于算法区分目标。二者分离后，后续可以在不改 UI 的情况下调整频率分配策略。

## 相关代码

- [StimulusLayout.cs](../../src/Ssvep.Speller.Core/Stimulus/StimulusLayout.cs)
- [StimulusTarget.cs](../../src/Ssvep.Speller.Core/Stimulus/StimulusTarget.cs)
- [TargetAction.cs](../../src/Ssvep.Speller.Core/Stimulus/TargetAction.cs)

