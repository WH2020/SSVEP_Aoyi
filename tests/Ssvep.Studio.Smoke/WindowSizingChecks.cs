using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ssvep.Studio.App;
using Ssvep.Speller.Core.Devices;

internal static class WindowSizingChecks
{
    public static async Task<int> Run(string output)
    {
        MainWindow? window = null;
        var results = new List<string>();
        void Check(bool value,string message) { if (!value) throw new Exception(message); }
        try
        {
            var cases = new[]
            {
                ("1080p-100",new Rect(0,0,1920,1040),1.0),
                ("1440p-150",new Rect(0,0,2560,1380),1.5),
                ("1600p-200",new Rect(0,0,2560,1540),2.0),
                ("1080p-200",new Rect(0,0,1920,1040),2.0),
                ("1366-150",new Rect(0,0,1366,728),1.5),
                ("4k-250",new Rect(0,0,3840,2080),2.5),
                ("secondary-left-125",new Rect(-1920,40,1920,1040),1.25),
                ("secondary-above-200",new Rect(0,-1440,2560,1380),2.0),
            };
            foreach (var (name,work,dpi) in cases)
            {
                var fitted = StudioWindowPlacement.Calculate(work,dpi,new Size(1280,840));
                Check(work.Contains(fitted.Pixels),name+": title bar or bottom outside work area");
                Check(fitted.Pixels.Top>work.Top && fitted.Pixels.Bottom<work.Bottom,name+": no taskbar/title margin");
                Check(fitted.MinimumDips.Width<=fitted.Dips.Width && fitted.MinimumDips.Height<=fitted.Dips.Height,
                    name+": minimum size overrides fitted bounds");
                var moved = StudioWindowPlacement.Calculate(work,dpi,new Size(1280,840),new Point(-10000,-10000));
                Check(work.Contains(moved.Pixels),name+": off-screen coordinates were not corrected");
                results.Add("PASS "+name);
            }
            Check(840*2>1540,"Regression scenario must exceed the screen before fitting");
            window = new MainWindow { Width=4000,Height=2400,ShowInTaskbar=false,ShowActivated=false };
            new WindowInteropHelper(window).EnsureHandle();
            var actual = StudioWindowPlacement.ReadScreen(window);
            Check(actual.WorkAreaPixels.Contains(actual.WindowPixels),"Actual HWND outer bounds exceed the monitor work area");
            File.WriteAllText(Path.Combine(output,"native-bounds.json"),JsonSerializer.Serialize(new
            {
                workArea=actual.WorkAreaPixels.ToString(),window=actual.WindowPixels.ToString(),dpiScale=actual.DpiScale,
                width=window.Width,height=window.Height,minWidth=window.MinWidth,minHeight=window.MinHeight
            },new JsonSerializerOptions {WriteIndented=true}));
            results.Add("PASS oversized native HWND startup is fitted, including non-client title bar");

            var diagnostics = new SynchroniBridgeDiagnostics();
            for (int i = 0; i < 2; i++) diagnostics.RecordEvent(new(SynchroniBridgeDiagnosticKind.SensorError,
                "MSG|LOST SAMPLE|MAC|AA:BB|TYPE|16|COUNT|10", DateTimeOffset.UtcNow));
            typeof(StudioRuntime).GetMethod("ReceiveDiagnostics", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window.Runtime, [diagnostics.CreateSnapshot(), 0L]);
            var lossLabel = (TextBlock)window.FindName("LossCountLabel");
            Check(lossLabel.Text == "丢包提示 2 次 · 累计丢样 20 点", "Footer did not display cumulative SDK loss counters");
            Check(string.IsNullOrEmpty(window.Runtime.Notice), "Loss was displayed as a fatal notice");
            results.Add("PASS loss diagnostic updates visible footer counters without fatal notice");

            foreach(var size in new[]{new Size(1280,800),new Size(928,456),new Size(850,430)})
            {
                var viewport=(FrameworkElement)window.Content;
                viewport.Measure(size); viewport.Arrange(new Rect(size)); viewport.UpdateLayout();
                await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
                viewport.UpdateLayout();
                foreach(string name in new[]{"PageTitle","EditorNav","RecordsNav","SaveButton","LossCountLabel"})
                {
                    var control=(FrameworkElement)window.FindName(name);
                    var bounds=control.TransformToAncestor(viewport).TransformBounds(new Rect(control.RenderSize));
                    Check(bounds.Top>=-1 && bounds.Bottom<=size.Height+1 && bounds.Left>=-1 && bounds.Right<=size.Width+1,
                        $"{size}: {name} clipped outside viewport ({bounds})");
                }
                var bitmap=new RenderTargetBitmap((int)size.Width,(int)size.Height,96,96,PixelFormats.Pbgra32);
                bitmap.Render(viewport);
                var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using(var file=File.Create(Path.Combine(output,$"viewport-{size.Width}.png"))) encoder.Save(file);
                results.Add($"PASS {size.Width}x{size.Height} client layout: heading, navigation and save action remain inside viewport");
            }
            ((Button)window.FindName("RunNav")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            foreach(var size in new[]{new Size(1280,800),new Size(928,456),new Size(850,430)})
            {
                var viewport=(FrameworkElement)window.Content;
                ((TextBlock)window.FindName("FrameRateLabel")).Text = "实时帧率 144.0 FPS";
                viewport.Measure(size); viewport.Arrange(new Rect(size)); viewport.UpdateLayout();
                foreach(string name in new[]{"FrameRateLabel","StartButton","PauseButton","StopButton"})
                {
                    var control=(FrameworkElement)window.FindName(name);
                    var bounds=control.TransformToAncestor(viewport).TransformBounds(new Rect(control.RenderSize));
                    Check(bounds.Top>=-1 && bounds.Bottom<=size.Height+1 && bounds.Left>=-1 && bounds.Right<=size.Width+1,
                        $"{size}: {name} clipped outside viewport ({bounds})");
                }
                var bitmap=new RenderTargetBitmap((int)size.Width,(int)size.Height,96,96,PixelFormats.Pbgra32);
                bitmap.Render(viewport);
                var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using(var file=File.Create(Path.Combine(output,$"run-viewport-{size.Width}.png"))) encoder.Save(file);
                results.Add($"PASS {size.Width}x{size.Height} run layout: live FPS label and experiment actions remain inside viewport");
            }
            File.WriteAllLines(Path.Combine(output,"result.txt"),results);
            return 0;
        }
        catch(Exception ex) { File.WriteAllLines(Path.Combine(output,"result.txt"),results.Append(ex.ToString())); Console.Error.WriteLine(ex); return 1; }
        finally { if(window is not null) { await window.Runtime.DisposeAsync(); window.Close(); } }
    }
}
