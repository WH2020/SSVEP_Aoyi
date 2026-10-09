namespace Ssvep.Speller.Core.Research;

public sealed record CalibrationTimingStatus(bool Ready,string Quality,string Message,double MarginSamples,
    ClockModel? SampleClock,ClockModel? RenderClock);

/// <summary>Ordinary trial labeling tolerates bounded receive jitter without upgrading clock quality.</summary>
public static class CalibrationClockPolicy
{
    public const string Version="calibration-timing-v1";
    public const string LimitedQuality="EstimatedWithJitter";
    public const double MaximumResidualSeconds=.1;

    public static CalibrationTimingStatus Evaluate(ClockModel? sample,ClockModel? render,int rate,double now)
    {
        if(rate<=0||!double.IsFinite(now)) throw new ArgumentOutOfRangeException(nameof(rate));
        string? reason=BlockReason(sample,1.0/rate,now,"脑电")??BlockReason(render,1,now,"画面");
        if(reason is not null) return new(false,"Uncalibrated","校准准备："+reason,0,sample,render);
        bool limited=sample!.Quality!="Estimated"||render!.Quality!="Estimated";
        // P95 residuals are an empirical boundary margin, not a physical latency guarantee.
        double margin=Math.Ceiling((sample.ResidualP95Seconds+render!.ResidualP95Seconds)*rate);
        return new(true,limited?LimitedQuality:"Estimated",limited?"时钟精度受限，继续校准":"时钟映射已就绪",
            margin,sample,render);
    }

    private static string? BlockReason(ClockModel? model,double nominal,double now,string label)
    {
        if(model is null) return label+"时钟尚未建立，正在收集时间戳";
        if(!double.IsFinite(model.SecondsPerUnit)||model.SecondsPerUnit<=0||!double.IsFinite(model.OffsetSeconds)
            ||!double.IsFinite(model.Pivot)||!double.IsFinite(model.FirstX)||!double.IsFinite(model.LastX)
            ||!double.IsFinite(model.ResidualP95Seconds)||model.ResidualP95Seconds<0)
            return label+"时钟映射无效，请检查采集时间戳";
        if(model.Observations<12||(model.LastX-model.FirstX)*nominal<3)
            return label+"时钟正在积累观测（至少 3 秒）";
        if(Math.Abs(model.SecondsPerUnit/nominal-1)>.01)
            return label+"时钟速率不稳定，请检查采样率与连续数据";
        if(model.ResidualP95Seconds>MaximumResidualSeconds)
            return $"{label}时间戳抖动较大（P95 {model.ResidualP95Seconds*1000:0} 毫秒），请检查传输后重试";
        double age=now-model.Estimate(model.LastX);
        if(!double.IsFinite(age)||age>1.5||age<-.25)
            return label+"时钟观测未更新，正在等待新时间戳";
        if(model.Quality is not ("Estimated" or "Degraded")) return label+"时钟正在建立映射";
        return null;
    }
}

public static class TrialTiming
{
    public static bool IsMapped(TrialMarker marker)=>
        marker.TimingQuality is "Estimated" or CalibrationClockPolicy.LimitedQuality
        &&marker.EstimatedSampleIndex is double index&&double.IsFinite(index)
        &&double.IsFinite(marker.AlignmentMarginSamples)&&marker.AlignmentMarginSamples>=0
        &&(marker.TimingQuality!="EstimatedWithJitter"||marker.AlignmentMarginSamples>0);

    public static double FirstUsableSample(TrialMarker marker)=>marker.EstimatedSampleIndex!.Value+marker.AlignmentMarginSamples;
    public static double LastUsableSample(TrialMarker marker)=>marker.EstimatedSampleIndex!.Value-marker.AlignmentMarginSamples;
}
