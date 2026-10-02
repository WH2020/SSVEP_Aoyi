namespace Ssvep.Speller.Core.Classification;

public static class ReferenceSignalGenerator
{
    public static double[,] Generate(
        double frequencyHz,
        int samplingRateHz,
        int sampleCount,
        int harmonicCount,
        double phaseRadians)
    {
        if (frequencyHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frequencyHz));
        }

        if (samplingRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(samplingRateHz));
        }

        if (sampleCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleCount));
        }

        if (harmonicCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(harmonicCount));
        }

        var matrix = new double[sampleCount, harmonicCount * 2];
        for (int sample = 0; sample < sampleCount; sample++)
        {
            double t = sample / (double)samplingRateHz;
            for (int harmonic = 1; harmonic <= harmonicCount; harmonic++)
            {
                double angle = 2.0 * Math.PI * harmonic * frequencyHz * t + phaseRadians;
                int column = (harmonic - 1) * 2;
                matrix[sample, column] = Math.Sin(angle);
                matrix[sample, column + 1] = Math.Cos(angle);
            }
        }

        return matrix;
    }
}
