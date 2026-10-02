using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ssvep.Speller.Core.Research;
using Ssvep.Speller.Core.Signal;

namespace Ssvep.Speller.Core.Devices;

public sealed class SynchroniBridgeEegStream : IEegStream
{
    private readonly SynchroniBridgeOptions options;
    private readonly SynchroniBridgeDiagnostics diagnostics = new();
    private readonly object healthGate = new();
    private readonly ClockDomainMapper domainMapper = new();
    private readonly ConcurrentDictionary<long,double> pendingPings = new();
    private Process? process;
    private TcpListener? listener;
    private CancellationTokenSource? lifetime;
    private string token = "";
    private string instance = "legacy";
    private double started;
    private double? lastHeartbeat;
    private double? lastValidSample;
    private long lastValidIndex = -1;
    private long lastHeartbeatSequence = -1;
    private bool controlClosed;
    private bool exited;
    private string sdkState = "unknown";
    private int samplingRateHz = 250;
    private int channelCount = 8;
    public int SamplingRateHz => samplingRateHz;
    public int ChannelCount => channelCount;
    public string SampleUnit { get; private set; } = "unknown";
    public double? ScaleToVolts { get; private set; }
    public string UnitSource { get; private set; } = "unknown";
    public event EventHandler<SynchroniBridgeDiagnosticsSnapshot>? DiagnosticsUpdated;
    public event Action<ClockRoundTrip>? ClockObserved;
    public event Action<ClockModel>? ClockModelUpdated;
    public SynchroniBridgeDiagnosticsSnapshot DiagnosticsSnapshot => diagnostics.CreateSnapshot();
    public SynchroniBridgeEegStream(SynchroniBridgeOptions options) => this.options=options;
    private static double Now => Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency;

    public BridgeHealthSnapshot Health
    {
        get
        {
            lock(healthGate)
            {
                bool ended=exited;
                try { ended |= process?.HasExited??false; } catch(InvalidOperationException) { }
                return BridgeHealthPolicy.Evaluate(Now,started,ended,controlClosed,lastHeartbeat,lastValidSample,sdkState,options.UseSampleBatches);
            }
        }
    }

