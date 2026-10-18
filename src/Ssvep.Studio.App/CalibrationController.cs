using System.Diagnostics;
using Ssvep.Speller.Core.Research;

namespace Ssvep.Studio.App;

public sealed class CalibrationController
{
    private readonly StudioRuntime runtime;
    private readonly StudioStimulusView view;
    private readonly Queue<CalibrationTrial> queue = new();
    private CalibrationTrial? current;
    private string phase="waiting";
    private string? pending;
    private string? pendingCue;
    private double phaseStarted;
    private double lastRendering;
    private double lastDrawHost;
    private double? observeStartSample;
    private TrialDecisionWindow? finalWindow;
    private string? heldFeedback;
    private bool endOfTrialFeedback;
    private int replacement;
    private int completed;
    private int groups;
    private int planned;
    private string phaseStatus="";
    public bool IsActive { get; private set; }
    public string Status { get; private set; }="个人校准：目标 1→6，共四组；刺激 4 秒后出一次结果 → 反馈 0.5 秒 → 下一轮。";
    public event Action? Changed;
    public CalibrationController(StudioRuntime runtime,StudioStimulusView view)
    { this.runtime=runtime; this.view=view; runtime.FramePresented+=OnFrame; view.CuePresented+=OnCuePresented; }

    public void Start(int? seed=null,bool independentTest=false)
    {
        if(!runtime.IsRunning) throw new InvalidOperationException("请先开始实验。");
        if(IsActive) return;
        int selectedSeed=seed??Random.Shared.Next();
        var plan=independentTest?new CalibrationPlan(selectedSeed,noIntentSeconds:660):CalibrationPlan.Personal(selectedSeed);
        endOfTrialFeedback=!independentTest;
        queue.Clear(); foreach(var trial in plan.Trials) queue.Enqueue(trial);
        runtime.RecordEvent("calibration-plan",new {plan.Seed,plan.Trials,plan.GroupCount,purpose=independentTest?"independent-test":"parameter-calibration",
            runtime.ActiveContext!.Profile.ProfileHash,plan.Algorithm,timingPolicy=CalibrationClockPolicy.Version,
            targetFeedbackPolicy=endOfTrialFeedback?TrialFeedbackSelection.Policy:TrialFeedbackSelection.LegacyPolicy});
        IsActive=true; current=null; pending=null; pendingCue=null; phase="waiting"; completed=0; replacement=0;
        groups=plan.GroupCount; planned=plan.Trials.Count; observeStartSample=null; heldFeedback=null; finalWindow=null;
        ShowWaiting(PreparationIssue()??"校准准备：即将开始目标 1 的四秒刺激");
    }

