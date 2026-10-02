using Ssvep.Speller.Core.Classification;
using Ssvep.Speller.Core.Decision;
using Ssvep.Speller.Core.Output;
using Ssvep.Speller.Core.Signal;
using Ssvep.Speller.Core.Stimulus;

namespace Ssvep.Speller.Tests;

public sealed class SimulatedTypingPipelineTests
{
    [Fact]
    public void SimulatedHelloSpaceSequenceProducesExpectedText()
    {
        IReadOnlyList<StimulusTarget> targets = StimulusLayout.CreateDefaultQwertyTargets();
        var classifier = new FbccaClassifier(harmonicCount: 3);
        var decisionEngine = new HybridDecisionEngine(new DecisionSettings(
            RequiredConsistentWindows: 1,
            MinBestScore: 0.2,
            MinScoreMargin: 0.01,
            CooldownWindows: 0));
        var composer = new TextComposer();

        foreach (string targetId in new[] { "H", "E", "L", "L", "O", "SPACE" })
        {
            StimulusTarget target = targets.Single(t => t.Id == targetId);
            double[,] window = SyntheticSsvepSignal.GenerateWindow(
                target.FrequencyHz,
                samplingRateHz: 250,
                sampleCount: 625,
                channelCount: 8,
                noiseAmplitude: 0.05,
                seed: targetId.GetHashCode(StringComparison.Ordinal));
            ClassificationResult classification = classifier.Classify(window, targets, samplingRateHz: 250);
            DecisionResult? decision = decisionEngine.Update(classification, Array.IndexOf(["H", "E", "L", "L", "O", "SPACE"], targetId));

            Assert.NotNull(decision);
            StimulusTarget committed = targets.Single(t => t.Id == decision.TargetId);
            composer.Apply(committed.Action);
        }

        Assert.Equal("HELLO ", composer.Text);
    }
}
