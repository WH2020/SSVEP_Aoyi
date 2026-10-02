using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Ssvep.Speller.Core.Classification;
using Ssvep.Speller.Core.Devices;
using Ssvep.Speller.Core.Signal;
using Ssvep.Speller.Core.Studio;

namespace Ssvep.Studio.App;

public sealed class StudioRuntime : IAsyncDisposable
{
    private readonly Dispatcher dispatcher;
    private readonly Func<SynchroniBridgeOptions> bridgeOptionsFactory;
    private readonly DispatcherTimer heartbeat;
    private readonly object dataGate = new();
    private CancellationTokenSource? connectionCancellation;
    private IEegStream? stream;
    private Task? acquisitionTask;
    private StudioWindowBuffer? activeWindow;
    private StudioRecorder? recorder;
    private DateTimeOffset? lastSampleAt;
    private long lastSampleIndex = -1;
    private long connectionGeneration;
    private long lastClassifiedSample = -1;
    private long observedWindowGeneration;
    private bool classifying;
    private bool awaitingFirstFrame;
    private bool closingRecord;
    private Task recordCompletion = Task.CompletedTask;
    private Task connectionRelease = Task.CompletedTask;
    private Task? disposal;
    private int recordingRate;
    private int recordingChannelCount;
    private bool acquisitionRejected;
    private (DateTimeOffset?, SynchroniBridgeDiagnosticKind, string)? lastDiagnostic;
    private DateTimeOffset lastImpedanceRecordedAt;
    private bool disposed;
    private long renderedFrames;
    private long firstFrameTicks;
    private long lastFrameTicks;
    private readonly Queue<long> recentFrameTicks = new();
    private int samplingRate = 250;
    private int channelCount = 8;
    private int[] selectedChannels = Enumerable.Range(0, 8).ToArray();
    private StudioElectrodeBinding[] electrodeBindings = [];
    private StudioContentPackage? frozenContent;
    private Semaphore? deviceLease;
    private int simulationTarget = 1;

    public StudioSessionController Session { get; } = new();
    public bool IsConnected { get; private set; }
    public bool IsConnecting { get; private set; }
    public bool IsSimulation { get; private set; }
    public bool IsRecording => recorder is not null || closingRecord;
    public bool IsRunning => Session.State == StudioSessionState.Running;
    public bool ConfigurationLocked => IsRunning || Session.State == StudioSessionState.Paused || IsRecording;
    public string ConnectionMessage { get; private set; } = "设备未连接";
    public string Notice { get; private set; } = "";
    public IReadOnlyList<ChannelImpedance> Impedance { get; private set; } = [];
    public DateTimeOffset? ImpedanceReceivedAt { get; private set; }
    public int ChannelCount => channelCount;
    public long SampleLossEventCount { get; private set; }
    public long LostSampleCount { get; private set; }
    /// <summary>Recent stimulus drawing rate; not a measurement of physical display refresh.</summary>
    public double? FrameRateHz
    {
        get
        {
            if (!IsRunning) return null;
            long now = Stopwatch.GetTimestamp();
            TrimFrameHistory(now);
            if (recentFrameTicks.Count < 2) return null;
            double seconds = Stopwatch.GetElapsedTime(recentFrameTicks.Peek(), now).TotalSeconds;
            return seconds > 0 ? (recentFrameTicks.Count - 1) / seconds : null;
        }
    }
    public string DataDirectory { get; }
    public int SimulationTarget { get => simulationTarget; set => simulationTarget = Math.Clamp(value, 0, 5); }
    public event EventHandler? Changed;

