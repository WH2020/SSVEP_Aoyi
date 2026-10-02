using System.Text;
using System.Text.Json;
using Ssvep.Speller.Core.Classification;
using Ssvep.Speller.Core.Research;
using Ssvep.Speller.Core.Signal;
using Ssvep.Speller.Core.Studio;

namespace Ssvep.Speller.Tests;

public sealed class ResearchTests
{
    private static EegSample Sample(long i,int channels=8,bool lost=false) => new(i,DateTimeOffset.UnixEpoch.AddSeconds(1+i/250.0),
        Enumerable.Range(0,channels).Select(c=>Math.Sin(2*Math.PI*9.2*i/250+c*.08)).ToArray(),i*4,lost);
    private static RunContext Context(ResearchProfile? p=null,int rate=250) => new(p??new(),StudioContentService.CreateDefault(),rate,8,Enumerable.Range(0,8),[],new(800,500),"uV",1e-6,"fixture");

    [Theory]
    [InlineData(250,625,50)] [InlineData(500,1250,100)]
    public void SampleGridDoesNotDependOnBatchBoundaries(int rate,int width,int step)
    {
        long[] Run(int chunk)
        {
            var scheduler=new SampleWindowScheduler(rate,8,17); var windows=new List<ScheduledWindow>();
            for(int start=0;start<width+17+step*6;start+=chunk)
                for(int i=start;i<Math.Min(width+17+step*6,start+chunk);i++)
                    if(scheduler.Push(Sample(i)) is { } window) windows.Add(window);
            Assert.All(windows,w=>Assert.Equal(width,w.Window.LastSampleIndex-w.Window.FirstSampleIndex+1));
            return windows.Select(w=>w.Window.LastSampleIndex).ToArray();
        }
        var one=Run(1); Assert.Equal(Run(13),one); Assert.Equal(Run(270),one);
        Assert.Equal(Enumerable.Range(0,7).Select(i=>(long)(17+width-1+i*step)),one);
    }

    [Fact]
    public void LossAndGapRestartWarmupAndNeverCreateCrossGapWindows()
    {
        var scheduler=new SampleWindowScheduler(250,8,10);
        for(int i=0;i<700;i++) scheduler.Push(Sample(i));
        long previous=scheduler.Continuity; scheduler.Push(Sample(700,lost:true));
        Assert.True(scheduler.Continuity>previous);
        var windows=new List<ScheduledWindow>();
        for(int i=701;i<1500;i++) if(scheduler.Push(Sample(i)) is { } window) windows.Add(window);
        Assert.Equal(711,windows[0].Window.FirstSampleIndex); Assert.Equal(1335,windows[0].Window.LastSampleIndex);
        Assert.All(windows,w=>Assert.True(w.Window.FirstSampleIndex>700));
        scheduler.Push(Sample(1600)); Assert.Equal(0,scheduler.Count);
    }

    [Fact]
    public void JcsPreservesEcmaNumbersAndRejectsAmbiguousInput()
    {
        string a="{\"z\":8.0,\"a\":[1e30,1e-7,0.000001,-0,333333333.33333329]}";
        Assert.Equal("{\"a\":[1e+30,1e-7,0.000001,0,333333333.3333333],\"z\":8}",Encoding.UTF8.GetString(CanonicalJson.Encode(a)));
        Assert.Equal(CanonicalJson.HashJson("{\"a\":8e0,\"b\":2}"),CanonicalJson.HashJson("{\"b\":2.0,\"a\":8}"));
        Assert.ThrowsAny<Exception>(()=>CanonicalJson.Encode("{\"a\":1,\"a\":2}"));
        Assert.ThrowsAny<Exception>(()=>CanonicalJson.Encode("{\"a\":1e999}"));
        Assert.ThrowsAny<Exception>(()=>CanonicalJson.Encode("{\"a\":\"\\ud800\"}"));
        Assert.NotEqual(CanonicalJson.HashJson("{\"a\":\"é\"}"),CanonicalJson.HashJson("{\"a\":\"e\\u0301\"}"));
    }

