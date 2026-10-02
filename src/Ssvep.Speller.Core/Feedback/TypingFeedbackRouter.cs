using Ssvep.Speller.Core.Classification;

namespace Ssvep.Speller.Core.Feedback;

public static class TypingFeedbackRouter
{
    public static TypingFeedbackDecisionInput Resolve(
        TypingFeedbackMode mode,
        ClassificationResult trueClassification,
        string shamTargetId,
        IReadOnlyList<string> targetIds)
    {
        if (mode == TypingFeedbackMode.TrueFeedback)
        {
            return new TypingFeedbackDecisionInput(
                trueClassification,
                FeedbackSource.TrueClassifier,
                trueClassification.BestTargetId);
        }

        string[] distinctTargetIds = targetIds.Distinct(StringComparer.Ordinal).ToArray();
        if (!distinctTargetIds.Contains(shamTargetId, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Unknown sham feedback target '{shamTargetId}'.", nameof(shamTargetId));
        }

        string secondTargetId = distinctTargetIds.FirstOrDefault(id => !StringComparer.Ordinal.Equals(id, shamTargetId))
            ?? shamTargetId;
        var scores = distinctTargetIds.ToDictionary(
            id => id,
            id => StringComparer.Ordinal.Equals(id, shamTargetId) ? 1.0 : 0.0,
            StringComparer.Ordinal);
        var shamClassification = new ClassificationResult(
            shamTargetId,
            1.0,
            secondTargetId,
            secondTargetId == shamTargetId ? 0.0 : scores[secondTargetId],
            scores);
        return new TypingFeedbackDecisionInput(shamClassification, FeedbackSource.ShamPreset, shamTargetId);
    }
}
