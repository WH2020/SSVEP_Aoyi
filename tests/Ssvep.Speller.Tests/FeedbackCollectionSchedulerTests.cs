using Ssvep.Speller.Core.Collection;
using Ssvep.Speller.Core.Feedback;
using Ssvep.Speller.Core.Stimulus;

namespace Ssvep.Speller.Tests;

public sealed class FeedbackCollectionSchedulerTests
{
    [Fact]
    public void TrueFeedbackModeSchedulesOnlyClassifierFeedbackTrials()
    {
        IReadOnlyList<StimulusTarget> targets = StimulusLayout.CreateDefaultQwertyTargets();
        DataCollectionSettings settings = DataCollectionSettings.CreateDefault(["1", "T"]) with
        {
            RepeatsPerTarget = 3,
            FeedbackMode = CollectionFeedbackMode.TrueFeedback,
            ShamTrialRatio = 1.0,
            ShamPositiveRatio = 0.0
        };

        IReadOnlyList<DataCollectionTrial> trials = DataCollectionScheduler.Generate(targets, settings);

        Assert.All(trials, trial => Assert.Equal(FeedbackSource.TrueClassifier, trial.FeedbackSource));
    }

    [Fact]
    public void ShamFeedbackModeSchedulesOnlyPresetFeedbackTrials()
    {
        IReadOnlyList<StimulusTarget> targets = StimulusLayout.CreateDefaultQwertyTargets();
        DataCollectionSettings settings = DataCollectionSettings.CreateDefault(["1", "T"]) with
        {
            RepeatsPerTarget = 3,
            FeedbackMode = CollectionFeedbackMode.ShamFeedback,
            ShamTrialRatio = 0.0,
            ShamPositiveRatio = 0.0
        };

        IReadOnlyList<DataCollectionTrial> trials = DataCollectionScheduler.Generate(targets, settings);

        Assert.All(trials, trial => Assert.Equal(FeedbackSource.ShamPreset, trial.FeedbackSource));
        Assert.All(trials, trial => Assert.NotEqual(trial.TargetId, trial.FeedbackTargetId));
        Assert.All(trials, trial => Assert.False(trial.FeedbackMatchesCue));
    }

    [Fact]
    public void MixedFeedbackModeUsesSeededShamTrialRatio()
    {
        IReadOnlyList<StimulusTarget> targets = StimulusLayout.CreateDefaultQwertyTargets();
        DataCollectionSettings settings = DataCollectionSettings.CreateDefault(["1", "T", "O", "P"]) with
        {
            RepeatsPerTarget = 6,
            RandomSeed = 2026,
            FeedbackMode = CollectionFeedbackMode.Mixed,
            ShamTrialRatio = 0.5,
            ShamPositiveRatio = 0.5
        };

        IReadOnlyList<DataCollectionTrial> first = DataCollectionScheduler.Generate(targets, settings);
        IReadOnlyList<DataCollectionTrial> second = DataCollectionScheduler.Generate(targets, settings);

        Assert.Equal(first.Select(trial => trial.FeedbackSource), second.Select(trial => trial.FeedbackSource));
        Assert.Contains(first, trial => trial.FeedbackSource == FeedbackSource.TrueClassifier);
        Assert.Contains(first, trial => trial.FeedbackSource == FeedbackSource.ShamPreset);
    }

    [Fact]
    public void ShamPositiveRatioCanGenerateCueMatchesAndMismatches()
    {
        IReadOnlyList<StimulusTarget> targets = StimulusLayout.CreateDefaultQwertyTargets();
        DataCollectionSettings settings = DataCollectionSettings.CreateDefault(["1", "T", "O"]) with
        {
            RepeatsPerTarget = 20,
            RandomSeed = 99,
            FeedbackMode = CollectionFeedbackMode.ShamFeedback,
            ShamPositiveRatio = 0.5
        };

        IReadOnlyList<DataCollectionTrial> trials = DataCollectionScheduler.Generate(targets, settings);

        Assert.Contains(trials, trial => trial.FeedbackMatchesCue);
        Assert.Contains(trials, trial => !trial.FeedbackMatchesCue);
    }
}
