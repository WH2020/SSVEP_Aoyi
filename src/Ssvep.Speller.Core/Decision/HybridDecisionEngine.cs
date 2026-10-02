using Ssvep.Speller.Core.Classification;
using Ssvep.Speller.Core.Feedback;

namespace Ssvep.Speller.Core.Decision;

public sealed class HybridDecisionEngine
{
    private readonly DecisionSettings settings;
    private string? previousTargetId;
    private int consistentCount;
    private int cooldownRemaining;

    public HybridDecisionEngine(DecisionSettings settings)
    {
        if (settings.RequiredConsistentWindows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings));
        }

        this.settings = settings;
    }

    public DecisionResult? Update(
        ClassificationResult result,
        long windowIndex,
        FeedbackSource source = FeedbackSource.TrueClassifier)
    {
        if (cooldownRemaining > 0)
        {
            cooldownRemaining--;
            previousTargetId = null;
            consistentCount = 0;
            return null;
        }

        bool confident = result.BestScore >= settings.MinBestScore && result.Margin >= settings.MinScoreMargin;
        if (!confident)
        {
            previousTargetId = null;
            consistentCount = 0;
            return null;
        }

        Track(result);
        if (consistentCount < settings.RequiredConsistentWindows)
        {
            return null;
        }

        cooldownRemaining = settings.CooldownWindows;
        consistentCount = 0;
        previousTargetId = null;
        return new DecisionResult(DecisionKind.Commit, result.BestTargetId, result.BestScore, windowIndex, source);
    }

    private void Track(ClassificationResult result)
    {
        if (result.BestTargetId == previousTargetId)
        {
            consistentCount++;
            return;
        }

        previousTargetId = result.BestTargetId;
        consistentCount = 1;
    }
}