    [Theory]
    [InlineData("samplingRateHz", "500")] [InlineData("windowSeconds","3")] [InlineData("phaseRadians","1")]
    [InlineData("targetCount","8")] [InlineData("encoding","\"square\"")] [InlineData("unknown","true")]
    public void ResearchImportsRejectRestrictedAndUnknownFields(string key,string value)
    {
        string json=new ResearchProfile().ToJson(); json=json.Insert(json.LastIndexOf('}'),$",\"{key}\":{value}");
        Assert.ThrowsAny<Exception>(()=>ResearchProfile.Parse(json));
    }

    [Fact]
    public void ProfileHashCoversEffectiveParametersAndSnapshotOwnsCollections()
    {
        double[] frequencies=[8,9,10,11,12,13]; var p=new ResearchProfile {TargetFrequenciesHz=frequencies};
        string hash=p.ProfileHash;
        Assert.Equal(hash,(p with {Name="重命名",Revision=9,ProfileId="other"}).ProfileHash);
        Assert.NotEqual(hash,(p with {Decision=new(.21,"absolute",.03)}).ProfileHash);
        var snapshot=Context(p); frequencies[0]=15;
        Assert.Equal(8,snapshot.Profile.TargetFrequenciesHz[0]);
        Assert.Equal(snapshot.Profile.ProfileHash,ResearchProfile.Parse(snapshot.Profile.ToJson()).ProfileHash);
        Assert.Throws<ArgumentException>(()=>(ResearchProfile.Legacy() with {Decision=new(.2,"absolute",.02)}).Validate());
    }

    [Theory] [InlineData(250)] [InlineData(500)]
    public void ButterworthFrequencyResponseAndStepStateAreStable(int rate)
    {
        var sections=StreamingFilterBank.DesignBandpass(6,48,rate);
        Assert.InRange(StreamingFilterBank.Response(sections,6,rate).Magnitude,.70710,.70712);
        Assert.InRange(StreamingFilterBank.Response(sections,48,rate).Magnitude,.70710,.70712);
        Assert.InRange(StreamingFilterBank.Response(sections,1,rate).Magnitude,0,.001);
        Assert.InRange(StreamingFilterBank.Response(sections,90,rate).Magnitude,0,.05);
        var filter=new StreamingFilterBank(new(),rate,8);
        Assert.InRange(filter.WarmupSamples,rate,rate*30);
        double maximum=0;
        for(int i=0;i<rate*10;i++) maximum=Math.Max(maximum,filter.Push(Enumerable.Repeat(i==0?1.0:0,8).ToArray()).Max(Math.Abs));
        Assert.InRange(maximum,.001,1);
        Assert.All(filter.Push(new double[8]),v=>Assert.InRange(Math.Abs(v),0,1e-6));
        filter.Reset(); Assert.All(filter.Push(new double[8]),v=>Assert.Equal(0,v));
    }

    [Theory] [InlineData(250)] [InlineData(500)]
    public void ResearchPipelineRecognizesNearlyCollinearChannelsWithoutDependingOnUnits(int rate)
    {
        foreach(double scale in new[] {1.0,1e-6,1e5})
        {
            var pipeline=new ResearchPipeline(Context(rate:rate)); ScheduledWindow? last=null;
            for(int i=0;i<rate*6;i++)
            {
                double[] values=Enumerable.Range(0,8).Select(c=>scale*(Math.Sin(2*Math.PI*9.2*i/rate+c*1e-5)+.3*Math.Sin(4*Math.PI*9.2*i/rate+c*1e-5))).ToArray();
                last=pipeline.Push(Sample(i) with {Channels=values})??last;
            }
            Assert.NotNull(last); var result=pipeline.Classify(last!);
            Assert.Equal("T2",result.Result.BestTargetId); Assert.All(result.Result.Scores.Values,s=>Assert.InRange(s,0,1));
            Assert.True(SelectionPolicy.Evaluate(result.Result,new()).Accepted);
        }
    }

    [Fact]
    public void LegacyPipelineRetainsExactOriginalWindowScores()
    {
        var pipeline=new ResearchPipeline(Context(ResearchProfile.Legacy())); ScheduledWindow? window=null;
        for(int i=0;i<625;i++) window=pipeline.Push(Sample(i))??window;
        var baseline=new FbccaClassifier().Classify(window!.Window.Samples,StudioRuntimeProfile.Targets,250);
        var result=pipeline.Classify(window);
        Assert.Equal(baseline.Scores,result.Result.Scores);
    }

