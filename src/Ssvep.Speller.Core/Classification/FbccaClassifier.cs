using Ssvep.Speller.Core.Stimulus;

namespace Ssvep.Speller.Core.Classification;

public sealed class FbccaClassifier : ISsvepClassifier
{
    private readonly int harmonicCount;

    public FbccaClassifier(int harmonicCount = 3)
    {
        if (harmonicCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(harmonicCount));
        }

        this.harmonicCount = harmonicCount;
    }

    public ClassificationResult Classify(
        double[,] window,
        IReadOnlyList<StimulusTarget> targets,
        int samplingRateHz)
    {
        if (targets.Count == 0)
        {
            throw new ArgumentException("At least one target is required.", nameof(targets));
        }

        int sampleCount = window.GetLength(0);
        IReadOnlyList<double[,]> subBands = FilterBankDecomposer.Decompose(
            window,
            samplingRateHz,
            harmonicCount);
        var scores = new Dictionary<string, double>(targets.Count);
        foreach (StimulusTarget target in targets)
        {
            double[,] reference = ReferenceSignalGenerator.Generate(
                target.FrequencyHz,
                samplingRateHz,
                sampleCount,
                harmonicCount,
                target.PhaseRadians);
            double score = 0.0;
            for (int subBand = 0; subBand < subBands.Count; subBand++)
            {
                double weight = Math.Pow(subBand + 1, -1.25) + 0.25;
                double rho = CcaMath.CanonicalCorrelation(subBands[subBand], reference);
                score += weight * rho * rho;
            }

            scores[target.Id] = score;
        }

        var ordered = scores.OrderByDescending(pair => pair.Value).ToArray();
        var best = ordered[0];
        var second = ordered.Length > 1 ? ordered[1] : new KeyValuePair<string, double>(string.Empty, 0.0);
        return new ClassificationResult(best.Key, best.Value, second.Key, second.Value, scores);
    }
}
