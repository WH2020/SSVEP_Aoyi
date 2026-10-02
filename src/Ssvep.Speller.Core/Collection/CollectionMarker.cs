namespace Ssvep.Speller.Core.Collection;

using Ssvep.Speller.Core.Feedback;

public sealed record CollectionMarker(
    DateTimeOffset Timestamp,
    long? SampleIndex,
    int TrialIndex,
    CollectionPhase Phase,
    string EventName,
    string TargetId,
    FeedbackSource? FeedbackSource,
    string? FeedbackTargetId,
    bool? FeedbackMatchesCue,
    long? RenderFrameIndex = null,
    long? RenderingTimeTicks = null);
