using System.Text.Json;
using Ssvep.Speller.Core.Collection;
using Ssvep.Speller.Core.Devices;
using Ssvep.Speller.Core.Feedback;
using Ssvep.Speller.Core.Logging;
using Ssvep.Speller.Core.Signal;
using Ssvep.Speller.Core.Stimulus;

namespace Ssvep.Speller.Tests;

public sealed class DataCollectionLogWriterTests
{
    [Fact]
    public async Task WriterPersistsSessionJsonSamplesCsvAndMarkersCsv()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ssvep-collection-log-tests", Guid.NewGuid().ToString("N"));
        IReadOnlyList<StimulusTarget> targets = StimulusLayout.CreateDefaultQwertyTargets();
        DataCollectionSettings settings = DataCollectionSettings.CreateDefault(["1"]) with
        {
            RepeatsPerTarget = 1,
            ShamPositiveRatio = 1.0
        };
        IReadOnlyList<DataCollectionTrial> trials = DataCollectionScheduler.Generate(targets, settings);
        var acquisition = new CollectionAcquisitionInfo(
            SourceMode: "SynchroniSdk",
            DeviceName: "OB6000C",
            DeviceAddress: "AA:BB",
            SamplingRateHz: 250,
            InputChannelCount: 32,
            SelectedChannelIndices: [24, 25, 26, 27, 28, 29, 30, 31],
            ImpedanceThresholdKiloOhms: 500,
            Impedance: [ChannelImpedance.FromOhms(24, 120_000)],
            Enable50HzNotch: true,
            EnableHighPass: false,
            EnableLowPass: false);

        await using (var writer = DataCollectionLogWriter.Start(
            directory,
            targets,
            settings,
            trials,
            "checkerboard-synchroni",
            acquisition))
        {
            writer.RecordSample(new EegSample(
                7,
                DateTimeOffset.UnixEpoch,
                Enumerable.Range(1, 32).Select(value => (double)value).ToArray(),
                DeviceElapsedMilliseconds: 168,
                IsLost: true));
            writer.RecordMarker(new CollectionMarker(
                DateTimeOffset.UnixEpoch,
                7,
                0,
                CollectionPhase.Stimulus,
                "StimulusStart",
                "1",
                null,
                null,
                null,
                RenderFrameIndex: 42,
                RenderingTimeTicks: 123));
            writer.RecordFrame(new StimulusFrameRecord(
                DateTimeOffset.UnixEpoch,
                RenderFrameIndex: 42,
                RenderingTimeTicks: 123,
                FrameIntervalMilliseconds: 16.67,
                TrialIndex: 0,
                Phase: CollectionPhase.Stimulus,
                TargetId: "1",
                StimulusElapsedSeconds: 0,
                IsFlickering: true,
                PhotodiodePatchIsBright: true));
            writer.RecordFrameStats(new FrameStats(60, 0));
        }

        string sessionDirectory = Assert.Single(Directory.GetDirectories(directory));
        Assert.True(File.Exists(Path.Combine(sessionDirectory, "session.json")));
        string samples = await File.ReadAllTextAsync(Path.Combine(sessionDirectory, "samples.csv"));
        string markers = await File.ReadAllTextAsync(Path.Combine(sessionDirectory, "markers.csv"));
        string frames = await File.ReadAllTextAsync(Path.Combine(sessionDirectory, "frames.csv"));

        string expectedChannelHeader = string.Join(",", Enumerable.Range(1, 32).Select(index => $"ch{index}"));
        string expectedChannelValues = string.Join(",", Enumerable.Range(1, 32));
        Assert.Contains($"sample_index,host_received_timestamp,device_elapsed_ms,is_lost,{expectedChannelHeader}", samples);
        Assert.Contains($"7,1970-01-01T00:00:00.0000000+00:00,168,True,{expectedChannelValues}", samples);
        Assert.Contains("timestamp,sample_index,render_frame_index,rendering_time_ticks,trial_index,phase,event_name,target_id,feedback_source,feedback_target_id,feedback_matches_cue", markers);
        Assert.Contains("1970-01-01T00:00:00.0000000+00:00,7,42,123,0,Stimulus,StimulusStart,1,,,", markers);
        Assert.Contains("timestamp,render_frame_index,rendering_time_ticks,frame_interval_ms,trial_index,phase,target_id,stimulus_elapsed_seconds,is_flickering,photodiode_patch_is_bright", frames);
        Assert.Contains("1970-01-01T00:00:00.0000000+00:00,42,123,16.67,0,Stimulus,1,0,True,True", frames);
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(sessionDirectory, "session.json")));
        Assert.Equal("ShamFeedback", document.RootElement.GetProperty("collectionFeedbackMode").GetString());
        JsonElement acquisitionElement = document.RootElement.GetProperty("acquisition");
        Assert.Equal("OB6000C", acquisitionElement.GetProperty("deviceName").GetString());
        Assert.Equal(32, acquisitionElement.GetProperty("inputChannelCount").GetInt32());
        Assert.Equal(8, acquisitionElement.GetProperty("selectedChannelIndices").GetArrayLength());
    }

    [Fact]
    public async Task WriterRejectsSamplesThatDoNotContainAllInputChannels()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ssvep-collection-log-tests", Guid.NewGuid().ToString("N"));
        IReadOnlyList<StimulusTarget> targets = StimulusLayout.CreateDefaultQwertyTargets();
        DataCollectionSettings settings = DataCollectionSettings.CreateDefault(["1"]) with { RepeatsPerTarget = 1 };
        IReadOnlyList<DataCollectionTrial> trials = DataCollectionScheduler.Generate(targets, settings);
        var acquisition = new CollectionAcquisitionInfo(
            SourceMode: "SynchroniSdk",
            DeviceName: "OB6000C",
            DeviceAddress: "AA:BB",
            SamplingRateHz: 250,
            InputChannelCount: 32,
            SelectedChannelIndices: [24, 25, 26, 27, 28, 29, 30, 31],
            ImpedanceThresholdKiloOhms: 500,
            Impedance: [],
            Enable50HzNotch: false,
            EnableHighPass: false,
            EnableLowPass: false);

        await using var writer = DataCollectionLogWriter.Start(
            directory,
            targets,
            settings,
            trials,
            "checkerboard-synchroni",
            acquisition);

        ArgumentException exception = Assert.Throws<ArgumentException>(() => writer.RecordSample(new EegSample(
            7,
            DateTimeOffset.UnixEpoch,
            [1, 2, 3, 4, 5, 6, 7, 8])));

        Assert.Contains("expects all 32 input EEG channels", exception.Message);
    }
}
