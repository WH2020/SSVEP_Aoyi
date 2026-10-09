namespace Ssvep.Speller.Core.Research;

public sealed record CalibrationTrial(string TrialId,string Label,int? TargetIndex,double CueSeconds,double ObserveSeconds,
    string? ReplacesTrialId=null,double WaitSeconds=0,int? GroupNumber=null);
public sealed record TrialMarker(string TrialId,string Phase,string Label,string? TargetId,double HostSeconds,
    double RenderingSeconds,string? ModelId,double? EstimatedSampleIndex,string TimingQuality,string? ReplacesTrialId=null,
    double? DrawHostSeconds=null,string? RenderModelId=null);

public sealed class CalibrationPlan
{
    public int Seed { get; }
    public int GroupCount { get; }
    public string Algorithm { get; }
    public IReadOnlyList<CalibrationTrial> Trials { get; }
    public static CalibrationPlan Personal(int seed) => new(seed,4,0,false,1,.5);

    public CalibrationPlan(int seed,int repetitions=10,double noIntentSeconds=180,bool randomize=true,double cueSeconds=2,double waitSeconds=0)
    {
        if(repetitions<1||!double.IsFinite(noIntentSeconds)||noIntentSeconds<0
            ||!double.IsFinite(cueSeconds)||cueSeconds<=0||!double.IsFinite(waitSeconds)||waitSeconds<0)
            throw new ArgumentOutOfRangeException(nameof(repetitions));
        Seed=seed; GroupCount=repetitions; Algorithm=randomize?"balanced-block-random-v1":"sequential-block-v1";
        var random=new Random(seed); var trials=new List<CalibrationTrial>();
        for(int block=0;block<repetitions;block++)
        {
            var order=Enumerable.Range(0,6).ToArray(); if(randomize) random.Shuffle(order);
            foreach(int target in order) trials.Add(new($"trial-{trials.Count+1:D3}","target",target,cueSeconds,4,
                WaitSeconds:waitSeconds,GroupNumber:block+1));
        }
        if(noIntentSeconds>0)
        {
            int parts=(int)Math.Ceiling(noIntentSeconds/60);
            for(int i=0;i<parts;i++) trials.Add(new($"no-intent-{i+1:D3}","no-intent",null,2,noIntentSeconds/parts));
        }
        Trials=trials.AsReadOnly();
    }
}
