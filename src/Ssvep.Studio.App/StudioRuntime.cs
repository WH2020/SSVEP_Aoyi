using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using System.Windows.Threading;
using Ssvep.Speller.Core.Devices;
using Ssvep.Speller.Core.Research;
using Ssvep.Speller.Core.Signal;
using Ssvep.Speller.Core.Studio;

namespace Ssvep.Studio.App;

/// <summary>The gate serializes commands, acquisition, health transitions and completed decisions.
/// Classification and recording have separate bounded workers. Dispatcher is display-only.</summary>
public sealed class StudioRuntime : IAsyncDisposable
{
    private readonly Dispatcher dispatcher;
    private readonly Func<SynchroniBridgeOptions> bridgeOptionsFactory;
    private readonly object dataGate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<Work> classification = Channel.CreateBounded<Work>(new BoundedChannelOptions(2)
    { SingleReader=false, FullMode=BoundedChannelFullMode.Wait });
    private readonly Task monitorTask;
    private readonly Task classifierTask;
    private CancellationTokenSource? connectionCancellation;
    private SynchroniBridgeEegStream? stream;
    private Task? acquisitionTask;
    private ResearchPipeline? activePipeline;
    private RunContext? context;
    private StudioRecorder? recorder;
    private ResearchProfile profile = ResearchProfile.Legacy().Freeze();
    private RobustClockMapper? sampleClock;
    private RobustClockMapper renderClock = new("wpf-rendering-seconds",1);
    private long lastSampleIndex = -1;
    private string? sourceClockEpoch;
    private double? lastSampleTime;
    private long connectionGeneration;
    private long recordingRun;
    private long batchSequence;
    private bool awaitingFirstFrame;
    private bool closingRecord;
    private Task recordCompletion = Task.CompletedTask;
    private Task connectionRelease = Task.CompletedTask;
    private Task? disposal;
    private bool acquisitionRejected;
    private (DateTimeOffset?, SynchroniBridgeDiagnosticKind, string)? lastDiagnostic;
    private DateTimeOffset lastImpedanceRecordedAt;
    private bool disposed;
    private long renderedFrames;
    private long longFrames;
    private double firstFrameTime;
    private double lastFrameTime;
    private readonly Queue<double> recentFrameTimes = new();
    private int samplingRate = 250;
    private int channelCount = 8;
    private int[] selectedChannels = Enumerable.Range(0,8).ToArray();
    private StudioElectrodeBinding[] electrodeBindings = [];
    private Semaphore? deviceLease;
    private int simulationTarget = 1;
    private int notificationPending;
    private Work? executing;
    private string? lastPresentedSelection;
    private BridgeHealthKind? previousHealth;
    private long validWindows;
    private long rejectedWindows;
    private long lastDecisionFirstSampleIndex;
    public bool IsOfflineBusy { get; private set; }
    private sealed class Work(ResearchPipeline pipeline,ScheduledWindow window,StudioRecorder owner,long generation,long run,double arrived)
    {
        public ResearchPipeline Pipeline { get; }=pipeline;
        public ScheduledWindow Window { get; }=window;
        public StudioRecorder Owner { get; }=owner;
        public long Generation { get; }=generation;
        public long Run { get; }=run;
        public double Arrived { get; }=arrived;
        public bool Cancelled { get; set; }
        public bool OutcomeRecorded { get; set; }
    }

