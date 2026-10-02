using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace Ssvep.Speller.Core.Studio;

public sealed class StudioRecorder
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
    private readonly Channel<object> queue = Channel.CreateBounded<object>(new BoundedChannelOptions(8192)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task writerTask;
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    private readonly bool simulation;
    private readonly int samplingRate;
    private readonly int[] channels;
    private readonly StudioElectrodeBinding[] electrodeBindings;
    private readonly string name;
    private Exception? failure;
    private int closing;
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public string DirectoryPath { get; }
    public Exception? Failure => Volatile.Read(ref failure);

    public StudioRecorder(string root, StudioContentPackage content, bool simulation, int samplingRate, int[] channels,
        IReadOnlyList<StudioElectrodeBinding>? electrodeBindings = null)
    {
        this.electrodeBindings = electrodeBindings?.ToArray() ?? [];
        this.simulation = simulation; this.samplingRate = samplingRate; this.channels = channels.ToArray(); name = content.Name;
        DirectoryPath = Path.Combine(root, $"{DateTime.Now:yyyyMMdd-HHmmss}-{SessionId[..8]}");
        Directory.CreateDirectory(DirectoryPath);
        StudioContentService.Save(Path.Combine(DirectoryPath, "content.ssvep.json"), content);
        WriteManifest(false, null, null);
        var stream = new StreamWriter(new FileStream(Path.Combine(DirectoryPath, "events.ndjson"), FileMode.CreateNew,
            FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous));
        writerTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var entry in queue.Reader.ReadAllAsync().ConfigureAwait(false))
                    await stream.WriteLineAsync(JsonSerializer.Serialize(entry, Json)).ConfigureAwait(false);
            }
            catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); }
            finally
            {
                try { await stream.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); }
            }
        });
    }

    public bool Record(string kind, object data)
    {
        if (Volatile.Read(ref closing) != 0 || Failure is not null) return false;
        if (queue.Writer.TryWrite(new { sessionId = SessionId, timestamp = DateTimeOffset.UtcNow, kind, data })) return true;
        Interlocked.CompareExchange(ref failure, new IOException("记录缓冲已满，实验已停止以避免丢失数据。"), null);
        return false;
    }

    public async Task CompleteAsync(bool completed, string? reason = null)
    {
        if (Interlocked.Exchange(ref closing, 1) != 0) { await writerTask.ConfigureAwait(false); return; }
        queue.Writer.TryComplete();
        await writerTask.ConfigureAwait(false);
        WriteManifest(completed && Failure is null, reason ?? Failure?.Message, DateTimeOffset.UtcNow);
        if (Failure is not null) throw new IOException("实验记录写入失败。", Failure);
    }

    private void WriteManifest(bool completed, string? failureReason, DateTimeOffset? endedAt)
    {
        var manifest = new { sessionId = SessionId, name, startedAt = started, endedAt, completed, failureReason,
            dataSource = simulation ? "simulation" : "synchroni-sdk", profile = StudioRuntimeProfile.Version,
            samplingRateHz = samplingRate, windowSeconds = StudioRuntimeProfile.WindowDuration.TotalSeconds,
            channels, electrodeBindings, targets = StudioRuntimeProfile.Targets.Select(t => new { t.Id, t.FrequencyHz, t.PhaseRadians }),
            impedanceLimitKiloOhms = StudioRuntimeProfile.MaximumImpedanceKiloOhms,
            impedanceMeasurementTimeAvailable = false };
        string path = Path.Combine(DirectoryPath, "manifest.json");
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(manifest, new JsonSerializerOptions(Json) { WriteIndented = true }));
        File.Move(temp, path, true);
    }
}
