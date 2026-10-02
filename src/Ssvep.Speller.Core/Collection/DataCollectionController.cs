namespace Ssvep.Speller.Core.Collection;

public sealed class DataCollectionController
{
    private readonly IReadOnlyList<DataCollectionTrial> trials;
    private readonly DataCollectionSettings settings;
    private TimeSpan phaseStartedAt;
    private DateTimeOffset legacyClockOrigin;

    public DataCollectionController(
        IReadOnlyList<DataCollectionTrial> trials,
        DataCollectionSettings settings)
    {
        this.trials = trials;
        this.settings = settings;
    }

    public CollectionPhase CurrentPhase { get; private set; } = CollectionPhase.Idle;

    public int CurrentTrialIndex { get; private set; }

    public DataCollectionTrial? CurrentTrial =>
        CurrentTrialIndex >= 0 && CurrentTrialIndex < trials.Count ? trials[CurrentTrialIndex] : null;

    public bool IsComplete => CurrentPhase == CollectionPhase.Complete;

    public IReadOnlyList<CollectionMarker> Start(DateTimeOffset timestamp, long? sampleIndex)
    {
        legacyClockOrigin = timestamp;
        return [Start(TimeSpan.Zero, timestamp, sampleIndex, renderFrameIndex: null)];
    }

    public CollectionMarker Start(
        TimeSpan renderingTime,
        DateTimeOffset timestamp,
        long? sampleIndex,
        long? renderFrameIndex)
    {
        CurrentTrialIndex = 0;
        if (trials.Count == 0)
        {
            CurrentPhase = CollectionPhase.Complete;
            phaseStartedAt = renderingTime;
            return CreateMarker(
                timestamp,
                sampleIndex,
                CollectionPhase.Complete,
                "CollectionComplete",
                renderFrameIndex,
                renderingTime.Ticks);
        }

        CurrentPhase = CollectionPhase.Rest;
        phaseStartedAt = renderingTime;
        return CreateMarker(
            timestamp,
            sampleIndex,
            CollectionPhase.Rest,
            "RestStart",
            renderFrameIndex,
            renderingTime.Ticks);
    }

    public IReadOnlyList<CollectionMarker> Advance(DateTimeOffset timestamp, long? sampleIndex)
    {
        TimeSpan renderingTime = timestamp - legacyClockOrigin;
        CollectionMarker? marker = Advance(renderingTime, timestamp, sampleIndex, renderFrameIndex: null);
        return marker is null ? [] : [marker];
    }

    public CollectionMarker? Advance(
        TimeSpan renderingTime,
        DateTimeOffset timestamp,
        long? sampleIndex,
        long? renderFrameIndex)
    {
        if (CurrentPhase is CollectionPhase.Idle or CollectionPhase.Complete ||
            renderingTime - phaseStartedAt < DurationForPhase(CurrentPhase))
        {
            return null;
        }

        CollectionPhase nextPhase = NextPhase();
        CurrentPhase = nextPhase;
        phaseStartedAt = renderingTime;
        return CreateMarker(
            timestamp,
            sampleIndex,
            nextPhase,
            EventNameForPhase(nextPhase),
            renderFrameIndex,
            renderingTime.Ticks);
    }

    public void RestartCurrentPhase(TimeSpan renderingTime)
    {
        if (CurrentPhase is CollectionPhase.Idle or CollectionPhase.Complete)
        {
            return;
        }

        phaseStartedAt = renderingTime;
    }

    private CollectionPhase NextPhase()
    {
        return CurrentPhase switch
        {
            CollectionPhase.Rest => CollectionPhase.Cue,
            CollectionPhase.Cue => CollectionPhase.Stimulus,
            CollectionPhase.Stimulus => CollectionPhase.Feedback,
            CollectionPhase.Feedback => CollectionPhase.InterTrial,
            CollectionPhase.InterTrial => AdvanceTrialOrComplete(),
            _ => CollectionPhase.Complete
        };
    }

    private CollectionPhase AdvanceTrialOrComplete()
    {
        CurrentTrialIndex++;
        return CurrentTrialIndex >= trials.Count ? CollectionPhase.Complete : CollectionPhase.Rest;
    }

    private TimeSpan DurationForPhase(CollectionPhase phase)
    {
        return phase switch
        {
            CollectionPhase.Rest => settings.RestDuration,
            CollectionPhase.Cue => settings.CueDuration,
            CollectionPhase.Stimulus => settings.StimulusDuration,
            CollectionPhase.Feedback => settings.FeedbackDuration,
            CollectionPhase.InterTrial => settings.InterTrialDuration,
            _ => TimeSpan.Zero
        };
    }

    private static string EventNameForPhase(CollectionPhase phase)
    {
        return phase switch
        {
            CollectionPhase.Rest => "RestStart",
            CollectionPhase.Cue => "CueStart",
            CollectionPhase.Stimulus => "StimulusStart",
            CollectionPhase.Feedback => "FeedbackStart",
            CollectionPhase.InterTrial => "InterTrialStart",
            CollectionPhase.Complete => "CollectionComplete",
            _ => "Idle"
        };
    }

    private CollectionMarker CreateMarker(
        DateTimeOffset timestamp,
        long? sampleIndex,
        CollectionPhase phase,
        string eventName,
        long? renderFrameIndex = null,
        long? renderingTimeTicks = null)
    {
        DataCollectionTrial? trial = CurrentTrial;
        return new CollectionMarker(
            timestamp,
            sampleIndex,
            CurrentTrialIndex,
            phase,
            eventName,
            trial?.TargetId ?? string.Empty,
            phase == CollectionPhase.Feedback ? trial?.FeedbackSource : null,
            phase == CollectionPhase.Feedback ? trial?.FeedbackTargetId : null,
            phase == CollectionPhase.Feedback ? trial?.FeedbackMatchesCue : null,
            renderFrameIndex,
            renderingTimeTicks);
    }
}
