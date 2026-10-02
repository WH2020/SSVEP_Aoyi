using Ssvep.Speller.Core.Collection;
using Ssvep.Speller.Core.Feedback;
using Ssvep.Speller.Core.Stimulus;

namespace Ssvep.Speller.Tests;

public sealed class DataCollectionControllerTests
{
    [Fact]
    public void ControllerAdvancesOnlyOnePhasePerPresentedFrame()
    {
        IReadOnlyList<StimulusTarget> targets = StimulusLayout.CreateDefaultQwertyTargets();
        DataCollectionSettings settings = DataCollectionSettings.CreateDefault(["1"]) with
        {
            RepeatsPerTarget = 1,
            RestDuration = TimeSpan.FromSeconds(1),
            CueDuration = TimeSpan.FromSeconds(1),
            StimulusDuration = TimeSpan.FromSeconds(1),
            FeedbackDuration = TimeSpan.FromSeconds(1),
            InterTrialDuration = TimeSpan.FromSeconds(1)
        };
        DataCollectionTrial trial = DataCollectionScheduler.Generate(targets, settings).Single();
        var controller = new DataCollectionController([trial], settings);
        DateTimeOffset startedAt = DateTimeOffset.UnixEpoch;

        controller.Start(TimeSpan.Zero, startedAt, sampleIndex: 10, renderFrameIndex: 100);

        CollectionMarker? marker = controller.Advance(
            TimeSpan.FromSeconds(10),
            startedAt + TimeSpan.FromSeconds(10),
            sampleIndex: 20,
            renderFrameIndex: 101);

        Assert.NotNull(marker);
        Assert.Equal(CollectionPhase.Cue, marker.Phase);
        Assert.Equal(startedAt + TimeSpan.FromSeconds(10), marker.Timestamp);
        Assert.Equal(101, marker.RenderFrameIndex);
        Assert.Equal(TimeSpan.FromSeconds(10).Ticks, marker.RenderingTimeTicks);
        Assert.Equal(CollectionPhase.Cue, controller.CurrentPhase);
        Assert.Null(controller.Advance(
            TimeSpan.FromSeconds(10),
            startedAt + TimeSpan.FromSeconds(10),
            sampleIndex: 20,
            renderFrameIndex: 102));
    }

    [Fact]
    public void RestartCurrentPhaseKeepsLateFeedbackVisibleForItsConfiguredDuration()
    {
        IReadOnlyList<StimulusTarget> targets = StimulusLayout.CreateDefaultQwertyTargets();
        DataCollectionSettings settings = DataCollectionSettings.CreateDefault(["1"]) with
        {
            RepeatsPerTarget = 1,
            RestDuration = TimeSpan.FromSeconds(1),
            CueDuration = TimeSpan.FromSeconds(1),
            StimulusDuration = TimeSpan.FromSeconds(1),
            FeedbackDuration = TimeSpan.FromSeconds(1),
            InterTrialDuration = TimeSpan.FromSeconds(1)
        };
        DataCollectionTrial trial = DataCollectionScheduler.Generate(targets, settings).Single();
        var controller = new DataCollectionController([trial], settings);
        DateTimeOffset start = DateTimeOffset.UnixEpoch;

        controller.Start(TimeSpan.Zero, start, null, 0);
        controller.Advance(TimeSpan.FromSeconds(1), start.AddSeconds(1), null, 1);
        controller.Advance(TimeSpan.FromSeconds(2), start.AddSeconds(2), null, 2);
        controller.Advance(TimeSpan.FromSeconds(3), start.AddSeconds(3), null, 3);
        Assert.Equal(CollectionPhase.Feedback, controller.CurrentPhase);

        controller.RestartCurrentPhase(TimeSpan.FromSeconds(10));

        Assert.Null(controller.Advance(TimeSpan.FromSeconds(10.5), start.AddSeconds(10.5), null, 4));
        Assert.NotNull(controller.Advance(TimeSpan.FromSeconds(11), start.AddSeconds(11), null, 5));
        Assert.Equal(CollectionPhase.InterTrial, controller.CurrentPhase);
    }

    [Fact]
    public void ControllerEmitsPhaseMarkersInOrder()
    {
        IReadOnlyList<StimulusTarget> targets = StimulusLayout.CreateDefaultQwertyTargets();
        DataCollectionSettings settings = DataCollectionSettings.CreateDefault(["1"]) with
        {
            RepeatsPerTarget = 1,
            RestDuration = TimeSpan.FromSeconds(1),
            CueDuration = TimeSpan.FromSeconds(1),
            StimulusDuration = TimeSpan.FromSeconds(1),
            FeedbackDuration = TimeSpan.FromSeconds(1),
            InterTrialDuration = TimeSpan.FromSeconds(1),
            ShamPositiveRatio = 1.0
        };
        DataCollectionTrial trial = DataCollectionScheduler.Generate(targets, settings).Single();
        var controller = new DataCollectionController([trial], settings);
        DateTimeOffset start = DateTimeOffset.UnixEpoch;

        List<CollectionMarker> markers = [];
        markers.AddRange(controller.Start(start, sampleIndex: 10));
        markers.AddRange(controller.Advance(start + TimeSpan.FromSeconds(1), sampleIndex: 20));
        markers.AddRange(controller.Advance(start + TimeSpan.FromSeconds(2), sampleIndex: 30));
        markers.AddRange(controller.Advance(start + TimeSpan.FromSeconds(3), sampleIndex: 40));
        markers.AddRange(controller.Advance(start + TimeSpan.FromSeconds(4), sampleIndex: 50));
        markers.AddRange(controller.Advance(start + TimeSpan.FromSeconds(5), sampleIndex: 60));

        Assert.Equal(
            [
                CollectionPhase.Rest,
                CollectionPhase.Cue,
                CollectionPhase.Stimulus,
                CollectionPhase.Feedback,
                CollectionPhase.InterTrial,
                CollectionPhase.Complete
            ],
            markers.Select(marker => marker.Phase).ToArray());
        Assert.Equal("1", markers[1].TargetId);
        Assert.Equal("1", markers[3].FeedbackTargetId);
        Assert.Equal(FeedbackSource.ShamPreset, markers[3].FeedbackSource);
        Assert.True(markers[3].FeedbackMatchesCue);
        Assert.True(controller.IsComplete);
    }
}
