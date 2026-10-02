using System.Text.Json;
using Ssvep.Speller.Core.Classification;
using Ssvep.Speller.Core.Decision;
using Ssvep.Speller.Core.Feedback;
using Ssvep.Speller.Core.Stimulus;

namespace Ssvep.Speller.Core.Logging;

public sealed class SessionLogWriter : IAsyncDisposable
{
    private readonly string logPath;
    private readonly SessionLog log;

    private SessionLogWriter(
        string directory,
        IReadOnlyList<StimulusTarget> targets,
        string stimulusStyle,
        string feedbackMode)
    {
        Directory.CreateDirectory(directory);
        logPath = Path.Combine(directory, $"ssvep-session-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}.json");
        log = new SessionLog
        {
            StartedAt = DateTimeOffset.UtcNow,
            StimulusStyle = stimulusStyle,
            FeedbackMode = feedbackMode,
            Targets = targets.Select(LogTarget.From).ToList()
        };
    }

    public static SessionLogWriter Start(
        string directory,
        IReadOnlyList<StimulusTarget> targets,
        string stimulusStyle,
        string feedbackMode = "真反馈")
    {
        return new SessionLogWriter(directory, targets, stimulusStyle, feedbackMode);
    }

    public void RecordGroundTruth(string targetId, DateTimeOffset timestamp)
    {
        log.GroundTruth.Add(new GroundTruthEvent(timestamp, targetId));
    }

    public void RecordClassification(ClassificationResult result)
    {
        log.Classifications.Add(new ClassificationEvent(
            DateTimeOffset.UtcNow,
            result.BestTargetId,
            result.BestScore,
            result.SecondTargetId,
            result.SecondScore,
            result.Scores));
    }

    public void RecordDecision(DecisionResult decision)
    {
        log.Decisions.Add(new DecisionEvent(
            DateTimeOffset.UtcNow,
            decision.Kind.ToString(),
            decision.TargetId,
            decision.Score,
            decision.WindowIndex,
            decision.Source.ToString()));
    }

    public void RecordText(string text)
    {
        log.TextEvents.Add(new TextEvent(DateTimeOffset.UtcNow, text));
    }

    public void RecordFrameStats(FrameStats frameStats)
    {
        log.FrameStats = frameStats;
    }

    public async ValueTask DisposeAsync()
    {
        log.EndedAt = DateTimeOffset.UtcNow;
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        await File.WriteAllTextAsync(logPath, JsonSerializer.Serialize(log, options)).ConfigureAwait(false);
    }

    private sealed class SessionLog
    {
        public DateTimeOffset StartedAt { get; set; }

        public DateTimeOffset? EndedAt { get; set; }

        public string StimulusStyle { get; set; } = string.Empty;

        public string FeedbackMode { get; set; } = string.Empty;

        public List<LogTarget> Targets { get; set; } = [];

        public List<GroundTruthEvent> GroundTruth { get; set; } = [];

        public List<ClassificationEvent> Classifications { get; set; } = [];

        public List<DecisionEvent> Decisions { get; set; } = [];

        public List<TextEvent> TextEvents { get; set; } = [];

        public FrameStats FrameStats { get; set; } = new(0, 0);
    }

    private sealed record LogTarget(
        string Id,
        string Label,
        double FrequencyHz,
        double PhaseRadians,
        string Action)
    {
        public static LogTarget From(StimulusTarget target)
        {
            return new LogTarget(
                target.Id,
                target.Label,
                target.FrequencyHz,
                target.PhaseRadians,
                target.Action.GetType().Name);
        }
    }

    private sealed record GroundTruthEvent(DateTimeOffset Timestamp, string TargetId);

    private sealed record ClassificationEvent(
        DateTimeOffset Timestamp,
        string BestTargetId,
        double BestScore,
        string SecondTargetId,
        double SecondScore,
        IReadOnlyDictionary<string, double> Scores);

    private sealed record DecisionEvent(
        DateTimeOffset Timestamp,
        string Kind,
        string TargetId,
        double Score,
        long WindowIndex,
        string FeedbackSource);

    private sealed record TextEvent(DateTimeOffset Timestamp, string Text);
}
