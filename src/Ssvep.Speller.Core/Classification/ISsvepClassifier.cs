using Ssvep.Speller.Core.Stimulus;

namespace Ssvep.Speller.Core.Classification;

public interface ISsvepClassifier
{
    ClassificationResult Classify(
        double[,] window,
        IReadOnlyList<StimulusTarget> targets,
        int samplingRateHz);
}
