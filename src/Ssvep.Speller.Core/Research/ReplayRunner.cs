using System.Text.Json;
using MathNet.Numerics.Distributions;

namespace Ssvep.Speller.Core.Research;

public sealed record ReplayDecision(long Run,long Continuity,long Ordinal,long FirstSampleIndex,long LastSampleIndex,
    double? EstimatedHostSeconds,string? ClockModelId,ResearchScores Scores,SelectionEvaluation Decision);
public sealed record TrialOutcome(string TrialId,string Label,string? TargetId,string? FirstSelection,bool Covered,bool Correct,double? DelaySeconds,string Status);
public sealed record MetricInterval(double? Value,double? Lower95,double? Upper95,int Numerator,int Denominator);
public sealed record RateInterval(double? Value,double? Lower95,double? Upper95);
public sealed record EvaluationMetrics(string Status,IReadOnlyList<TrialOutcome> Trials,MetricInterval Accuracy,MetricInterval Coverage,
    double? FalseTriggersPerMinute,int FalseTriggers,double NoIntentMinutes,double? MedianDelaySeconds,double? DelayP95Seconds,
    double WrongSelectionSeconds,string TimingBasis,RateInterval? FalseTriggerInterval=null,double LongestWrongSelectionSeconds=0,
    IReadOnlyDictionary<string,IReadOnlyDictionary<string,int>>? Confusion=null);
public sealed record ReplayConditions(string RunConfigHash,int SamplingRateHz,int ChannelCount,IReadOnlyList<int> RecognitionChannels,
    DisplayGeometry Display,string SampleUnit,string HardwareFilterStatus="not-verified");
public sealed record ReplayReport(string SoftwareVersion,string SourceSessionId,string DataSource,string ProfileHash,string AlgorithmVersion,
    string ScoreVersion,DateTimeOffset CreatedAt,int WindowCount,EvaluationMetrics Metrics,IReadOnlyList<ReplayDecision> Decisions,
    IReadOnlyList<string> Warnings,string EvaluationMode="algorithm-replay",string ValidationStatus="unverified",
    ReplayConditions? Conditions=null,RecordedFeedbackReport? RecordedFeedback=null);

