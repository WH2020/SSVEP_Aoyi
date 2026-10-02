using Ssvep.Speller.Core.Classification;
using Ssvep.Speller.Core.Decision;
using Ssvep.Speller.Core.Feedback;

namespace Ssvep.Speller.Tests;

public sealed class TypingFeedbackRouterTests
{
    [Fact]
    public void TrueFeedbackUsesClassifierBestTargetAndMarksDecisionSource()
    {
        var engine = new HybridDecisionEngine(new DecisionSettings(
            RequiredConsistentWindows: 1,
            MinBestScore: 0.55,
            MinScoreMargin: 0.08,
            CooldownWindows: 0));
        ClassificationResult real = Result("A", 0.82, "B", 0.21);

        TypingFeedbackDecisionInput input = TypingFeedbackRouter.Resolve(
            TypingFeedbackMode.TrueFeedback,
            real,
            shamTargetId: "B",
            targetIds: ["A", "B", "C"]);
        DecisionResult? decision = engine.Update(input.Classification, windowIndex: 0, input.Source);

        Assert.NotNull(decision);
        Assert.Equal("A", decision.TargetId);
        Assert.Equal(FeedbackSource.TrueClassifier, decision.Source);
        Assert.Same(real, input.Classification);
    }

    [Fact]
    public void ShamFeedbackCommitsPresetTargetAfterSameDecisionRules()
    {
        var engine = new HybridDecisionEngine(new DecisionSettings(
            RequiredConsistentWindows: 3,
            MinBestScore: 0.55,
            MinScoreMargin: 0.08,
            CooldownWindows: 1));
        ClassificationResult real = Result("A", 0.90, "B", 0.15);

        DecisionResult? decision = null;
        for (int windowIndex = 0; windowIndex < 3; windowIndex++)
        {
            TypingFeedbackDecisionInput input = TypingFeedbackRouter.Resolve(
                TypingFeedbackMode.ShamFeedback,
                real,
                shamTargetId: "B",
                targetIds: ["A", "B", "C"]);
            decision = engine.Update(input.Classification, windowIndex, input.Source);
        }

        Assert.NotNull(decision);
        Assert.Equal("B", decision.TargetId);
        Assert.Equal(FeedbackSource.ShamPreset, decision.Source);
        Assert.Null(engine.Update(
            TypingFeedbackRouter.Resolve(
                TypingFeedbackMode.ShamFeedback,
                real,
                shamTargetId: "B",
                targetIds: ["A", "B", "C"]).Classification,
            windowIndex: 3,
            FeedbackSource.ShamPreset));
    }

    private static ClassificationResult Result(
        string bestTargetId,
        double bestScore,
        string secondTargetId,
        double secondScore)
    {
        return new ClassificationResult(
            bestTargetId,
            bestScore,
            secondTargetId,
            secondScore,
            new Dictionary<string, double>
            {
                [bestTargetId] = bestScore,
                [secondTargetId] = secondScore
            });
    }
}
