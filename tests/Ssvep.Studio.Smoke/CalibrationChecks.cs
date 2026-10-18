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
        // Isolate this off-screen timing fixture from desktop focus changes. Exercise the production guard explicitly below.
        var focusGuard=typeof(MainWindow).GetMethod("Window_Deactivated",BindingFlags.Instance|BindingFlags.NonPublic)!.CreateDelegate<EventHandler>(window);
        window.Deactivated-=focusGuard;
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
                phase=text.StartsWith("请注视")?"observe":text.StartsWith("本轮")?"feedback":text.StartsWith("四秒刺激结束")?"deciding":"other";
                if(phase=="observe") target=view.CueTargetId!;
                phases.Add(new(phase,target,time));
                if(phase=="observe"&&target=="T1") Console.WriteLine(status.Text);
            };
            view.PresentationRendered+=(time,_,selected,_)=>
            {
                if(collect) frames.Add(new(phase,target,selected,time));
            };
            Click("CalibrationStart");
            await Until(()=>view.CueTargetId=="T1"&&status.Text.Contains("第 1/4 组"),20000);
            Check(view.StimulusEnabled&&!view.FeedbackEnabled&&view.PresentedTargetId is null,"Stimulation leaked an early classifier result");
            Snapshot(window,Path.Combine(output,"stimulation.png"));
            await Until(()=>status.Text.Contains("本轮结果")&&view.PresentedTargetId=="T2",10000);
            Check(view.CueTargetId is null&&!view.StimulusEnabled,"Feedback retained the cue border or flicker");
            Snapshot(window,Path.Combine(output,"feedback.png"));
            // Subsequent raw classifier updates must not replace the one terminal result during its feedback interval.
            var calibration=(CalibrationController)typeof(MainWindow).GetField("calibration",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
            var snapshot=typeof(StudioRuntime).GetField("lastRecognition",BindingFlags.Instance|BindingFlags.NonPublic)!;
            var gate=typeof(StudioRuntime).GetField("dataGate",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window.Runtime)!;
            var original=window.Runtime.LastRecognition!;
            foreach(var decision in new[] {new SelectionEvaluation(true,"T4","Accepted",.9,.1,.8),new SelectionEvaluation(false,null,"LowScore",.01,.01,0)})
            {
                lock(gate) snapshot.SetValue(window.Runtime,original with {Decision=decision});
                view.SelectedTargetId=decision.TargetId; calibration.ObserveState();
                Check(view.PresentedTargetId=="T2"&&view.FeedbackEnabled,"A later classification replaced or cleared the terminal result");
            }
            lock(gate) snapshot.SetValue(window.Runtime,original);
            await Until(()=>view.CueTargetId=="T2"&&status.Text.Contains("目标 2/6"),3000);
            Check(view.StimulusEnabled&&view.PresentedTargetId is null,"Next stimulus inherited feedback or added a wait");
            Snapshot(window,Path.Combine(output,"next-stimulation.png"));
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
                var observe=M("observe-start"); var end=M("observe-end"); var feedbackStart=M("feedback-start"); var feedbackEnd=M("feedback-end");
                Check(observe.TargetId==$"T{index%6+1}","Target order changed between groups");
                Check(M("deciding-start").RenderingSeconds==end.RenderingSeconds,"Stimulus end is not tied to the next drawing receipt");
                double observeDuration=end.RenderingSeconds-observe.RenderingSeconds;
                double decisionWait=feedbackStart.RenderingSeconds-end.RenderingSeconds;
                double feedbackDuration=feedbackEnd.RenderingSeconds-feedbackStart.RenderingSeconds;
                Check(observeDuration>=4&&observeDuration<4.35&&decisionWait>=0&&decisionWait<.35&&feedbackDuration>=.5&&feedbackDuration<.85,"Incorrect 4 second stimulus / 0.5 second feedback cadence");
                Check(trial.All(m=>m.DrawHostSeconds>0&&m.EstimatedSampleIndex is not null),"Missing drawing or sample-clock evidence");
                Check(!frames.Any(f=>f.Rendering>=observe.RenderingSeconds&&f.Rendering<end.RenderingSeconds&&f.Selected is not null),"A result appeared before four full seconds of stimulus");
                var shown=frames.Where(f=>f.Rendering>=feedbackStart.RenderingSeconds&&f.Rendering<=feedbackEnd.RenderingSeconds).ToArray();
                Check(shown.Length>0&&shown.All(f=>f.Selected=="T2"),"Feedback was missing, changed or cleared during the half-second interval");
                double? nextStart=index<23?trials[index+1].Single(m=>m.Phase=="observe-start").RenderingSeconds:null;
                Check(nextStart is null||nextStart-feedbackEnd.RenderingSeconds<.1,"An extra cue or wait delayed the next trial/group");
                timing.Add(new {trialId=trial.Key,observeDuration,decisionWait,feedbackDuration,nextStart});
            }
            Check(phases.Count(p=>p.Phase=="observe")==24&&phases.Count(p=>p.Phase=="feedback")==24,"A stimulus or feedback interval was skipped or repeated");
            Check(!markers.Any(m=>m.Phase.StartsWith("cue-")||m.Phase.StartsWith("wait-")),"Personal calibration retained a separate cue/wait phase");
            Check(reader.Events("calibration-ended").Single().Data.GetProperty("completed").GetInt32()==24,"Completion count is wrong");
            var feedback=reader.Events("trial-feedback").Select(e=>JsonSerializer.Deserialize<TrialFeedbackSelection>(e.Data.GetRawText(),CanonicalJson.Options)!).ToArray();
            Check(feedback.Length==24&&feedback.Select(f=>f.TrialId).Distinct().Count()==24&&feedback.All(f=>f.TargetId=="T2"),"Terminal feedback was missing, repeated or substituted with the cue target");
            foreach(var result in feedback)
            {
                var trial=trials.Single(t=>t.Key==result.TrialId);
                var start=trial.Single(m=>m.Phase=="observe-start"); var end=trial.Single(m=>m.Phase=="observe-end");
                Check(result.CommittedHostSeconds>=end.DrawHostSeconds,"A final result was committed before stimulus end");
                long expected=reader.Events("recognition").Where(e=>e.Data.GetProperty("firstSampleIndex").GetInt64()>=TrialTiming.FirstUsableSample(start)
                    &&e.Data.GetProperty("lastSampleIndex").GetInt64()<=TrialTiming.LastUsableSample(end))
                    .Max(e=>e.Data.GetProperty("lastSampleIndex").GetInt64());
                Check(result.LastSampleIndex==expected,"Feedback did not use the last complete sample-grid window");
            }
            var replay=await Task.Run(()=>ReplayRunner.Run(window.Runtime.LastSessionDirectory!));
            var report=replay.RecordedFeedback!;
            Check(report.Trials.Count==24&&report.Trials.All(t=>t.FirstSelection=="T2"&&t.DrawDelaySeconds>=4),"Report differs from actual terminal feedback");
            Check(replay.Metrics.Trials.All(t=>t.FirstSelection=="T2"&&t.DelaySeconds>=4),"Replay used early rather than terminal decisions");
            File.WriteAllText(Path.Combine(output,"timing.json"),JsonSerializer.Serialize(timing,CanonicalJson.Options));
            results.Add("PASS: 24 real WPF trials; four ordered groups; 4 seconds of stimulation without early borders, one terminal sample-grid decision, 0.5 seconds of static T2 feedback; immediate next trial; no extra cue/wait; subsequent T4 and rejection cannot change feedback; recorded results and replay agree.");
            Console.WriteLine(results[^1]);

            // Interrupting stimulation queues a replacement; an already displayed terminal result remains a completed trial.
            Click("StartButton"); Click("CalibrationStart");
            await Until(()=>view.CueTargetId=="T1"&&status.Text.Contains("第 1/4 组"),20000);
            await Task.Delay(1000);
            Click("PauseButton");
            Check(!view.StimulusEnabled&&view.CueTargetId is null&&view.PresentedTargetId is null,"Pause left cue or feedback visible");
            Click("StartButton");
            await Until(()=>view.CueTargetId=="T2"&&status.Text.Contains("目标 2/6"),20000);
            await Until(()=>status.Text.Contains("本轮结果"),10000);
            Click("PauseButton"); Click("StartButton");
            await Until(()=>view.CueTargetId=="T3"&&status.Text.Contains("目标 3/6"),20000);
            Check(status.Text.Contains("已完成 1/24"),"Pausing during feedback invalidated a completed trial");
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
            results.Add("PASS: pause/restart during stimulation queues replacement, feedback pause preserves completed trials, distraction queues replacement, cancel restores normal display.");
            Console.WriteLine(results[^1]);
            window.Runtime.ApplyProfile(new ResearchProfile {Decision=new(MinScore:1)});
            Click("StartButton"); Click("CalibrationStart");
            await Until(()=>view.CueTargetId=="T1"&&status.Text.Contains("第 1/4 组"),20000);
            await Until(()=>status.Text.Contains("本轮未识别"),10000);
            Check(!view.StimulusEnabled&&view.PresentedTargetId is null,"Rejected result drew an accepted border");
            Snapshot(window,Path.Combine(output,"rejected.png"));
            await Until(()=>view.CueTargetId=="T2"&&status.Text.Contains("目标 2/6"),3000);
            FindButtons(window).Single(b=>Equals(b.Content,"结束校准")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.Deactivated+=focusGuard; focusGuard(window,EventArgs.Empty);
            Check(!window.Runtime.IsRunning&&window.Runtime.Session.Message.Contains("失去焦点"),"Production focus-loss protection changed");
            Click("StopButton"); await Until(()=>!window.Runtime.IsRecording);
            var rejected=new SessionReader(window.Runtime.LastSessionDirectory!); rejected.VerifyFiles();
            var rejection=JsonSerializer.Deserialize<TrialFeedbackSelection>(rejected.Events("trial-feedback").Single().Data.GetRawText(),CanonicalJson.Options)!;
            Check(rejection.TargetId is null&&rejection.Reason=="LowScore","Rejection substituted an earlier result or used the wrong reason");
            results.Add("PASS: terminal threshold rejection shows 未识别 once, no selection border, then immediately advances after 0.5 seconds.");
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