public static class ReplayRunner
{
    public static ReplayReport Run(string folder,ResearchProfile? candidate=null,CancellationToken cancellation=default,IProgress<int>? progress=null)
    {
        var reader=new SessionReader(folder); reader.VerifyFiles();
        var original=reader.Context; var profile=candidate??original.Profile;
        profile.Validate(original.SamplingRateHz);
        if(!profile.TargetFrequenciesHz.SequenceEqual(original.Profile.TargetFrequenciesHz)) throw new ArgumentException("刺激频率改变必须重新采集，不能用这份 EEG 比较新频率。");
        var context=new RunContext(profile,original.Content,original.SamplingRateHz,original.ChannelCount,original.Channels,original.ElectrodeBindings,original.Display,original.SampleUnit,original.ScaleToVolts,original.UnitSource);
        var pipeline=new ResearchPipeline(context); var results=new List<ReplayDecision>();
        var evidence=reader.Events("filter-reset","state","clock-model","trial-marker","trial-exclusion","recognition","frame").ToArray();
        var resets=evidence.Where(e=>e.Kind=="filter-reset"&&e.Data.TryGetProperty("reason",out var why)&&why.GetString()=="unlocated-loss-notice")
            .GroupBy(e=>e.Data.GetProperty("run").GetInt64()).ToDictionary(g=>g.Key,g=>new Queue<long>(g.Select(e=>e.Data.GetProperty("afterSampleIndex").GetInt64()).Order()));
        var ends=evidence.Where(e=>e.Kind=="state"&&e.Data.TryGetProperty("afterSampleIndex",out _)&&e.Data.GetProperty("state").GetString()!="started")
            .GroupBy(e=>e.Data.GetProperty("run").GetInt64()).ToDictionary(g=>g.Key,g=>g.Min(e=>e.Data.GetProperty("afterSampleIndex").GetInt64()));
        var models=evidence.Where(e=>e.Kind=="clock-model").Select(e=>JsonSerializer.Deserialize<ClockModel>(e.Data.GetRawText(),CanonicalJson.Options)!)
            .Where(m=>m.SourceDomain=="device-sample-index").OrderBy(m=>m.LastX).ToArray();
        long currentRun=-1; bool wasEnabled=false;
        foreach(var stored in reader.ReadSamples())
        {
            cancellation.ThrowIfCancellationRequested();
            if(!stored.Info.RecognitionEnabled) { wasEnabled=false; continue; }
            if(stored.Info.RunGeneration!=currentRun||!wasEnabled) { pipeline=new(context); currentRun=stored.Info.RunGeneration; }
            wasEnabled=true;
            var mapping=models.LastOrDefault(m=>m.LastX<=stored.Batch.Samples[^1].SampleIndex&&m.Quality=="Estimated");
            foreach(var sample in stored.Batch.Samples)
            {
                cancellation.ThrowIfCancellationRequested();
                if(ends.TryGetValue(currentRun,out long end)&&sample.SampleIndex>end) break;
                if(resets.TryGetValue(currentRun,out var pending))
                    while(pending.Count>0&&sample.SampleIndex>pending.Peek()) { pending.Dequeue(); pipeline.Reset(); }
                var window=pipeline.Push(sample);
                if(window is null) continue;
                var scores=pipeline.Classify(window);
                results.Add(new(currentRun,window.Continuity,window.Ordinal,window.Window.FirstSampleIndex,window.Window.LastSampleIndex,
                    mapping?.Estimate(window.Window.LastSampleIndex),mapping?.ModelId,scores,SelectionPolicy.Evaluate(scores.Result,profile.Decision)));
                if(results.Count%20==0) progress?.Report(results.Count);
            }
        }
        var metrics=ReplayEvaluator.Evaluate(results,evidence,original.SamplingRateHz);
        return new("0.3.0",reader.Manifest.GetProperty("sessionId").GetString()!,reader.Manifest.GetProperty("dataSource").GetString()!,profile.ProfileHash,
            profile.AlgorithmVersion,profile.ScoreVersion,DateTimeOffset.UtcNow,results.Count,metrics,results,reader.Warnings.Distinct().ToArray(),
            Conditions:new(original.RunConfigHash,original.SamplingRateHz,original.ChannelCount,original.Channels,original.Display,original.SampleUnit),
            RecordedFeedback:RecordedFeedbackEvaluator.Evaluate(evidence,original.Profile.ProfileHash));
    }
}