    [Fact]
    public void SelectionRejectsTiesAndUsesSingleQualifiedDecision()
    {
        var scores=Enumerable.Range(1,6).ToDictionary(i=>$"T{i}",i=>i<=2?.5:.1);
        var result=new ClassificationResult("T1",.5,"T2",.5,scores);
        Assert.Equal("AmbiguousTie",SelectionPolicy.Evaluate(result,new(0,"relative",0)).Reason);
        scores["T1"]=.9; var evaluation=SelectionPolicy.Evaluate(result,new(.15,"relative",.15));
        Assert.True(evaluation.Accepted); Assert.Equal("T1",evaluation.TargetId);
        scores["T3"]=double.NaN; Assert.Equal("InvalidScore",SelectionPolicy.Evaluate(result,new()).Reason);
    }

    [Fact]
    public void RobustClockSeparatesDriftFromReceiveOutliersAndKeepsFixedBiasUnknown()
    {
        var mapper=new RobustClockMapper("samples",1.0/250); double slope=.004*(1+80e-6);
        for(int i=0;i<400;i++) mapper.Observe(1_000_000_000L+i*10,123+slope*i*10+(i%17==0?.05:0)+(i%3-1)*.0001);
        var model=mapper.Current!;
        Assert.InRange(model.PeriodDriftPpm,75,85); Assert.InRange(model.Estimate(1_000_000_000),122.999,123.001);
        Assert.Null(model.CalibrationOffsetSeconds);
        string epoch=mapper.EpochId; mapper.Observe(1_000_000_000L+399*10,999); Assert.Equal(epoch,mapper.EpochId);
        mapper.Observe(0,200); Assert.NotEqual(epoch,mapper.EpochId); Assert.Null(mapper.Current);
    }

    [Theory]
    [InlineData(true,false,9.9,9.9,"ready",BridgeHealthKind.BridgeExited)]
    [InlineData(false,true,9.9,9.9,"ready",BridgeHealthKind.BridgeIpcFailure)]
    [InlineData(false,false,6,9.9,"ready",BridgeHealthKind.BridgeUnresponsive)]
    [InlineData(false,false,9.9,9,"ready",BridgeHealthKind.DataStalled)]
    [InlineData(false,false,9.9,9.9,"disconnected",BridgeHealthKind.DeviceDisconnected)]
    [InlineData(false,false,9.9,9.9,"ready",BridgeHealthKind.Healthy)]
    public void HealthUsesIndependentEvidence(bool exited,bool closed,double heartbeat,double sample,string state,BridgeHealthKind expected)
        =>Assert.Equal(expected,BridgeHealthPolicy.Evaluate(10,0,exited,closed,heartbeat,sample,state).Kind);

    [Fact]
    public void RenderingClockDeduplicatesCallbacksAndRestartsEpochOnBackwardTime()
    {
        var clock=new PresentationClock(); clock.Reset(); Assert.True(clock.Advance(100));
        Assert.True(clock.Advance(100.1)); Assert.Equal(.1,clock.PhaseSeconds,6);
        Assert.False(clock.Advance(100.1)); long epoch=clock.Epoch;
        Assert.True(clock.Advance(1)); Assert.Equal(epoch+1,clock.Epoch); Assert.Equal(0,clock.PhaseSeconds);
    }

    [Fact]
    public void BinaryRecoversCommittedBlocksAndDetectsCorruption()
    {
        string folder=Temp();
        try
        {
            using(var writer=new BinarySampleWriter(folder,new(1,250,8,"hash","uV",1e-6,"fixture",Enumerable.Range(1,8).Select(i=>$"CH{i}").ToArray())))
                for(int b=0;b<3;b++) writer.Write(new(b,"epoch",250,Enumerable.Range(b*10,10).Select(i=>Sample(i,lost:i>=10&&i<20)).ToArray(),b,b),1,1);
            var reader=new BinarySampleReader(Path.Combine(folder,"samples.bin")); var blocks=reader.Read().ToArray();
            Assert.Equal(3,blocks.Length); Assert.True(blocks[1].Batch.Samples[0].IsLost); Assert.Equal(Sample(29).Channels,blocks[2].Batch.Samples[^1].Channels);
            using(var file=new FileStream(Path.Combine(folder,"samples.bin"),FileMode.Open,FileAccess.Write)) {file.Position=blocks[1].FileOffset+15; file.WriteByte(0xff); file.SetLength(blocks[2].FileOffset+5);}
            Assert.Single(reader.Read()); Assert.Equal(2,reader.Warnings.Count);
            reader.RebuildIndex(Path.Combine(folder,"recovered.idx")); Assert.Equal(40,new FileInfo(Path.Combine(folder,"recovered.idx")).Length);
            Assert.Equal(0xe3069283u,Crc32C.Compute(Encoding.ASCII.GetBytes("123456789")));
        }
        finally {Directory.Delete(folder,true);}
    }

