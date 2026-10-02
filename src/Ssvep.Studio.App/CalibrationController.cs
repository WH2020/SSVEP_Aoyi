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
    private int replacement;
    private int completed;
    public bool IsActive { get; private set; }
    public string Status { get; private set; }="校准建议：六目标各十次，另采三分钟无意图。";
    public event Action? Changed;
    public CalibrationController(StudioRuntime runtime,StudioStimulusView view)
    { this.runtime=runtime; this.view=view; runtime.FramePresented+=OnFrame; view.CuePresented+=OnCuePresented; }

    public void Start(int? seed=null,bool independentTest=false)
    {
        if(!runtime.IsRunning) throw new InvalidOperationException("请先开始实验。");
        if(IsActive) return;
        var plan=new CalibrationPlan(seed??Random.Shared.Next(),noIntentSeconds:independentTest?660:180); queue.Clear();
        foreach(var trial in plan.Trials) queue.Enqueue(trial);
        runtime.RecordEvent("calibration-plan",new {plan.Seed,plan.Trials,purpose=independentTest?"independent-test":"parameter-calibration",
            runtime.ActiveContext!.Profile.ProfileHash,algorithm="balanced-block-random-v1"});
        IsActive=true; current=null; pending=null; phase="waiting"; completed=0; replacement=0;
        view.CueText="校准准备：等待连续脑电与时钟映射"; Status=view.CueText; Changed?.Invoke();
    }

    public void ObserveState()
    {
        if(!IsActive) return;
        if(!runtime.IsRecording) { Cancel(); return; }
        if((!runtime.IsRunning||runtime.LastScores is null)&&current is not null)
        {
            ExcludeCurrent("pause-or-presentation-interruption");
            pending=null; phase="waiting"; view.CueText="校准休息中，恢复实验后继续"; Status=view.CueText; Changed?.Invoke();
        }
    }
    public void MarkDistracted()
    {
        if(!IsActive||current is null) throw new InvalidOperationException("当前没有可标记的试次。");
        ExcludeCurrent("operator-marked-distraction-or-contact-adjustment");
        pending=null; phase="waiting"; Status="已记录排除原因，试次将在末尾补采。"; Changed?.Invoke();
    }
    private void ExcludeCurrent(string reason)
    {
        if(current is null) return;
        runtime.RecordEvent("trial-exclusion",new {trialId=current.TrialId,reason,replacementPlanned=true});
        queue.Enqueue(current with {TrialId=current.TrialId+"-r"+(++replacement),ReplacesTrialId=current.TrialId}); current=null;
    }
    public void Cancel()
    {
        if(IsActive) runtime.RecordEvent("calibration-ended",new {completed,remaining=queue.Count,unfinishedTrial=current?.TrialId});
        IsActive=false; current=null; queue.Clear(); view.CueText=""; Status="校准已停止；原始数据和试次标记保留。"; Changed?.Invoke();
    }
    private void OnFrame(double rendering,double host)
    {
        if(!IsActive||!runtime.IsRunning) return;
        if(pending is not null) return;
        if(phase=="waiting")
        {
            if(runtime.LastScores is null||runtime.SampleClock?.Quality!="Estimated"||runtime.RenderClock?.Quality!="Estimated") return;
            if(queue.Count==0)
            {
                runtime.RecordEvent("calibration-ended",new {completed,remaining=0}); IsActive=false; view.CueText="校准完成"; Status="校准完成，可结束实验后进行回放与阈值比较。"; Changed?.Invoke(); return;
            }
            current=queue.Dequeue();
            SetPhase("cue",current.TargetIndex is int target?$"准备注视目标 {target+1}":"准备保持无意图：看中央空白或目标外"); return;
        }
        if(current is null) return;
        if(phase=="cue"&&rendering-phaseStarted>=current.CueSeconds)
        {
            Marker("cue-end",rendering,host);
            SetPhase("observe",current.TargetIndex is int target?$"请注视目标 {target+1}":"保持无意图 · 自然眨眼，不注视刺激目标");
        }
        else if(phase=="observe"&&rendering-phaseStarted>=current.ObserveSeconds)
        {
            Marker("observe-end",rendering,host); completed++; current=null; phase="waiting";
        }
    }
    private void SetPhase(string next,string cue)
    {
        pending=next; pendingCue=cue; view.CueText=cue;
    }
    private void OnCuePresented(string cue,double rendering,double host)
    {
        if(!IsActive||!runtime.IsRunning||pending is null||cue!=pendingCue) return;
        phase=pending; pending=null; pendingCue=null; phaseStarted=rendering;
        Marker(phase+"-start",rendering,host);
        Status=$"已完成 {completed} 个试次 · {cue}"; Changed?.Invoke();
    }
    private void Marker(string mark,double rendering,double drawHost)
    {
        if(current is null) return;
        var sample=runtime.SampleClock; var display=runtime.RenderClock;
        double host=display?.Quality=="Estimated"?display.Estimate(rendering):drawHost;
        bool mapped=sample?.Quality=="Estimated"&&display?.Quality=="Estimated";
        runtime.RecordMarker(new TrialMarker(current.TrialId,mark,current.Label,current.TargetIndex is int target?$"T{target+1}":null,
            host,rendering,sample?.ModelId,mapped?sample!.Invert(host):null,mapped?"Estimated":"Uncalibrated",current.ReplacesTrialId,drawHost,display?.ModelId));
    }
}
