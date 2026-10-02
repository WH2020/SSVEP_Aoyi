using System.Runtime.CompilerServices;

namespace Ssvep.Speller.Core.Signal;

public interface IEegStream : IAsyncDisposable
{
    int SamplingRateHz { get; }

    int ChannelCount { get; }

    IAsyncEnumerable<EegSample> ReadSamplesAsync(CancellationToken cancellationToken);
}

public sealed class SimulatedEegStream : IEegStream
{
    private readonly Func<long, double> targetFrequencyAtSample;
    private readonly Random random;
    private long sampleIndex;

    public SimulatedEegStream(
        Func<long, double> targetFrequencyAtSample,
        int samplingRateHz = 250,
        int channelCount = 8,
        double noiseAmplitude = 0.2,
        int seed = 1234)
    {
        this.targetFrequencyAtSample = targetFrequencyAtSample;
        SamplingRateHz = samplingRateHz;
        ChannelCount = channelCount;
        NoiseAmplitude = noiseAmplitude;
        random = new Random(seed);
    }

    public int SamplingRateHz { get; }

    public int ChannelCount { get; }

    public double NoiseAmplitude { get; }

    public async IAsyncEnumerable<EegSample> ReadSamplesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / SamplingRateHz));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            double frequency = targetFrequencyAtSample(sampleIndex);
            double[,] oneSample = SyntheticSsvepSignal.GenerateWindow(
                frequency,
                SamplingRateHz,
                sampleCount: 1,
                ChannelCount,
                NoiseAmplitude,
                random.Next(),
                sampleIndex);

            var channels = new double[ChannelCount];
            for (int channel = 0; channel < ChannelCount; channel++)
            {
                channels[channel] = oneSample[0, channel];
            }

            yield return new EegSample(sampleIndex, DateTimeOffset.UtcNow, channels);
            sampleIndex++;
        }
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
