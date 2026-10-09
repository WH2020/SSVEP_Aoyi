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
    private double? observeStartSample;
    private int replacement;
    private int completed;
    private int groups;
    private int planned;
    public bool IsActive { get; private set; }
    public string Status { get; private set; }="个人校准：目标 1→6，依次完成四组；提示 1 秒 → 实际反馈 4 秒 → 等待 0.5 秒。";
    public event Action? Changed;
    public CalibrationController(StudioRuntime runtime,StudioStimulusView view)
    { this.runtime=runtime; this.view=view; runtime.FramePresented+=OnFrame; view.CuePresented+=OnCuePresented; }

    public void Start(int? seed=null,bool independentTest=false)
    {
        if(!runtime.IsRunning) throw new InvalidOperationException("请先开始实验。");
        if(IsActive) return;
        int selectedSeed=seed??Random.Shared.Next();
        var plan=independentTest?new CalibrationPlan(selectedSeed,noIntentSeconds:660):CalibrationPlan.Personal(selectedSeed);
        queue.Clear(); foreach(var trial in plan.Trials) queue.Enqueue(trial);
        runtime.RecordEvent("calibration-plan",new {plan.Seed,plan.Trials,plan.GroupCount,purpose=independentTest?"independent-test":"parameter-calibration",
            runtime.ActiveContext!.Profile.ProfileHash,plan.Algorithm});
        IsActive=true; current=null; pending=null; pendingCue=null; phase="waiting"; completed=0; replacement=0;
        groups=plan.GroupCount; planned=plan.Trials.Count; observeStartSample=null;
        ShowWaiting("校准准备：等待连续脑电与时钟映射");
    }

    public void ObserveState()
    {
        if(!IsActive) return;
        if(!runtime.IsRecording) { Cancel(); return; }
        if((!runtime.IsRunning||runtime.LastScores is null)&&current is not null)
        {
            // A completed feedback interval is retained if an interruption occurs during its wait.
            if(phase!="wait") ExcludeCurrent("pause-or-presentation-interruption");
            current=null; pending=null; pendingCue=null; phase="waiting";
            ShowWaiting("校准休息中，恢复实验后继续");
        }
        UpdateFeedback();
    }
    public void MarkDistracted()
    {
        if(!IsActive||current is null||phase=="wait") throw new InvalidOperationException("请在目标提示或实际反馈期间标记走神。");
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
        if(IsActive) runtime.RecordEvent("calibration-ended",new {completed,remaining=queue.Count,unfinishedTrial=phase=="wait"?null:current?.TrialId});
        IsActive=false; current=null; pending=null; pendingCue=null; queue.Clear(); observeStartSample=null;
        view.ClearCalibrationPresentation(); Status="校准已停止；原始数据和试次标记保留。"; Changed?.Invoke();
    }
    private void ShowWaiting(string text)
    {
        observeStartSample=null; view.SetCalibrationPresentation(text,null,false); Status=text; Changed?.Invoke();
    }
    private void UpdateFeedback()
    {
        // A result from the preceding target, cue or rest must never appear as this trial's feedback.
        view.FeedbackEnabled=pending is null&&phase=="observe"&&runtime.IsRunning
            &&observeStartSample is double start&&runtime.LastDecisionFirstSampleIndex is long first&&first>=start;
    }
    private void OnFrame(double rendering,double host)
    {
        if(!IsActive||!runtime.IsRunning||pending is not null) return;
        UpdateFeedback();
        if(phase=="waiting") { NextTrial(); return; }
        if(current is null) return;
        if(phase=="cue"&&rendering-phaseStarted>=current.CueSeconds)
            SetPhase("observe",current.TargetIndex is int target?$"请注视目标 {target+1} · 实际反馈 4 秒":"保持无意图 · 自然眨眼，不注视刺激目标");
        else if(phase=="observe"&&rendering-phaseStarted>=current.ObserveSeconds)
            SetPhase("wait",current.WaitSeconds>0?$"等待 {current.WaitSeconds:0.0} 秒":"准备下一试次");
        else if(phase=="wait"&&rendering-phaseStarted>=current.WaitSeconds)
        {
            Marker("wait-end",rendering,host); current=null; phase="waiting"; NextTrial();
        }
    }
    private void NextTrial()
    {
        if(queue.Count==0)
        {
            runtime.RecordEvent("calibration-ended",new {completed,remaining=0}); IsActive=false;
            view.SetCalibrationPresentation("校准完成",null,false);
            Status=$"校准完成，共 {completed} 个有效试次，可结束实验后进行回放与阈值比较。"; Changed?.Invoke(); return;
        }
        if(runtime.LastScores is null||runtime.SampleClock?.Quality!="Estimated"||runtime.RenderClock?.Quality!="Estimated") return;
        current=queue.Dequeue();
        SetPhase("cue",current.TargetIndex is int target?$"准备注视目标 {target+1} · 看黄色提示框":"准备保持无意图：看中央空白或目标外");
    }
    private void SetPhase(string next,string cue)
    {
        pending=next; pendingCue=cue;
        view.SetCalibrationPresentation(cue,next=="cue"&&current?.TargetIndex is int target?$"T{target+1}":null,next=="observe");
    }
    private void OnCuePresented(string cue,double rendering,double host)
    {
        if(!IsActive||!runtime.IsRunning||pending is null||cue!=pendingCue) return;
        // Start and end intervals at the drawing receipt of the new presentation.
        if(phase=="cue") Marker("cue-end",rendering,host);
        else if(phase=="observe") { Marker("observe-end",rendering,host); completed++; }
        phase=pending; pending=null; pendingCue=null; phaseStarted=rendering;
        var marker=Marker(phase+"-start",rendering,host);
        observeStartSample=phase=="observe"?marker?.EstimatedSampleIndex:null;
        string progress=current?.GroupNumber is int group?$"第 {group}/{groups} 组 · 目标 {current.TargetIndex+1}/6 · ":"";
        string retry=current?.ReplacesTrialId is not null?"补采 · ":"";
        Status=$"{retry}{progress}已完成 {completed}/{planned} · {cue}"; Changed?.Invoke();
    }
    private TrialMarker? Marker(string mark,double rendering,double drawHost)
    {
        if(current is null) return null;
        var sample=runtime.SampleClock; var display=runtime.RenderClock;
        double host=display?.Quality=="Estimated"?display.Estimate(rendering):drawHost;
        bool mapped=sample?.Quality=="Estimated"&&display?.Quality=="Estimated";
        var marker=new TrialMarker(current.TrialId,mark,current.Label,current.TargetIndex is int target?$"T{target+1}":null,
            host,rendering,sample?.ModelId,mapped?sample!.Invert(host):null,mapped?"Estimated":"Uncalibrated",current.ReplacesTrialId,drawHost,display?.ModelId);
        runtime.RecordMarker(marker); return marker;
    }
}