    private bool CurrentCompleted => phase is "wait" or "feedback";
    public void ObserveState()
    {
        if(!IsActive) return;
        if(!runtime.IsRecording) { Cancel(); return; }
        string? issue=PreparationIssue();
        if(issue is not null)
        {
            if(current is not null)
            {
                if(CurrentCompleted) CloseFeedback();
                else ExcludeCurrent(!runtime.IsRunning||runtime.LastScores is null
                    ?"pause-or-presentation-interruption":"clock-mapping-unusable");
            }
            current=null; pending=null; pendingCue=null; phase="waiting";
            ShowWaiting(issue); return;
        }
        UpdateFeedback();
        UpdatePhaseStatus();
    }
    public void MarkDistracted()
    {
        if(!IsActive||current is null||CurrentCompleted) throw new InvalidOperationException("请在目标提示或刺激期间标记走神。");
        ExcludeCurrent("operator-marked-distraction-or-contact-adjustment");
        pending=null; pendingCue=null; phase="waiting";
        ShowWaiting("已记录排除原因，试次将在末尾补采。");
    }
    private void ExcludeCurrent(string reason)
    {
        if(current is null) return;
        runtime.RecordEvent("trial-exclusion",new {trialId=current.TrialId,reason,replacementPlanned=true});
        queue.Enqueue(current with {TrialId=current.TrialId+"-r"+(++replacement),ReplacesTrialId=current.TrialId}); current=null;
    }
    public void Cancel()
    {
        if(IsActive)
        {
            CloseFeedback();
            runtime.RecordEvent("calibration-ended",new {completed,remaining=queue.Count,unfinishedTrial=CurrentCompleted?null:current?.TrialId});
        }
        IsActive=false; current=null; pending=null; pendingCue=null; queue.Clear(); observeStartSample=null; heldFeedback=null; finalWindow=null;
        view.SelectedTargetId=runtime.IsRunning?runtime.Session.SelectedTargetId:null;
        view.ClearCalibrationPresentation(); Status="校准已停止；原始数据和试次标记保留。"; Changed?.Invoke();
    }
    private void ShowWaiting(string text)
    {
        observeStartSample=null; heldFeedback=null; finalWindow=null; phaseStatus="";
        if(view.CueText!=text||view.CueTargetId is not null||view.StimulusEnabled||view.FeedbackEnabled)
            view.SetCalibrationPresentation(text,null,false);
        SetStatus(text);
    }
    private string? PreparationIssue()
    {
        if(!runtime.IsRunning) return "校准已暂停："+runtime.Session.Message+"；请点击“恢复实验”";
        if(runtime.LastScores is null)
        {
            double seconds=2.5+(runtime.ActiveContext?.WarmupSamples??0)/(double)runtime.SamplingRate;
            return $"校准准备：正在积累连续脑电窗口（约 {seconds:0.0} 秒，丢样后重新计时）";
        }
        var timing=runtime.CalibrationTiming;
        return timing.Ready?null:timing.Message;
    }
    private void SetStatus(string text)
    {
        if(Status==text) return;
        Status=text; Changed?.Invoke();
    }
    private void UpdatePhaseStatus()
    {
        if(phaseStatus.Length==0) return;
        SetStatus(phaseStatus+(!endOfTrialFeedback&&heldFeedback is not null?" · 本次结果已保持":"")
            +(runtime.CalibrationTiming.Quality==CalibrationClockPolicy.LimitedQuality?" · 时钟精度受限":""));
    }
    private void UpdateFeedback()
    {
        if(endOfTrialFeedback)
        {
            // Raw 200 ms estimates never draw during a personal-calibration stimulation interval.
            bool showing=runtime.IsRunning&&(pending??phase)=="feedback";
            view.SelectedTargetId=showing?heldFeedback:null;
            view.FeedbackEnabled=showing&&heldFeedback is not null;
            return;
        }
        var candidate=runtime.LastRecognition;
        bool observing=pending is null&&phase=="observe"&&runtime.IsRunning;
        bool eligible=observing&&observeStartSample is double start&&candidate is not null&&candidate.FirstSampleIndex>=start;
        if(current?.Label=="target")
        {
            // Keep the established independent-test protocol and its latency acceptance criterion.
            if(heldFeedback is null&&eligible&&candidate!.Decision is {Accepted:true,TargetId:not null})
            {
                heldFeedback=candidate.Decision.TargetId;
                runtime.RecordEvent("trial-feedback",new TrialFeedbackSelection(current.TrialId,heldFeedback,
                    candidate.FirstSampleIndex,candidate.LastSampleIndex,candidate.HostSeconds,Now));
                UpdatePhaseStatus();
            }
            view.SelectedTargetId=heldFeedback;
            view.FeedbackEnabled=observing&&heldFeedback is not null;
        }
        else
        {
            view.SelectedTargetId=eligible&&candidate!.Decision.Accepted?candidate.Decision.TargetId:null;
            view.FeedbackEnabled=eligible;
        }
    }
    private static double Now => Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency;
    private void OnFrame(double rendering,double host)
    {
        lastRendering=rendering; lastDrawHost=host;
        if(!IsActive||!runtime.IsRunning||pending is not null) return;
        if(phase=="waiting") { NextTrial(); return; }
        if(PreparationIssue() is not null) { ObserveState(); return; }
        UpdateFeedback();
        if(current is null) return;
        if(phase=="cue"&&rendering-phaseStarted>=current.CueSeconds)
            SetPhase("observe",current.TargetIndex is int target?$"请注视目标 {target+1} · 实际反馈 4 秒":"保持无意图 · 自然眨眼，不注视刺激目标");
        else if(phase=="observe"&&rendering-phaseStarted>=current.ObserveSeconds)
            SetPhase(endOfTrialFeedback?"deciding":"wait",endOfTrialFeedback?"四秒刺激结束 · 正在生成本轮结果":
                current.WaitSeconds>0?$"等待 {current.WaitSeconds:0.0} 秒":"准备下一试次");
        else if(phase=="deciding") CommitFinalFeedback(rendering);
        else if(phase=="feedback"&&rendering-phaseStarted>=current.WaitSeconds)
        {
            CloseFeedback(); current=null; phase="waiting"; NextTrial();
        }
        else if(phase=="wait"&&rendering-phaseStarted>=current.WaitSeconds)
        {
            Marker("wait-end",rendering,host); current=null; phase="waiting"; NextTrial();
        }
    }
    private void CommitFinalFeedback(double rendering)
    {
        var result=finalWindow is null?null:runtime.RecognitionFor(finalWindow);
        // The final grid window can arrive after the display boundary because of transport/worker latency.
        // Wait for that exact window, never substitute an earlier accepted window; cap the wait at one second.
        if(finalWindow is not null&&result is null&&rendering-phaseStarted<1) return;
        heldFeedback=result?.Decision is {Accepted:true}?result.Decision.TargetId:null;
        string reason=result?.Decision.Reason??(finalWindow is null?"NoValidWindow":"DecisionTimeout");
        double committed=Now;
        runtime.RecordEvent("trial-feedback",new TrialFeedbackSelection(current!.TrialId,heldFeedback,
            finalWindow?.FirstSampleIndex,finalWindow?.LastSampleIndex,result?.HostSeconds??committed,committed,reason));
        string message=heldFeedback is not null?$"本轮结果：目标 {heldFeedback[1..]} · 反馈 0.5 秒":
            reason is "NoValidWindow" or "DecisionTimeout"?"本轮未识别：有效数据不足 · 反馈 0.5 秒":"本轮未识别：未达到阈值 · 反馈 0.5 秒";
        SetPhase("feedback",message);
        UpdateFeedback();
    }
    private void CloseFeedback()
    {
        if(phase=="feedback"&&current is not null) Marker("feedback-end",lastRendering,lastDrawHost);
    }
    private void NextTrial()
    {
        if(queue.Count==0)
        {
            runtime.RecordEvent("calibration-ended",new {completed,remaining=0}); IsActive=false;
            view.SetCalibrationPresentation("校准完成",null,false);
            Status=$"校准完成，共 {completed} 个有效试次，可结束实验后进行回放与阈值比较。"; Changed?.Invoke(); return;
        }
        string? issue=PreparationIssue();
        if(issue is not null) { ShowWaiting(issue); return; }
        current=queue.Dequeue(); observeStartSample=null; finalWindow=null; heldFeedback=null;
        if(endOfTrialFeedback)
            SetPhase("observe",$"请注视目标 {current.TargetIndex+1} · 刺激 4 秒后显示一次结果");
        else SetPhase("cue",current.TargetIndex is int target?$"准备注视目标 {target+1} · 看黄色提示框":"准备保持无意图：看中央空白或目标外");
    }
    private void SetPhase(string next,string cue)
    {
        pending=next; pendingCue=cue;
        if(next!="feedback") heldFeedback=null;
        bool guide=next=="cue"||(endOfTrialFeedback&&next=="observe");
        view.SetCalibrationPresentation(cue,guide&&current?.TargetIndex is int target?$"T{target+1}":null,next=="observe");
    }
    private void OnCuePresented(string cue,double rendering,double host)
    {
        if(!IsActive||!runtime.IsRunning||pending is null||cue!=pendingCue) return;
        // Begin and end stimulus intervals at actual drawing receipts, not dispatcher timer ticks.
        if(phase=="cue") Marker("cue-end",rendering,host);
        else if(phase=="observe")
        {
            var end=Marker("observe-end",rendering,host);
            if(endOfTrialFeedback)
                finalWindow=observeStartSample is double start&&end is not null&&TrialTiming.IsMapped(end)
                    ?runtime.FinalTrialWindow(start,TrialTiming.LastUsableSample(end)):null;
            else completed++;
        }
        phase=pending; pending=null; pendingCue=null; phaseStarted=rendering;
        var marker=Marker(phase+"-start",rendering,host);
        if(phase=="observe") observeStartSample=marker is not null&&TrialTiming.IsMapped(marker)?TrialTiming.FirstUsableSample(marker):null;
        if(phase=="feedback") completed++;
        string progress=current?.GroupNumber is int group?$"第 {group}/{groups} 组 · 目标 {current.TargetIndex+1}/6 · ":"";
        string retry=current?.ReplacesTrialId is not null?"补采 · ":"";
        phaseStatus=$"{retry}{progress}已完成 {completed}/{planned} · {cue}"; UpdatePhaseStatus();
        if(phase=="deciding") CommitFinalFeedback(rendering);
    }
    private TrialMarker? Marker(string mark,double rendering,double drawHost)
    {
        if(current is null) return null;
        var timing=runtime.CalibrationTiming; var sample=timing.SampleClock; var display=timing.RenderClock;
        bool mapped=timing.Ready;
        double host=mapped?display!.Estimate(rendering):drawHost;
        var marker=new TrialMarker(current.TrialId,mark,current.Label,current.TargetIndex is int target?$"T{target+1}":null,
            host,rendering,sample?.ModelId,mapped?sample!.Invert(host):null,timing.Quality,current.ReplacesTrialId,drawHost,display?.ModelId,timing.MarginSamples);
        runtime.RecordMarker(marker); return marker;
    }
}
