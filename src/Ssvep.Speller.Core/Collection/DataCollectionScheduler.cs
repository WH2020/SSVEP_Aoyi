using Ssvep.Speller.Core.Stimulus;
using Ssvep.Speller.Core.Feedback;

namespace Ssvep.Speller.Core.Collection;

public static class DataCollectionScheduler
{
    public static IReadOnlyList<DataCollectionTrial> Generate(
        IReadOnlyList<StimulusTarget> targets,
        DataCollectionSettings settings)
    {
        if (settings.RepeatsPerTarget <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Repeats per target must be positive.");
        }

        if (settings.ShamPositiveRatio is < 0.0 or > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Sham feedback ratio must be between 0 and 1.");
        }

        if (settings.ShamTrialRatio is < 0.0 or > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Sham trial ratio must be between 0 and 1.");
        }

        Dictionary<string, StimulusTarget> targetById = targets.ToDictionary(target => target.Id);
        string[] targetIds = settings.TargetIds.Distinct(StringComparer.Ordinal).ToArray();
        if (targetIds.Length == 0)
        {
            throw new ArgumentException("At least one collection target is required.", nameof(settings));
        }

        foreach (string targetId in targetIds)
        {
            if (!targetById.ContainsKey(targetId))
            {
                throw new ArgumentException($"Unknown collection target '{targetId}'.", nameof(settings));
            }
        }

        var random = new Random(settings.RandomSeed);
        var orderedTargetIds = new List<string>(targetIds.Length * settings.RepeatsPerTarget);
        for (int repeat = 0; repeat < settings.RepeatsPerTarget; repeat++)
        {
            orderedTargetIds.AddRange(targetIds);
        }

        Shuffle(orderedTargetIds, random);

        var trials = new List<DataCollectionTrial>(orderedTargetIds.Count);
        for (int index = 0; index < orderedTargetIds.Count; index++)
        {
            StimulusTarget target = targetById[orderedTargetIds[index]];
            FeedbackSource feedbackSource = FeedbackSourceForTrial(settings, random);
            bool feedbackMatchesCue = feedbackSource == FeedbackSource.TrueClassifier ||
                random.NextDouble() < settings.ShamPositiveRatio;
            string feedbackTargetId = feedbackMatchesCue
                ? target.Id
                : PickNonTarget(targetIds, target.Id, random);
            trials.Add(new DataCollectionTrial(
                index,
                target.Id,
                target.Label,
                target.FrequencyHz,
                feedbackTargetId,
                feedbackMatchesCue || feedbackTargetId == target.Id,
                feedbackSource));
        }

        return trials;
    }

    private static FeedbackSource FeedbackSourceForTrial(DataCollectionSettings settings, Random random)
    {
        return settings.FeedbackMode switch
        {
            CollectionFeedbackMode.TrueFeedback => FeedbackSource.TrueClassifier,
            CollectionFeedbackMode.ShamFeedback => FeedbackSource.ShamPreset,
            CollectionFeedbackMode.Mixed => random.NextDouble() < settings.ShamTrialRatio
                ? FeedbackSource.ShamPreset
                : FeedbackSource.TrueClassifier,
            _ => FeedbackSource.ShamPreset
        };
    }

    private static string PickNonTarget(IReadOnlyList<string> targetIds, string targetId, Random random)
    {
        string[] nonTargets = targetIds.Where(id => !StringComparer.Ordinal.Equals(id, targetId)).ToArray();
        return nonTargets.Length == 0 ? targetId : nonTargets[random.Next(nonTargets.Length)];
    }

    private static void Shuffle<T>(IList<T> values, Random random)
    {
        for (int i = values.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }
}
