using MathNet.Numerics.LinearAlgebra;
using Ssvep.Speller.Core.Classification;
using Ssvep.Speller.Core.Signal;

namespace Ssvep.Speller.Core.Research;

public sealed record ResearchScores(ClassificationResult Result, IReadOnlyDictionary<string,double> RawScores, string ScoreVersion);
public sealed record SelectionEvaluation(bool Accepted, string? TargetId, string Reason, double BestScore, double SecondScore, double Gap);

public static class SelectionPolicy
{
    public static SelectionEvaluation Evaluate(ClassificationResult result, ResearchDecision settings)
    {
        var values = result.Scores;
        if (values.Count != 6 || Enumerable.Range(1,6).Any(i => !values.ContainsKey($"T{i}")) || values.Values.Any(v => !double.IsFinite(v) || v < 0))
            return new(false,null,"InvalidScore",0,0,0);
        var order = values.OrderByDescending(p => p.Value).ToArray();
        double first = order[0].Value, second = order[1].Value;
        double gap = settings.GapMode == "relative" ? (first-second)/Math.Max(first,1e-12) : first-second;
        string reason = first-second <= 1e-12 ? "AmbiguousTie" : first < settings.MinScore ? "LowScore" : gap < settings.MinGap ? "SmallGap" : "Accepted";
        return new(reason == "Accepted",reason == "Accepted" ? order[0].Key : null,reason,first,second,gap);
    }
}

/// <summary>Push is called serially. Classify consumes immutable copied windows on a separate worker.</summary>
public sealed class ResearchPipeline
{
    private readonly RunContext context;
    private readonly StreamingFilterBank? filter;
    private long? previous;
    public SampleWindowScheduler Scheduler { get; }
    public IReadOnlyList<IReadOnlyList<Sos>>? FilterCoefficients => filter?.Coefficients;

    public ResearchPipeline(RunContext context)
    {
        this.context = context;
        filter = context.Profile.IsLegacy ? null : new(context.Profile.Filter,context.SamplingRateHz,8);
        Scheduler = new(context.SamplingRateHz,filter is null ? 8 : 24,context.WarmupSamples);
    }

    public void Reset() { previous = null; filter?.Reset(); Scheduler.Reset(); }

    public ScheduledWindow? Push(EegSample sample)
    {
        bool invalid = sample.IsLost || sample.Channels.Length != context.ChannelCount || context.Channels.Any(c => !double.IsFinite(sample.Channels[c]));
        if (invalid || previous is long prior && sample.SampleIndex != prior+1) Reset();
        previous = sample.SampleIndex;
        if (invalid) return null;
        var projection = context.Channels.Select(c => sample.Channels[c]).ToArray();
        return Scheduler.Push(sample with { Channels = filter?.Push(projection) ?? projection });
    }

    public ResearchScores Classify(ScheduledWindow window)
    {
        var p = context.Profile;
        if (p.IsLegacy)
        {
            var legacy = new FbccaClassifier().Classify(window.Window.Samples,p.Targets(),context.SamplingRateHz);
            return new(legacy,legacy.Scores,p.ScoreVersion);
        }
        int count = window.Window.Samples.GetLength(0);
        var bands = Enumerable.Range(0,3).Select(b =>
        {
            var data = new double[count,8];
            for (int row=0;row<count;row++) for(int c=0;c<8;c++) data[row,c] = window.Window.Samples[row,b*8+c];
            return Whiten(data);
        }).ToArray();
        var raw = new Dictionary<string,double>();
        foreach (var t in p.Targets())
        {
            var reference = Whiten(ReferenceSignalGenerator.Generate(t.FrequencyHz,context.SamplingRateHz,count,p.HarmonicCount,t.PhaseRadians));
            double score = 0;
            for (int b=0;b<3;b++)
            {
                var cross = bands[b].TransposeThisAndMultiply(reference) / (count-1);
                double rho = Math.Clamp(cross.Svd(false).S[0],0,1);
                score += p.BandWeights[b]*rho*rho;
            }
            raw[t.Id] = score;
        }
        var normalized = raw.ToDictionary(p => p.Key,pair => pair.Value/p.BandWeights.Sum());
        var sorted = normalized.OrderByDescending(pair => pair.Value).ToArray();
        var result = new ClassificationResult(sorted[0].Key,sorted[0].Value,sorted[1].Key,sorted[1].Value,normalized);
        return new(result,raw,p.ScoreVersion);
    }

    private static Matrix<double> Whiten(double[,] input)
    {
        var x = Matrix<double>.Build.DenseOfArray(input);
        for(int c=0;c<x.ColumnCount;c++)
        {
            double mean=x.Column(c).Average();
            double scale=Math.Sqrt(x.Column(c).Sum(v => (v-mean)*(v-mean))/Math.Max(1,x.RowCount-1));
            for(int r=0;r<x.RowCount;r++) x[r,c] = scale > 1e-30 ? (x[r,c]-mean)/scale : 0;
        }
        var covariance = x.TransposeThisAndMultiply(x)/(x.RowCount-1);
        var eigen = covariance.Evd(Symmetricity.Symmetric);
        var diagonal = Matrix<double>.Build.DenseDiagonal(x.ColumnCount,x.ColumnCount,i => 1/Math.Sqrt(Math.Max(0,eigen.EigenValues[i].Real)+1e-6));
        return x * eigen.EigenVectors * diagonal * eigen.EigenVectors.Transpose();
    }
}