public static class ReplayEvaluator
{
    public static EvaluationMetrics Evaluate(IReadOnlyList<ReplayDecision> decisions,IReadOnlyList<SessionEvent> events,int rate)
    {
        var markers=events.Where(e=>e.Kind=="trial-marker").Select(e=>JsonSerializer.Deserialize<TrialMarker>(e.Data.GetRawText(),CanonicalJson.Options)!).ToArray();
        var excluded=events.Where(e=>e.Kind=="trial-exclusion").Select(e=>e.Data.GetProperty("trialId").GetString()).ToHashSet();
        var outcomes=new List<TrialOutcome>(); int falseTriggers=0; double noIntentSeconds=0,wrongSeconds=0,longestWrong=0;
        foreach(var group in markers.GroupBy(m=>m.TrialId))
        {
            var start=group.FirstOrDefault(m=>m.Phase=="observe-start"); var end=group.LastOrDefault(m=>m.Phase=="observe-end");
            if(start is null||end is null||excluded.Contains(group.Key)||start.EstimatedSampleIndex is null||end.EstimatedSampleIndex is null
                ||start.TimingQuality!="Estimated"||end.TimingQuality!="Estimated"||end.EstimatedSampleIndex<=start.EstimatedSampleIndex)
            { outcomes.Add(new(group.Key,start?.Label??"unknown",start?.TargetId,null,false,false,null,"excluded-or-unmapped")); continue; }
            // Only complete windows inside the instructed interval count, with no reset at target changes.
            var valid=decisions.Where(d=>d.FirstSampleIndex>=start.EstimatedSampleIndex&&d.LastSampleIndex<=end.EstimatedSampleIndex).ToArray();
            if(start.Label=="target")
            {
                var first=valid.FirstOrDefault(d=>d.Decision.Accepted);
                double? delay=first is null?null:(first.LastSampleIndex-start.EstimatedSampleIndex)/rate;
                outcomes.Add(new(group.Key,start.Label,start.TargetId,first?.Decision.TargetId,first is not null,
                    first?.Decision.TargetId==start.TargetId,delay,valid.Length==0?"no-valid-window":"evaluated"));
            }
            else if(start.Label=="no-intent")
            {
                string? previous=null; double episode=0;
                for(int i=0;i<valid.Length;i++)
                {
                    var current=valid[i];
                    if(i>0&&(current.Run!=valid[i-1].Run||current.Continuity!=valid[i-1].Continuity||current.LastSampleIndex-valid[i-1].LastSampleIndex!=rate/5))
                    {previous=null;episode=0;}
                    double seconds=Math.Min(.2,Math.Max(0,(end.EstimatedSampleIndex.Value-current.LastSampleIndex)/rate));
                    if(i+1<valid.Length) seconds=Math.Min(.2,Math.Max(0,(valid[i+1].LastSampleIndex-current.LastSampleIndex)/(double)rate));
                    noIntentSeconds+=seconds;
                    string? target=current.Decision.TargetId;
                    if(target is not null&&target!=previous) falseTriggers++;
                    if(target is not null) {wrongSeconds+=seconds;episode+=seconds;longestWrong=Math.Max(longestWrong,episode);} else episode=0;
                    previous=target;
                }
                outcomes.Add(new(group.Key,start.Label,null,null,false,false,null,valid.Length==0?"no-valid-window":"evaluated"));
            }
        }
        var targets=outcomes.Where(t=>t.Label=="target"&&t.Status is "evaluated" or "no-valid-window").ToArray();
        int accepted=targets.Count(t=>t.Covered),correct=targets.Count(t=>t.Correct);
        var delays=targets.Where(t=>t.Correct&&t.DelaySeconds is not null).Select(t=>t.DelaySeconds!.Value).ToArray();
        return new(markers.Length==0?"缺少标签":targets.Length==0?"缺少可映射有效试次":"算法回放估计",outcomes,
            Wilson(correct,accepted),Wilson(accepted,targets.Length),noIntentSeconds>0?falseTriggers/(noIntentSeconds/60):null,
            falseTriggers,noIntentSeconds/60,delays.Length==0?null:RobustClockMapper.Quantile(delays,.5),delays.Length==0?null:RobustClockMapper.Quantile(delays,.95),
            wrongSeconds,"sample-index estimate; complete windows only; not measured display latency; fixed acquisition/display delay uncalibrated",
            Poisson(falseTriggers,noIntentSeconds/60),longestWrong,
            Enumerable.Range(1,6).ToDictionary(t=>$"T{t}",t=>(IReadOnlyDictionary<string,int>)Enumerable.Range(1,6).Select(s=>$"T{s}").Append("rejected")
                .ToDictionary(s=>s,s=>targets.Count(tr=>tr.TargetId==$"T{t}"&&(tr.FirstSelection??"rejected")==s))));
    }

    public static RateInterval Poisson(int count,double minutes)=>minutes<=0?new(null,null,null)
        :new(count/minutes,count==0?0:ChiSquared.InvCDF(2*count,.025)/(2*minutes),ChiSquared.InvCDF(2*(count+1),.975)/(2*minutes));

    private static MetricInterval Wilson(int numerator,int denominator)
    {
        if(denominator==0) return new(null,null,null,numerator,denominator);
        double p=numerator/(double)denominator,z=1.959963984540054,z2=z*z,d=1+z2/denominator;
        double center=(p+z2/(2*denominator))/d,half=z*Math.Sqrt(p*(1-p)/denominator+z2/(4.0*denominator*denominator))/d;
        return new(p,Math.Max(0,center-half),Math.Min(1,center+half),numerator,denominator);
    }
}
