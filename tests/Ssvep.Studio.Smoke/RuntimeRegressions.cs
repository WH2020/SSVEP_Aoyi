using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using Ssvep.Speller.Core.Devices;
using Ssvep.Speller.Core.Signal;
using Ssvep.Speller.Core.Studio;
using Ssvep.Studio.App;

internal static class RuntimeRegressions
{
    private static string output = "";
    private static string python = "";
    private static readonly List<string> results = [];
    public static async Task<int> Run(string root, string executable)
    {
        output = root; python = executable;
        await Case("I1-stop-dispose-drains", StopDispose);
        await Case("I2-paused-rate-change", () => Fixture("format-pause", async r =>
        { r.Pause(); Trigger(); await AssertFault(r); AssertManifest(false, 250); }));
        await Case("I2-before-first-frame-channel-change", EarlyFormat);
        await Case("I2-before-first-frame-rate-change", () => InjectFormat(false, 500, 8, 100000));
        await Case("I2-paused-channel-change", () => InjectFormat(true, 250, 9, 100000));
        await Case("I2-paused-index-restart", () => InjectFormat(true, 250, 8, 0));
        await Case("I3-returning-frame-gap", FrameGap);
        await Case("I4-impedance-is-advisory", () => Fixture("impedance-pulse", async r =>
        {
            long run = r.Session.RunGeneration;
            Trigger(); await Until(() => r.ContactStatus.IsReady && File.Exists(Control + ".sent"));
            await Task.Delay(300);
            Check(r.IsRunning && r.IsConnected && r.IsRecording && r.Session.RunGeneration == run,
                "Transient bad impedance paused or restarted the experiment");
            await r.StopAsync();
            Check(Events().Contains("600000"), "Bad impedance evidence missing from record");
        }));
        foreach (string mode in new[] { "sensor-error", "explicit-disconnect", "enum-disconnect", "error" })
            await Case("I5-" + mode, () => Fixture(mode, async r =>
            { Trigger(); await AssertFault(r); AssertManifest(false, 250); Check(Events().Contains(mode.EndsWith("disconnect") ? "isconnected" : mode), "Original diagnostic missing"); }));
        await Case("I5-startup-releases-lease", StartupRetry);
        foreach (string mode in new[] { "loss", "loss-gap", "loss-repeat" })
            await Case("I6-" + mode, () => Fixture(mode, r => SampleLoss(r, mode)));
        foreach (string mode in new[] { "high", "no-impedance", "unknown-impedance", "stale" })
            await Case("I7-" + mode, () => AdvisoryImpedance(mode));
        File.WriteAllLines(Path.Combine(output, "result.txt"), results);
        return results.Any(x => x.StartsWith("FAIL")) ? 1 : 0;
    }
    private static string Control => Path.Combine(Environment.GetEnvironmentVariable("SSVEP_STUDIO_DATA")!, "trigger");
    private static async Task Case(string name, Func<Task> test)
    {
        string data = Path.Combine(output, name); Directory.CreateDirectory(data);
        Environment.SetEnvironmentVariable("SSVEP_STUDIO_DATA", data);
        Environment.SetEnvironmentVariable("SSVEP_FIXTURE_CONTROL", Path.Combine(data, "trigger"));
        try { await test(); results.Add("PASS " + name); }
        catch (Exception ex) { results.Add("FAIL " + name + ": " + ex.Message); }
        Console.WriteLine(results[^1]);
    }
    private static SynchroniBridgeOptions Options(string? path = null) => new(python, [],
        path ?? Path.GetFullPath("tests/Ssvep.Studio.Smoke/sdk_fixture.py"), 100, null, null, 10, 60000, false, false, false);
    private static DispatcherTimer Frames(StudioRuntime runtime)
    {
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render,
            (_, _) => runtime.RecordFrame(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency), Dispatcher.CurrentDispatcher);
        timer.Start(); return timer;
    }
    private static async Task Fixture(string mode, Func<StudioRuntime, Task> test)
    {
        await using var runtime = new StudioRuntime(Dispatcher.CurrentDispatcher, () => Options());
        var frames = Frames(runtime);
        try
        {
            await runtime.ConnectAsync(false, mode);
            await Until(() => runtime.IsConnected);
            runtime.Start(StudioContentService.CreateDefault());
            await Until(() => runtime.Session.SelectedTargetId == "T2", 15000);
            await test(runtime);
        }
        finally { frames.Stop(); }
    }
    private static async Task StopDispose()
    {
        var runtime = new StudioRuntime(Dispatcher.CurrentDispatcher);
        using var blocker = new SerializationBarrier();
        try
        {
            await runtime.ConnectAsync(true, null); await Until(() => runtime.IsConnected);
            runtime.Start(StudioContentService.CreateDefault());
            var recorder = (StudioRecorder)typeof(StudioRuntime).GetField("recorder", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime)!;
            recorder.Record("completion-barrier", blocker);
            await Until(() => blocker.Entered.IsSet);
            Task stop = runtime.StopAsync();
            Task dispose = runtime.DisposeAsync().AsTask();
            // Allow connection cancellation to finish: the writer remains deterministically blocked.
            await Task.WhenAny(dispose, Task.Delay(200));
            bool waited = !dispose.IsCompleted;
            blocker.Release.Set();
            await Task.WhenAll(stop, dispose);
            Check(waited, "Dispose completed while recorder writer was held behind barrier");
            AssertManifest(true, 250);
            Check(Events().Contains("completion-barrier"), "Writer did not drain");
        }
        finally { blocker.Release.Set(); await runtime.DisposeAsync(); }
    }
    private static async Task EarlyFormat()
        => await InjectFormat(false, 250, 9, 100000);
    private static async Task InjectFormat(bool paused, int rate, int channels, long index)
    {
        await using var runtime = new StudioRuntime(Dispatcher.CurrentDispatcher);
        await runtime.ConnectAsync(true, null); await Until(() => runtime.IsConnected);
        runtime.Start(StudioContentService.CreateDefault());
        if (paused) runtime.Pause();
        long generation = (long)typeof(StudioRuntime).GetField("connectionGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime)!;
        typeof(StudioRuntime).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(runtime,
            [new EegSample(index, DateTimeOffset.UtcNow, new double[channels]), rate, generation]);
        await AssertFault(runtime); AssertManifest(false, 250);
        foreach (string line in Events().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        { using var doc = JsonDocument.Parse(line); if (doc.RootElement.GetProperty("kind").GetString() == "sample")
            Check(doc.RootElement.GetProperty("data").GetProperty("channels").GetArrayLength() == 8, "Foreign channel format entered old recording"); }
    }
    private static async Task FrameGap()
    {
        await using var runtime = new StudioRuntime(Dispatcher.CurrentDispatcher);
        var frames = Frames(runtime);
        try
        {
            await runtime.ConnectAsync(true, null); await Until(() => runtime.IsConnected);
            runtime.Start(StudioContentService.CreateDefault());
            await Until(() => runtime.Session.SelectedTargetId == "T2", 15000);
            // Blocking the UI prevents heartbeat from observing the gap; this is the first returning callback.
            Thread.Sleep(650); runtime.RecordFrame(123);
            Check(runtime.Session.State == StudioSessionState.Paused && runtime.Session.SelectedTargetId is null,
                "Returning frame erased a >500 ms gap before heartbeat");
            await Task.Delay(300); Check(runtime.Session.SelectedTargetId is null, "Late result restored gap border");
            await runtime.StopAsync();
        }
        finally { frames.Stop(); }
    }
    private static async Task StartupRetry()
    {
        await using var failed = new StudioRuntime(Dispatcher.CurrentDispatcher, () => Options("missing-fixture-script.py"));
        await failed.ConnectAsync(false, null); await Until(() => !failed.IsConnecting);
        await using var retry = new StudioRuntime(Dispatcher.CurrentDispatcher, () => Options());
        await retry.ConnectAsync(false, "good");
        await Until(() => retry.IsConnected, 3000);
    }
    private static async Task AdvisoryImpedance(string mode)
    {
        await using var runtime = new StudioRuntime(Dispatcher.CurrentDispatcher, () => Options());
        var frames = Frames(runtime);
        try
        {
            await runtime.ConnectAsync(false, mode); await Until(() => runtime.IsConnected);
            if (mode == "stale") await Until(() => !runtime.ContactStatus.IsReady, 12000);
            Check(!runtime.ContactStatus.IsReady, "Fixture must report unresolved contact status");
            if (mode == "no-impedance") Check(runtime.Impedance.Count == 0, "Fixture unexpectedly supplied impedance");
            if (mode == "unknown-impedance") Check(runtime.Impedance.All(v => v.Quality == ImpedanceQuality.Unknown), "Zero values were displayed as normal");
            runtime.SetChannels(Enumerable.Range(0, 7));
            bool rejected = false;
            try { runtime.Start(StudioContentService.CreateDefault()); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && !runtime.IsRecording, "Removing impedance gate also removed the eight-channel requirement");
            runtime.SetChannels(Enumerable.Range(0, 8));
            runtime.Start(StudioContentService.CreateDefault());
            await Until(() => runtime.Session.SelectedTargetId == "T2", 15000);
            Check(runtime.IsRunning && runtime.IsConnected && runtime.IsRecording && !runtime.ContactStatus.IsReady,
                "Unresolved impedance blocked recognition or was presented as normal");
            runtime.Pause();
            runtime.Start(StudioContentService.CreateDefault());
            await Until(() => runtime.Session.SelectedTargetId == "T2", 15000);
            Check(runtime.IsRunning && runtime.IsConnected && !runtime.ContactStatus.IsReady, "Unresolved impedance blocked resume");
            await runtime.StopAsync(); AssertManifest(true, 250);
            Check(Events().Contains("impedance"), "Advisory impedance was omitted from recording");
        }
        finally { frames.Stop(); }
    }
    private static async Task SampleLoss(StudioRuntime runtime, string mode)
    {
        long events = mode == "loss-repeat" ? 2 : 1;
        long samples = mode == "loss-gap" ? 100 : events * 10;
        long beforeRun = runtime.Session.RunGeneration;
        Trigger();
        await Until(() => runtime.SampleLossEventCount == events && runtime.Session.RunGeneration > beforeRun);
        Check(runtime.IsConnected && runtime.IsRunning && runtime.IsRecording, "Loss ended/paused acquisition or recording");
        Check(runtime.LostSampleCount == samples && runtime.Session.SelectedTargetId is null, "Loss count or border reset incorrect");
        Check(string.IsNullOrEmpty(runtime.Notice), "Loss produced a fatal notice");
        if (mode == "loss-repeat")
        {
            await Task.Delay(400);
            Trigger(); events *= 2; samples *= 2;
            await Until(() => runtime.SampleLossEventCount == events);
            Check(runtime.IsRunning && runtime.IsConnected, "Repeated loss interrupted acquisition");
        }
        await Until(() => runtime.Session.SelectedTargetId == "T2", 12000);
        Check(runtime.LostSampleCount == samples && runtime.SampleLossEventCount == events, "Repeated sample snapshots inflated count");
        if (mode == "loss")
        {
            runtime.Pause(); Trigger();
            await Until(() => runtime.SampleLossEventCount == 2);
            Check(runtime.Session.State == StudioSessionState.Paused && runtime.IsConnected && runtime.IsRecording, "Loss changed an explicitly paused session");
            runtime.Start(StudioContentService.CreateDefault());
            await Until(() => runtime.Session.SelectedTargetId == "T2", 12000);
        }
        await runtime.StopAsync(); AssertManifest(true, 250);
        Check(Events().Contains("LOST SAMPLE") && Events().Contains("sample-loss"), "Loss evidence missing from saved record");
        if (mode == "loss")
        {
            await runtime.DisconnectAsync();
            await runtime.ConnectAsync(false, "good");
            await Until(() => runtime.IsConnected);
            Check(runtime.SampleLossEventCount == 0 && runtime.LostSampleCount == 0, "Reconnection did not reset counters");
        }
    }
    private static void Trigger() => File.WriteAllText(Control, Guid.NewGuid().ToString());
    private static async Task AssertFault(StudioRuntime runtime)
    {
        await Until(() => runtime.Session.State == StudioSessionState.Faulted && !runtime.IsRecording, 2500);
        Check(!runtime.IsRunning && runtime.Session.SelectedTargetId is null && !runtime.IsConnected, "Fault must clear border and require reconnection");
        bool refused = false;
        try { runtime.Start(StudioContentService.CreateDefault()); } catch (InvalidOperationException) { refused = true; }
        Check(refused, "Faulted acquisition resumed without preparation");
    }
    private static string Events() => File.ReadAllText(Directory.GetFiles(Environment.GetEnvironmentVariable("SSVEP_STUDIO_DATA")!, "events.ndjson", SearchOption.AllDirectories).Single());
    private static void AssertManifest(bool completed, int rate)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(Environment.GetEnvironmentVariable("SSVEP_STUDIO_DATA")!, "manifest.json", SearchOption.AllDirectories).Single()));
        Check(doc.RootElement.GetProperty("completed").GetBoolean() == completed, "Wrong completion outcome");
        Check(doc.RootElement.GetProperty("samplingRateHz").GetInt32() == rate, "Manifest format changed");
        Check(doc.RootElement.GetProperty("endedAt").ValueKind == JsonValueKind.String, "Manifest not finalized");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Until(Func<bool> predicate, int timeout = 5000)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate()) { if (watch.ElapsedMilliseconds > timeout) throw new Exception("Timed out waiting for required runtime state"); await Task.Delay(25); }
    }
    private sealed class SerializationBarrier : IDisposable
    {
        internal readonly ManualResetEventSlim Entered = new();
        internal readonly ManualResetEventSlim Release = new();
        public string Value { get { Entered.Set(); Release.Wait(); return "drained"; } }
        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }
}
