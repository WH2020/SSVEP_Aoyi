namespace Ssvep.Speller.Core.Research;

public sealed record CalibrationTrial(string TrialId,string Label,int? TargetIndex,double CueSeconds,double ObserveSeconds,string? ReplacesTrialId=null);
public sealed record TrialMarker(string TrialId,string Phase,string Label,string? TargetId,double HostSeconds,
    double RenderingSeconds,string? ModelId,double? EstimatedSampleIndex,string TimingQuality,string? ReplacesTrialId=null,
    double? DrawHostSeconds=null,string? RenderModelId=null);

public sealed class CalibrationPlan
{
    public int Seed { get; }
    public IReadOnlyList<CalibrationTrial> Trials { get; }
    public CalibrationPlan(int seed,int repetitions=10,double noIntentSeconds=180)
    {
        if(repetitions<1||noIntentSeconds<0) throw new ArgumentOutOfRangeException(nameof(repetitions));
        Seed=seed; var random=new Random(seed); var trials=new List<CalibrationTrial>();
        for(int block=0;block<repetitions;block++)
        {
            var order=Enumerable.Range(0,6).ToArray(); random.Shuffle(order);
            foreach(int target in order) trials.Add(new($"trial-{trials.Count+1:D3}","target",target,2,4));
        }
        if(noIntentSeconds>0)
        {
            int parts=(int)Math.Ceiling(noIntentSeconds/60);
            for(int i=0;i<parts;i++) trials.Add(new($"no-intent-{i+1:D3}","no-intent",null,2,noIntentSeconds/parts));
        }
        Trials=trials.AsReadOnly();
    }
}
