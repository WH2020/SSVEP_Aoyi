using Ssvep.Speller.Core.Classification;
using Ssvep.Speller.Core.Signal;
using Ssvep.Speller.Core.Stimulus;

namespace Ssvep.Speller.Tests;

public sealed class ClassifierTests
{
    [Fact]
    public void FilterBankAttenuatesFundamentalBelowHigherSubbandCutoff()
    {
        const int samplingRateHz = 250;
        var window = new double[625, 1];
        for (int sample = 0; sample < window.GetLength(0); sample++)
        {
            window[sample, 0] = Math.Sin(2.0 * Math.PI * 10.0 * sample / samplingRateHz);
        }

        IReadOnlyList<double[,]> subBands = FilterBankDecomposer.Decompose(
            window,
            samplingRateHz,
            subBandCount: 3);

        Assert.Equal(3, subBands.Count);
        double firstBandRms = RootMeanSquare(subBands[0]);
        double secondBandRms = RootMeanSquare(subBands[1]);
        Assert.True(firstBandRms > secondBandRms * 4.0);
    }

    [Fact]
    public void CcaClassifierSelectsTheMatchingFrequencyForCleanSyntheticSignal()
    {
        IReadOnlyList<StimulusTarget> targets = CreateTargets();
        double[,] window = SyntheticSsvepSignal.GenerateWindow(
            targetFrequencyHz: 10.0,
            samplingRateHz: 250,
            sampleCount: 625,
            channelCount: 8,
            noiseAmplitude: 0.0,
            seed: 1);

        ClassificationResult result = new CcaClassifier(harmonicCount: 3)
            .Classify(window, targets, samplingRateHz: 250);

        Assert.Equal("T10", result.BestTargetId);
        Assert.True(result.BestScore > result.SecondScore);
    }

    [Fact]
    public void FbccaClassifierHasAtLeastTheCcaMarginForHarmonicRichSignal()
    {
        IReadOnlyList<StimulusTarget> targets = CreateTargets();
        double[,] window = SyntheticSsvepSignal.GenerateWindow(
            targetFrequencyHz: 11.0,
            samplingRateHz: 250,
            sampleCount: 625,
            channelCount: 8,
            noiseAmplitude: 0.25,
            seed: 42);

        ClassificationResult cca = new CcaClassifier(harmonicCount: 3)
            .Classify(window, targets, samplingRateHz: 250);
        ClassificationResult fbcca = new FbccaClassifier(harmonicCount: 3)
            .Classify(window, targets, samplingRateHz: 250);

        Assert.Equal("T11", fbcca.BestTargetId);
        Assert.True(fbcca.Margin >= cca.Margin);
    }

    private static IReadOnlyList<StimulusTarget> CreateTargets()
    {
        return new[]
        {
            new StimulusTarget("T09", "9", 9.0, 0.0, new TargetAction.Letter('A')),
            new StimulusTarget("T10", "10", 10.0, 0.0, new TargetAction.Letter('B')),
            new StimulusTarget("T11", "11", 11.0, 0.0, new TargetAction.Letter('C')),
            new StimulusTarget("T12", "12", 12.0, 0.0, new TargetAction.Letter('D')),
        };
    }

    private static double RootMeanSquare(double[,] data)
    {
        return Math.Sqrt(data.Cast<double>().Select(value => value * value).Average());
    }
}
