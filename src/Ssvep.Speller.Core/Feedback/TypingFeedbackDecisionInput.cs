using Ssvep.Speller.Core.Classification;

namespace Ssvep.Speller.Core.Feedback;

public sealed record TypingFeedbackDecisionInput(
    ClassificationResult Classification,
    FeedbackSource Source,
    string FeedbackTargetId);
