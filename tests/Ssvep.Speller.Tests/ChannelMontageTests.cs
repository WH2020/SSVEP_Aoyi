using Ssvep.Speller.Core.Signal;

namespace Ssvep.Speller.Tests;

public sealed class ChannelMontageTests
{
    [Fact]
    public void DefaultMontageSelectsFirstEightChannelsFromThirtyTwo()
    {
        ChannelMontage montage = ChannelMontage.CreateDefault(inputChannelCount: 32, outputChannelCount: 8);

        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7], montage.SelectedChannelIndices);
    }

    [Fact]
    public void ProjectSampleKeepsOnlySelectedChannelsInOrder()
    {
        var montage = new ChannelMontage([15, 14, 13, 12, 11, 10, 9, 8], inputChannelCount: 32);
        var input = Enumerable.Range(1, 32).Select(value => (double)value).ToArray();

        double[] projected = montage.Project(input);

        Assert.Equal([16, 15, 14, 13, 12, 11, 10, 9], projected);
    }

    [Fact]
    public void MontageRequiresExactlyEightUniqueValidChannels()
    {
        Assert.Throws<ArgumentException>(() => new ChannelMontage([0, 1, 2, 3, 4, 5, 6], 32));
        Assert.Throws<ArgumentException>(() => new ChannelMontage([0, 1, 2, 3, 4, 5, 6, 6], 32));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChannelMontage([0, 1, 2, 3, 4, 5, 6, 32], 32));
    }
}