    public async IAsyncEnumerable<EegSample> ReadSamplesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach(var batch in ReadBatchesAsync(cancellationToken).ConfigureAwait(false))
            foreach(var sample in batch.Samples) yield return sample;
    }

    public async IAsyncEnumerable<SampleBatch> ReadBatchesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        lifetime=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken cancellation=lifetime.Token;
        started=Now;
        if(options.UseSampleBatches)
        {
            token=Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            listener=new(IPAddress.Loopback,0); listener.Start(1);
        }
        Task control=Task.CompletedTask,errors=Task.CompletedTask;
        try
        {
            process=StartProcess();
            var owner=process;
            RecordEvent(new(SynchroniBridgeDiagnosticKind.Info,"Synchroni 桥接进程已启动，正在扫描蓝牙设备...",DateTimeOffset.UtcNow));
            errors=DrainErrorsAsync(owner,cancellation);
            if(listener is not null) control=ControlLoop(cancellation);
            long legacyBatch=0;
            while(!cancellation.IsCancellationRequested)
            {
                string? line=await owner.StandardOutput.ReadLineAsync(cancellation).ConfigureAwait(false);
                if(line is null) break;
                double received=Now;
                using var doc=JsonDocument.Parse(line);
                var root=doc.RootElement;
                string? type=root.TryGetProperty("type",out var kind)?kind.GetString():null;
                if(type=="meta")
                {
                    if(!SynchroniBridgeSampleParser.TryParse(line,out _,out var metadata) || metadata is null) throw new InvalidDataException("无效 SDK 元数据。");
                    samplingRateHz=metadata.SamplingRateHz; channelCount=metadata.ChannelCount;
                    SampleUnit=root.TryGetProperty("sampleUnit",out var unit)?unit.GetString()??"unknown":"unknown";
                    ScaleToVolts=root.TryGetProperty("scaleToVolts",out var scale)&&scale.TryGetDouble(out var number)&&double.IsFinite(number)&&number>0?number:null;
                    UnitSource=root.TryGetProperty("unitSource",out var source)?source.GetString()??"unknown":"unknown";
                    diagnostics.RecordMetadata(metadata); RaiseDiagnosticsUpdated(); continue;
                }
                SampleBatch? batch=null;
                if(type=="sample-batch")
                {
                    if(root.GetProperty("protocolVersion").GetInt32()!=2) throw new InvalidDataException("不支持的桥接协议版本。");
                    int rate=root.GetProperty("samplingRateHz").GetInt32(),count=root.GetProperty("channelCount").GetInt32();
                    var frames=new List<EegSample>();
                    foreach(var item in root.GetProperty("samples").EnumerateArray())
                    {
                        if(!SynchroniBridgeSampleParser.TryParseFrame(item.GetRawText(),out var frame,out _) || frame is null
                            || frame.SamplingRateHz!=rate || frame.ChannelCount!=count || frame.Sample.Channels.Length!=count) throw new InvalidDataException("批次样本格式不一致。");
                        frames.Add(frame.Sample); diagnostics.RecordSample(frame);
                    }
                    if(frames.Count==0 || frames.Count>50000) throw new InvalidDataException("无效样本批次长度。");
                    double pythonSeconds=root.GetProperty("receivedPerfNs").GetInt64()/1e9;
                    ClockModel? mapping; lock(healthGate) mapping=domainMapper.Current;
                    batch=new(root.GetProperty("batchId").GetInt64(),root.GetProperty("bridgeInstanceId").GetString()!,rate,frames,
                        pythonSeconds,received,"python-perf-counter-seconds",mapping?.ModelId,
                        mapping?.Quality=="Estimated"?mapping.Estimate(pythonSeconds):null);
                    samplingRateHz=rate; channelCount=count;
                }
                else if(type=="sample" && SynchroniBridgeSampleParser.TryParseFrame(line,out var frame,out _) && frame is not null)
                {
                    diagnostics.RecordSample(frame); samplingRateHz=frame.SamplingRateHz; channelCount=frame.ChannelCount;
                    // Legacy stdout has no SDK batch clock; receive times remain explicitly uncalibrated.
                    batch=new(++legacyBatch,instance,samplingRateHz,[frame.Sample],received,received,"legacy-ipc-receive");
                }
                if(batch is null) continue;
                lock(healthGate)
                    foreach(var sample in batch.Samples)
                        if(!sample.IsLost && sample.SampleIndex>lastValidIndex && sample.Channels.All(double.IsFinite))
                        { lastValidSample=received; lastValidIndex=sample.SampleIndex; }
                RaiseDiagnosticsUpdated();
                yield return batch;
            }
            if(!cancellation.IsCancellationRequested)
            {
                lock(healthGate) { exited=owner.HasExited; if(!exited) controlClosed=true; }
                RecordEvent(new(SynchroniBridgeDiagnosticKind.Error,exited?"桥接进程已退出":"桥接样本通道已关闭",DateTimeOffset.UtcNow));
            }
        }
        finally
        {
            lifetime.Cancel(); listener?.Stop();
            await StopProcessAsync().ConfigureAwait(false);
            try { await Task.WhenAll(control,errors).ConfigureAwait(false); } catch(OperationCanceledException) { }
        }
    }

    private async Task ControlLoop(CancellationToken cancellation)
    {
        try
        {
            using var client=await listener!.AcceptTcpClientAsync(cancellation).ConfigureAwait(false);
            using var network=client.GetStream();
            using var reader=new StreamReader(network,Encoding.UTF8,false,4096,true);
            using var writer=new StreamWriter(network,new UTF8Encoding(false),4096,true) { AutoFlush=true };
            string? hello=await reader.ReadLineAsync(cancellation).ConfigureAwait(false);
            using(var doc=JsonDocument.Parse(hello??"null"))
            {
                var root=doc.RootElement;
                if(root.GetProperty("type").GetString()!="hello" || root.GetProperty("token").GetString()!=token || root.GetProperty("protocolVersion").GetInt32()!=2) throw new InvalidDataException("桥接控制握手失败。");
                instance=root.GetProperty("bridgeInstanceId").GetString()!;
            }
            using var controlLifetime=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            Task ping=PingLoop(writer,controlLifetime.Token);
            try
            {
                while(!cancellation.IsCancellationRequested)
                {
                    string? line=await reader.ReadLineAsync(cancellation).ConfigureAwait(false); double received=Now;
                    if(line is null) break;
                    if(line.Length>65536) throw new InvalidDataException("桥接控制帧过长。");
                    using var doc=JsonDocument.Parse(line); var root=doc.RootElement;
                    if(root.GetProperty("type").GetString()=="heartbeat")
                    {
                        var hb=JsonSerializer.Deserialize<BridgeHeartbeat>(root.GetRawText(),new JsonSerializerOptions(JsonSerializerDefaults.Web));
                        if(hb is null || hb.ProtocolVersion!=2 || hb.BridgeInstanceId!=instance) throw new InvalidDataException("心跳实例不匹配。");
                        lock(healthGate) if(hb.Sequence>lastHeartbeatSequence)
                        { lastHeartbeatSequence=hb.Sequence; lastHeartbeat=received; sdkState=hb.SdkState; }
                    }
                    else if(root.GetProperty("type").GetString()=="pong")
                    {
                        long seq=root.GetProperty("sequence").GetInt64();
                        if(!pendingPings.TryRemove(seq,out double sent) || root.GetProperty("hostSend").GetDouble()!=sent) continue;
                        var observation=new ClockRoundTrip(seq,sent,root.GetProperty("pythonReceive").GetDouble(),root.GetProperty("pythonSend").GetDouble(),received);
                        ClockModel? model; lock(healthGate) model=domainMapper.Observe(observation);
                        ClockObserved?.Invoke(observation); if(model is not null) ClockModelUpdated?.Invoke(model);
                    }
                }
            }
            finally { controlLifetime.Cancel(); try { await ping.ConfigureAwait(false); } catch(OperationCanceledException) { } }
            if(!cancellation.IsCancellationRequested) lock(healthGate) controlClosed=true;
        }
        catch(OperationCanceledException) when(cancellation.IsCancellationRequested) { }
        catch(Exception ex) when(ex is IOException or SocketException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            if(!cancellation.IsCancellationRequested)
            {
                lock(healthGate) controlClosed=true;
                RecordEvent(new(SynchroniBridgeDiagnosticKind.Info,"桥接控制通道失败："+ex.Message,DateTimeOffset.UtcNow));
            }
        }
    }

    private async Task PingLoop(StreamWriter writer,CancellationToken cancellation)
    {
        long seq=0;
        using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        do
        {
            double sent=Now; pendingPings[++seq]=sent;
            if(pendingPings.Count>128) foreach(long stale in pendingPings.Keys.Where(k => k<seq-64)) pendingPings.TryRemove(stale,out _);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new {type="ping",sequence=seq,hostSend=sent}).AsMemory(),cancellation).ConfigureAwait(false);
        } while(await timer.WaitForNextTickAsync(cancellation).ConfigureAwait(false));
    }

    private Process StartProcess()
    {
        if(!File.Exists(options.BridgeScriptPath)) throw new FileNotFoundException("未找到 Synchroni Python 桥接脚本。",options.BridgeScriptPath);
        var info=new ProcessStartInfo { FileName=options.PythonExecutablePath,UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true };
        info.StandardOutputEncoding=Encoding.UTF8; info.StandardErrorEncoding=Encoding.UTF8;
        info.Environment["PYTHONIOENCODING"]="utf-8"; info.Environment["PYTHONUTF8"]="1";
        foreach(string arg in options.PythonPrefixArguments) info.ArgumentList.Add(arg);
        foreach(string arg in new[] {"-u",options.BridgeScriptPath,"--scan-ms",options.ScanPeriodMs.ToString(),"--package-count",options.PackageSampleCount.ToString(),"--power-refresh-ms",options.PowerRefreshIntervalMs.ToString()}) info.ArgumentList.Add(arg);
        if(!string.IsNullOrWhiteSpace(options.DeviceAddress)) { info.ArgumentList.Add("--device-address"); info.ArgumentList.Add(options.DeviceAddress); }
        if(!string.IsNullOrWhiteSpace(options.DeviceNamePrefix)) { info.ArgumentList.Add("--device-name-prefix"); info.ArgumentList.Add(options.DeviceNamePrefix); }
        if(options.Enable50HzNotch) info.ArgumentList.Add("--filter-50hz");
        if(options.EnableHighPass) info.ArgumentList.Add("--filter-hpf");
        if(options.EnableLowPass) info.ArgumentList.Add("--filter-lpf");
        if(options.UseSampleBatches)
        {
            info.ArgumentList.Add("--batch-protocol");
            info.Environment["SSVEP_CONTROL_PORT"]=((IPEndPoint)listener!.LocalEndpoint).Port.ToString();
            info.Environment["SSVEP_CONTROL_TOKEN"]=token;
        }
        return Process.Start(info)??throw new IOException("启动桥接进程失败。");
    }

    private async Task DrainErrorsAsync(Process owner,CancellationToken cancellation)
    {
        try
        {
            while(!cancellation.IsCancellationRequested)
            {
                string? line=await owner.StandardError.ReadLineAsync(cancellation).ConfigureAwait(false);
                if(line is null) return;
                if(SynchroniBridgeDiagnosticParser.TryParse(line,out var item)&&item is not null) RecordEvent(item);
                else RecordEvent(new(SynchroniBridgeDiagnosticKind.Info,line,DateTimeOffset.UtcNow));
            }
        }
        catch(OperationCanceledException) when(cancellation.IsCancellationRequested) { }
    }

    private void RecordEvent(SynchroniBridgeDiagnosticEvent item)
    {
        if(item.Kind==SynchroniBridgeDiagnosticKind.State) lock(healthGate) sdkState=item.Message;
        diagnostics.RecordEvent(item); RaiseDiagnosticsUpdated();
    }
    private void RaiseDiagnosticsUpdated() => DiagnosticsUpdated?.Invoke(this,diagnostics.CreateSnapshot());
    public async ValueTask DisposeAsync()
    {
        lifetime?.Cancel(); listener?.Stop(); await StopProcessAsync().ConfigureAwait(false);
    }
    private async Task StopProcessAsync()
    {
        var owner=Interlocked.Exchange(ref process,null);
        if(owner is null) return;
        try { if(!owner.HasExited) { owner.Kill(true); await owner.WaitForExitAsync().ConfigureAwait(false); } }
        catch(InvalidOperationException) { }
        finally { owner.Dispose(); }
    }
}
