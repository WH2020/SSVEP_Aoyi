using System.Text.Json;

namespace Ssvep.Speller.Core.Research;

public sealed record ThresholdCandidate(ResearchDecision Settings,EvaluationMetrics Training,EvaluationMetrics Holdout,bool MeetsTrainingTargets);
public sealed record ThresholdSearchResult(string SourceSessionId,string ProfileHash,IReadOnlyList<string> TrainingTrials,
    IReadOnlyList<string> HoldoutTrials,IReadOnlyList<ThresholdCandidate> Candidates,string ValidationStatus="requires-independent-session");

public static class ParameterSearch
{
    public static ThresholdSearchResult Run(ReplayReport report,IReadOnlyList<SessionEvent> events,int rate,CancellationToken cancellation=default)
    {
        if(events.Any(e=>e.Kind=="calibration-plan"&&e.Data.TryGetProperty("purpose",out var purpose)&&purpose.GetString()=="independent-test"))
            throw new InvalidOperationException("独立测试会话不参与阈值搜索，请另用校准数据挑参。");
        if(report.ScoreVersion!="weighted-rho2-normalized-v1") throw new InvalidOperationException("请先用研发处理链回放，再扫描归一化阈值。");
        var trials=events.Where(e=>e.Kind=="trial-marker").Select(e=>JsonSerializer.Deserialize<TrialMarker>(e.Data.GetRawText(),CanonicalJson.Options)!)
            .Where(m=>m.Phase=="observe-start").DistinctBy(m=>m.TrialId).GroupBy(m=>m.TargetId??m.Label).ToArray();
        if(trials.Length==0) throw new InvalidOperationException("缺少校准标签，不能据此推荐阈值。");
        var train=new HashSet<string>(); var holdout=new HashSet<string>();
        foreach(var group in trials)
        {
            var values=group.OrderBy(m=>CanonicalJson.BytesHash(System.Text.Encoding.UTF8.GetBytes(m.TrialId)),StringComparer.Ordinal).ToArray();
            int count=values.Length<2?values.Length:Math.Clamp((int)Math.Floor(values.Length*.8),1,values.Length-1);
            foreach(var marker in values.Take(count)) train.Add(marker.TrialId);
            foreach(var marker in values.Skip(count)) holdout.Add(marker.TrialId);
        }
        SessionEvent[] Filter(HashSet<string> ids)=>events.Where(e=>e.Kind!="trial-marker"||ids.Contains(e.Data.GetProperty("trialId").GetString()!)).ToArray();
        var trainingEvents=Filter(train); var holdoutEvents=Filter(holdout); var candidates=new List<ThresholdCandidate>();
        foreach(double threshold in new[] {.10,.12,.15,.18,.21})
            foreach(string mode in new[] {"relative","absolute"})
                foreach(double gap in mode=="relative"?new[] {.10,.15,.20}:new[] {.015,.025,.035,.045})
                {
                    cancellation.ThrowIfCancellationRequested();
                    var settings=new ResearchDecision(threshold,mode,gap);
                    var rescored=report.Decisions.Select(d=>d with {Decision=SelectionPolicy.Evaluate(d.Scores.Result,settings)}).ToArray();
                    var training=ReplayEvaluator.Evaluate(rescored,trainingEvents,rate); var validation=ReplayEvaluator.Evaluate(rescored,holdoutEvents,rate);
                    bool meets=training.Accuracy.Value>=.95&&training.Coverage.Value>=.80&&training.FalseTriggersPerMinute<=.2;
                    candidates.Add(new(settings,training,validation,meets));
                }
        // Candidate ranking uses training only. The held-out trials never choose the winner.
        return new(report.SourceSessionId,report.ProfileHash,train.Order().ToArray(),holdout.Order().ToArray(),
            candidates.OrderByDescending(c=>c.MeetsTrainingTargets).ThenBy(c=>c.Training.FalseTriggersPerMinute??double.MaxValue)
                .ThenByDescending(c=>c.Training.Coverage.Value??0).ThenByDescending(c=>c.Training.Accuracy.Value??0).ToArray());
    }
}
