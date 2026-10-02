using Ssvep.Speller.Core.Classification;

namespace Ssvep.Speller.Core.Studio;

public enum StudioSessionState { Preparing, Running, Paused, Completed, Faulted }

/// <summary>Called by one application dispatcher. Background work returns its captured run generation.</summary>
public sealed class StudioSessionController
{
    public StudioSessionState State { get; private set; } = StudioSessionState.Preparing;
    public long RunGeneration { get; private set; }
    public string? SelectedTargetId { get; private set; }
    public string Message { get; private set; } = "请先连接设备并选择识别通道";
    private long lastWindow = -1;

    public long Start()
    {
        if (State == StudioSessionState.Running) throw new InvalidOperationException("实验已经运行。");
        RunGeneration++;
        lastWindow = -1;
        SelectedTargetId = null;
        State = StudioSessionState.Running;
        Message = "正在准备有效脑电数据";
        return RunGeneration;
    }

    public void Pause(string reason) => Transition(StudioSessionState.Paused, reason);
    public void Stop() => Transition(StudioSessionState.Completed, "实验已结束");
    public void Fault(string reason) => Transition(StudioSessionState.Faulted, reason);
    public void Prepare() => Transition(StudioSessionState.Preparing, "请先连接设备并选择识别通道");

    public void InvalidateWindow()
    {
        if (State != StudioSessionState.Running) return;
        RunGeneration++;
        lastWindow = -1;
        SelectedTargetId = null;
        Message = "实验进行中 · 等待连续脑电数据";
    }

    public bool AcceptResult(long generation, long lastSampleIndex, ClassificationResult result)
    {
        if (State != StudioSessionState.Running || generation != RunGeneration || lastSampleIndex <= lastWindow)
            return false;
        lastWindow = lastSampleIndex;
        bool valid = StudioRuntimeProfile.Targets.Any(t => t.Id == result.BestTargetId)
            && double.IsFinite(result.BestScore) && double.IsFinite(result.SecondScore)
            && result.Scores.Values.All(double.IsFinite)
            && result.BestScore >= .1 && result.Margin >= .02;
        SelectedTargetId = valid ? result.BestTargetId : null;
        Message = valid ? "实验进行中" : "暂未识别到有效目标";
        return valid;
    }

    private void Transition(StudioSessionState state, string message)
    {
        RunGeneration++;
        State = state;
        SelectedTargetId = null;
        Message = message;
    }
}
