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
    public static async Task<int> Run(string output,bool groupTransitions=false)
    {
        var runtime=new StudioRuntime(Dispatcher.CurrentDispatcher);
        var content=StudioContentService.CreateDefault();
        var view=new StudioStimulusView(); view.SetContent(content);
        var status=new TextBlock {Height=44};
        var panel=new DockPanel(); DockPanel.SetDock(status,Dock.Top); panel.Children.Add(status); panel.Children.Add(view);
        var window=new Window {Content=panel,Left=-10000,Top=-10000,Width=960,Height=680,ShowInTaskbar=false,ShowActivated=false};
        var controller=new CalibrationController(runtime,view);
        controller.Changed+=()=>status.Text=controller.Status;
        double receiveJitter=groupTransitions?0:.034;
        view.CuePresented+=(_,_,_)=>
        {
            if(!groupTransitions) return;
            if(view.CueTargetId=="T6"&&Volatile.Read(ref receiveJitter)==0)
            {
                // Reproduce a good first group whose clock quality drops just before the next group.
                Volatile.Write(ref receiveJitter,.034);
                Console.WriteLine("Injected 34 ms receive jitter at the first group's sixth cue.");
            }
            if(view.CueTargetId=="T1") Console.WriteLine(controller.Status);
        };
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
                        MappedHostSeconds:received+Volatile.Read(ref receiveJitter)*Math.Sin(batch*2*Math.PI/7),DeviceTimeSource:"simulation");
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
            if(groupTransitions)
                Check(runtime.SampleClock?.Quality=="Estimated","First group did not start with a good clock");
            else
            {
                Check(runtime.SampleClock is {Quality:"Degraded",ResidualP95Seconds:>.02},"Fixture did not reproduce the original clock gate");
                Check(runtime.CalibrationTiming.Ready&&controller.Status.Contains("时钟精度受限"),"Limited timing was blocked or silently upgraded");
                Console.WriteLine($"PASS: first cue with sample residual P95 {runtime.SampleClock!.ResidualP95Seconds*1000:0.0} ms and original quality Degraded.");
            }
            await Until(()=>view.StimulusEnabled&&view.PresentedTargetId=="T2","Actual classifier feedback did not appear",10000);
            if(groupTransitions)
                await Until(()=>
                {
                    Check(runtime.IsRunning,"Group calibration paused: "+runtime.Session.Message);
                    return controller.Status.StartsWith("校准完成");
                },"Calibration did not finish all four groups after clock quality dropped",150000);
            else
                await Until(()=>view.CueTargetId=="T3"&&controller.Status.Contains("目标 3/6"),"Calibration did not progress through two complete trials",16000);
            if(controller.IsActive) controller.Cancel();
            await runtime.StopAsync();
            var reader=new SessionReader(runtime.LastSessionDirectory!); reader.VerifyFiles();
            var markers=reader.Events("trial-marker").Select(e=>JsonSerializer.Deserialize<TrialMarker>(e.Data.GetRawText(),CanonicalJson.Options)!).ToArray();
            int expected=groupTransitions?24:2;
            Check(markers.Count(m=>m.Phase=="observe-end")==expected,"Unexpected completed trial count");
            Check(!reader.Events("trial-exclusion").Any(),"Bounded jitter caused spurious trial exclusions");
            Check(markers.All(m=>TrialTiming.IsMapped(m)&&m.AlignmentMarginSamples>0),
                "Markers lost timing quality or their alignment margin");
            if(groupTransitions)
            {
                Check(markers.Where(m=>m.Phase=="cue-start").Select(m=>m.TargetId)
                    .SequenceEqual(Enumerable.Range(0,24).Select(i=>$"T{i%6+1}")),"Target/group order changed");
                var transitions=new List<object>();
                for(int group=2;group<=4;group++)
                {
                    int previous=6*(group-1);
                    var end=markers.Single(m=>m.TrialId==$"trial-{previous:D3}"&&m.Phase=="observe-end");
                    var cue=markers.Single(m=>m.TrialId==$"trial-{previous+1:D3}"&&m.Phase=="cue-start");
                    double gap=cue.RenderingSeconds-end.RenderingSeconds;
                    Check(gap>=.5&&gap<.85,$"Group {group} stalled after the 0.5 second wait: {gap:0.000} seconds");
                    Check(cue.TimingQuality==CalibrationClockPolicy.LimitedQuality,$"Group {group} did not exercise limited timing");
                    transitions.Add(new {group,secondsFromPreviousFeedbackEnd=gap,cue.TimingQuality,cue.AlignmentMarginSamples});
                }
                File.WriteAllText(Path.Combine(output,"group-transitions.json"),JsonSerializer.Serialize(transitions,CanonicalJson.Options));
            }
            else Check(markers.All(m=>m.TimingQuality==CalibrationClockPolicy.LimitedQuality),"Limited timing was silently upgraded");
            var replay=await Task.Run(()=>ReplayRunner.Run(runtime.LastSessionDirectory!));
            Check(replay.Metrics.Trials.Count(t=>t.Status=="evaluated")==expected&&replay.Metrics.Status.Contains("时钟精度受限"),
                "Replay silently discarded limited-clock trials");
            Check(replay.RecordedFeedback!.Trials.Count(t=>t.FirstSelection=="T2")==expected,"Recorded feedback could not use the jitter-limited labels");
            var export=await Task.Run(()=>BrainVisionExporter.Export(runtime.LastSessionDirectory!,output));
            Check(export.Warnings.Any(w=>w.Contains("时钟精度受限")),"Export omitted the timing limitation");
            string sidecar=File.ReadAllText(Path.Combine(export.Directory,"events.tsv"));
            Check(sidecar.Contains("alignment_margin_samples")&&sidecar.Contains(CalibrationClockPolicy.LimitedQuality),"Sidecar lost limited-clock evidence");
            string exportedMarkers=string.Join("\n",Directory.GetFiles(export.Directory,"*.vmrk").Select(File.ReadAllText));
            foreach(var marker in markers.Where(m=>m.TimingQuality==CalibrationClockPolicy.LimitedQuality))
                Check(!exportedMarkers.Contains(marker.TrialId+":"+marker.Phase+":"),"Export represented uncertain event times as precise markers");
            File.WriteAllText(Path.Combine(output,"result.txt"),groupTransitions
                ?"PASS: four ordered groups and 24 actual WPF trials; receive jitter increases to 34 ms at group 1 target 6; all three group transitions follow the 0.5 second wait without restarting preparation; all 24 trials retain real T2 feedback and replay; mixed timing quality is preserved in BrainVision export. No hardware used."
                :"PASS: real WPF drawing and sample-clock fitting with synthetic 34 ms receive jitter; pause before first trial explains recovery; two complete cue/feedback/wait trials; real T2 feedback; limited-clock markers preserve boundary margins; replay and recorded feedback evaluate both trials; BrainVision preserves quality and margins in events.tsv and warns instead of creating precise .vmrk events. No hardware used.");
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
