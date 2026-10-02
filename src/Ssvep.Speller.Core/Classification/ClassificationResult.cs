namespace Ssvep.Speller.Core.Classification;

public sealed record ClassificationResult(
    string BestTargetId,
    double BestScore,
    string SecondTargetId,
    double SecondScore,
    IReadOnlyDictionary<string, double> Scores)
{
    public double Margin => BestScore - SecondScore;
}
