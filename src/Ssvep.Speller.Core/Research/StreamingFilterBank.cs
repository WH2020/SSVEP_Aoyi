using System.Numerics;

namespace Ssvep.Speller.Core.Research;

public sealed record Sos(double B0, double B1, double B2, double A1, double A2);

/// <summary>Fourth-order analog Butterworth prototype, bandpass transform, bilinear transform, conjugate SOS.</summary>
public sealed class StreamingFilterBank
{
    public const string Version = "butterworth4-bilinear-sos-v1";
    private readonly Sos[][] bands;
    private readonly Sos? notch;
    private readonly double[,,,] state;
    private readonly double[,] notchState;
    private readonly int channels;
    public int WarmupSamples { get; }
    public IReadOnlyList<IReadOnlyList<Sos>> Coefficients => bands.Select(b => (IReadOnlyList<Sos>)Array.AsReadOnly(b)).ToArray();

    public StreamingFilterBank(ResearchFilter filter, int rate, int channels)
    {
        this.channels = channels;
        bands = filter.BandsHz.Select(b => DesignBandpass(b[0], b[1], rate)).ToArray();
        if (filter.SoftwareNotchHz is double f) notch = DesignNotch(f, filter.NotchQ, rate);
        state = new double[bands.Length, channels, 4, 2]; notchState = new double[channels, 2];
        double radius = bands.SelectMany(b => b).Concat(notch is null ? [] : new[] { notch }).Max(PoleRadius);
        WarmupSamples = Math.Max(rate, (int)Math.Ceiling(Math.Log(1e-6) / Math.Log(radius)));
        if (WarmupSamples > rate * 30) throw new ArgumentException("滤波收敛超过内部 30 秒预算，请增加子带宽度或提高下限。");
    }

    public void Reset() { Array.Clear(state); Array.Clear(notchState); }

    public double[] Push(IReadOnlyList<double> sample)
    {
        if (sample.Count != channels) throw new ArgumentException("滤波通道数发生变化。");
        var output = new double[bands.Length * channels];
        for (int c = 0; c < channels; c++)
        {
            double x = sample[c];
            if (notch is not null) x = Apply(notch, x, ref notchState[c,0], ref notchState[c,1]);
            for (int b = 0; b < bands.Length; b++)
            {
                double y = x;
                for (int s = 0; s < bands[b].Length; s++) y = Apply(bands[b][s], y, ref state[b,c,s,0], ref state[b,c,s,1]);
                if (!double.IsFinite(y)) throw new ArithmeticException("连续滤波出现非有限输出。");
                output[b * channels + c] = y;
            }
        }
        return output;
    }

    private static double Apply(Sos s, double x, ref double z1, ref double z2)
    {
        double y = s.B0 * x + z1;
        z1 = s.B1 * x - s.A1 * y + z2; z2 = s.B2 * x - s.A2 * y;
        return y;
    }

    public static Sos[] DesignBandpass(double low, double high, double fs)
    {
        if (!(low > 0 && high > low && high < fs/2)) throw new ArgumentException("无效带通边界。");
        double w1 = 2 * fs * Math.Tan(Math.PI * low / fs), w2 = 2 * fs * Math.Tan(Math.PI * high / fs);
        double bw = w2-w1, w0 = Math.Sqrt(w1*w2);
        var poles = new List<Complex>();
        for (int k = 0; k < 4; k++)
        {
            var p = Complex.FromPolarCoordinates(1, Math.PI * (2*k+5)/8);
            var d = Complex.Sqrt(bw*bw*p*p - 4*w0*w0);
            foreach (var a in new[] { (bw*p+d)/2, (bw*p-d)/2 }) poles.Add((2*fs+a)/(2*fs-a));
        }
        var sos = poles.Where(p => p.Imaginary > 1e-10).OrderBy(p => p.Magnitude)
            .Select(p => new Sos(1,0,-1,-2*p.Real,p.Magnitude*p.Magnitude)).ToArray();
        if (sos.Length != 4 || poles.Any(p => p.Magnitude >= 1)) throw new ArithmeticException("滤波器极点不稳定。");
        double center = fs/Math.PI*Math.Atan(w0/(2*fs));
        double gain = Math.Pow(1 / Response(sos, center, fs).Magnitude, .25);
        return sos.Select(s => s with { B0 = s.B0*gain, B1 = s.B1*gain, B2 = s.B2*gain }).ToArray();
    }

    private static Sos DesignNotch(double frequency, double q, int fs)
    {
        double w = 2*Math.PI*frequency/fs, beta = Math.Tan(w/(2*q)), gain = 1/(1+beta);
        return new(gain,-2*gain*Math.Cos(w),gain,-2*gain*Math.Cos(w),2*gain-1);
    }

    public static Complex Response(IEnumerable<Sos> sections, double frequency, double fs)
    {
        var z = Complex.FromPolarCoordinates(1,-2*Math.PI*frequency/fs);
        return sections.Aggregate(Complex.One, (h,s) => h*(s.B0+s.B1*z+s.B2*z*z)/(1+s.A1*z+s.A2*z*z));
    }

    private static double PoleRadius(Sos s)
    {
        var root = Complex.Sqrt(s.A1*s.A1-4*s.A2);
        return Math.Max(((-s.A1+root)/2).Magnitude, ((-s.A1-root)/2).Magnitude);
    }
}
