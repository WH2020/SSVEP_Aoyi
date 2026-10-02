namespace Ssvep.Speller.Core.Signal;

public sealed class EegRingBuffer
{
    private readonly object gate = new();
    private readonly double[,] samples;
    private readonly long[] sampleIndices;
    private readonly DateTimeOffset[] timestamps;
    private readonly double?[] deviceElapsedMilliseconds;
    private readonly bool[] lostSamples;
    private int nextIndex;
    private int count;

    public EegRingBuffer(int channelCount, int capacitySamples)
    {
        if (channelCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channelCount));
        }

        if (capacitySamples <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacitySamples));
        }

        ChannelCount = channelCount;
        CapacitySamples = capacitySamples;
        samples = new double[capacitySamples, channelCount];
        sampleIndices = new long[capacitySamples];
        timestamps = new DateTimeOffset[capacitySamples];
        deviceElapsedMilliseconds = new double?[capacitySamples];
        lostSamples = new bool[capacitySamples];
    }

    public int ChannelCount { get; }

    public int CapacitySamples { get; }

    public int Count
    {
        get
        {
            lock (gate)
            {
                return count;
            }
        }
    }

    public void Append(EegSample sample)
    {
        if (sample.Channels.Length != ChannelCount)
        {
            throw new ArgumentException("Sample channel count does not match the buffer.", nameof(sample));
        }

        lock (gate)
        {
            for (int channel = 0; channel < ChannelCount; channel++)
            {
                samples[nextIndex, channel] = sample.Channels[channel];
            }

            sampleIndices[nextIndex] = sample.SampleIndex;
            timestamps[nextIndex] = sample.Timestamp;
            deviceElapsedMilliseconds[nextIndex] = sample.DeviceElapsedMilliseconds;
            lostSamples[nextIndex] = sample.IsLost;
            nextIndex = (nextIndex + 1) % CapacitySamples;
            count = Math.Min(count + 1, CapacitySamples);
        }
    }

    public bool TryReadLatestWindow(int sampleCount, out double[,] window)
    {
        if (sampleCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleCount));
        }

        if (!TryReadLatestWindowSnapshot(sampleCount, out EegWindowSnapshot? snapshot))
        {
            window = new double[0, 0];
            return false;
        }

        window = snapshot!.Samples;
        return true;
    }

    public bool TryReadLatestWindowSnapshot(int sampleCount, out EegWindowSnapshot? snapshot)
    {
        if (sampleCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleCount));
        }

        lock (gate)
        {
            if (count < sampleCount)
            {
                snapshot = null;
                return false;
            }

            var window = new double[sampleCount, ChannelCount];
            int start = (nextIndex - sampleCount + CapacitySamples) % CapacitySamples;
            bool containsLostSamples = false;
            bool isContiguous = true;
            long firstSampleIndex = sampleIndices[start];
            long previousSampleIndex = firstSampleIndex;

            for (int row = 0; row < sampleCount; row++)
            {
                int source = (start + row) % CapacitySamples;
                for (int channel = 0; channel < ChannelCount; channel++)
                {
                    window[row, channel] = samples[source, channel];
                }

                containsLostSamples |= lostSamples[source];
                if (row > 0 && sampleIndices[source] != previousSampleIndex + 1)
                {
                    isContiguous = false;
                }

                previousSampleIndex = sampleIndices[source];
            }

            int last = (nextIndex - 1 + CapacitySamples) % CapacitySamples;
            snapshot = new EegWindowSnapshot(
                window,
                firstSampleIndex,
                sampleIndices[last],
                timestamps[last],
                containsLostSamples,
                isContiguous);
            return true;
        }
    }

    public bool TryGetLatestSampleMetadata(out EegSampleMetadata metadata)
    {
        lock (gate)
        {
            if (count == 0)
            {
                metadata = new EegSampleMetadata(0, default, null, false);
                return false;
            }

            int last = (nextIndex - 1 + CapacitySamples) % CapacitySamples;
            metadata = new EegSampleMetadata(
                sampleIndices[last],
                timestamps[last],
                deviceElapsedMilliseconds[last],
                lostSamples[last]);
            return true;
        }
    }

    public bool TryGetLatestWindowQuality(int sampleCount, out EegWindowQuality quality)
    {
        if (sampleCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleCount));
        }

        lock (gate)
        {
            if (count < sampleCount)
            {
                quality = new EegWindowQuality(0, 0, false, false);
                return false;
            }

            int start = (nextIndex - sampleCount + CapacitySamples) % CapacitySamples;
            long firstSampleIndex = sampleIndices[start];
            long previousSampleIndex = firstSampleIndex;
            bool containsLostSamples = false;
            bool isContiguous = true;
            for (int row = 0; row < sampleCount; row++)
            {
                int source = (start + row) % CapacitySamples;
                containsLostSamples |= lostSamples[source];
                if (row > 0 && sampleIndices[source] != previousSampleIndex + 1)
                {
                    isContiguous = false;
                }

                previousSampleIndex = sampleIndices[source];
            }

            int last = (nextIndex - 1 + CapacitySamples) % CapacitySamples;
            quality = new EegWindowQuality(
                firstSampleIndex,
                sampleIndices[last],
                containsLostSamples,
                isContiguous);
            return true;
        }
    }
}
