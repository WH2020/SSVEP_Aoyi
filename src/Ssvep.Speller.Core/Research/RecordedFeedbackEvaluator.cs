using System.Text.Json;

namespace Ssvep.Speller.Core.Research;

public sealed record RecordedTrialFeedback(string TrialId,string? TargetId,string? FirstSelection,bool Correct,
    double? DecisionDelaySeconds,double? DrawDelaySeconds);
public sealed record RecordedFeedbackReport(string Status,string ProfileHash,IReadOnlyList<RecordedTrialFeedback> Trials,
    int FalseTriggers,int InheritedTriggers,double NoIntentTotalSeconds,double NoIntentEvaluableSeconds,double WrongBorderSeconds,
    double LongestWrongBorderSeconds,RateInterval FalseTriggersPerMinute,double? DecisionMedianSeconds,double? DrawMedianSeconds,
    double? DecisionP95Seconds,double? DrawP95Seconds,string TimingBasis="WPF draw callback/host monotonic; not physical photon latency");

/// <summary>Observed live feedback is separate from counterfactual algorithm replay.</summary>
public static class RecordedFeedbackEvaluator
{
    private sealed record Frame(double Time,string? Target);
    private sealed record Recognition(double Time,long First,long Last,string? Target,bool Accepted);
    public static RecordedFeedbackReport Evaluate(IReadOnlyList<SessionEvent> events,string profileHash)
    {
        var frames=events.Where(e=>e.Kind=="frame"&&e.Data.TryGetProperty("targetId",out _))
            .Select(e=>new Frame(e.Data.GetProperty("hostSeconds").GetDouble(),e.Data.GetProperty("targetId").GetString())).OrderBy(f=>f.Time).ToArray();
        var decisions=events.Where(e=>e.Kind=="recognition"&&e.Data.TryGetProperty("decision",out _)).Select(e=>
        {
            var d=e.Data;var decision=d.GetProperty("decision");
            return new Recognition(d.GetProperty("hostSeconds").GetDouble(),d.GetProperty("firstSampleIndex").GetInt64(),d.GetProperty("lastSampleIndex").GetInt64(),
                decision.GetProperty("targetId").GetString(),decision.GetProperty("accepted").GetBoolean());
        }).OrderBy(d=>d.Time).ToArray();
        var decisionTimes=decisions.Select(d=>d.Time).ToArray();
        var markers=events.Where(e=>e.Kind=="trial-marker").Select(e=>JsonSerializer.Deserialize<TrialMarker>(e.Data.GetRawText(),CanonicalJson.Options)!).ToArray();
        var excluded=events.Where(e=>e.Kind=="trial-exclusion").Select(e=>e.Data.GetProperty("trialId").GetString()).ToHashSet();
        bool holdsFeedback=events.Any(e=>e.Kind=="calibration-plan"&&e.Data.TryGetProperty("targetFeedbackPolicy",out var policy)
            &&policy.GetString()==TrialFeedbackSelection.Policy);
        var heldFeedback=events.Where(e=>e.Kind=="trial-feedback")
            .Select(e=>JsonSerializer.Deserialize<TrialFeedbackSelection>(e.Data.GetRawText(),CanonicalJson.Options)!).ToArray();
        var trials=new List<RecordedTrialFeedback>();
        int triggers=0,inherited=0; double total=0,evaluable=0,wrong=0,longest=0;
        foreach(var group in markers.GroupBy(m=>m.TrialId))
        {
            var start=group.FirstOrDefault(m=>m.Phase=="observe-start");var end=group.LastOrDefault(m=>m.Phase=="observe-end");
            if(start is null||end is null||excluded.Contains(group.Key)) continue;
            double a=start.DrawHostSeconds??start.HostSeconds,b=end.DrawHostSeconds??end.HostSeconds;
            if(b<=a) continue;
            if(start.Label=="target"&&TrialTiming.IsMapped(start)&&TrialTiming.IsMapped(end))
            {
                double first=TrialTiming.FirstUsableSample(start),last=TrialTiming.LastUsableSample(end);
                if(holdsFeedback)
                {
                    var held=heldFeedback.FirstOrDefault(f=>f.TrialId==group.Key);
                    bool valid=held is not null&&held.FirstSampleIndex>=first&&held.LastSampleIndex<=last
                        &&held.DecisionHostSeconds>=a&&held.CommittedHostSeconds>=held.DecisionHostSeconds&&held.CommittedHostSeconds<=b;
                    var frame=valid?frames.FirstOrDefault(f=>f.Time>=held!.CommittedHostSeconds&&f.Time<=b&&f.Target==held.TargetId):null;
                    trials.Add(new(group.Key,start.TargetId,frame?.Target,frame is not null&&frame.Target==start.TargetId,
                        frame is null?null:held!.DecisionHostSeconds-a,frame?.Time-a));
                }
                else
                {
                    var decision=decisions.FirstOrDefault(d=>d.Accepted&&d.First>=first&&d.Last<=last&&d.Time>=a&&d.Time<=b);
                    var frame=decision is null?null:frames.FirstOrDefault(f=>f.Time>=decision.Time&&f.Time<=b&&f.Target==decision.Target);
                    trials.Add(new(group.Key,start.TargetId,decision?.Target,decision?.Target==start.TargetId,
                        decision?.Time-a,frame?.Time-a));
                }
            }
            if(start.Label!="no-intent") continue;
            total+=b-a; string? previous=null; double episode=0,lastEnd=double.NegativeInfinity; bool firstInterval=true;
            for(int i=0;i+1<frames.Length;i++)
            {
                var frame=frames[i]; double from=Math.Max(a,frame.Time),to=Math.Min(b,frames[i+1].Time);
                if(to<=from||frames[i+1].Time-frame.Time>.5) continue;
                // Exclude intervals without a recently evaluated window; raw pauses do not dilute rates.
                int decisionIndex=Array.BinarySearch(decisionTimes,from);
                if(decisionIndex<0) decisionIndex=~decisionIndex-1;
                var decision=decisionIndex>=0?decisions[decisionIndex]:null;
                if(decision is null||from-decision.Time>.5) continue;
                to=Math.Min(to,decision.Time+.5);
                if(to<=from) continue;
                if(from-lastEnd>.000001) {previous=null;episode=0;}
                if(frame.Target is not null&&frame.Target!=previous)
                {
                    triggers++;
                    if(firstInterval&&frames.LastOrDefault(f=>f.Time<a)?.Target==frame.Target) inherited++;
                }
                double duration=to-from; evaluable+=duration;
                if(frame.Target is not null) {wrong+=duration;episode+=duration;longest=Math.Max(longest,episode);} else episode=0;
                previous=frame.Target;lastEnd=to;firstInterval=false;
            }
        }
        double? Q(IEnumerable<double?> values,double q)
        {var valid=values.OfType<double>().ToArray();return valid.Length==0?null:RobustClockMapper.Quantile(valid,q);}
        bool limited=markers.Any(m=>m.TimingQuality==CalibrationClockPolicy.LimitedQuality);
        return new(frames.Length==0?"缺少绘制回执":markers.Length==0?"缺少标签":limited?"记录中的在线反馈（含时钟精度受限标签）":"记录中的在线反馈",profileHash,trials,triggers,inherited,total,evaluable,wrong,longest,
            ReplayEvaluator.Poisson(triggers,evaluable/60),Q(trials.Where(t=>t.Correct).Select(t=>t.DecisionDelaySeconds),.5),Q(trials.Where(t=>t.Correct).Select(t=>t.DrawDelaySeconds),.5),
            Q(trials.Where(t=>t.Correct).Select(t=>t.DecisionDelaySeconds),.95),Q(trials.Where(t=>t.Correct).Select(t=>t.DrawDelaySeconds),.95));
    }
}
