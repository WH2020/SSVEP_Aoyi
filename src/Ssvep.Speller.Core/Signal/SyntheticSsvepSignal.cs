namespace Ssvep.Speller.Core.Signal;

public static class SyntheticSsvepSignal
{
    private static readonly double[] ChannelWeights = [1.00, 0.94, 0.88, 0.82, 0.76, 0.70, 0.64, 0.58];

    public static double[,] GenerateWindow(
        double targetFrequencyHz,
        int samplingRateHz,
        int sampleCount,
        int channelCount,
        double noiseAmplitude,
        int seed,
        long startSampleIndex = 0)
    {
        if (samplingRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(samplingRateHz));
        }

        if (sampleCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleCount));
        }

        if (channelCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channelCount));
        }

        var random = new Random(seed);
        var window = new double[sampleCount, channelCount];
        for (int sample = 0; sample < sampleCount; sample++)
        {
            double t = (startSampleIndex + sample) / (double)samplingRateHz;
            double source =
                Math.Sin(2.0 * Math.PI * targetFrequencyHz * t) +
                0.65 * Math.Sin(2.0 * Math.PI * 2.0 * targetFrequencyHz * t + 0.15) +
                0.35 * Math.Sin(2.0 * Math.PI * 3.0 * targetFrequencyHz * t + 0.30);

            for (int channel = 0; channel < channelCount; channel++)
            {
                double weight = channel < ChannelWeights.Length ? ChannelWeights[channel] : 0.5;
                double phase = channel * 0.04;
                double channelSignal = weight * source + 0.08 * Math.Sin(2.0 * Math.PI * targetFrequencyHz * t + phase);
                window[sample, channel] = channelSignal + noiseAmplitude * (random.NextDouble() * 2.0 - 1.0);
            }
        }

        return window;
    }
}