    private static double Now => Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency;
    public StudioSessionController Session { get; } = new();
    public bool IsConnected { get; private set; }
    public bool IsConnecting { get; private set; }
    public bool IsSimulation { get; private set; }
    public bool IsRecording { get { lock(dataGate) return recorder is not null||closingRecord; } }
    public bool IsRunning => Session.State==StudioSessionState.Running;
    public bool ConfigurationLocked => IsRunning||Session.State is StudioSessionState.Paused or StudioSessionState.Stopping||IsRecording||IsOfflineBusy;
    public string ConnectionMessage { get; private set; }="设备未连接";
    public string Notice { get; private set; }="";
    public IReadOnlyList<ChannelImpedance> Impedance { get; private set; }=[];
    public DateTimeOffset? ImpedanceReceivedAt { get; private set; }
    public int ChannelCount => channelCount;
    public int SamplingRate => samplingRate;
    public long SampleLossEventCount { get; private set; }
    public long LostSampleCount { get; private set; }
    public ResearchProfile Profile { get { lock(dataGate) return profile; } }
    public RunContext? ActiveContext { get { lock(dataGate) return context; } }
    public ResearchScores? LastScores { get; private set; }
    public SelectionEvaluation? LastDecision { get; private set; }
    public long? LastDecisionFirstSampleIndex { get { lock(dataGate) return LastScores is null?null:lastDecisionFirstSampleIndex; } }
    public double? ClassificationMilliseconds { get; private set; }
    public ClockModel? SampleClock { get { lock(dataGate) return sampleClock?.Current; } }
    public ClockModel? RenderClock { get { lock(dataGate) return renderClock.Current; } }
    public BridgeHealthSnapshot? BridgeHealth => stream?.Health;
    public long ValidWindows => Interlocked.Read(ref validWindows);
    public long RejectedWindows => Interlocked.Read(ref rejectedWindows);
    public string? LastSessionDirectory { get; private set; }
    public string DataDirectory { get; }
    public int SimulationTarget { get=>Volatile.Read(ref simulationTarget); set=>Volatile.Write(ref simulationTarget,Math.Clamp(value,0,5)); }
    public event EventHandler? Changed;
    public event Action<double,double>? FramePresented;
    public double? FrameRateHz
    {
        get { lock(dataGate) { TrimFrames(Now); return !IsRunning||recentFrameTimes.Count<2?null:(recentFrameTimes.Count-1)/(Now-recentFrameTimes.Peek()); } }
    }
    public long LongFrameCount => Interlocked.Read(ref longFrames);
    public double? FrameIntervalP95Milliseconds
    {
        get { lock(dataGate) { var frames=recentFrameTimes.ToArray(); return frames.Length<2?null:1000*RobustClockMapper.Quantile(frames.Zip(frames.Skip(1),(a,b)=>b-a),.95); } }
    }

    public StudioRuntime(Dispatcher dispatcher,Func<SynchroniBridgeOptions>? bridgeOptionsFactory=null)
    {
        this.dispatcher=dispatcher; this.bridgeOptionsFactory=bridgeOptionsFactory??StudioSdkOptions.CreateDefault;
        DataDirectory=Environment.GetEnvironmentVariable("SSVEP_STUDIO_DATA")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SsvepStudio","Sessions");
        monitorTask=Task.Run(MonitorAsync); classifierTask=Task.Run(ClassifyAsync);
    }

    public void ApplyProfile(ResearchProfile candidate)
    {
        lock(dataGate) { if(ConfigurationLocked) throw new InvalidOperationException("请结束实验后修改参数。"); candidate.Validate(IsConnected?samplingRate:500); profile=candidate.Freeze(); }
        Notify();
    }
    public void BeginOfflineWork()
    {
        lock(dataGate) { if(ConfigurationLocked) throw new InvalidOperationException("请先结束实验或等待当前任务完成。"); IsOfflineBusy=true; }
        Notify();
    }
    public void EndOfflineWork() { lock(dataGate) IsOfflineBusy=false; Notify(); }
    public bool UsesChannel(int channel) { lock(dataGate) return selectedChannels.Contains(channel); }
    public StudioChannelSetup ChannelSetup { get { lock(dataGate) return new(channelCount,electrodeBindings.ToArray(),selectedChannels.ToArray()); } }
    public void ApplyChannelSetup(StudioChannelSetup setup)
    {
        lock(dataGate)
        {
            if(ConfigurationLocked) throw new InvalidOperationException("请结束实验后修改通道配置。");
            var valid=StudioElectrodes.Validate(setup);
            if(valid.ChannelCount!=channelCount) throw new InvalidOperationException("通道配置与当前设备不一致。");
            electrodeBindings=valid.Bindings.ToArray(); selectedChannels=valid.SelectedChannels.ToArray();
        }
        Notify();
    }
    public void SetChannels(IEnumerable<int> channels)
    { lock(dataGate) { if(ConfigurationLocked) return; selectedChannels=channels.Distinct().Order().ToArray(); } Notify(); }
    public StudioImpedanceStatus ContactStatus => IsSimulation?new(true,"模拟数据 · 无真实阻抗测量")
        :StudioImpedanceMonitor.Evaluate(Impedance,ImpedanceReceivedAt,selectedChannels,DateTimeOffset.UtcNow);

