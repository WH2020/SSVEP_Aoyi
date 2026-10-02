namespace Ssvep.Speller.Core.Collection;

public sealed record DataCollectionSettings(
    IReadOnlyList<string> TargetIds,
    int RepeatsPerTarget,
    int RandomSeed,
    TimeSpan RestDuration,
    TimeSpan CueDuration,
    TimeSpan StimulusDuration,
    TimeSpan FeedbackDuration,
    TimeSpan InterTrialDuration,
    double ShamPositiveRatio,
    CollectionFeedbackMode FeedbackMode = CollectionFeedbackMode.ShamFeedback,
    double ShamTrialRatio = 1.0)
{
    public static DataCollectionSettings CreateDefault(IEnumerable<string> targetIds)
    {
        return new DataCollectionSettings(
            targetIds.ToArray(),
            RepeatsPerTarget: 5,
            RandomSeed: 20260623,
            RestDuration: TimeSpan.FromSeconds(1.0),
            CueDuration: TimeSpan.FromSeconds(0.8),
            StimulusDuration: TimeSpan.FromSeconds(2.5),
            FeedbackDuration: TimeSpan.FromSeconds(0.6),
            InterTrialDuration: TimeSpan.FromSeconds(0.8),
            ShamPositiveRatio: 0.5,
            FeedbackMode: CollectionFeedbackMode.ShamFeedback,
            ShamTrialRatio: 1.0);
    }
}
