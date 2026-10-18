using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ssvep.Speller.Core.Research;
using Ssvep.Studio.App;

internal static class CalibrationChecks
{
    private sealed record DrawnPhase(string Phase,string? Target,double Rendering);
    private sealed record DrawnFrame(string Phase,string? CueTarget,string? Selected,double Rendering);

    public static async Task<int> Run(string output)
    {
        var window=new MainWindow {Left=-10000,Top=-10000,Width=1280,Height=840,ShowInTaskbar=false,ShowActivated=false};
        // App placement centers on Loaded; move only this test window outside the desktop afterwards.
        window.Loaded+=(_,_)=> { window.Left=-10000; window.Top=-10000; };
        window.Show();
        var results=new List<string>();
        try
        {
            void Click(string name)=>((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var view=(StudioStimulusView)window.FindName("RunStimulus");
            var status=(TextBlock)window.FindName("CalibrationStatus");
            ((CheckBox)window.FindName("ResearchToggle")).IsChecked=true;
            window.Runtime.ApplyProfile(new ResearchProfile());
            ((ComboBox)window.FindName("SourceCombo")).SelectedIndex=1;
            Click("PrepareNav"); Click("ConnectButton"); await Until(()=>window.Runtime.IsConnected);
            Click("RunNav"); Click("StartButton");
            await Until(()=>window.Runtime.Session.SelectedTargetId=="T2",20000);

            var phases=new List<DrawnPhase>(); var frames=new List<DrawnFrame>();
            string phase="",target=""; bool collect=true;
            view.CuePresented+=(text,time,_)=>
            {
                if(!collect) return;
                phase=view.CueTargetId is not null?"cue":view.StimulusEnabled?"observe":text.StartsWith("等待 ")?"wait":"other";
                if(phase=="cue") target=view.CueTargetId!;
                phases.Add(new(phase,target,time));
                if(phase=="cue"&&target=="T1") Console.WriteLine(status.Text);
            };
            view.PresentationRendered+=(time,_,selected,_)=>
            {
                if(collect) frames.Add(new(phase,target,selected,time));
            };
            Click("CalibrationStart");
            await Until(()=>view.CueTargetId=="T1"&&status.Text.Contains("第 1/4 组"),20000);
            Check(!view.StimulusEnabled&&!view.FeedbackEnabled&&view.PresentedTargetId is null,"Cue leaked flicker or classifier feedback");
            Snapshot(window,Path.Combine(output,"cue.png"));
            await Until(()=>status.Text.Contains("请注视目标 1")&&view.PresentedTargetId=="T2",10000);
            Check(view.CueTargetId is null&&view.StimulusEnabled,"Cue border survived into actual feedback");
            Snapshot(window,Path.Combine(output,"feedback.png"));
            // Inject subsequent classifier outcomes at the presenter boundary: neither a new target nor rejection may change the held frame.
            var calibration=(CalibrationController)typeof(MainWindow).GetField("calibration",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
            var snapshot=typeof(StudioRuntime).GetField("lastRecognition",BindingFlags.Instance|BindingFlags.NonPublic)!;
            var gate=typeof(StudioRuntime).GetField("dataGate",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window.Runtime)!;
            var original=window.Runtime.LastRecognition!;
            foreach(var decision in new[] {new SelectionEvaluation(true,"T4","Accepted",.9,.1,.8),new SelectionEvaluation(false,null,"LowScore",.01,.01,0)})
            {
                lock(gate) snapshot.SetValue(window.Runtime,original with {Decision=decision});
                view.SelectedTargetId=decision.TargetId; calibration.ObserveState();
                Check(view.PresentedTargetId=="T2"&&view.FeedbackEnabled,"A later classification replaced or cleared the trial's first feedback");
                Snapshot(window,Path.Combine(output,decision.Accepted?"held-after-new-target.png":"held-after-rejection.png"));
            }
            lock(gate) snapshot.SetValue(window.Runtime,original);
            await Until(()=>status.Text.Contains("等待 0.5 秒"),6000);
            Check(!view.StimulusEnabled&&view.CueTargetId is null&&view.PresentedTargetId is null,"Wait did not clear the previous target");
            Snapshot(window,Path.Combine(output,"wait.png"));
            await Until(()=>
            {
                Check(window.Runtime.IsRunning,"Calibration interrupted: "+window.Runtime.Session.Message);
                return status.Text.StartsWith("校准完成");
            },160000);
            collect=false;
            Check(!view.StimulusEnabled&&view.PresentedTargetId is null,"Calibration completion left stimuli active");
            Click("StopButton"); await Until(()=>!window.Runtime.IsRecording);

            var reader=new SessionReader(window.Runtime.LastSessionDirectory!); reader.VerifyFiles();
            var plan=reader.Events("calibration-plan").Single().Data;
            Check(plan.GetProperty("algorithm").GetString()=="sequential-block-v1"&&plan.GetProperty("trials").GetArrayLength()==24,"Incorrect recorded personal plan");
            Check(plan.GetProperty("targetFeedbackPolicy").GetString()==TrialFeedbackSelection.Policy,"Feedback policy was not recorded");
            var markers=reader.Events("trial-marker").Select(e=>JsonSerializer.Deserialize<TrialMarker>(e.Data.GetRawText(),CanonicalJson.Options)!).ToArray();
            var trials=markers.GroupBy(m=>m.TrialId).ToArray();
            Check(trials.Length==24&&!reader.Events("trial-exclusion").Any(),"Four-group run did not complete exactly 24 uninterrupted trials");
            var timing=new List<object>();
            foreach(var (trial,index) in trials.Select((trial,index)=>(trial,index)))
            {
                TrialMarker M(string p)=>trial.Single(m=>m.Phase==p);
                var cue=M("cue-start"); var observe=M("observe-start"); var wait=M("wait-start"); var end=M("wait-end");
                Check(cue.TargetId==$"T{index%6+1}","Target order changed between groups");
                Check(M("cue-end").RenderingSeconds==observe.RenderingSeconds&&M("observe-end").RenderingSeconds==wait.RenderingSeconds,"Boundaries are not tied to the new drawing receipt");
                double cueDuration=observe.RenderingSeconds-cue.RenderingSeconds;
                double observeDuration=wait.RenderingSeconds-observe.RenderingSeconds;
                double waitDuration=end.RenderingSeconds-wait.RenderingSeconds;
                Check(cueDuration>=1&&cueDuration<1.35&&observeDuration>=4&&observeDuration<4.35&&waitDuration>=.5&&waitDuration<.85,"Incorrect 1 / 4 / 0.5 second cadence");
                Check(trial.All(m=>m.DrawHostSeconds>0&&m.EstimatedSampleIndex is not null),"Missing drawing or sample-clock evidence");
                Check(frames.Any(f=>f.Rendering>=observe.RenderingSeconds&&f.Rendering<wait.RenderingSeconds&&f.Selected=="T2"),"An observe interval lost its actual classifier feedback");
                var shown=frames.Where(f=>f.Rendering>=observe.RenderingSeconds&&f.Rendering<wait.RenderingSeconds).SkipWhile(f=>f.Selected is null).ToArray();
                Check(shown.All(f=>f.Selected=="T2"),"A target trial changed or cleared its first feedback before the wait");
                Check(!frames.Any(f=>f.Rendering>=observe.RenderingSeconds&&f.Rendering<observe.RenderingSeconds+2.3&&f.Selected is not null),"Feedback from a previous window leaked into a new observe interval");
                timing.Add(new {trialId=trial.Key,cueDuration,observeDuration,waitDuration});
            }
            Check(frames.Where(f=>f.Phase is "cue" or "wait").All(f=>f.Selected is null),"Cue/wait frames recorded a phantom selection");
            Check(phases.Count(p=>p.Phase=="cue")==24&&phases.Count(p=>p.Phase=="observe")==24&&phases.Count(p=>p.Phase=="wait")==24,"A phase was skipped or repeated");
            Check(reader.Events("calibration-ended").Single().Data.GetProperty("completed").GetInt32()==24,"Completion count is wrong");
            var feedback=reader.Events("trial-feedback").Select(e=>JsonSerializer.Deserialize<TrialFeedbackSelection>(e.Data.GetRawText(),CanonicalJson.Options)!).ToArray();
            Check(feedback.Length==24&&feedback.Select(f=>f.TrialId).Distinct().Count()==24&&feedback.All(f=>f.TargetId=="T2"),"Feedback was missing, repeated or substituted with the cue target");
            var report=RecordedFeedbackEvaluator.Evaluate(reader.Events("frame","recognition","calibration-plan","trial-marker","trial-feedback","trial-exclusion").ToArray(),window.Runtime.Profile.ProfileHash);
            Check(report.Trials.Count==24&&report.Trials.All(t=>t.FirstSelection=="T2"&&t.DrawDelaySeconds is not null),"Report differs from actual held feedback");
            File.WriteAllText(Path.Combine(output,"timing.json"),JsonSerializer.Serialize(timing,CanonicalJson.Options));
            results.Add("PASS: 24 real WPF trials; four ordered groups; 1 / 4 / 0.5 second drawing cadence; one held T2 feedback per trial while cueing other targets; subsequent T4 and rejection cannot change the border; no inherited border; records and feedback report agree.");
            Console.WriteLine(results[^1]);

            // Interrupting held feedback clears it and queues a replacement; the post-feedback wait retains completed trials.
            Click("StartButton"); Click("CalibrationStart");
            await Until(()=>view.CueTargetId=="T1"&&status.Text.Contains("第 1/4 组"),20000);
            await Until(()=>status.Text.Contains("请注视目标 1")&&view.PresentedTargetId=="T2",10000);
            Click("PauseButton");
            Check(!view.StimulusEnabled&&view.CueTargetId is null&&view.PresentedTargetId is null,"Pause left cue or feedback visible");
            Click("StartButton");
            await Until(()=>view.CueTargetId=="T2"&&status.Text.Contains("目标 2/6"),20000);
            await Until(()=>status.Text.Contains("等待 0.5 秒"),10000);
            Click("PauseButton"); Click("StartButton");
            await Until(()=>view.CueTargetId=="T3"&&status.Text.Contains("目标 3/6"),20000);
            Check(status.Text.Contains("已完成 1/24"),"Pausing during wait invalidated a completed trial");
            var exclude=FindButtons(window).Single(b=>Equals(b.Content,"标记走神并补采"));
            exclude.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(()=>view.CueTargetId=="T4"&&status.Text.Contains("目标 4/6"));
            FindButtons(window).Single(b=>Equals(b.Content,"结束校准")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(view.CueTargetId is null&&view.CueText==""&&view.StimulusEnabled&&view.FeedbackEnabled,"Cancel did not restore normal experiment display");
            Click("StopButton"); await Until(()=>!window.Runtime.IsRecording);
            var interrupted=new SessionReader(window.Runtime.LastSessionDirectory!); interrupted.VerifyFiles();
            var excluded=interrupted.Events("trial-exclusion").Select(e=>e.Data.GetProperty("trialId").GetString()).ToArray();
            Check(excluded.SequenceEqual(new[]{"trial-001","trial-003"}),"Pause/distraction exclusions changed or duplicated completed data");
            Check(interrupted.Events("calibration-ended").Single().Data.GetProperty("completed").GetInt32()==1,"Cancellation lost completed trial count");
            results.Add("PASS: pause/restart, wait preserves completed trials, distraction queues replacement, cancel clears pending cues and restores normal display.");
            Console.WriteLine(results[^1]);
            File.WriteAllLines(Path.Combine(output,"result.txt"),results);
            return 0;
        }
        finally { await window.Runtime.DisposeAsync(); window.Close(); }
    }

    private static IEnumerable<Button> FindButtons(DependencyObject node)
    {
        if(node is Button button) yield return button;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)
            foreach(var child in FindButtons(VisualTreeHelper.GetChild(node,i))) yield return child;
    }
    private static async Task Until(Func<bool> condition,int milliseconds=10000)
    {
        var watch=Stopwatch.StartNew();
        while(!condition()) { if(watch.ElapsedMilliseconds>milliseconds) throw new TimeoutException("Calibration state did not arrive"); await Task.Delay(20); }
    }
    private static void Check(bool value,string message) { if(!value) throw new InvalidOperationException(message); }
    private static void Snapshot(Window window,string path)
    {
        window.UpdateLayout(); var image=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);
        image.Render(window); var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var file=File.Create(path); encoder.Save(file);
    }
}
