namespace Ssvep.Speller.Core.Research;

public sealed record ClockObservation(double X, double HostSeconds, double Weight = 1);
public sealed record ClockModel(string ModelId, string EpochId, string SourceDomain, string HostDomain,
    double Pivot, double OffsetSeconds, double SecondsPerUnit, double PeriodDriftPpm,
    double FirstX, double LastX, int Observations, int ExcludedObservations,
    double ResidualMadSeconds, double ResidualP95Seconds, string Quality, double? CalibrationOffsetSeconds = null)
{
    public double Estimate(double x) => OffsetSeconds + SecondsPerUnit*(x-Pivot);
    public double Invert(double hostSeconds) => Pivot + (hostSeconds-OffsetSeconds)/SecondsPerUnit;
}

/// <summary>Centered iteratively reweighted Huber regression. Receive-delay bias is intentionally not called calibration.</summary>
public sealed class RobustClockMapper
{
    public const string Version = "huber-clock-v1";
    private readonly Queue<ClockObservation> observations = new();
    private readonly double nominalSecondsPerUnit;
    private readonly string sourceDomain;
    private double? lastX;
    private double? lastTime;
    private int excluded;
    private int sinceFit;
    public string EpochId { get; private set; } = Guid.NewGuid().ToString("N");
    public ClockModel? Current { get; private set; }

    public RobustClockMapper(string sourceDomain, double nominalSecondsPerUnit)
    {
        this.sourceDomain = sourceDomain; this.nominalSecondsPerUnit = nominalSecondsPerUnit;
        if (!(nominalSecondsPerUnit > 0)) throw new ArgumentOutOfRangeException(nameof(nominalSecondsPerUnit));
    }

    public void Reset()
    {
        observations.Clear(); lastX=null; lastTime=null; excluded=0; sinceFit=0; Current=null;
        EpochId=Guid.NewGuid().ToString("N");
    }

    public ClockModel? Observe(double x, double hostSeconds, double weight = 1)
    {
        if (!double.IsFinite(x) || !double.IsFinite(hostSeconds) || !double.IsFinite(weight) || weight <= 0) { excluded++; return null; }
        if (lastX is double duplicate && x == duplicate) { excluded++; return null; }
        if (lastX is double prior && x < prior || lastTime is double priorTime && hostSeconds < priorTime-.5) Reset();
        // Backlogged SDK callbacks delivered in a burst are not independent high-quality timing evidence.
        if (lastTime is double time && hostSeconds-time < .002) weight *= .02;
        lastX=x; lastTime=hostSeconds;
        observations.Enqueue(new(x,hostSeconds,weight));
        while(observations.Count > 600 || observations.Count > 1 && hostSeconds-observations.Peek().HostSeconds > 90) observations.Dequeue();
        if (observations.Count < 6 || ++sinceFit < 10 && Current is not null) return null;
        sinceFit=0;
        Current=Fit(observations.ToArray(),nominalSecondsPerUnit,sourceDomain,EpochId,excluded);
        return Current;
    }

    public static ClockModel Fit(IReadOnlyList<ClockObservation> input, double nominal, string domain, string epoch, int excluded = 0)
    {
        if (input.Count < 2) throw new ArgumentException("至少需要两个时钟观测。");
        double pivot=input[0].X, origin=input[0].HostSeconds, slope=nominal, offset=0;
        var weights=input.Select(p => p.Weight).ToArray();
        for(int iteration=0;iteration<8;iteration++)
        {
            double sum=weights.Sum(), mx=0, my=0;
            for(int i=0;i<input.Count;i++) { mx+=weights[i]*(input[i].X-pivot); my+=weights[i]*(input[i].HostSeconds-origin); }
            mx/=sum; my/=sum;
            double xx=0, xy=0;
            for(int i=0;i<input.Count;i++)
            {
                double dx=input[i].X-pivot-mx;
                xx+=weights[i]*dx*dx; xy+=weights[i]*dx*(input[i].HostSeconds-origin-my);
            }
            slope=xx > 1e-20 ? xy/xx : nominal; offset=my-slope*mx;
            var residuals=input.Select(p => p.HostSeconds-origin-offset-slope*(p.X-pivot)).ToArray();
            double median=Quantile(residuals,.5);
            double scale=Math.Max(1e-6,1.4826*Quantile(residuals.Select(r => Math.Abs(r-median)),.5));
            for(int i=0;i<weights.Length;i++) weights[i]=input[i].Weight*Math.Min(1,1.345*scale/Math.Max(1e-12,Math.Abs(residuals[i]-median)));
        }
        double[] errors=input.Select(p => Math.Abs(p.HostSeconds-origin-offset-slope*(p.X-pivot))).ToArray();
        double mad=Quantile(errors,.5), p95=Quantile(errors,.95);
        bool finite=double.IsFinite(slope) && slope > 0 && double.IsFinite(offset);
        string quality=!finite || Math.Abs(slope/nominal-1) > .01 || p95 > .02 ? "Degraded"
            : input.Count < 12 || (input[^1].X-input[0].X)*nominal < 3 ? "Uncalibrated" : "Estimated";
        if (!finite) { slope=nominal; offset=0; }
        return new(Guid.NewGuid().ToString("N"),epoch,domain,"host-stopwatch-seconds",pivot,origin+offset,slope,
            (slope/nominal-1)*1e6,input[0].X,input[^1].X,input.Count,excluded,mad,p95,quality);
    }

    public static double Quantile(IEnumerable<double> data, double q)
    {
        var sorted=data.Order().ToArray();
        if(sorted.Length==0) return 0;
        double index=(sorted.Length-1)*q; int lo=(int)index, hi=Math.Min(lo+1,sorted.Length-1);
        return sorted[lo]+(sorted[hi]-sorted[lo])*(index-lo);
    }
}

public sealed record ClockRoundTrip(long Sequence, double HostSend, double PythonReceive, double PythonSend, double HostReceive)
{
    public double Rtt => (HostReceive-HostSend)-(PythonSend-PythonReceive);
}

public sealed class ClockDomainMapper
{
    private readonly Queue<ClockRoundTrip> observations = new();
    public ClockModel? Current { get; private set; }
    public string EpochId { get; } = Guid.NewGuid().ToString("N");
    public ClockModel? Observe(ClockRoundTrip observation)
    {
        if (!double.IsFinite(observation.Rtt) || observation.Rtt < 0 || observation.Rtt > 1 || observation.PythonSend < observation.PythonReceive) return null;
        observations.Enqueue(observation);
        while(observations.Count>120) observations.Dequeue();
        double limit=RobustClockMapper.Quantile(observations.Select(p => p.Rtt),.25)+.001;
        var low=observations.Where(p => p.Rtt<=limit).OrderBy(p => p.HostSend)
            .Select(p => new ClockObservation((p.PythonReceive+p.PythonSend)/2,(p.HostSend+p.HostReceive)/2)).ToArray();
        if(low.Length < 2) return null;
        Current=RobustClockMapper.Fit(low,1,"python-perf-counter-seconds",EpochId);
        return Current;
    }
}
