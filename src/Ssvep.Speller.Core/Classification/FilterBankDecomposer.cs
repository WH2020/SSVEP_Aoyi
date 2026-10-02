namespace Ssvep.Speller.Core.Classification;

public static class FilterBankDecomposer
{
    private const int FirTapCount = 129;
    private const double FirstLowerCutoffHz = 8.0;
    private const double LowerCutoffStepHz = 8.0;
    private const double UpperCutoffHz = 90.0;

    public static IReadOnlyList<double[,]> Decompose(
        double[,] window,
        int samplingRateHz,
        int subBandCount)
    {
        if (samplingRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(samplingRateHz));
        }

        if (subBandCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(subBandCount));
        }

        double nyquistHz = samplingRateHz / 2.0;
        double upperCutoffHz = Math.Min(UpperCutoffHz, nyquistHz - 1.0);
        var result = new List<double[,]>(subBandCount);
        for (int band = 0; band < subBandCount; band++)
        {
            double lowerCutoffHz = FirstLowerCutoffHz + band * LowerCutoffStepHz;
            if (lowerCutoffHz >= upperCutoffHz)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(subBandCount),
                    "The requested filter bank exceeds the Nyquist frequency.");
            }

            double[] coefficients = DesignBandPassFir(
                samplingRateHz,
                lowerCutoffHz,
                upperCutoffHz,
                FirTapCount);
            result.Add(ApplyFir(window, coefficients));
        }

        return result;
    }

    private static double[] DesignBandPassFir(
        int samplingRateHz,
        double lowerCutoffHz,
        double upperCutoffHz,
        int tapCount)
    {
        var coefficients = new double[tapCount];
        int midpoint = (tapCount - 1) / 2;
        for (int index = 0; index < tapCount; index++)
        {
            int offset = index - midpoint;
            double ideal = LowPassKernel(upperCutoffHz, samplingRateHz, offset) -
                           LowPassKernel(lowerCutoffHz, samplingRateHz, offset);
            double hamming = 0.54 - 0.46 * Math.Cos(2.0 * Math.PI * index / (tapCount - 1));
            coefficients[index] = ideal * hamming;
        }

        double centerFrequencyHz = (lowerCutoffHz + upperCutoffHz) / 2.0;
        double responseReal = 0;
        double responseImaginary = 0;
        for (int index = 0; index < coefficients.Length; index++)
        {
            double angle = -2.0 * Math.PI * centerFrequencyHz * index / samplingRateHz;
            responseReal += coefficients[index] * Math.Cos(angle);
            responseImaginary += coefficients[index] * Math.Sin(angle);
        }

        double gain = Math.Sqrt(responseReal * responseReal + responseImaginary * responseImaginary);
        if (gain > 1e-12)
        {
            for (int index = 0; index < coefficients.Length; index++)
            {
                coefficients[index] /= gain;
            }
        }

        return coefficients;
    }

    private static double LowPassKernel(double cutoffHz, int samplingRateHz, int offset)
    {
        double normalizedCutoff = cutoffHz / samplingRateHz;
        return offset == 0
            ? 2.0 * normalizedCutoff
            : Math.Sin(2.0 * Math.PI * normalizedCutoff * offset) / (Math.PI * offset);
    }

    private static double[,] ApplyFir(double[,] window, IReadOnlyList<double> coefficients)
    {
        int sampleCount = window.GetLength(0);
        int channelCount = window.GetLength(1);
        int midpoint = (coefficients.Count - 1) / 2;
        var filtered = new double[sampleCount, channelCount];

        for (int sample = 0; sample < sampleCount; sample++)
        {
            for (int channel = 0; channel < channelCount; channel++)
            {
                double value = 0;
                for (int tap = 0; tap < coefficients.Count; tap++)
                {
                    int source = ReflectIndex(sample + tap - midpoint, sampleCount);
                    value += coefficients[tap] * window[source, channel];
                }

                filtered[sample, channel] = value;
            }
        }

        return filtered;
    }

    private static int ReflectIndex(int index, int length)
    {
        if (length <= 1)
        {
            return 0;
        }

        while (index < 0 || index >= length)
        {
            index = index < 0 ? -index : 2 * length - index - 2;
        }

        return index;
    }
}