    public async Task ConnectAsync(bool simulation,string? deviceAddress)
    {
        if(IsConnecting||ConfigurationLocked) return;
        await DisconnectAsync();
        long generation;
        lock(dataGate)
        {
            SampleLossEventCount=0; LostSampleCount=0; Notice=""; Session.Prepare();
            IsSimulation=simulation; IsConnecting=true; acquisitionRejected=false; previousHealth=null;
            ConnectionMessage=simulation?"正在准备模拟脑电":"正在连接脑电设备…";
            generation=++connectionGeneration; connectionCancellation=new(); sampleClock=null; sourceClockEpoch=null;
        }
        var cancellation=connectionCancellation.Token;
        try
        {
            if(simulation)
            {
                samplingRate=250; channelCount=8;
                acquisitionTask=Task.Run(()=>RunSimulationAsync(generation,cancellation),cancellation);
            }
            else
            {
                deviceLease=new(1,1,@"Local\SsvepStudio-Synchroni");
                if(!deviceLease.WaitOne(0)) { deviceLease.Dispose(); deviceLease=null; throw new IOException("脑电设备正在被另一个实验窗口使用。"); }
                var options=bridgeOptionsFactory() with { DeviceAddress=string.IsNullOrWhiteSpace(deviceAddress)?null:deviceAddress.Trim(),UseSampleBatches=true };
                string? python=Environment.GetEnvironmentVariable("SSVEP_STUDIO_PYTHON");
                if(!string.IsNullOrWhiteSpace(python)) options=options with {PythonExecutablePath=python,PythonPrefixArguments=[]};
                var bridge=new SynchroniBridgeEegStream(options); stream=bridge;
                bridge.DiagnosticsUpdated+=(_,snapshot)=>ReceiveDiagnostics(snapshot,generation);
                bridge.ClockObserved+=observation=> { lock(dataGate) if(generation==connectionGeneration) recorder?.Record("clock-observation",observation); };
                bridge.ClockModelUpdated+=model=> { lock(dataGate) if(generation==connectionGeneration) recorder?.Record("clock-model",model); };
                acquisitionTask=Task.Run(async ()=>
                {
                    try
                    {
                        await foreach(var batch in bridge.ReadBatchesAsync(cancellation).ConfigureAwait(false)) ReceiveBatch(batch,generation);
                        if(!cancellation.IsCancellationRequested) ConnectionFailure(generation,bridge.Health.Message);
                    }
                    catch(OperationCanceledException) when(cancellation.IsCancellationRequested) { }
                    catch(Exception ex) { ConnectionFailure(generation,"桥接采集失败："+ex.Message); }
                },cancellation);
            }
        }
        catch(Exception ex)
        {
            lock(dataGate) { IsConnecting=false; ConnectionMessage=ex.Message; }
            await ReleaseConnectionAsync();
        }
        Notify();
    }

    public async Task DisconnectAsync()
    {
        if(IsRecording||IsRunning) await EndAsync(false,"设备已断开");
        lock(dataGate) { ++connectionGeneration; IsConnected=false; IsConnecting=false; activePipeline=null; lastSampleTime=null; lastSampleIndex=-1; }
        await ReleaseConnectionAsync();
        lock(dataGate) { Impedance=[]; ImpedanceReceivedAt=null; acquisitionRejected=false; lastDiagnostic=null; ConnectionMessage="设备未连接"; }
        Notify();
    }

    private Task ReleaseConnectionAsync()
    {
        lock(dataGate) return !connectionRelease.IsCompleted?connectionRelease:connectionRelease=ReleaseConnectionCoreAsync();
    }
    private async Task ReleaseConnectionCoreAsync()
    {
        connectionCancellation?.Cancel();
        if(acquisitionTask is not null) { try { await acquisitionTask.ConfigureAwait(false); } catch(OperationCanceledException) { } acquisitionTask=null; }
        if(stream is not null) { await stream.DisposeAsync(); stream=null; }
        connectionCancellation?.Dispose(); connectionCancellation=null;
        if(deviceLease is not null) { deviceLease.Release(); deviceLease.Dispose(); deviceLease=null; }
        lock(dataGate) IsConnecting=false;
        Notify();
    }

