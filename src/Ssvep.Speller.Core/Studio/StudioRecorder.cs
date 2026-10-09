using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Ssvep.Speller.Core.Research;

namespace Ssvep.Speller.Core.Studio;

public sealed class StudioRecorder
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals, WriteIndented = false };
    private readonly Channel<Entry> queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(8192)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly object gate = new();
    private readonly Task writerTask;
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    private readonly bool simulation;
    private readonly RunContext context;
    private readonly Dictionary<string,ClockModel> clocks = new();
    private Exception? failure;
    private Task? completion;
    private bool closing;
    private long sequence;
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public string DirectoryPath { get; }
    public Exception? Failure => Volatile.Read(ref failure);
    private sealed record SampleEntry(SampleBatch Batch,long Run,long Continuity,bool Enabled);
    private sealed record Entry(long Sequence,string SessionId,DateTimeOffset Timestamp,double HostSeconds,string Kind,object Data);

    public StudioRecorder(string root, StudioContentPackage content, bool simulation, int samplingRate, int[] channels,
        IReadOnlyList<StudioElectrodeBinding>? electrodeBindings = null, RunContext? context = null)
    {
        this.context=context ?? new(ResearchProfile.Legacy(),content,samplingRate,Math.Max(8,channels.Max()+1),channels,electrodeBindings??[]);
        this.simulation=simulation;
        DirectoryPath=Path.Combine(root,$"{DateTime.Now:yyyyMMdd-HHmmss}-{SessionId[..8]}");
        BinarySampleWriter? samples=null; StreamWriter? events=null;
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            StudioContentService.Save(Path.Combine(DirectoryPath,"content.ssvep.json"),this.context.Content);
            File.WriteAllText(Path.Combine(DirectoryPath,"research-profile.json"),this.context.Profile.ToJson());
            WriteManifest(false,null,null,"preparing");
            var header=new BinarySampleHeader(1,this.context.SamplingRateHz,this.context.ChannelCount,this.context.RunConfigHash,
                this.context.SampleUnit,this.context.ScaleToVolts,this.context.UnitSource,
                Enumerable.Range(0,this.context.ChannelCount).Select(c => this.context.ElectrodeBindings.FirstOrDefault(b => b.ChannelIndex==c)?.Electrode ?? $"CH{c+1}").ToArray());
            samples=new(DirectoryPath,header);
            events=new(new FileStream(Path.Combine(DirectoryPath,"events.ndjson"),FileMode.CreateNew,FileAccess.Write,FileShare.Read,65536,FileOptions.Asynchronous));
            WriteManifest(false,null,null,"recording");
            var sampleWriter=samples; var eventWriter=events;
            writerTask=Task.Run(async () =>
            {
                try
                {
                    await foreach(var entry in queue.Reader.ReadAllAsync().ConfigureAwait(false))
                    {
                        if(entry.Data is SampleEntry batch) sampleWriter.Write(batch.Batch,batch.Run,batch.Continuity,batch.Enabled);
                        else
                        {
                            if(entry.Data is ClockModel model) clocks[model.EpochId]=model;
                            await eventWriter.WriteLineAsync(JsonSerializer.Serialize(entry,Json)).ConfigureAwait(false);
                        }
                    }
                }
                catch(Exception ex) { Interlocked.CompareExchange(ref failure,ex,null); }
                finally
                {
                    try { sampleWriter.Dispose(); await eventWriter.DisposeAsync().ConfigureAwait(false); }
                    catch(Exception ex) { Interlocked.CompareExchange(ref failure,ex,null); }
                }
            });
        }
        catch(Exception ex)
        {
            samples?.Dispose(); events?.Dispose();
            try { if(Directory.Exists(DirectoryPath)) WriteManifest(false,ex.Message,DateTimeOffset.UtcNow,"start-failed"); } catch(IOException) { } catch(UnauthorizedAccessException) { }
            throw;
        }
    }

    public bool RecordBatch(SampleBatch batch,long run,long continuity,bool recognitionEnabled)
    {
        var frozen=batch with { Samples=batch.Samples.Select(s => s with { Channels=(double[])s.Channels.Clone() }).ToArray() };
        return Enqueue("sample-block",new SampleEntry(frozen,run,continuity,recognitionEnabled));
    }

    public bool Record(string kind,object data)
    {
        if(kind=="sample") throw new ArgumentException("原始样本必须通过 RecordBatch 写入二进制文件。",nameof(kind));
        return Enqueue(kind,data);
    }

    private bool Enqueue(string kind,object data)
    {
        lock(gate)
        {
            if(closing || Failure is not null) return false;
            if(queue.Writer.TryWrite(new(++sequence,SessionId,DateTimeOffset.UtcNow,Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency,kind,data))) return true;
            Interlocked.CompareExchange(ref failure,new IOException("记录缓冲已满，实验已停止以避免丢失数据。"),null);
            return false;
        }
    }

    public Task CompleteAsync(bool completed,string? reason=null)
    {
        lock(gate)
        {
            if(completion is not null) return completion;
            closing=true; queue.Writer.TryComplete();
            return completion=Finish(completed,reason);
        }
    }

    private async Task Finish(bool completed,string? reason)
    {
        await writerTask.ConfigureAwait(false);
        WriteManifest(completed&&Failure is null,reason??Failure?.Message,DateTimeOffset.UtcNow,completed&&Failure is null ? "completed" : "incomplete");
        if(Failure is not null) throw new IOException("实验记录写入失败。",Failure);
    }

    private void WriteManifest(bool completed,string? reason,DateTimeOffset? ended,string status)
    {
        var files=new Dictionary<string,object>();
        if(ended is not null)
            foreach(string name in new[] { "content.ssvep.json","research-profile.json","samples.bin","samples.idx","events.ndjson" })
            {
                string file=Path.Combine(DirectoryPath,name);
                if(File.Exists(file)) { using var input=File.OpenRead(file); files[name]=new { length=input.Length,sha256=Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant() }; }
            }
        var manifest=new
        {
            schemaVersion=2,softwareVersion="0.3.1",sessionId=SessionId,name=context.Content.Name,startedAt=started,endedAt=ended,completed,status,failureReason=reason,
            dataSource=simulation ? "simulation" : "synchroni-sdk",profile=StudioRuntimeProfile.Version,
            samplingRateHz=context.SamplingRateHz,channelCount=context.ChannelCount,windowSeconds=2.5,
            channels=context.Channels,electrodeBindings=context.ElectrodeBindings,targets=context.Profile.Targets().Select(t => new {t.Id,t.FrequencyHz,t.PhaseRadians}),
            researchProfile=context.Profile,context.Profile.ProfileHash,context.RunConfigHash,
            hashVersion=CanonicalJson.Version,sampleFormat=BinarySampleWriter.Format,schedulingVersion=SampleWindowScheduler.Version,
            stepSamples=context.SamplingRateHz/5,context.WarmupSamples,filterVersion=StreamingFilterBank.Version,
            filterCoefficients=context.Profile.IsLegacy ? null : new StreamingFilterBank(context.Profile.Filter,context.SamplingRateHz,8).Coefficients,
            context.Display,context.SampleUnit,context.ScaleToVolts,context.UnitSource,
            reference="unknown",ground="unknown",hardwareFilterStatus="unverified",deviceTimestampSource="derived-from-sample-index",
            hostClock=new {domain="host-stopwatch-seconds",frequency=Stopwatch.Frequency},clockModelVersion=RobustClockMapper.Version,
            clockModels=clocks.Values.ToArray(),clockModelEvents="events.ndjson",calibrationOffsetSeconds=(double?)null,
            timingPrecision="software-estimate; physical latency uncalibrated",files,
            impedanceLimitKiloOhms=StudioRuntimeProfile.MaximumImpedanceKiloOhms,impedanceMeasurementTimeAvailable=false
        };
        string path=Path.Combine(DirectoryPath,"manifest.json"),temp=path+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(manifest,new JsonSerializerOptions(Json){WriteIndented=true}));
        File.Move(temp,path,true);
    }
}
