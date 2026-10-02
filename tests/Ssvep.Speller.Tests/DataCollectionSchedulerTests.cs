using Ssvep.Speller.Core.Collection;
using Ssvep.Speller.Core.Stimulus;

namespace Ssvep.Speller.Tests;

public sealed class DataCollectionSchedulerTests
{
    [Fact]
    public void SchedulerGeneratesFiveRepeatsPerDefaultTarget()
    {
        IReadOnlyList<StimulusTarget> targets = StimulusLayout.CreateDefaultQwertyTargets();
        DataCollectionSettings settings = DataCollectionSettings.CreateDefault(
            StimulusLayout.CreateDefaultCollectionTargets(targets).Select(target => target.Id));

        IReadOnlyList<DataCollectionTrial> trials = DataCollectionScheduler.Generate(targets, settings);

        Assert.Equal(40, trials.Count);
        foreach (IGrouping<string, DataCollectionTrial> group in trials.GroupBy(trial => trial.TargetId))
        {
            Assert.Equal(5, group.Count());
        }
    }

    [Fact]
    public void SchedulerIsReproducibleForSameSeed()
    {
        IReadOnlyList<StimulusTarget> targets = StimulusLayout.CreateDefaultQwertyTargets();
        DataCollectionSettings settings = DataCollectionSettings.CreateDefault(["1", "T", "O"]) with
        {
            RandomSeed = 1234
        };

        IReadOnlyList<DataCollectionTrial> first = DataCollectionScheduler.Generate(targets, settings);
        IReadOnlyList<DataCollectionTrial> second = DataCollectionScheduler.Generate(targets, settings);

        Assert.Equal(
            first.Select(trial => $"{trial.TargetId}:{trial.FeedbackTargetId}:{trial.FeedbackMatchesCue}"),
            second.Select(trial => $"{trial.TargetId}:{trial.FeedbackTargetId}:{trial.FeedbackMatchesCue}"));
    }

    [Fact]
    public void ShamFeedbackCanPointToNonTargetWithoutClassifierInput()
    {
        IReadOnlyList<StimulusTarget> targets = StimulusLayout.CreateDefaultQwertyTargets();
        DataCollectionSettings settings = DataCollectionSettings.CreateDefault(["1", "T"]) with
        {
            RepeatsPerTarget = 8,
            RandomSeed = 42,
            ShamPositiveRatio = 0.0
        };

        IReadOnlyList<DataCollectionTrial> trials = DataCollectionScheduler.Generate(targets, settings);

        Assert.All(trials, trial => Assert.NotEqual(trial.TargetId, trial.FeedbackTargetId));
        Assert.All(trials, trial => Assert.False(trial.FeedbackMatchesCue));
    }
}