    private async Task RunSimulationAsync(long generation,CancellationToken cancellation)
    {
        double origin=Now; long index=0; var random=new Random(42); string epoch=Guid.NewGuid().ToString("N");
        try
        {
            using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(20));
            while(await timer.WaitForNextTickAsync(cancellation).ConfigureAwait(false))
            {
                double received=Now; long due=(long)((received-origin)*250);
                var samples=new List<EegSample>();
                ResearchProfile current; lock(dataGate) current=context?.Profile??profile;
                while(index<due)
                {
                    double f=current.TargetFrequenciesHz[SimulationTarget],t=index/250.0;
                    double[] values=Enumerable.Range(0,8).Select(c=>20*(Math.Sin(2*Math.PI*f*t+c*.08)+.35*Math.Sin(4*Math.PI*f*t+c*.08)+(random.NextDouble()-.5)*.18)).ToArray();
                    samples.Add(new(index++,DateTimeOffset.UtcNow,values));
                }
                if(samples.Count>0) ReceiveBatch(new(++batchSequence,epoch,250,samples,received,received,"host-stopwatch-seconds",MappedHostSeconds:received,DeviceTimeSource:"simulation"),generation);
            }
        }
        catch(OperationCanceledException) when(cancellation.IsCancellationRequested) { }
        catch(Exception ex) { ConnectionFailure(generation,"模拟数据失败："+ex.Message); }
    }

    // Retained for integration fixtures that inject a single raw sample.
    private void Receive(EegSample sample,int rate,long generation)
        =>ReceiveBatch(new(++batchSequence,"injected",rate,[sample],Now,Now),generation);

    private void ReceiveBatch(SampleBatch batch,long generation)
    {
        lock(dataGate)
        {
            if(generation!=connectionGeneration||acquisitionRejected||batch.Samples.Count==0) return;
            int count=batch.Samples[0].Channels.Length;
            bool indexBackwards=batch.Samples[0].SampleIndex<=lastSampleIndex || batch.Samples.Zip(batch.Samples.Skip(1)).Any(p=>p.Second.SampleIndex<=p.First.SampleIndex);
            bool epochChanged=sourceClockEpoch is not null && sourceClockEpoch!=batch.ClockEpoch;
            if(recorder is not null && (context!.SamplingRateHz!=batch.SamplingRateHz||context.ChannelCount!=count||indexBackwards||epochChanged))
            { recorder.Record("acquisition-format-mismatch",new {batch.SamplingRateHz,count,previous=lastSampleIndex,first=batch.Samples[0].SampleIndex}); ConnectionFailure(generation,"脑电格式或样本序号发生变化，请重新连接。"); return; }
            if(batch.Samples.Any(s=>s.Channels.Length!=count)) { ConnectionFailure(generation,"批次内通道数不一致。"); return; }
            if(indexBackwards||epochChanged) { sampleClock?.Reset(); lastSampleTime=null; }
            sourceClockEpoch=batch.ClockEpoch;
            if(samplingRate!=batch.SamplingRateHz||sampleClock is null) sampleClock=new("device-sample-index",1.0/batch.SamplingRateHz);
            samplingRate=batch.SamplingRateHz; channelCount=count;
            recorder?.RecordBatch(batch,recordingRun,activePipeline?.Scheduler.Continuity??0,IsRunning&&activePipeline is not null);
            var lastValid=batch.Samples.LastOrDefault(s=>!s.IsLost&&s.Channels.All(double.IsFinite));
            if(lastValid is not null)
            {
                lastSampleTime=batch.HostReceivedSeconds;
                if(batch.MappedHostSeconds is double mapped&&lastValid.SampleIndex==batch.Samples[^1].SampleIndex)
                {
                    recorder?.Record("clock-observation",new {batch.BatchId,batch.ClockEpoch,sampleIndex=lastValid.SampleIndex,batch.SourceReceivedSeconds,batch.HostReceivedSeconds,mapped,batch.DomainModelId});
                    var model=sampleClock.Observe(lastValid.SampleIndex,mapped);
                    if(model is not null) recorder?.Record("clock-model",model);
                }
            }
            if(IsConnecting&&lastValid is not null) { IsConnecting=false; IsConnected=true; ConnectionMessage=IsSimulation?"模拟脑电已连接":"脑电设备已连接"; }
            foreach(var sample in batch.Samples)
            {
                lastSampleIndex=sample.SampleIndex;
                if(!IsRunning||activePipeline is null||recorder is null) continue;
                long before=activePipeline.Scheduler.Continuity;
                var window=activePipeline.Push(sample);
                if(before!=activePipeline.Scheduler.Continuity)
                {
                    CancelWindows("DataGap"); Session.InvalidateWindow(); LastDecision=null; LastScores=null;
                    recorder.Record("filter-reset",new {run=recordingRun,afterSampleIndex=sample.SampleIndex-1,continuity=activePipeline.Scheduler.Continuity,reason="sample-gap-or-invalid"});
                }
                if(window is null) continue;
                recorder.Record("window-ready",new {run=recordingRun,window.Ordinal,window.Continuity,window.Anchor,window.WarmupSamples,window.Window.FirstSampleIndex,window.Window.LastSampleIndex});
                var work=new Work(activePipeline,window,recorder,Session.RunGeneration,recordingRun,batch.HostReceivedSeconds);
                if(!classification.Writer.TryWrite(work)) { CancelWindow(work,"Overload"); Pause("分类队列过载，请检查系统负载后恢复"); }
            }
        }
    }

    public void Start(StudioContentPackage content,DisplayGeometry? display=null)
    {
        lock(dataGate)
        {
            if(IsOfflineBusy) throw new InvalidOperationException("请等待回放或导出完成。");
            if(IsRunning||closingRecord) return;
            try
            {
                if(!IsConnected||acquisitionRejected||!FreshSamples()) throw new InvalidOperationException("请先连接设备并等待有效脑电数据。");
                if(recorder is not null&&display is not null&&(Math.Abs(context!.Display.Width-display.Width)>.5
                    ||Math.Abs(context.Display.Height-display.Height)>.5||Math.Abs(context.Display.DpiX-display.DpiX)>1e-6||Math.Abs(context.Display.DpiY-display.DpiY)>1e-6))
                    throw new InvalidOperationException("刺激区域尺寸或屏幕缩放已改变，请结束本次会话后新开实验。");
                if(recorder is null)
                {
                    if(selectedChannels.Length!=8||selectedChannels.Distinct().Count()!=8||selectedChannels.Any(c=>c<0||c>=channelCount))
                        throw new InvalidOperationException("请选择八个不同的有效识别通道。");
                    var next=new RunContext(profile,content,samplingRate,channelCount,selectedChannels,electrodeBindings,display,
                        IsSimulation?"uV":stream?.SampleUnit??"unknown",IsSimulation?1e-6:stream?.ScaleToVolts,IsSimulation?"synthetic-uV":stream?.UnitSource??"unknown");
                    var nextRecorder=new StudioRecorder(DataDirectory,next.Content,IsSimulation,samplingRate,selectedChannels,electrodeBindings,next);
                    context=next; recorder=nextRecorder; LastSessionDirectory=nextRecorder.DirectoryPath;
                    sampleClock?.Reset(); validWindows=0; rejectedWindows=0;
                }
                recordingRun=Session.Start(); awaitingFirstFrame=true; activePipeline=null;
                firstFrameTime=0; lastFrameTime=Now; renderedFrames=0; longFrames=0; recentFrameTimes.Clear(); renderClock=new("wpf-rendering-seconds",1);
                LastScores=null; LastDecision=null; lastPresentedSelection=null; Notice="";
                recorder.Record("state",new {state="started",run=recordingRun,simulation=IsSimulation,context!.RunConfigHash});
                if(!IsSimulation) recorder.Record("sample-loss",new {events=SampleLossEventCount,samples=LostSampleCount});
                RecordImpedance();
            }
            catch(Exception ex)
            {
                if(recorder is null) { context=null; Session.PreparationFailed("准备失败："+ex.Message); Notice=Session.Message; }
                Notify(); throw;
            }
        }
        Notify();
    }

    public void RecordFrame(double renderingSeconds) => RecordPresentation(renderingSeconds,renderingSeconds,null,0);
    public void RecordPresentation(double renderingSeconds,double phaseSeconds,string? selected,long presentationEpoch)
    {
        double now=Now;
        lock(dataGate)
        {
            if(!IsRunning) return;
            if(now-lastFrameTime>.5) { Pause("刺激画面已中断，请恢复窗口后重新开始"); return; }
            if(awaitingFirstFrame)
            {
                activePipeline=new(context!); awaitingFirstFrame=false; firstFrameTime=now;
                recorder?.Record("filter-reset",new {run=recordingRun,afterSampleIndex=lastSampleIndex,reason="first-frame",warmupSamples=context!.WarmupSamples,coefficients=activePipeline.FilterCoefficients});
            }
            if(renderedFrames>0&&now-lastFrameTime>.05) longFrames++;
            renderedFrames++; lastFrameTime=now; recentFrameTimes.Enqueue(now); TrimFrames(now);
            var model=renderClock.Observe(renderingSeconds,now);
            if(model is not null) recorder?.Record("clock-model",model);
            recorder?.Record("frame",new {run=recordingRun,frame=renderedFrames,renderingSeconds,phaseSeconds,presentationEpoch,hostSeconds=now,
                targetId=selected,hostMonotonicTicks=Stopwatch.GetTimestamp(),sampleIndex=lastSampleIndex,sampleModelId=sampleClock?.Current?.ModelId,renderModelId=renderClock.Current?.ModelId});
            if(lastPresentedSelection!=selected)
            {
                recorder?.Record("selection-presented",new {run=recordingRun,targetId=selected,renderingSeconds,hostSeconds=now,sampleModelId=sampleClock?.Current?.ModelId});
                lastPresentedSelection=selected;
            }
        }
        FramePresented?.Invoke(renderingSeconds,now);
    }

    public void RecordMarker(object marker) { lock(dataGate) recorder?.Record("trial-marker",marker); }
    public void RecordEvent(string kind,object data) { lock(dataGate) recorder?.Record(kind,data); }
    public void Pause(string message="实验已暂停")
    {
        lock(dataGate)
        {
            if(!IsRunning) return;
            CancelWindows("Paused"); Session.Pause(message); activePipeline=null; awaitingFirstFrame=false; recentFrameTimes.Clear();
            recorder?.Record("state",new {state="paused",run=recordingRun,message,afterSampleIndex=lastSampleIndex});
        }
        Notify();
    }
    public Task StopAsync()=>EndAsync(true,null);
    private void TrimFrames(double now) { while(recentFrameTimes.Count>0&&now-recentFrameTimes.Peek()>1) recentFrameTimes.Dequeue(); }

    private Task EndAsync(bool completed,string? reason)
    {
        lock(dataGate)
        {
            if(closingRecord) return recordCompletion;
            CancelWindows("Stopping"); Session.BeginStopping();
            var ending=recorder; recorder=null; activePipeline=null; awaitingFirstFrame=false; recentFrameTimes.Clear();
            closingRecord=ending is not null;
            ending?.Record("state",new {state=completed?"completed":"faulted",run=recordingRun,reason,afterSampleIndex=lastSampleIndex});
            var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); recordCompletion=completion.Task;
            _=CompleteRecordAsync(ending,completed,reason,completion); Notify(); return recordCompletion;
        }
    }
    private async Task CompleteRecordAsync(StudioRecorder? ending,bool completed,string? reason,TaskCompletionSource completion)
    {
        try { if(ending is not null) await ending.CompleteAsync(completed,reason).ConfigureAwait(false); }
        catch(Exception ex) { completed=false; reason="记录保存失败："+ex.Message; Notice=reason; }
        finally
        {
            lock(dataGate) { closingRecord=false; context=null; if(completed) Session.Stop(); else Session.Fault(reason??"实验异常中断"); }
            Notify(); completion.SetResult();
        }
    }
    private bool FreshSamples()=>lastSampleTime is double time&&Now-time<=.5;
    private async Task ClassifyAsync()
    {
        while(await classification.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            Work job;
            // Claiming a job and cancelling queued work share the same gate. Stop
            // cannot miss a job between its removal from the channel and registration.
            lock(dataGate)
            {
                if(!classification.Reader.TryRead(out var next)||next.Cancelled) continue;
                job=next; executing=job;
            }
            try
            {
                double started=Now;
                var scores=job.Pipeline.Classify(job.Window);
                double finished=Now;
                lock(dataGate)
                {
                    if(job.Cancelled||!ReferenceEquals(job.Owner,recorder)||!IsRunning||Session.RunGeneration!=job.Generation
                        ||!ReferenceEquals(job.Pipeline,activePipeline)||job.Pipeline.Scheduler.Continuity!=job.Window.Continuity)
                    { CancelWindow(job,"StaleResult"); continue; }
                    if(finished-job.Arrived>1) { CancelWindow(job,"Overload"); Pause("识别结果过期，请检查系统负载后恢复"); continue; }
                    var decision=SelectionPolicy.Evaluate(scores.Result,context!.Profile.Decision);
                    bool accepted=Session.ApplyEvaluation(job.Generation,job.Window.Window.LastSampleIndex,decision);
                    lastDecisionFirstSampleIndex=job.Window.Window.FirstSampleIndex;
                    LastScores=scores; LastDecision=decision; ClassificationMilliseconds=(finished-started)*1000;
                    validWindows++; if(!accepted) rejectedWindows++;
                    recorder!.Record("recognition",new {run=job.Run,job.Window.Ordinal,job.Window.Continuity,job.Window.Anchor,
                        job.Window.Window.FirstSampleIndex,job.Window.Window.LastSampleIndex,result=scores.Result,scores.RawScores,scores.ScoreVersion,
                        decision,accepted,hostSeconds=finished,classificationMilliseconds=ClassificationMilliseconds,context.RunConfigHash,modelId=sampleClock?.Current?.ModelId});
                    recorder.Record("selection",new {run=job.Run,targetId=Session.SelectedTargetId,hostSeconds=finished});
                    job.OutcomeRecorded=true;
                }
                Notify();
            }
            catch(Exception ex)
            {
                lock(dataGate) if(!job.Cancelled&&ReferenceEquals(job.Owner,recorder)) { Notice="识别处理失败："+ex.Message; _=EndAsync(false,Notice); }
                Notify();
            }
            finally { lock(dataGate) if(ReferenceEquals(executing,job)) executing=null; }
        }
    }
    private void CancelWindow(Work job,string reason)
    {
        if(job.OutcomeRecorded) return; job.Cancelled=true; job.OutcomeRecorded=true;
        job.Owner.Record("window-cancelled",new {run=job.Run,job.Window.Ordinal,job.Window.Continuity,job.Window.Window.FirstSampleIndex,job.Window.Window.LastSampleIndex,reason});
    }
    private void CancelWindows(string reason)
    { if(executing is not null) CancelWindow(executing,reason); while(classification.Reader.TryRead(out var queued)) CancelWindow(queued,reason); }

    private async Task MonitorAsync()
    {
        try
        {
            using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(200));
            while(await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
            {
                bool release=false;
                lock(dataGate)
                {
                    if(recorder?.Failure is Exception error) { Notice=error.Message; _=EndAsync(false,Notice); }
                    var health=stream?.Health;
                    if(health is not null&&previousHealth!=health.Kind)
                    {
                        previousHealth=health.Kind; recorder?.Record("bridge-health",health);
                        if(health.Kind is BridgeHealthKind.BridgeExited or BridgeHealthKind.BridgeIpcFailure or BridgeHealthKind.DeviceDisconnected)
                        { ConnectionFailure(connectionGeneration,health.Message); release=true; }
                    }
                    if(IsRunning)
                    {
                        if(health?.Kind==BridgeHealthKind.BridgeUnresponsive) Pause(health.Message);
                        else if(!FreshSamples()) Pause("设备无有效新数据，请检查后恢复");
                        else if(Now-lastFrameTime>.5) Pause("刺激画面已中断，请恢复窗口后重新开始");
                        else if(firstFrameTime>0&&Now-firstFrameTime>2&&renderedFrames/(Now-firstFrameTime)<30) Pause("刺激画面更新不足，请检查显示环境后恢复");
                    }
                    if(acquisitionRejected&&acquisitionTask?.IsCompleted==true) release=true;
                    if(recorder is not null&&!IsSimulation&&DateTimeOffset.UtcNow-lastImpedanceRecordedAt>=TimeSpan.FromSeconds(1)) RecordImpedance();
                }
                if(release) await ReleaseConnectionAsync().ConfigureAwait(false);
                Notify();
            }
        }
        catch(OperationCanceledException) when(lifetime.IsCancellationRequested) { }
    }

    private void ReceiveDiagnostics(SynchroniBridgeDiagnosticsSnapshot snapshot,long generation)
    {
        lock(dataGate)
        {
            if(generation!=connectionGeneration||disposed) return;
            if(recorder is not null&&(snapshot.SamplingRateHz!=context!.SamplingRateHz||snapshot.ChannelCount!=context.ChannelCount))
            { ConnectionFailure(generation,"设备数据格式发生变化，请重新连接后开始实验。"); return; }
            if(snapshot.SampleLossEventCount>SampleLossEventCount||snapshot.ReportedLostSampleCount>LostSampleCount)
            {
                SampleLossEventCount=Math.Max(SampleLossEventCount,snapshot.SampleLossEventCount); LostSampleCount=Math.Max(LostSampleCount,snapshot.ReportedLostSampleCount);
                recorder?.Record("sample-loss",new {events=SampleLossEventCount,samples=LostSampleCount});
                if(IsRunning&&activePipeline is not null)
                {
                    CancelWindows("SampleLossNotice"); activePipeline.Reset(); Session.InvalidateWindow(); LastScores=null; LastDecision=null;
                    recorder?.Record("filter-reset",new {run=recordingRun,afterSampleIndex=lastSampleIndex,reason="unlocated-loss-notice"});
                }
            }
            bool changed=!Impedance.SequenceEqual(snapshot.Impedance);
            Impedance=snapshot.Impedance; ImpedanceReceivedAt=snapshot.ImpedanceReceivedAt;
            if(changed) RecordImpedance();
            var diagnostic=(snapshot.LastEventAt,snapshot.LastMessageKind,snapshot.LastMessage);
            if(lastDiagnostic==diagnostic) return;
            lastDiagnostic=diagnostic;
            recorder?.Record("device-diagnostic",new {kind=snapshot.LastMessageKind.ToString(),message=snapshot.LastMessage,snapshot.LastEventAt});
            if(SynchroniSampleLoss.TryParse(snapshot.LastMessageKind,snapshot.LastMessage,out _)) return;
            bool disconnected=snapshot.LastMessageKind==SynchroniBridgeDiagnosticKind.State&&snapshot.LastMessage.Trim().ToLowerInvariant() is "disconnected" or "devicestateex.disconnected";
            if(disconnected||snapshot.LastMessageKind is SynchroniBridgeDiagnosticKind.Error or SynchroniBridgeDiagnosticKind.SensorError)
                ConnectionFailure(generation,(disconnected?"设备明确报告断连：":"桥接报告异常：")+snapshot.LastMessage);
        }
    }
    private void RecordImpedance()
    {
        if(recorder is null||IsSimulation) return;
        recorder.Record("impedance",new {run=recordingRun,receivedAt=ImpedanceReceivedAt,measurementTimeAvailable=false,values=Impedance,status=ContactStatus});
        lastImpedanceRecordedAt=DateTimeOffset.UtcNow;
    }
    private void ConnectionFailure(long generation,string message)
    {
        lock(dataGate)
        {
            if(generation!=connectionGeneration||disposed||acquisitionRejected) return;
            acquisitionRejected=true; IsConnected=false; ConnectionMessage=message; Notice=message;
            _=EndAsync(false,message); connectionCancellation?.Cancel();
        }
        Notify();
    }
    private void Notify()
    {
        if(dispatcher.HasShutdownStarted) return;
        if(dispatcher.CheckAccess()) { Changed?.Invoke(this,EventArgs.Empty); return; }
        if(Interlocked.Exchange(ref notificationPending,1)!=0) return;
        dispatcher.BeginInvoke(()=> { Interlocked.Exchange(ref notificationPending,0); if(!disposed) Changed?.Invoke(this,EventArgs.Empty); },DispatcherPriority.Background);
    }
    public ValueTask DisposeAsync()=>new(disposal??=DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        if(disposed) return;
        disposed=true; lifetime.Cancel();
        if(IsRecording) await EndAsync(false,"应用关闭");
        await DisconnectAsync();
        classification.Writer.TryComplete(); await Task.WhenAll(monitorTask,classifierTask);
        lifetime.Dispose();
    }
}
