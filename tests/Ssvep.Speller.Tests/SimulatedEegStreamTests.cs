using Ssvep.Speller.Core.Signal;

namespace Ssvep.Speller.Tests;

public sealed class SimulatedEegStreamTests
{
    [Fact]
    public async Task SimulatedStreamEmitsEightChannelSamplesWithAdvancingPhase()
    {
        await using var stream = new SimulatedEegStream(
            targetFrequencyAtSample: _ => 10.0,
            samplingRateHz: 250,
            channelCount: 8,
            noiseAmplitude: 0.0,
            seed: 7);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var samples = new List<EegSample>();

        await foreach (EegSample sample in stream.ReadSamplesAsync(cts.Token))
        {
            samples.Add(sample);
            if (samples.Count == 3)
            {
                break;
            }
        }

        Assert.All(samples, sample => Assert.Equal(8, sample.Channels.Length));
        Assert.Equal(0, samples[0].SampleIndex);
        Assert.Equal(1, samples[1].SampleIndex);
        Assert.Equal(2, samples[2].SampleIndex);
        Assert.NotEqual(samples[0].Channels[0], samples[1].Channels[0]);
        Assert.NotEqual(samples[1].Channels[0], samples[2].Channels[0]);
    }
}
