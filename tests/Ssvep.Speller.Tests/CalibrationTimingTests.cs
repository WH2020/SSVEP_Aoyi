using System.Text.Json;
using Ssvep.Speller.Core.Classification;
using Ssvep.Speller.Core.Research;

namespace Ssvep.Speller.Tests;

public sealed class CalibrationTimingTests
{
    private static ClockModel Model(double rate,double residual=.001,string quality="Estimated")=>new(
        "model","epoch",rate==1?"wpf-rendering-seconds":"device-sample-index","host-stopwatch-seconds",
        10*rate,100,1/rate,0,0,10*rate,600,0,residual/2,residual,quality);

    [Theory]
    [InlineData(.025)] [InlineData(.031)] [InlineData(.09)]
    public void MatureReceiveJitterDoesNotPreventOrdinaryCalibration(double residual)
    {
        var sample=Model(250,residual,"Degraded") with {SecondsPerUnit=.004*(1-.0015),PeriodDriftPpm=-1500};
        var result=CalibrationClockPolicy.Evaluate(sample,Model(1),250,100.1);
        Assert.True(result.Ready); Assert.Equal("EstimatedWithJitter",result.Quality);
        Assert.Equal(Math.Ceiling((residual+.001)*250),result.MarginSamples);
        Assert.Equal("Degraded",result.SampleClock!.Quality);
        Assert.Contains("精度受限",result.Message);
    }
    [Fact]
    public void GoodClocksStillUseEstimatedLabelsAndSmallBoundaryMargins()
    {
        var result=CalibrationClockPolicy.Evaluate(Model(500,.002),Model(1),500,100.1);
        Assert.True(result.Ready); Assert.Equal("Estimated",result.Quality); Assert.Equal(2,result.MarginSamples);
    }
    [Fact]
    public void MissingImmatureAndStaleClocksExplainTheirWait()
    {
        Assert.Contains("脑电时钟尚未建立",CalibrationClockPolicy.Evaluate(null,Model(1),250,100.1).Message);
        foreach(var sample in new[] {Model(250) with {Observations=6},Model(250) with {FirstX=2000}})
        {
            var result=CalibrationClockPolicy.Evaluate(sample,Model(1),250,100.1);
            Assert.False(result.Ready); Assert.Contains("至少 3 秒",result.Message);
        }
        var stale=CalibrationClockPolicy.Evaluate(Model(250),Model(1) with {OffsetSeconds=102},250,102.1);
        Assert.False(stale.Ready); Assert.Contains("未更新",stale.Message);
    }
    [Fact]
    public void ImplausibleRateExcessiveJitterAndInvalidClocksRemainBlocked()
    {
        foreach(var sample in new[]
        {
            Model(250,.101,"Degraded"),Model(250) with {SecondsPerUnit=.0041},
            Model(250) with {SecondsPerUnit=double.NaN},Model(250) with {ResidualP95Seconds=double.NaN},
            Model(250) with {OffsetSeconds=101},Model(250) with {Quality="Uncalibrated"}
        }) Assert.False(CalibrationClockPolicy.Evaluate(sample,Model(1),250,100.1).Ready);
        var render=CalibrationClockPolicy.Evaluate(Model(250),Model(1,.15,"Degraded"),250,100.1);
        Assert.False(render.Ready); Assert.Contains("画面时间戳抖动较大",render.Message);
    }
    [Fact]
    public void SyntheticBluetoothTimingReproducesStrictQualityFailureButRemainsUsable()
    {
        var observations=Enumerable.Range(0,600).Select(i=>new ClockObservation(i*10,100+i*.04*.9985+.03*Math.Sin(i*2*Math.PI/7))).ToArray();
        var sample=RobustClockMapper.Fit(observations,.004,"device-sample-index","test");
        double host=sample.Estimate(sample.LastX);
        Assert.Equal("Degraded",sample.Quality); Assert.True(sample.ResidualP95Seconds>.02);
        var result=CalibrationClockPolicy.Evaluate(sample,Model(1) with {OffsetSeconds=host},250,host+.05);
        Assert.True(result.Ready); Assert.Equal("EstimatedWithJitter",result.Quality);
    }

    private static SessionEvent Event(string kind,object data)=>new(1,kind,null,JsonSerializer.SerializeToElement(data,CanonicalJson.Options));
    private static TrialMarker Marker(string phase,double index)=>new("t",phase,"target","T2",index/250,index/250,"model",index,
        CalibrationClockPolicy.LimitedQuality,DrawHostSeconds:index/250,AlignmentMarginSamples:8);
    private static ReplayDecision Decision(long first,long last,string target)
    {
        var scores=Enumerable.Range(1,6).ToDictionary(i=>$"T{i}",i=>$"T{i}"==target?.9:.1);
        var result=new ClassificationResult(target,.9,"T6",.1,scores);
        return new(1,1,last,first,last,last/250.0,"model",new(result,scores,"weighted-rho2-normalized-v1"),SelectionPolicy.Evaluate(result,new()));
    }
    [Fact]
    public void JitterLimitedLabelsAreEvaluatedOnlyWithinRecordedMargins()
    {
        var events=new[] {Event("trial-marker",Marker("observe-start",0)),Event("trial-marker",Marker("observe-end",1000))};
        var report=ReplayEvaluator.Evaluate([Decision(0,624,"T1"),Decision(50,674,"T2"),Decision(380,1004,"T1")],events,250);
        Assert.Equal(1,report.Accuracy.Value); Assert.Equal("T2",report.Trials.Single().FirstSelection);
        Assert.Contains("时钟精度受限",report.Status);
        Assert.Null(ReplayEvaluator.Evaluate([Decision(0,624,"T1"),Decision(380,1004,"T1")],events,250).Accuracy.Value);
        Assert.False(TrialTiming.IsMapped(Marker("observe-start",0) with {TimingQuality="Degraded"}));
        Assert.False(TrialTiming.IsMapped(Marker("observe-start",0) with {AlignmentMarginSamples=double.NaN}));
    }
    [Fact]
    public void RecordedFeedbackUsesTheSameJitterMarginsAsReplay()
    {
        SessionEvent[] events=[Event("trial-marker",Marker("observe-start",0)),Event("trial-marker",Marker("observe-end",1000)),
            Event("recognition",new {hostSeconds=2.6,firstSampleIndex=0,lastSampleIndex=624,decision=new {accepted=true,targetId="T1"}}),
            Event("recognition",new {hostSeconds=2.8,firstSampleIndex=50,lastSampleIndex=674,decision=new {accepted=true,targetId="T2"}}),
            Event("frame",new {hostSeconds=2.65,targetId="T1"}),Event("frame",new {hostSeconds=2.85,targetId="T2"})];
        var report=RecordedFeedbackEvaluator.Evaluate(events,"hash");
        Assert.Equal("T2",report.Trials.Single().FirstSelection); Assert.True(report.Trials.Single().Correct);
        Assert.Contains("时钟精度受限",report.Status);
    }
}
