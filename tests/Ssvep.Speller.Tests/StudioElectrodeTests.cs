using System.Text.Json;
using Ssvep.Speller.Core.Studio;

namespace Ssvep.Speller.Tests;

public sealed class StudioElectrodeTests
{
    [Fact]
    public void RealDefaultsDoNotInventPhysicalElectrodeMapping()
    {
        Assert.Empty(StudioElectrodes.Create(32).Bindings);
        Assert.Equal(Enumerable.Range(0,8), StudioElectrodes.Create(32).SelectedChannels);
        Assert.Equal("O1", StudioElectrodes.Create(8, simulation: true).Bindings[0].Electrode);
    }

    [Fact]
    public void DuplicateLocationsAndLegacyAliasesCannotMapToTwoChannels()
    {
        var setup = StudioElectrodes.Assign(StudioElectrodes.Create(32), 7, "t3");
        Assert.Equal("T7", setup.Bindings.Single().Electrode);
        Assert.Throws<ArgumentException>(() => StudioElectrodes.Assign(setup, 4, "T7"));
        Assert.Throws<ArgumentException>(() => StudioElectrodes.Assign(setup, 4, "T3"));
        Assert.Empty(StudioElectrodes.Assign(setup, 7, null).Bindings);
    }

    [Fact]
    public void PositionAndChannelSelectionAreIndependentAndPreserveSdkIndices()
    {
        var setup = StudioElectrodes.Assign(StudioElectrodes.Create(32), 15, "O1");
        setup = StudioElectrodes.Validate(setup with { SelectedChannels = [15,2,3,4,5,6,7,8] });
        Assert.Equal(15, setup.Bindings.Single().ChannelIndex);
        Assert.Equal(new[] {2,3,4,5,6,7,8,15}, setup.SelectedChannels);
        Assert.Single(StudioElectrodes.Assign(setup, 15, "O2").Bindings);
        Assert.Throws<ArgumentException>(() => StudioElectrodes.Validate(setup with { SelectedChannels = [32] }));
        Assert.Throws<ArgumentOutOfRangeException>(() => StudioElectrodes.Assign(setup, -1, "O1"));
    }

    [Fact]
    public void StoredSetupSurvivesReloadAndInvalidSaveDoesNotReplaceIt()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var setup = StudioElectrodes.Assign(StudioElectrodes.Create(16), 15, "Oz") with { SelectedChannels = [0,15] };
            StudioElectrodes.Save(path, setup);
            var restored = StudioElectrodes.Load(path, 16);
            Assert.Equal(setup.Bindings, restored.Bindings);
            Assert.Equal(setup.SelectedChannels, restored.SelectedChannels);
            Assert.Throws<InvalidDataException>(() => StudioElectrodes.Load(path, 8));
            Assert.Throws<ArgumentException>(() => StudioElectrodes.Save(path, setup with { SelectedChannels = [0,0] }));
            Assert.Equal(setup.SelectedChannels, StudioElectrodes.Load(path, 16).SelectedChannels);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RecorderFreezesElectrodeMappingWithSelectedSdkIndices()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            StudioElectrodeBinding[] bindings = [new(0,"O1"), new(7,"O2")];
            var recorder = new StudioRecorder(root, StudioContentService.CreateDefault(), true,250,
                [0,1,2,3,4,5,6,7], bindings);
            bindings[0] = new(0,"Fp1");
            await recorder.CompleteAsync(true);
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(recorder.DirectoryPath,"manifest.json")));
            Assert.Equal("O1", manifest.RootElement.GetProperty("electrodeBindings")[0].GetProperty("electrode").GetString());
            Assert.Equal(7, manifest.RootElement.GetProperty("electrodeBindings")[1].GetProperty("channelIndex").GetInt32());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root,true); }
    }
}
