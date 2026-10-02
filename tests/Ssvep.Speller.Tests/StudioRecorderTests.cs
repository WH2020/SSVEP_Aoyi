using System.Text.Json;
using Ssvep.Speller.Core.Studio;
using Ssvep.Speller.Core.Research;
using Ssvep.Speller.Core.Signal;

namespace Ssvep.Speller.Tests;

public sealed class StudioRecorderTests
{
    [Fact]
    public async Task CompletedSessionPersistsSamplesAndRecognitionWithManifest()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var recorder = new StudioRecorder(root, StudioContentService.CreateDefault(), true, 250, [0,1,2,3,4,5,6,7]);
            Assert.True(recorder.RecordBatch(new(1,"fixture",250,[new EegSample(12,DateTimeOffset.UtcNow,Enumerable.Range(1,8).Select(x=>(double)x).ToArray())],1,1),1,1,true));
            Assert.True(recorder.Record("recognition", new { run = 1, target = "T2", firstSample = 0, lastSample = 624 }));
            await recorder.CompleteAsync(true);
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(recorder.DirectoryPath, "manifest.json")));
            Assert.True(manifest.RootElement.GetProperty("completed").GetBoolean());
            var lines = File.ReadAllLines(Path.Combine(recorder.DirectoryPath, "events.ndjson"));
            Assert.Single(lines);
            Assert.Equal(12,new BinarySampleReader(Path.Combine(recorder.DirectoryPath,"samples.bin")).Read().Single().Batch.Samples.Single().SampleIndex);
            using var item = JsonDocument.Parse(lines[0]);
            Assert.Equal("T2", item.RootElement.GetProperty("data").GetProperty("target").GetString());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FaultedSessionIsExplicitlyIncomplete()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var recorder = new StudioRecorder(root, StudioContentService.CreateDefault(), false, 500, [0,1,2,3,4,5,6,7]);
            await recorder.CompleteAsync(false, "设备连接中断");
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(recorder.DirectoryPath, "manifest.json")));
            Assert.False(doc.RootElement.GetProperty("completed").GetBoolean());
            Assert.Equal("设备连接中断", doc.RootElement.GetProperty("failureReason").GetString());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
