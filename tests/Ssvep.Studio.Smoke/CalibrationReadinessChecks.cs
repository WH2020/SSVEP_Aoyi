using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Ssvep.Speller.Core.Research;
using Ssvep.Speller.Core.Signal;
using Ssvep.Speller.Core.Studio;
using Ssvep.Studio.App;

internal static class CalibrationReadinessChecks
{
    public static async Task<int> Run(string output)
    {
        var runtime=new StudioRuntime(Dispatcher.CurrentDispatcher);
        var content=StudioContentService.CreateDefault();
        var view=new StudioStimulusView(); view.SetContent(content);
        var status=new TextBlock {Height=44};
        var panel=new DockPanel(); DockPanel.SetDock(status,Dock.Top); panel.Children.Add(status); panel.Children.Add(view);
        var window=new Window {Content=panel,Left=-10000,Top=-10000,Width=960,Height=680,ShowInTaskbar=false,ShowActivated=false};
        var controller=new CalibrationController(runtime,view);
        controller.Changed+=()=>status.Text=controller.Status;
        runtime.Changed+=(_,_)=>
        {
            view.SetRunning(runtime.IsRunning); view.SelectedTargetId=runtime.Session.SelectedTargetId;
            controller.ObserveState();
        };
        view.PresentationRendered+=runtime.RecordPresentation;
        runtime.ApplyProfile(new ResearchProfile());
        // Inject contiguous samples through the real batch/clock pipeline without taking the hardware lease.
        typeof(StudioRuntime).GetProperty(nameof(StudioRuntime.IsSimulation))!.SetValue(runtime,true);
        typeof(StudioRuntime).GetProperty(nameof(StudioRuntime.IsConnecting))!.SetValue(runtime,true);
        var receive=typeof(StudioRuntime).GetMethod("ReceiveBatch",BindingFlags.Instance|BindingFlags.NonPublic)!;
        using var cancellation=new CancellationTokenSource();
        var feed=Task.Run(async()=>
        {
            double Now()=>Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency;
            double origin=Now(); long sample=0,batch=0;
            using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(20));
            try
            {
                while(await timer.WaitForNextTickAsync(cancellation.Token))
                {
                    double received=Now(); long due=(long)((received-origin)*250);
                    var samples=new List<EegSample>();
                    for(;sample<due;sample++)
                    {
                        double t=sample/250.0;
                        samples.Add(new(sample,DateTimeOffset.UtcNow,Enumerable.Range(0,8)
                            .Select(c=>20*Math.Sin(2*Math.PI*9.2*t+c*.04)+6*Math.Sin(2*Math.PI*18.4*t)+.2*Math.Sin(2*Math.PI*3*t+c)).ToArray()));
                    }
                    if(samples.Count==0) continue;
                    batch++;
                    var incoming=new SampleBatch(batch,"synthetic-receive-jitter",250,samples,received,received,
                        MappedHostSeconds:received+.034*Math.Sin(batch*2*Math.PI/7),DeviceTimeSource:"simulation");
                    receive.Invoke(runtime,[incoming,0L]);
                }
            }
            catch(OperationCanceledException) when(cancellation.IsCancellationRequested) { }
        });
        try
        {
            window.Show();
            await Until(()=>runtime.IsConnected,"Synthetic samples did not connect");
            runtime.Start(content); controller.Start(); runtime.Pause();
            Check(controller.Status.Contains("校准已暂停")&&controller.Status.Contains("恢复实验"),
                "Pausing before the first trial left the generic preparation message");
            runtime.Start(content);
            await Until(()=>view.CueTargetId=="T1"&&controller.Status.Contains("第 1/4 组"), "Jitter-limited calibration never drew its first cue",30000);
            Check(runtime.SampleClock is {Quality:"Degraded",ResidualP95Seconds:>.02},"Fixture did not reproduce the original clock gate");
            Check(runtime.CalibrationTiming.Ready&&controller.Status.Contains("时钟精度受限"),"Limited timing was blocked or silently upgraded");
            Console.WriteLine($"PASS: first cue with sample residual P95 {runtime.SampleClock!.ResidualP95Seconds*1000:0.0} ms and original quality Degraded.");
            await Until(()=>view.StimulusEnabled&&view.PresentedTargetId=="T2","Actual classifier feedback did not appear",10000);
            await Until(()=>view.CueTargetId=="T3"&&controller.Status.Contains("目标 3/6"),"Calibration did not progress through two complete trials",16000);
            controller.Cancel(); await runtime.StopAsync();
            var reader=new SessionReader(runtime.LastSessionDirectory!); reader.VerifyFiles();
            var markers=reader.Events("trial-marker").Select(e=>JsonSerializer.Deserialize<TrialMarker>(e.Data.GetRawText(),CanonicalJson.Options)!).ToArray();
            Check(markers.Count(m=>m.Phase=="observe-end")==2,"Unexpected completed trial count");
            Check(!reader.Events("trial-exclusion").Any(),"Bounded jitter caused spurious trial exclusions");
            Check(markers.All(m=>m.TimingQuality==CalibrationClockPolicy.LimitedQuality&&TrialTiming.IsMapped(m)&&m.AlignmentMarginSamples>0),
                "Markers lost timing quality or their alignment margin");
            var replay=await Task.Run(()=>ReplayRunner.Run(runtime.LastSessionDirectory!));
            Check(replay.Metrics.Trials.Count(t=>t.Status=="evaluated")==2&&replay.Metrics.Status.Contains("时钟精度受限"),
                "Replay silently discarded limited-clock trials");
            Check(replay.RecordedFeedback!.Trials.Count(t=>t.FirstSelection=="T2")==2,"Recorded feedback could not use the jitter-limited labels");
            var export=await Task.Run(()=>BrainVisionExporter.Export(runtime.LastSessionDirectory!,output));
            Check(export.Warnings.Any(w=>w.Contains("时钟精度受限")),"Export omitted the timing limitation");
            string sidecar=File.ReadAllText(Path.Combine(export.Directory,"events.tsv"));
            Check(sidecar.Contains("alignment_margin_samples")&&sidecar.Contains(CalibrationClockPolicy.LimitedQuality),"Sidecar lost limited-clock evidence");
            Check(!Directory.GetFiles(export.Directory,"*.vmrk").Any(path=>File.ReadAllText(path).Contains("trial-")),
                "Export represented uncertain event times as precise markers");
            File.WriteAllText(Path.Combine(output,"result.txt"),
                "PASS: real WPF drawing and sample-clock fitting with synthetic 34 ms receive jitter; pause before first trial explains recovery; two complete cue/feedback/wait trials; real T2 feedback; limited-clock markers preserve boundary margins; replay and recorded feedback evaluate both trials; BrainVision preserves quality and margins in events.tsv and warns instead of creating precise .vmrk events. No hardware used.");
            Console.WriteLine(File.ReadAllText(Path.Combine(output,"result.txt")));
            return 0;
        }
        finally
        {
            cancellation.Cancel();
            try { await feed; }
            finally { await runtime.DisposeAsync(); window.Close(); }
        }
    }

    private static async Task Until(Func<bool> condition,string message,int timeout=10000)
    {
        var watch=Stopwatch.StartNew();
        while(!condition()) { if(watch.ElapsedMilliseconds>timeout) throw new TimeoutException(message); await Task.Delay(20); }
    }
    private static void Check(bool value,string message) { if(!value) throw new InvalidOperationException(message); }
}
