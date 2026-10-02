using Ssvep.Speller.Core.Classification;
using Ssvep.Speller.Core.Decision;

namespace Ssvep.Speller.Tests;

public sealed class HybridDecisionEngineTests
{
    [Fact]
    public void LowConfidenceWindowBreaksTheConsistencySequence()
    {
        var engine = new HybridDecisionEngine(new DecisionSettings(
            RequiredConsistentWindows: 3,
            MinBestScore: 0.55,
            MinScoreMargin: 0.12,
            CooldownWindows: 0));
        var confident = new ClassificationResult(
            "A",
            0.80,
            "B",
            0.60,
            new Dictionary<string, double> { ["A"] = 0.80, ["B"] = 0.60 });
        var lowConfidence = new ClassificationResult(
            "A",
            0.50,
            "B",
            0.45,
            new Dictionary<string, double> { ["A"] = 0.50, ["B"] = 0.45 });

        Assert.Null(engine.Update(confident, 0));
        Assert.Null(engine.Update(confident, 1));
        Assert.Null(engine.Update(lowConfidence, 2));
        Assert.Null(engine.Update(confident, 3));
        Assert.Null(engine.Update(confident, 4));
        Assert.NotNull(engine.Update(confident, 5));
    }

    [Fact]
    public void EngineCommitsAfterConsistentConfidentWindowsThenHonorsCooldown()
    {
        var engine = new HybridDecisionEngine(new DecisionSettings(
            RequiredConsistentWindows: 3,
            MinBestScore: 0.55,
            MinScoreMargin: 0.12,
            CooldownWindows: 2));

        var result = new ClassificationResult(
            "A",
            0.80,
            "B",
            0.60,
            new Dictionary<string, double> { ["A"] = 0.80, ["B"] = 0.60 });

        Assert.Null(engine.Update(result, 0));
        Assert.Null(engine.Update(result, 1));

        DecisionResult? commit = engine.Update(result, 2);

        Assert.NotNull(commit);
        Assert.Equal("A", commit.TargetId);
        Assert.Equal(DecisionKind.Commit, commit.Kind);
        Assert.Null(engine.Update(result, 3));
        Assert.Null(engine.Update(result, 4));
        Assert.Null(engine.Update(result, 5));
    }

    [Fact]
    public void EngineDoesNotCommitWhenMarginIsTooSmall()
    {
        var engine = new HybridDecisionEngine(new DecisionSettings(
            RequiredConsistentWindows: 3,
            MinBestScore: 0.55,
            MinScoreMargin: 0.12,
            CooldownWindows: 2));

        var lowMargin = new ClassificationResult(
            "A",
            0.80,
            "B",
            0.76,
            new Dictionary<string, double> { ["A"] = 0.80, ["B"] = 0.76 });

        Assert.Null(engine.Update(lowMargin, 0));
        Assert.Null(engine.Update(lowMargin, 1));
        Assert.Null(engine.Update(lowMargin, 2));
    }
}
