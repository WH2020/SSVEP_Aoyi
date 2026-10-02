namespace Ssvep.Speller.Core.Decision;

using Ssvep.Speller.Core.Feedback;

public enum DecisionKind
{
    Commit
}

public sealed record DecisionResult(
    DecisionKind Kind,
    string TargetId,
    double Score,
    long WindowIndex,
    FeedbackSource Source = FeedbackSource.TrueClassifier);
