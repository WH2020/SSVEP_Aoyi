using System.Globalization;
using System.Text.Json;
using Ssvep.Speller.Core.Classification;
using Ssvep.Speller.Core.Collection;
using Ssvep.Speller.Core.Feedback;
using Ssvep.Speller.Core.Signal;
using Ssvep.Speller.Core.Stimulus;

namespace Ssvep.Speller.Core.Logging;

public sealed class DataCollectionLogWriter : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly string sessionJsonPath;
    private readonly string framesCsvPath;
    private readonly StreamWriter samplesWriter;
    private readonly StreamWriter markersWriter;
    private readonly CollectionSessionLog log;
    private readonly List<StimulusFrameRecord> frames = [];
    private readonly int sampleChannelCount;
    private long sampleCount;
    private bool isDisposed;

    private DataCollectionLogWriter(
        string rootDirectory,
        IReadOnlyList<StimulusTarget> targets,
        DataCollectionSettings settings,
        IReadOnlyList<DataCollectionTrial> trials,
        string stimulusStyle,
        CollectionAcquisitionInfo? acquisition)
    {
        Directory.CreateDirectory(rootDirectory);
        SessionDirectory = Path.Combine(rootDirectory, $"ssvep-collection-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}");
        Directory.CreateDirectory(SessionDirectory);
        sessionJsonPath = Path.Combine(SessionDirectory, "session.json");
        framesCsvPath = Path.Combine(SessionDirectory, "frames.csv");
        samplesWriter = new StreamWriter(Path.Combine(SessionDirectory, "samples.csv"));
        markersWriter = new StreamWriter(Path.Combine(SessionDirectory, "markers.csv"));
        sampleChannelCount = acquisition?.InputChannelCount ?? 8;
        if (sampleChannelCount <= 0)
        {
            throw new ArgumentException("Collection logging requires a positive input channel count.", nameof(acquisition));
        }

        string channelHeader = string.Join(",", Enumerable.Range(1, sampleChannelCount).Select(index => $"ch{index}"));
        samplesWriter.WriteLine($"sample_index,host_received_timestamp,device_elapsed_ms,is_lost,{channelHeader}");
        markersWriter.WriteLine("timestamp,sample_index,render_frame_index,rendering_time_ticks,trial_index,phase,event_name,target_id,feedback_source,feedback_target_id,feedback_matches_cue");
        log = new CollectionSessionLog
        {
            StartedAt = DateTimeOffset.UtcNow,
            StimulusStyle = stimulusStyle,
            CollectionFeedbackMode = settings.FeedbackMode.ToString(),
            Acquisition = acquisition,
            Settings = settings,
            Targets = targets.Select(LogTarget.From).ToList(),
            Trials = trials.ToList()
        };
    }

    public string SessionDirectory { get; }

    public static DataCollectionLogWriter Start(
        string rootDirectory,
        IReadOnlyList<StimulusTarget> targets,
        DataCollectionSettings settings,
        IReadOnlyList<DataCollectionTrial> trials,
        string stimulusStyle,
        CollectionAcquisitionInfo? acquisition = null)
    {
        return new DataCollectionLogWriter(rootDirectory, targets, settings, trials, stimulusStyle, acquisition);
    }

    public void RecordSample(EegSample sample)
    {
        if (sample.Channels.Length != sampleChannelCount)
        {
            throw new ArgumentException(
                $"Collection logging expects all {sampleChannelCount} input EEG channels, got {sample.Channels.Length}.",
                nameof(sample));
        }

        string line = string.Join(
            ",",
            new[]
            {
                sample.SampleIndex.ToString(CultureInfo.InvariantCulture),
                sample.Timestamp.ToString("O", CultureInfo.InvariantCulture),
                sample.DeviceElapsedMilliseconds?.ToString("G17", CultureInfo.InvariantCulture) ?? string.Empty,
                sample.IsLost.ToString(CultureInfo.InvariantCulture)
            }.Concat(sample.Channels.Select(channel => channel.ToString("G17", CultureInfo.InvariantCulture))));
        lock (gate)
        {
            if (isDisposed)
            {
                return;
            }

            samplesWriter.WriteLine(line);
            sampleCount++;
        }
    }

    public void RecordMarker(CollectionMarker marker)
    {
        string line = string.Join(
            ",",
            Escape(marker.Timestamp.ToString("O", CultureInfo.InvariantCulture)),
            Escape(marker.SampleIndex?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
            Escape(marker.RenderFrameIndex?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
            Escape(marker.RenderingTimeTicks?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
            Escape(marker.TrialIndex.ToString(CultureInfo.InvariantCulture)),
            Escape(marker.Phase.ToString()),
            Escape(marker.EventName),
            Escape(marker.TargetId),
            Escape(marker.FeedbackSource?.ToString() ?? string.Empty),
            Escape(marker.FeedbackTargetId ?? string.Empty),
            Escape(marker.FeedbackMatchesCue?.ToString(CultureInfo.InvariantCulture) ?? string.Empty));
        lock (gate)
        {
            if (isDisposed)
            {
                return;
            }

            markersWriter.WriteLine(line);
            log.Markers.Add(marker);
        }
    }

    public void RecordClassification(
        string targetId,
        FeedbackSource source,
        ClassificationResult result)
    {
        lock (gate)
        {
            if (isDisposed)
            {
                return;
            }

            log.Classifications.Add(new CollectionClassificationEvent(
                DateTimeOffset.UtcNow,
                targetId,
                source.ToString(),
                result.BestTargetId,
                result.BestScore,
                result.SecondTargetId,
                result.SecondScore,
                result.Scores));
        }
    }

    public void RecordFrame(StimulusFrameRecord frame)
    {
        lock (gate)
        {
            if (!isDisposed)
            {
                frames.Add(frame);
            }
        }
    }

    public void RecordFrameStats(FrameStats frameStats)
    {
        log.FrameStats = frameStats;
    }

    public async ValueTask DisposeAsync()
    {
        log.EndedAt = DateTimeOffset.UtcNow;
        log.SampleCount = sampleCount;
        StimulusFrameRecord[] frameSnapshot;
        lock (gate)
        {
            isDisposed = true;
            frameSnapshot = frames.ToArray();
            samplesWriter.Flush();
            markersWriter.Flush();
        }

        await samplesWriter.DisposeAsync().ConfigureAwait(false);
        await markersWriter.DisposeAsync().ConfigureAwait(false);
        await WriteFramesCsvAsync(frameSnapshot).ConfigureAwait(false);
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        await File.WriteAllTextAsync(sessionJsonPath, JsonSerializer.Serialize(log, options)).ConfigureAwait(false);
    }

    private async Task WriteFramesCsvAsync(IReadOnlyList<StimulusFrameRecord> frameSnapshot)
    {
        await using var writer = new StreamWriter(framesCsvPath);
        await writer.WriteLineAsync(
            "timestamp,render_frame_index,rendering_time_ticks,frame_interval_ms,trial_index,phase,target_id,stimulus_elapsed_seconds,is_flickering,photodiode_patch_is_bright");
        foreach (StimulusFrameRecord frame in frameSnapshot)
        {
            string line = string.Join(
                ",",
                Escape(frame.Timestamp.ToString("O", CultureInfo.InvariantCulture)),
                frame.RenderFrameIndex.ToString(CultureInfo.InvariantCulture),
                frame.RenderingTimeTicks.ToString(CultureInfo.InvariantCulture),
                frame.FrameIntervalMilliseconds.ToString("0.######", CultureInfo.InvariantCulture),
                frame.TrialIndex.ToString(CultureInfo.InvariantCulture),
                frame.Phase.ToString(),
                Escape(frame.TargetId),
                frame.StimulusElapsedSeconds.ToString("0.#########", CultureInfo.InvariantCulture),
                frame.IsFlickering.ToString(CultureInfo.InvariantCulture),
                frame.PhotodiodePatchIsBright.ToString(CultureInfo.InvariantCulture));
            await writer.WriteLineAsync(line);
        }
    }

    private static string Escape(string value)
    {
        return value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;
    }

    private sealed class CollectionSessionLog
    {
        public DateTimeOffset StartedAt { get; set; }

        public DateTimeOffset? EndedAt { get; set; }

        public string StimulusStyle { get; set; } = string.Empty;

        public string CollectionFeedbackMode { get; set; } = string.Empty;

        public CollectionAcquisitionInfo? Acquisition { get; set; }

        public DataCollectionSettings? Settings { get; set; }

        public List<LogTarget> Targets { get; set; } = [];

        public List<DataCollectionTrial> Trials { get; set; } = [];

        public List<CollectionMarker> Markers { get; set; } = [];

        public List<CollectionClassificationEvent> Classifications { get; set; } = [];

        public long SampleCount { get; set; }

        public FrameStats FrameStats { get; set; } = new(0, 0);
    }

    private sealed record CollectionClassificationEvent(
        DateTimeOffset Timestamp,
        string TargetId,
        string FeedbackSource,
        string BestTargetId,
        double BestScore,
        string SecondTargetId,
        double SecondScore,
        IReadOnlyDictionary<string, double> Scores);

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
}
