using System.Text.Json;
using Ssvep.Speller.Core.Classification;
using Ssvep.Speller.Core.Decision;
using Ssvep.Speller.Core.Feedback;
using Ssvep.Speller.Core.Logging;
using Ssvep.Speller.Core.Stimulus;

namespace Ssvep.Speller.Tests;

public sealed class SessionLogWriterTests
{
    [Fact]
    public async Task WriterPersistsStimulusClassificationDecisionAndTextEvents()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ssvep-log-tests", Guid.NewGuid().ToString("N"));
        var targets = new[]
        {
            new StimulusTarget("H", "H", 9.0, 0.0, new TargetAction.Letter('H')),
            new StimulusTarget("SPACE", "SPACE", 9.2, 0.0, new TargetAction.Space()),
        };

        await using (var writer = SessionLogWriter.Start(directory, targets, "checkerboard", "假反馈"))
        {
            writer.RecordGroundTruth("H", DateTimeOffset.UnixEpoch);
            writer.RecordClassification(new ClassificationResult(
                "H",
                0.9,
                "SPACE",
                0.3,
                new Dictionary<string, double> { ["H"] = 0.9, ["SPACE"] = 0.3 }));
            writer.RecordDecision(new DecisionResult(DecisionKind.Commit, "H", 0.9, 3, FeedbackSource.ShamPreset));
            writer.RecordText("H");
            writer.RecordFrameStats(new FrameStats(60.0, 1));
        }

        string logPath = Assert.Single(Directory.GetFiles(directory, "*.json"));
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(logPath));

        JsonElement root = document.RootElement;
        Assert.Equal("checkerboard", root.GetProperty("stimulusStyle").GetString());
        Assert.Equal("假反馈", root.GetProperty("feedbackMode").GetString());
        Assert.Equal(2, root.GetProperty("targets").GetArrayLength());
        Assert.Equal("H", root.GetProperty("groundTruth")[0].GetProperty("targetId").GetString());
        Assert.Equal("H", root.GetProperty("classifications")[0].GetProperty("bestTargetId").GetString());
        Assert.Equal("H", root.GetProperty("decisions")[0].GetProperty("targetId").GetString());
        Assert.Equal("ShamPreset", root.GetProperty("decisions")[0].GetProperty("feedbackSource").GetString());
        Assert.Equal("H", root.GetProperty("textEvents")[0].GetProperty("text").GetString());
        Assert.Equal(1, root.GetProperty("frameStats").GetProperty("droppedFrames").GetInt32());
    }
}
