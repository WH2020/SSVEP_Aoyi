namespace Ssvep.Speller.Core.Signal;

public sealed class ChannelMontage
{
    private const int DefaultOutputChannelCount = 8;
    private readonly int[] selectedChannelIndices;

    public ChannelMontage(IReadOnlyList<int> selectedChannelIndices, int inputChannelCount)
        : this(selectedChannelIndices, inputChannelCount, DefaultOutputChannelCount)
    {
    }

    public ChannelMontage(
        IReadOnlyList<int> selectedChannelIndices,
        int inputChannelCount,
        int outputChannelCount)
    {
        ArgumentNullException.ThrowIfNull(selectedChannelIndices);
        if (inputChannelCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputChannelCount), "Input channel count must be positive.");
        }

        if (outputChannelCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputChannelCount), "Output channel count must be positive.");
        }

        if (selectedChannelIndices.Count != outputChannelCount)
        {
            throw new ArgumentException($"Exactly {outputChannelCount} channels must be selected.", nameof(selectedChannelIndices));
        }

        var seen = new HashSet<int>();
        foreach (int channelIndex in selectedChannelIndices)
        {
            if (channelIndex < 0 || channelIndex >= inputChannelCount)
            {
                throw new ArgumentOutOfRangeException(nameof(selectedChannelIndices), $"Channel index {channelIndex} is outside 0-{inputChannelCount - 1}.");
            }

            if (!seen.Add(channelIndex))
            {
                throw new ArgumentException("Selected channels must be unique.", nameof(selectedChannelIndices));
            }
        }

        this.selectedChannelIndices = selectedChannelIndices.ToArray();
        InputChannelCount = inputChannelCount;
        OutputChannelCount = outputChannelCount;
    }

    public IReadOnlyList<int> SelectedChannelIndices => selectedChannelIndices;

    public int InputChannelCount { get; }

    public int OutputChannelCount { get; }

    public static ChannelMontage CreateDefault(int inputChannelCount, int outputChannelCount = DefaultOutputChannelCount)
    {
        if (inputChannelCount < outputChannelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(inputChannelCount), "Input channel count cannot be smaller than the selected montage size.");
        }

        int[] indices = Enumerable.Range(0, outputChannelCount).ToArray();
        return new ChannelMontage(indices, inputChannelCount, outputChannelCount);
    }

    public double[] Project(double[] channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        if (channels.Length < InputChannelCount)
        {
            throw new ArgumentException($"Expected at least {InputChannelCount} channels, got {channels.Length}.", nameof(channels));
        }

        var projected = new double[OutputChannelCount];
        for (int i = 0; i < selectedChannelIndices.Length; i++)
        {
            projected[i] = channels[selectedChannelIndices[i]];
        }

        return projected;
    }
}