    [Fact]
    public async Task RecordedRawWindowsReplayAndBrainVisionRetainsUnitsAndGaps()
    {
        string folder=Temp();
        try
        {
            var context=Context(ResearchProfile.Legacy()); var live=new ResearchPipeline(context); var windows=new List<ScheduledWindow>();
            var recorder=new StudioRecorder(folder,context.Content,true,250,context.Channels.ToArray(),[],context);
            for(int start=0;start<1250;start+=25)
            {
                var samples=Enumerable.Range(start,25).Select(i=>Sample(i,lost:i>=700&&i<710)).ToArray();
                recorder.RecordBatch(new(start/25,"epoch",250,samples,start/250.0,start/250.0),1,1,true);
                foreach(var sample in samples) if(live.Push(sample) is { } window) windows.Add(window);
            }
            await recorder.CompleteAsync(true);
            var report=ReplayRunner.Run(recorder.DirectoryPath);
            Assert.Equal(windows.Select(w=>w.Window.LastSampleIndex),report.Decisions.Select(d=>d.LastSampleIndex));
            Assert.Equal("缺少标签",report.Metrics.Status); Assert.Null(report.Metrics.Accuracy.Value);
            Assert.Empty(report.Warnings);
            var exported=BrainVisionExporter.Export(recorder.DirectoryPath,folder);
            Assert.Equal(2,exported.SegmentCount); Assert.Equal(1240,exported.SampleCount);
            using var raw=new BinaryReader(File.OpenRead(Path.Combine(exported.Directory,"segment-0001.eeg")));
            Assert.Equal((float)(Sample(0).Channels[1]*10),ReadSecond(raw));
            Assert.Contains("SamplingInterval=4000",File.ReadAllText(Path.Combine(exported.Directory,"segment-0001.vhdr")));
        }
        finally {Directory.Delete(folder,true);}
    }
    private static float ReadSecond(BinaryReader reader) {reader.ReadSingle(); return reader.ReadSingle();}
    [Fact]
    public void FiveHundredHzProfileCanBeSavedButIsRejectedFor250HzAcquisition()
    {
        var profile=new ResearchProfile {Filter=new() {BandsHz=new double[][] {[6,180],[14,180],[22,180]}}};
        var frozen=ResearchProfile.Parse(profile.ToJson()); frozen.Validate(500);
        Assert.Throws<ArgumentException>(()=>Context(frozen,250));
        Assert.Equal(180,Context(frozen,500).Profile.Filter.BandsHz[0][1]);
    }
    private static SessionEvent Event(string kind,object data)=>new(1,kind,0,JsonSerializer.SerializeToElement(data,CanonicalJson.Options));
    private static SessionEvent Mark(string id,string phase,string label,string? target,double index)=>Event("trial-marker",
        new TrialMarker(id,phase,label,target,index/250,index/250,"model",index,"Estimated",DrawHostSeconds:index/250));
    private static ReplayDecision Decision(long first,long last,string? target,long continuity=1)
    {
        var scores=Enumerable.Range(1,6).ToDictionary(i=>$"T{i}",i=>$"T{i}"==target?.9:.1);
        var result=new ClassificationResult(target??"T1",target is null?.1:.9,"T2",.1,scores);
        return new(1,continuity,last,first,last,last/250.0,"model",new(result,scores,"weighted-rho2-normalized-v1"),SelectionPolicy.Evaluate(result,new()));
    }
    [Fact]
    public void FirstWrongChoiceCannotBeRewrittenAndUnlabeledAccuracyIsUnavailable()
    {
        SessionEvent[] markers=[Mark("t","observe-start","target","T2",0),Mark("t","observe-end","target","T2",1000)];
        var report=ReplayEvaluator.Evaluate([Decision(0,624,"T1"),Decision(50,674,"T2")],markers,250);
        Assert.Equal(0,report.Accuracy.Value);Assert.Equal(1,report.Coverage.Value);Assert.Equal("T1",report.Trials[0].FirstSelection);
        Assert.Equal(1,report.Confusion!["T2"]["T1"]);Assert.Null(report.MedianDelaySeconds);
        Assert.Null(ReplayEvaluator.Evaluate([Decision(0,624,"T1")],[],250).Accuracy.Value);
    }
    [Fact]
    public void NoIntentCountsTransitionsAndResetsAcrossGapsWithPoissonInterval()
    {
        SessionEvent[] markers=[Mark("n","observe-start","no-intent",null,0),Mark("n","observe-end","no-intent",null,1200)];
        var report=ReplayEvaluator.Evaluate([Decision(0,624,"T1"),Decision(50,674,"T1"),Decision(100,724,null),
            Decision(150,774,"T1"),Decision(300,924,"T1",2)],markers,250);
        Assert.Equal(3,report.FalseTriggers);Assert.Equal(.8,report.WrongSelectionSeconds,9);Assert.Equal(.4,report.LongestWrongSelectionSeconds,9);
        Assert.True(report.FalseTriggerInterval!.Upper95>report.FalseTriggersPerMinute);
        var zero=ReplayEvaluator.Poisson(0,10);Assert.Equal(0,zero.Value);Assert.InRange(zero.Upper95!.Value,.3688,.3690);
    }
    [Fact]
    public void RecordedFeedbackIncludesInheritedBorderAndExcludesMissingFrames()
    {
        var events=new List<SessionEvent> {Mark("n","observe-start","no-intent",null,250),Mark("n","observe-end","no-intent",null,1000)};
        for(int i=0;i<45;i++)
        {
            double t=.5+i*.1;
            events.Add(Event("recognition",new {hostSeconds=t,firstSampleIndex=0,lastSampleIndex=624,decision=new {accepted=true,targetId="T1"}}));
            if(t<2||t>3) events.Add(Event("frame",new {hostSeconds=t,targetId=t<1.5?"T1":(string?)null}));
        }
        var report=RecordedFeedbackEvaluator.Evaluate(events,"hash");
        Assert.Equal(1,report.FalseTriggers);Assert.Equal(1,report.InheritedTriggers);
        Assert.InRange(report.NoIntentEvaluableSeconds,1.7,1.9);Assert.InRange(report.WrongBorderSeconds,.49,.51);
        Assert.Equal(3,report.NoIntentTotalSeconds);
    }
    [Fact]
    public void IndependentTestDataCannotSelectThresholds()
    {
        var metrics=ReplayEvaluator.Evaluate([],[],250);
        var report=new ReplayReport("0.3.0","s","simulation","hash","fbcca-research-v1","weighted-rho2-normalized-v1",DateTimeOffset.UtcNow,0,metrics,[],[]);
        Assert.Throws<InvalidOperationException>(()=>ParameterSearch.Run(report,[Event("calibration-plan",new {purpose="independent-test"})],250));
        var plan=new CalibrationPlan(42,noIntentSeconds:660);
        Assert.Equal(60,plan.Trials.Count(t=>t.Label=="target"));Assert.Equal(11,plan.Trials.Count(t=>t.Label=="no-intent"));
    }
    [Fact]
    public async Task SimulationAndUnplannedSessionsCannotGrantLocalVerification()
    {
        string folder=Temp();
        try
        {
            var context=Context();
            var recorder=new StudioRecorder(folder,context.Content,true,250,context.Channels.ToArray(),[],context);
            recorder.RecordBatch(new(1,"e",250,[Sample(0)],0,0),1,1,true);await recorder.CompleteAsync(true);
            var report=ReplayRunner.Run(recorder.DirectoryPath);
            var repository=new ValidationRepository(folder);
            Assert.Throws<InvalidOperationException>(()=>repository.Register(context.Profile,recorder.DirectoryPath,report,"P01","QA"));
            Assert.Null(repository.Latest(context.Profile));
        }
        finally {Directory.Delete(folder,true);}
    }
    private static string Temp() {string path=Path.Combine(Path.GetTempPath(),"ssvep-research-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);return path;}
}
