using Ssvep.Speller.Core.Stimulus;

namespace Ssvep.Speller.Tests;

public sealed class StimulusLayoutTests
{
    [Fact]
    public void DefaultQwertyTargetsContainDigitsLettersAndCommandsWithExpectedFrequencies()
    {
        IReadOnlyList<StimulusTarget> targets = StimulusLayout.CreateDefaultQwertyTargets();

        Assert.Equal(40, targets.Count);
        Assert.Contains(targets, target => target.Id == "1" && target.Action is TargetAction.Digit);
        Assert.Contains(targets, target => target.Id == "0" && target.Action is TargetAction.Digit);
        Assert.Contains(targets, target => target.Id == "Q" && target.Action is TargetAction.Letter);
        Assert.Contains(targets, target => target.Id == "M" && target.Action is TargetAction.Letter);
        Assert.Contains(targets, target => target.Id == "SPACE" && target.Action is TargetAction.Space);
        Assert.Contains(targets, target => target.Id == "DEL" && target.Action is TargetAction.Delete);
        Assert.Contains(targets, target => target.Id == "PAUSE" && target.Action is TargetAction.Pause);
        Assert.Contains(targets, target => target.Id == "CLEAR" && target.Action is TargetAction.Clear);

        double[] frequencies = targets.Select(target => target.FrequencyHz).Order().ToArray();
        Assert.Equal(8.0, frequencies[0], 3);
        Assert.Equal(15.8, frequencies[^1], 3);
        Assert.All(frequencies.Zip(frequencies.Skip(1)), pair =>
            Assert.Equal(0.2, pair.Second - pair.First, 3));

        for (int index = 0; index < targets.Count; index++)
        {
            Assert.Equal((index % 4) * Math.PI / 2.0, targets[index].PhaseRadians, 6);
        }
    }

    [Fact]
    public void DefaultCollectionSubsetMatchesPlannedCalibrationTargets()
    {
        IReadOnlyList<StimulusTarget> targets = StimulusLayout.CreateDefaultQwertyTargets();

        StimulusTarget[] subset = StimulusLayout.CreateDefaultCollectionTargets(targets).ToArray();

        Assert.Equal(["1", "T", "O", "D", "J", "X", "N", "PAUSE"], subset.Select(target => target.Id).ToArray());
        Assert.Equal(8.0, subset[0].FrequencyHz, 3);
        Assert.Equal(8.8, subset[1].FrequencyHz, 3);
        Assert.Equal(9.6, subset[2].FrequencyHz, 3);
        Assert.Equal(10.4, subset[3].FrequencyHz, 3);
        Assert.Equal(11.2, subset[4].FrequencyHz, 3);
        Assert.Equal(12.0, subset[5].FrequencyHz, 3);
        Assert.Equal(12.8, subset[6].FrequencyHz, 3);
        Assert.Equal(15.6, subset[7].FrequencyHz, 3);
    }
}
