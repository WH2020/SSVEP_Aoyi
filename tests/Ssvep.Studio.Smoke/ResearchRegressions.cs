using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ssvep.Speller.Core.Devices;
using Ssvep.Speller.Core.Research;
using Ssvep.Speller.Core.Studio;
using Ssvep.Studio.App;

internal static class ResearchRegressions
{
    private static string output="",python="";
    private static readonly List<string> results=[];
    public static async Task<int> Run(string folder,string executable,bool uiOnly=false)
    {
        output=folder; python=executable;
        WriteReferenceArtifacts();
        if(!uiOnly)
        {
            await Case("R1-preparing-rollback",PreparationRollback);
            await Case("R2-sample-scheduling-and-replay",SampleScheduling);
            foreach(string mode in new[] {"batch-no-data","batch-heartbeat","batch-disconnect","batch-exit"})
                await Case("R3-"+mode,()=>Health(mode));
            await Case("R4-batch-clocks-binary-500",BatchClocks);
        }
        await Case("R5-research-ui",ResearchUi);
        if(!uiOnly) await Case("R6-brainvision-events-gaps",ExportEvents);
        await Case("R7-replay-search-ui",ReplayUi);
        File.WriteAllLines(Path.Combine(output,"result.txt"),results);
        return results.Any(s=>s.StartsWith("FAIL"))?1:0;
    }
    private static void WriteReferenceArtifacts()
    {
        string[] inputs=["{\"z\":8.0,\"a\":[1e30,1e-7,0.000001,-0,333333333.33333329]}",
            "{\"\\ufb33\":1,\"\\ud83d\\ude00\":2,\"\\u20ac\":3,\"a\":\"é\",\"b\":\"e\\u0301\"}",
            "{\"b\":2,\"a\":8e0}",new ResearchProfile().ToJson()];
        var jcs=inputs.Select(input=>new {input,canonical=System.Text.Encoding.UTF8.GetString(CanonicalJson.Encode(input)),sha256=CanonicalJson.HashJson(input)});
        var filters=new[] {250,500}.SelectMany(rate=>new double[][] {[6,48],[14,48],[22,48],[1,120]}
            .Select(band=>new {rate,band,sections=StreamingFilterBank.DesignBandpass(band[0],band[1],rate)}));
        File.WriteAllText(Path.Combine(output,"reference-artifacts.json"),JsonSerializer.Serialize(new {jcs,filters},CanonicalJson.Options));
    }
    private static async Task Case(string name,Func<Task> work)
    {
        string root=Path.Combine(output,name); Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("SSVEP_STUDIO_DATA",Path.Combine(root,"sessions"));
        Environment.SetEnvironmentVariable("SSVEP_FIXTURE_CONTROL",Path.Combine(root,"trigger"));
        try {await work();results.Add("PASS "+name);} catch(Exception ex) {results.Add("FAIL "+name+": "+ex);}
        Console.WriteLine(results[^1]); File.WriteAllLines(Path.Combine(output,"result.txt"),results);
    }
    private static StudioRuntime Runtime()=>new(Dispatcher.CurrentDispatcher,()=>new SynchroniBridgeOptions(python,[],Path.GetFullPath("tests/Ssvep.Studio.Smoke/research_fixture.py"),100,null,null,10,60000,false,false,false));
    private static DispatcherTimer Frames(StudioRuntime runtime)
    {
        var timer=new DispatcherTimer(TimeSpan.FromMilliseconds(16),DispatcherPriority.Render,(_,_)=>
        {
            double time=Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency;
            runtime.RecordPresentation(time,time,runtime.Session.SelectedTargetId,1);
        },Dispatcher.CurrentDispatcher); timer.Start();return timer;
    }
    private static async Task PreparationRollback()
    {
        string path=Environment.GetEnvironmentVariable("SSVEP_STUDIO_DATA")!;
        File.WriteAllText(path,"intentional-file-obstruction");
        await using var runtime=Runtime(); await runtime.ConnectAsync(true,null); await Until(()=>runtime.IsConnected);
        bool failed=false;try {runtime.Start(StudioContentService.CreateDefault());}catch(IOException){failed=true;}
        Check(failed&&runtime.Session.State==StudioSessionState.Preparing&&!runtime.ConfigurationLocked&&runtime.IsConnected,"Preparation failure did not roll back to editable connected state");
        File.Delete(path); Directory.CreateDirectory(path); runtime.Start(StudioContentService.CreateDefault());
        Check(runtime.IsRunning&&runtime.IsRecording,"Retry failed after correcting directory");await runtime.StopAsync();
    }
    private static async Task SampleScheduling()
    {
        await using var runtime=Runtime();var frames=Frames(runtime);
        try
        {
            await runtime.ConnectAsync(true,null);await Until(()=>runtime.IsConnected); runtime.Start(StudioContentService.CreateDefault());
            await Until(()=>runtime.ValidWindows>=2);
            long before=runtime.ValidWindows; Thread.Sleep(290);
            Check(runtime.ValidWindows>before&&runtime.IsRunning,"Classification remained dependent on UI dispatcher");
            bool locked=false;try {runtime.ApplyProfile(new());}catch(InvalidOperationException){locked=true;} Check(locked,"Running profile changed");
            string hash=runtime.ActiveContext!.RunConfigHash;runtime.Pause();runtime.Start(StudioContentService.CreateDefault());
            Check(hash==runtime.ActiveContext!.RunConfigHash,"Resume changed snapshot");
            await Until(()=>runtime.Session.SelectedTargetId=="T2");await runtime.StopAsync();
            string session=runtime.LastSessionDirectory!;
            var reader=new SessionReader(session);
            long[] online=reader.Events("window-ready").Select(e=>e.Data.GetProperty("lastSampleIndex").GetInt64()).ToArray();
            var replay=await Task.Run(()=>ReplayRunner.Run(session));
            Check(online.SequenceEqual(replay.Decisions.Select(d=>d.LastSampleIndex)),"Online and replay window boundaries differ");
            Check(reader.Events().All(e=>e.Kind!="sample"),"Per-sample JSON still recorded");
        }
        finally {frames.Stop();}
    }
    private static async Task Health(string mode)
    {
        await using var runtime=Runtime();var frames=Frames(runtime);
        try
        {
            await runtime.ConnectAsync(false,mode);await Until(()=>runtime.IsConnected);runtime.Start(StudioContentService.CreateDefault());
            await Until(()=>runtime.Session.SelectedTargetId=="T2");File.WriteAllText(Environment.GetEnvironmentVariable("SSVEP_FIXTURE_CONTROL")!,"trigger");
            bool pauses=mode is "batch-no-data" or "batch-heartbeat";
            if(pauses)
            {
                await Until(()=>runtime.Session.State==StudioSessionState.Paused,6000);
                Check(runtime.IsConnected&&runtime.IsRecording&&runtime.Session.SelectedTargetId is null,"Recoverable health fault disconnected/ended session");
                Check(runtime.BridgeHealth?.Kind==(mode=="batch-no-data"?BridgeHealthKind.DataStalled:BridgeHealthKind.BridgeUnresponsive),"Wrong health attribution");
                await runtime.StopAsync();
            }
            else
            {
                await Until(()=>runtime.Session.State==StudioSessionState.Faulted&&!runtime.IsRecording);
                Check(!runtime.IsConnected&&runtime.Session.SelectedTargetId is null,"Confirmed terminal fault did not terminate safely");
            }
        }
        finally {frames.Stop();}
    }
    private static async Task BatchClocks()
    {
        await using var runtime=Runtime();var frames=Frames(runtime);
        try
        {
            await runtime.ConnectAsync(false,"batch-500");await Until(()=>runtime.IsConnected);
            runtime.ApplyProfile(new());runtime.Start(StudioContentService.CreateDefault());
            await Until(()=>runtime.SampleClock?.Quality=="Estimated"&&runtime.Session.SelectedTargetId=="T2",20000);
            Check(runtime.ChannelCount==32&&runtime.SamplingRate==500,"Batch metadata lost");
            await runtime.StopAsync();
            var reader=new SessionReader(runtime.LastSessionDirectory!);
            Check(reader.Context.ChannelCount==32,"Raw channels were reduced to recognition subset");
            Check(reader.Events("clock-model").Count()>4,"Clock revisions not journaled");
            var models=reader.Manifest.GetProperty("clockModels");Check(models.GetArrayLength()>=3,"Manifest missing sample, bridge or rendering clocks");
            var exported=await Task.Run(()=>BrainVisionExporter.Export(reader.Folder,Path.Combine(output,"brainvision")));
            File.WriteAllText(Path.Combine(output,"mne-session.json"),JsonSerializer.Serialize(new {source=reader.Folder,export=exported.Directory},CanonicalJson.Options));
        }
        finally {frames.Stop();}
    }
    private static async Task ResearchUi()
    {
        var window=new MainWindow {Left=-10000,Top=-10000,Width=1280,Height=840,ShowInTaskbar=false};window.Show();
        try
        {
            void Click(string name)=>((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((CheckBox)window.FindName("ResearchToggle")).IsChecked=true; Click("ResearchNav");
            var panel=(ResearchPanel)((ContentControl)window.FindName("ResearchPage")).Content;
            panel.Load(new ResearchProfile {Name="自动化测试候选"});Check(panel.Read().TargetFrequenciesHz.Count==6,"Frequency editor changed target count");
            Snapshot(window,Path.Combine(output,"parameters.png"));
            window.Runtime.ApplyProfile(panel.Read());
            ((ComboBox)window.FindName("SourceCombo")).SelectedIndex=1;Click("PrepareNav");Click("ConnectButton");await Until(()=>window.Runtime.IsConnected);
            Click("RunNav");Click("StartButton");await Until(()=>window.Runtime.Session.SelectedTargetId=="T2",20000);
            Check(((TextBlock)window.FindName("DiagnosticsText")).Text.Contains("T2"),"Scores not exposed in diagnostics");
            Click("CalibrationStart");await Until(()=>((TextBlock)window.FindName("CalibrationStatus")).Text.Contains("请注视"),20000);
            Snapshot(window,Path.Combine(output,"research-running.png"));
            await Task.Delay(4300);
            Click("StopButton");await Until(()=>!window.Runtime.IsRecording);
            var reader=new SessionReader(window.Runtime.LastSessionDirectory!);
            Check(reader.Events("trial-marker").Any(e=>e.Data.GetProperty("phase").GetString()=="observe-start"),"Calibration cue not recorded");
            Check(reader.Events("trial-marker").Where(e=>e.Data.GetProperty("phase").GetString()=="observe-start").All(e=>e.Data.GetProperty("drawHostSeconds").GetDouble()>0),"Label has no actual draw receipt");
        }
        finally {await window.Runtime.DisposeAsync();window.Close();}
    }
    private static async Task ExportEvents()
    {
        var context=new RunContext(new(),StudioContentService.CreateDefault(),250,8,Enumerable.Range(0,8),[new(0,"O1"),new(1,"Oz")],new(800,500),"uV",1e-6,"synthetic-QA");
        var recorder=new StudioRecorder(Environment.GetEnvironmentVariable("SSVEP_STUDIO_DATA")!,context.Content,true,250,context.Channels.ToArray(),context.ElectrodeBindings,context);
        var model=RobustClockMapper.Fit(Enumerable.Range(0,20).Select(i=>new ClockObservation(i*50,100+i*.2)).ToArray(),.004,"device-sample-index","qa");
        recorder.Record("clock-model",model);
        foreach(double index in new[] {100,250.5,300,500,1000})
            recorder.Record("trial-marker",new TrialMarker("试次,一","observe-start","target","T2",model.Estimate(index),index*.004,model.ModelId,index,"Estimated"));
        for(int start=0;start<1000;start+=25)
        {
            var samples=Enumerable.Range(start,25).Where(i=>i!=500).Select(i=>new Ssvep.Speller.Core.Signal.EegSample(i,DateTimeOffset.UnixEpoch,
                Enumerable.Range(0,8).Select(c=>i==700&&c==0?double.NaN:20*Math.Sin(i*.1+c)).ToArray(),i*4,i==300)).ToArray();
            recorder.RecordBatch(new(start/25,"qa",250,samples,100+start*.004,100+start*.004),1,1,true);
        }
        await recorder.CompleteAsync(true);
        var result=BrainVisionExporter.Export(recorder.DirectoryPath,Path.Combine(output,"brainvision-markers"));
        Check(result.SegmentCount==4&&result.SampleCount==997,"Invalid and missing samples were interpolated");
        File.WriteAllText(Path.Combine(output,"mne-markers.json"),JsonSerializer.Serialize(new {source=recorder.DirectoryPath,export=result.Directory},CanonicalJson.Options));
    }
    private static async Task ReplayUi()
    {
        string source=Directory.GetDirectories(Path.Combine(output,"R5-research-ui","sessions")).Single();
        var window=new MainWindow {Left=-10000,Top=-10000,Width=1280,Height=840,ShowInTaskbar=false};window.Show();
        try
        {
            ((CheckBox)window.FindName("ResearchToggle")).IsChecked=true;
            ((Button)window.FindName("ReplayNav")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var panel=(ReplayPanel)((ContentControl)window.FindName("ReplayPage")).Content;
            var profiles=(ResearchPanel)((ContentControl)window.FindName("ResearchPage")).Content;
            window.Runtime.ApplyProfile(new());panel.SetFolder(source);window.UpdateLayout();
            IEnumerable<DependencyObject> Descendants(DependencyObject node)
            {
                yield return node;
                for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)
                    foreach(var child in Descendants(VisualTreeHelper.GetChild(node,i))) yield return child;
            }
            void Click(string caption)=>Descendants(panel).OfType<Button>().Single(b=>Equals(b.Content,caption)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            bool Status(string part)=>Descendants(panel).OfType<TextBlock>().Any(t=>t.Text.Contains(part));
            Click("回放对比");await Until(()=>!window.Runtime.IsOfflineBusy&&Status("回放完成"),20000);
            Check(Descendants(panel).OfType<TextBox>().Any(t=>t.Text.Contains("原记录在线反馈")&&t.Text.Contains("95% 区间")),"Replay report fields not displayed");
            Click("扫描阈值");await Until(()=>!window.Runtime.IsOfflineBusy&&Status("扫描 35 组"),20000);
            var list=Descendants(panel).OfType<ListBox>().Single();Check(list.Items.Count==35,"Threshold candidates missing");list.SelectedIndex=0;
            string before=window.Runtime.Profile.ProfileHash;Click("保存选中阈值为候选");
            Check(profiles.Repository.List().Count==2&&window.Runtime.Profile.ProfileHash==before,"Saving candidate changed active profile or failed to persist");
            Snapshot(window,Path.Combine(output,"replay.png"));
        }
        finally {await window.Runtime.DisposeAsync();window.Close();}
    }
    private static void Snapshot(Window window,string path)
    {
        window.UpdateLayout();var image=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(window);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(path);encoder.Save(file);
    }
    private static void Check(bool value,string message) {if(!value)throw new Exception(message);}
    private static async Task Until(Func<bool> condition,int ms=10000)
    {var clock=Stopwatch.StartNew();while(!condition()){if(clock.ElapsedMilliseconds>ms)throw new TimeoutException("Required state did not arrive");await Task.Delay(25);}}
}