    public StudioRuntime(Dispatcher dispatcher, Func<SynchroniBridgeOptions>? bridgeOptionsFactory = null)
    {
        this.dispatcher = dispatcher;
        this.bridgeOptionsFactory = bridgeOptionsFactory ?? StudioSdkOptions.CreateDefault;
        DataDirectory = Environment.GetEnvironmentVariable("SSVEP_STUDIO_DATA")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SsvepStudio", "Sessions");
        heartbeat = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, Tick, dispatcher);
        heartbeat.Start();
    }

    public bool UsesChannel(int channel) => selectedChannels.Contains(channel);
    public StudioChannelSetup ChannelSetup => new(channelCount, electrodeBindings.ToArray(), selectedChannels.ToArray());
    public void ApplyChannelSetup(StudioChannelSetup setup)
    {
        if (ConfigurationLocked) throw new InvalidOperationException("请结束实验后修改通道配置。");
        var validated = StudioElectrodes.Validate(setup);
        if (validated.ChannelCount != channelCount) throw new InvalidOperationException("通道配置与当前设备不一致。");
        electrodeBindings = validated.Bindings.ToArray();
        selectedChannels = validated.SelectedChannels.ToArray();
        Notify();
    }
    public void SetChannels(IEnumerable<int> channels)
    {
        if (ConfigurationLocked) return;
        selectedChannels = channels.Distinct().Order().ToArray();
        Notify();
    }

    public StudioImpedanceStatus ContactStatus => IsSimulation
        ? new(true, "模拟数据 · 无真实阻抗测量")
        : StudioImpedanceMonitor.Evaluate(Impedance, ImpedanceReceivedAt, selectedChannels, DateTimeOffset.UtcNow);

    public async Task ConnectAsync(bool simulation, string? deviceAddress)
    {
        if (IsConnecting || ConfigurationLocked) return;
        await DisconnectAsync();
        SampleLossEventCount = 0; LostSampleCount = 0;
        Notice = "";
        Session.Prepare();
        IsSimulation = simulation;
        IsConnecting = true;
        ConnectionMessage = simulation ? "正在准备模拟脑电" : "正在连接脑电设备…";
        long generation = ++connectionGeneration;
        connectionCancellation = new CancellationTokenSource();
        var cancellation = connectionCancellation.Token;
        try
        {
            if (simulation)
            {
                samplingRate = 250; channelCount = 8;
                acquisitionTask = Task.Run(() => RunSimulationAsync(generation, cancellation), cancellation);
            }
            else
            {
                deviceLease = new Semaphore(1, 1, @"Local\SsvepStudio-Synchroni");
                if (!deviceLease.WaitOne(0)) { deviceLease.Dispose(); deviceLease = null; throw new IOException("脑电设备正在被另一个实验窗口使用。"); }
                var options = bridgeOptionsFactory() with
                { DeviceAddress = string.IsNullOrWhiteSpace(deviceAddress) ? null : deviceAddress.Trim() };
                // A deployment can supply a pre-provisioned SDK Python runtime without any end-user timing settings.
                string? python = Environment.GetEnvironmentVariable("SSVEP_STUDIO_PYTHON");
                if (!string.IsNullOrWhiteSpace(python)) options = options with { PythonExecutablePath = python, PythonPrefixArguments = [] };
                var bridge = new SynchroniBridgeEegStream(options);
                stream = bridge;
                bridge.DiagnosticsUpdated += (_, snapshot) =>
                {
                    // Block a changed format before the bridge yields its next sample, even if the UI is busy.
                    lock (dataGate)
                    {
                        if (generation != connectionGeneration) return;
                        if (recorder is not null && (snapshot.SamplingRateHz != recordingRate || snapshot.ChannelCount != recordingChannelCount))
                            RejectAcquisition(generation, "设备数据格式发生变化，请重新连接后开始实验。", snapshot);
                    }
                    dispatcher.BeginInvoke(() => ReceiveDiagnostics(snapshot, generation), DispatcherPriority.Normal);
                };
                acquisitionTask = Task.Run(async () =>
                {
                    try
                    {
                        await foreach (var sample in bridge.ReadSamplesAsync(cancellation).ConfigureAwait(false))
                            Receive(sample, bridge.SamplingRateHz, generation);
                        if (!cancellation.IsCancellationRequested)
                            PostConnectionFailure(generation, "设备连接已结束，请检查连接后重试。");
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                    catch (Exception ex) { PostConnectionFailure(generation, "设备连接失败：" + ex.Message); }
                }, cancellation);
            }
        }
        catch (Exception ex) { IsConnecting = false; ConnectionMessage = ex.Message; await ReleaseConnectionAsync(); }
        Notify();
    }

    public async Task DisconnectAsync()
    {
        if (IsRecording || IsRunning) await EndAsync(false, "设备已断开");
        ++connectionGeneration;
        await ReleaseConnectionAsync();
        IsConnected = false; IsConnecting = false;
        Impedance = []; ImpedanceReceivedAt = null;
        lock (dataGate) { lastSampleAt = null; lastSampleIndex = -1; activeWindow = null; acquisitionRejected = false; }
        lastDiagnostic = null;
        ConnectionMessage = "设备未连接";
        Notify();
    }

    private Task ReleaseConnectionAsync()
    {
        if (!connectionRelease.IsCompleted) return connectionRelease;
        return connectionRelease = ReleaseConnectionCoreAsync();
    }

    private async Task ReleaseConnectionCoreAsync()
    {
        connectionCancellation?.Cancel();
        if (acquisitionTask is not null)
        { try { await acquisitionTask; } catch (OperationCanceledException) { } acquisitionTask = null; }
        if (stream is not null) { await stream.DisposeAsync(); stream = null; }
        connectionCancellation?.Dispose(); connectionCancellation = null;
        if (deviceLease is not null) { deviceLease.Release(); deviceLease.Dispose(); deviceLease = null; }
    }

    private async Task RunSimulationAsync(long generation, CancellationToken cancellation)
    {
        var clock = Stopwatch.StartNew();
        long index = 0;
        var random = new Random(42);
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
            while (await timer.WaitForNextTickAsync(cancellation).ConfigureAwait(false))
            {
                long due = (long)(clock.Elapsed.TotalSeconds * 250);
                // Batched pacing avoids depending on sub-millisecond Windows timer resolution.
                while (index < due)
                {
                    double frequency = StudioRuntimeProfile.Targets[Volatile.Read(ref simulationTarget)].FrequencyHz;
                    double time = index / 250.0;
                    var values = Enumerable.Range(0, 8).Select(c =>
                        Math.Sin(2 * Math.PI * frequency * time + c * .08)
                        + .35 * Math.Sin(4 * Math.PI * frequency * time + c * .08)
                        + (random.NextDouble() - .5) * .18).ToArray();
                    Receive(new EegSample(index++, DateTimeOffset.UtcNow, values), 250, generation);
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { PostConnectionFailure(generation, "模拟数据失败：" + ex.Message); }
    }

    private void Receive(EegSample sample, int rate, long generation)
    {
        if (generation != Volatile.Read(ref connectionGeneration)) return;
        lock (dataGate)
        {
            if (generation != connectionGeneration || acquisitionRejected) return;
            if (recorder is not null && (rate != recordingRate || sample.Channels.Length != recordingChannelCount || sample.SampleIndex <= lastSampleIndex))
            {
                RejectAcquisition(generation, "脑电数据格式或样本序号发生变化，请重新连接。",
                    new { rate, channelCount = sample.Channels.Length, sample.SampleIndex, previousSampleIndex = lastSampleIndex });
                return;
            }
            samplingRate = rate; channelCount = sample.Channels.Length;
            lastSampleAt = sample.Timestamp; lastSampleIndex = sample.SampleIndex;
            activeWindow?.Append(sample);
            recorder?.Record("sample", sample);
        }
    }

    public void Start(StudioContentPackage content)
    {
        if (IsRunning || closingRecord) return;
        if (!IsConnected || acquisitionRejected || !FreshSamples()) throw new InvalidOperationException("请先连接设备并等待有效脑电数据。");
        if (selectedChannels.Length != 8 || selectedChannels.Any(c => c < 0 || c >= channelCount))
            throw new InvalidOperationException("请选择 8 个有效通道用于识别。");
        _ = StudioRuntimeProfile.WindowSamples(samplingRate);
        StudioContentService.Validate(content);
        lock (dataGate)
        {
            if (recorder is null)
            {
                frozenContent = content with { Targets = content.Targets.ToArray() };
                recordingRate = samplingRate; recordingChannelCount = channelCount;
                recorder = new StudioRecorder(DataDirectory, frozenContent, IsSimulation, recordingRate, selectedChannels, electrodeBindings);
                lastImpedanceRecordedAt = DateTimeOffset.MinValue;
            }
        }
        Session.Start();
        lastClassifiedSample = -1;
        observedWindowGeneration = 0;
        awaitingFirstFrame = true;
        firstFrameTicks = 0; lastFrameTicks = Stopwatch.GetTimestamp(); renderedFrames = 0;
        recentFrameTicks.Clear();
        lock (dataGate) activeWindow = null;
        Notice = "";
        recorder.Record("state", new { state = "started", run = Session.RunGeneration, simulation = IsSimulation });
        if (!IsSimulation) recorder.Record("sample-loss", new { events = SampleLossEventCount, samples = LostSampleCount });
        Notify();
    }

    public void RecordFrame(double elapsedSeconds)
    {
        if (!IsRunning) return;
        long ticks = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(lastFrameTicks, ticks).TotalMilliseconds > 500)
        {
            Pause("刺激画面已中断，请恢复窗口后重新开始");
            return;
        }
        if (awaitingFirstFrame)
        {
            lock (dataGate) activeWindow = new StudioWindowBuffer(samplingRate, channelCount, lastSampleIndex);
            awaitingFirstFrame = false; firstFrameTicks = ticks;
        }
        renderedFrames++;
        lastFrameTicks = ticks;
        recentFrameTicks.Enqueue(ticks);
        TrimFrameHistory(ticks);
        recorder?.Record("frame", new { run = Session.RunGeneration, frame = renderedFrames, elapsedSeconds,
            hostMonotonicTicks = ticks, sampleIndex = Interlocked.Read(ref lastSampleIndex) });
    }

    public void Pause(string message = "实验已暂停")
    {
        if (!IsRunning) return;
        Session.Pause(message);
        lock (dataGate) activeWindow = null;
        awaitingFirstFrame = false;
        recentFrameTicks.Clear();
        recorder?.Record("state", new { state = "paused", run = Session.RunGeneration, message });
        Notify();
    }

    public Task StopAsync() => EndAsync(true, null);

    private void TrimFrameHistory(long now)
    {
        while (recentFrameTicks.Count > 0 && Stopwatch.GetElapsedTime(recentFrameTicks.Peek(), now).TotalSeconds > 1)
            recentFrameTicks.Dequeue();
    }

    private Task EndAsync(bool completed, string? reason)
    {
        if (closingRecord) return recordCompletion;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        recordCompletion = completion.Task;
        if (completed) Session.Stop(); else Session.Fault(reason ?? "实验异常中断");
        StudioRecorder? ending;
        lock (dataGate) { activeWindow = null; ending = recorder; recorder = null; }
        awaitingFirstFrame = false;
        recentFrameTicks.Clear();
        closingRecord = ending is not null;
        ending?.Record("state", new { state = completed ? "completed" : "faulted", run = Session.RunGeneration, reason });
        Notify();
        _ = CompleteRecordAsync(ending, completed, reason, completion);
        return completion.Task;
    }

    private async Task CompleteRecordAsync(StudioRecorder? ending, bool completed, string? reason, TaskCompletionSource completion)
    {
        try { if (ending is not null) await ending.CompleteAsync(completed, reason); }
        catch (Exception ex) { Notice = "记录保存失败：" + ex.Message; Session.Fault(Notice); }
        finally { closingRecord = false; frozenContent = null; Notify(); completion.SetResult(); }
    }

    private void Fail(string message)
    {
        Notice = message;
        if (IsRunning || recorder is not null) _ = EndAsync(false, message);
        else { Session.Fault(message); Notify(); }
    }

    private bool FreshSamples()
    {
        lock (dataGate) return lastSampleAt is not null && DateTimeOffset.UtcNow - lastSampleAt <= StudioRuntimeProfile.MaximumSampleAge;
    }

    private async void Tick(object? sender, EventArgs args)
    {
        if (disposed) return;
        if (IsConnecting && FreshSamples())
        { IsConnecting = false; IsConnected = true; ConnectionMessage = IsSimulation ? "模拟脑电已连接" : "脑电设备已连接"; }
        if (recorder?.Failure is not null) { Fail(recorder.Failure.Message); return; }
        if (IsRunning && !FreshSamples()) Pause("脑电数据中断，请检查连接后恢复");
        if (IsRunning && Stopwatch.GetElapsedTime(lastFrameTicks).TotalMilliseconds > 500)
            Pause("刺激画面已中断，请恢复窗口后重新开始");
        if (IsRunning && firstFrameTicks != 0 && Stopwatch.GetElapsedTime(firstFrameTicks).TotalSeconds > 2
            && renderedFrames / Stopwatch.GetElapsedTime(firstFrameTicks).TotalSeconds < 30)
            Pause("刺激画面更新不足，请检查显示环境后恢复");

        if (IsRunning && !awaitingFirstFrame)
        {
            StudioWindowBuffer? buffer;
            EegWindowSnapshot? window = null;
            long windowGeneration;
            bool invalidFullWindow;
            lock (dataGate)
            {
                buffer = activeWindow;
                windowGeneration = buffer?.Generation ?? 0;
                if (windowGeneration != observedWindowGeneration)
                {
                    observedWindowGeneration = windowGeneration;
                    Session.InvalidateWindow();
                    recorder?.Record("data-gap", new { run = Session.RunGeneration, windowGeneration, lastSampleIndex });
                }
                bool ready = buffer is not null && buffer.TryRead(DateTimeOffset.UtcNow, out window);
                invalidFullWindow = buffer is not null && !ready && buffer.Count >= buffer.RequiredSamples;
            }
            if (!classifying && buffer is not null && window is not null && window.LastSampleIndex > lastClassifiedSample)
            {
                classifying = true;
                lastClassifiedSample = window.LastSampleIndex;
                long run = Session.RunGeneration;
                int rate = samplingRate;
                int[] channels = selectedChannels.ToArray();
                var owner = recorder;
                try
                {
                    var result = await Task.Run(() =>
                    {
                        var projected = new double[window.Samples.GetLength(0), channels.Length];
                        for (int row = 0; row < projected.GetLength(0); row++)
                            for (int col = 0; col < channels.Length; col++) projected[row, col] = window.Samples[row, channels[col]];
                        return new FbccaClassifier().Classify(projected, StudioRuntimeProfile.Targets, rate);
                    });
                    // Reject late results after a stop, recorder replacement, or an acquisition gap.
                    lock (dataGate)
                    {
                        if (!ReferenceEquals(owner, recorder) || Session.RunGeneration != run || !IsRunning
                            || !ReferenceEquals(buffer, activeWindow) || buffer.Generation != windowGeneration) return;
                        if (DateTimeOffset.UtcNow - window.LastTimestamp > TimeSpan.FromSeconds(1))
                        { Pause("识别结果已过期，请检查系统负载后恢复"); return; }
                        bool accepted = Session.AcceptResult(run, window.LastSampleIndex, result);
                        owner?.Record("recognition", new { run, window.FirstSampleIndex, window.LastSampleIndex, result, accepted });
                        owner?.Record("selection", new { run, targetId = Session.SelectedTargetId });
                    }
                }
                catch (Exception ex) { if (run == Session.RunGeneration) Fail("识别处理失败：" + ex.Message); }
                finally { classifying = false; }
            }
            else if (invalidFullWindow)
                Pause("脑电窗口不连续或包含无效数据，请检查连接后恢复");
        }
        if (recorder is not null && !IsSimulation && DateTimeOffset.UtcNow - lastImpedanceRecordedAt >= TimeSpan.FromSeconds(1))
            RecordImpedance();
        Notify();
    }

    private void RejectAcquisition(long generation, string message, object evidence)
    {
        if (acquisitionRejected) return;
        acquisitionRejected = true; activeWindow = null;
        recorder?.Record("acquisition-format-mismatch", evidence);
        PostConnectionFailure(generation, message);
    }

    private void ReceiveDiagnostics(SynchroniBridgeDiagnosticsSnapshot snapshot, long generation)
    {
        if (generation != connectionGeneration || disposed) return;
        // stdout/stderr snapshots can be queued out of order; cumulative counts never decrease.
        if (snapshot.SampleLossEventCount > SampleLossEventCount || snapshot.ReportedLostSampleCount > LostSampleCount)
        {
            SampleLossEventCount = Math.Max(SampleLossEventCount, snapshot.SampleLossEventCount);
            LostSampleCount = Math.Max(LostSampleCount, snapshot.ReportedLostSampleCount);
            recorder?.Record("sample-loss", new { events = SampleLossEventCount, samples = LostSampleCount });
            Notify();
        }
        bool changed = !Impedance.SequenceEqual(snapshot.Impedance);
        var oldStatus = ContactStatus;
        Impedance = snapshot.Impedance;
        ImpedanceReceivedAt = snapshot.ImpedanceReceivedAt;
        var status = ContactStatus;
        if (changed || oldStatus != status) RecordImpedance();
        var diagnostic = (snapshot.LastEventAt, snapshot.LastMessageKind, snapshot.LastMessage);
        if (lastDiagnostic == diagnostic) return;
        lastDiagnostic = diagnostic;
        recorder?.Record("device-diagnostic", new { kind = snapshot.LastMessageKind.ToString(), message = snapshot.LastMessage, snapshot.LastEventAt });
        if (SynchroniSampleLoss.TryParse(snapshot.LastMessageKind, snapshot.LastMessage, out _)) return;
        bool disconnected = snapshot.LastMessageKind == SynchroniBridgeDiagnosticKind.State
            && snapshot.LastMessage.Trim().ToLowerInvariant() is "disconnected" or "devicestateex.disconnected";
        if (snapshot.LastMessageKind is SynchroniBridgeDiagnosticKind.Error or SynchroniBridgeDiagnosticKind.SensorError || disconnected)
            PostConnectionFailure(generation, "设备报告异常：" + snapshot.LastMessage);
    }

    private void RecordImpedance()
    {
        if (recorder is null || IsSimulation) return;
        recorder.Record("impedance", new { run = Session.RunGeneration, receivedAt = ImpedanceReceivedAt,
            measurementTimeAvailable = false, values = Impedance, status = ContactStatus });
        lastImpedanceRecordedAt = DateTimeOffset.UtcNow;
    }

    private void PostConnectionFailure(long generation, string message) => dispatcher.BeginInvoke(async () =>
    {
        if (generation != connectionGeneration || disposed) return;
        ++connectionGeneration;
        lock (dataGate) { acquisitionRejected = true; activeWindow = null; }
        IsConnected = false; IsConnecting = false; ConnectionMessage = message;
        Notice = message;
        await EndAsync(false, message);
        await ReleaseConnectionAsync();
    });

    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);

    public ValueTask DisposeAsync() => new(disposal ??= DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        if (disposed) return;
        disposed = true; heartbeat.Stop();
        if (IsRecording) await EndAsync(false, "应用关闭");
        await DisconnectAsync();
    }
}
