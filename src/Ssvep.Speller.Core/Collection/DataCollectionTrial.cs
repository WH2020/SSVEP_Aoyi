namespace Ssvep.Speller.Core.Collection;

using Ssvep.Speller.Core.Feedback;

public sealed record DataCollectionTrial(
    int TrialIndex,
    string TargetId,
    string TargetLabel,
    double FrequencyHz,
    string FeedbackTargetId,
    bool FeedbackMatchesCue,
    FeedbackSource FeedbackSource);
