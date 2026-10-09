using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ssvep.Studio.App;
using Ssvep.Speller.Core.Devices;
using Ssvep.Speller.Core.Studio;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string output = Path.GetFullPath(args.FirstOrDefault() ?? "outputs/studio-development/smoke");
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("SSVEP_STUDIO_DATA", Path.Combine(output, "data"));
        var app = new Application();
        int exit = 1;
        app.DispatcherUnhandledException += (_, error) =>
        {
            File.AppendAllText(Path.Combine(output, "result.txt"), "\nUNHANDLED: " + error.Exception);
            error.Handled = true; exit = 1; app.Shutdown();
        };
        app.Startup += async (_, _) =>
        {
            MainWindow? window = null;
            try
            {
                if (args.Length > 1 && args[1] is "--calibration-readiness" or "--calibration-groups")
                {
                    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    exit = await CalibrationReadinessChecks.Run(output,args[1]=="--calibration-groups");
                    return;
                }
                if (args.Length > 1 && args[1] == "--calibration")
                {
                    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    exit = await CalibrationChecks.Run(output);
                    return;
                }
                if (args.Length > 1 && args[1] == "--research")
                {
                    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    exit = await ResearchRegressions.Run(output,args[2],args.Contains("--ui-only"));
                    return;
                }
                if (args.Length > 1 && args[1] == "--regressions")
                {
                    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    exit = await RuntimeRegressions.Run(output, args[2]);
                    return;
                }
                if (args.Length > 1 && args[1] == "--headmap")
                {
                    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    exit = await HeadMapChecks.Run(output,args[2]);
                    return;
                }
                if (args.Length > 1 && args[1] == "--window-size")
                {
                    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    exit = await WindowSizingChecks.Run(output);
                    return;
                }
                bool sdkFixture = args.Length > 1 && args[1] == "--sdk";
                window = new MainWindow(sdkFixture ? () => new SynchroniBridgeOptions(
                    args[2], [], Path.GetFullPath("tests/Ssvep.Studio.Smoke/sdk_fixture.py"),
                    100, null, null, 10, 60000, false, false, false) : null)
                { Width = 1280, Height = 840, ShowInTaskbar = false };
                // Off-screen layout still uses the actual WPF controls and renderer.
                window.Left = -10000; window.Top = -10000;
                window.Show();
                void Click(string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
                Check(((ListBox)window.FindName("TargetList")).Items.Count == 6, "Editor must expose six fixed slots");
                TextBlock fpsLabel = (TextBlock)window.FindName("FrameRateLabel");
                Check(window.Runtime.FrameRateHz is null && fpsLabel.Text == "实时帧率 -- FPS", "Idle view showed a stale frame rate");
                Snapshot(window, Path.Combine(output, "editor.png"));
                if (sdkFixture)
                {
                    Click("PrepareNav");
                    ((TextBox)window.FindName("DeviceAddress")).Text = "high";
                    Click("ConnectButton");
                    await Until(() => window.Runtime.IsConnected && window.Runtime.Impedance.Count == 8, "Fixture high impedance did not connect");
                    Check(!window.Runtime.ContactStatus.IsReady, "High impedance must remain visible as unresolved");
                    Check(((TextBlock)window.FindName("ContactLabel")).Text.Contains("仅供参考"), "Impedance UI did not explain advisory status");
                    Click("RunNav"); Click("StartButton");
                    Check(window.Runtime.IsRunning && window.Runtime.IsRecording, "High impedance blocked experiment start");
                    await Until(() => window.Runtime.Session.SelectedTargetId == "T2", "High impedance blocked recognition", 15000);
                    Click("PauseButton"); Click("StartButton");
                    await Until(() => window.Runtime.Session.SelectedTargetId == "T2", "High impedance blocked resume", 15000);
                    Click("StopButton"); await Until(() => !window.Runtime.IsRecording, "High impedance session did not finish");
                    await window.Runtime.DisconnectAsync();

                    foreach (string mode in new[] { "no-impedance", "unknown-impedance" })
                    {
                        Click("PrepareNav"); ((TextBox)window.FindName("DeviceAddress")).Text = mode;
                        Click("ConnectButton"); await Until(() => window.Runtime.IsConnected, mode + " fixture did not connect");
                        Check(!window.Runtime.ContactStatus.IsReady, mode + " displayed unresolved contact as normal");
                        Click("RunNav"); Click("StartButton");
                        await Until(() => window.Runtime.Session.SelectedTargetId == "T2", mode + " blocked recognition", 15000);
                        Check(window.Runtime.IsRunning && window.Runtime.IsRecording, mode + " interrupted experiment");
                        Click("StopButton"); await Until(() => !window.Runtime.IsRecording, mode + " session did not finish");
                        await window.Runtime.DisconnectAsync();
                    }

                    Click("PrepareNav");
                    ((TextBox)window.FindName("DeviceAddress")).Text = "500";
                    Click("ConnectButton");
                    await Until(() => window.Runtime.IsConnected && window.Runtime.ContactStatus.IsReady, "500 Hz fixture not ready");
                    Click("RunNav"); Click("StartButton");
                    await Until(() => window.Runtime.Session.SelectedTargetId != null, "500 Hz fixture recognition failed", 15000);
                    Check(window.Runtime.Session.SelectedTargetId == "T2", "500 Hz fixture did not select T2");
                    Click("StopButton"); await Until(() => !window.Runtime.IsRecording, "500 Hz session did not finish");
                    await window.Runtime.DisconnectAsync();

                    Click("PrepareNav");
                    ((TextBox)window.FindName("DeviceAddress")).Text = "stale";
                    Click("ConnectButton");
                    await Until(() => window.Runtime.IsConnected && window.Runtime.ContactStatus.IsReady, "Stale fixture not ready");
                    Click("RunNav"); Click("StartButton");
                    await Until(() => window.Runtime.Session.SelectedTargetId != null, "Fixture recognition failed", 15000);
                    await Until(() => !window.Runtime.ContactStatus.IsReady, "Stale impedance was displayed as current", 12000);
                    Check(window.Runtime.IsConnected && !window.Runtime.ContactStatus.IsReady, "Fresh EEG must not refresh cached impedance");
                    Check(window.Runtime.IsRunning && window.Runtime.IsRecording && window.Runtime.Session.SelectedTargetId == "T2",
                        "Stale impedance paused experiment or cleared valid selection");
                    Click("StopButton"); await Until(() => !window.Runtime.IsRecording, "Stale session not closed");
                    await window.Runtime.DisconnectAsync();

                    Click("PrepareNav");
                    ((TextBox)window.FindName("DeviceAddress")).Text = "disconnect";
                    Click("ConnectButton");
                    await Until(() => window.Runtime.IsConnected && window.Runtime.ContactStatus.IsReady, "Disconnect fixture not ready");
                    Click("RunNav"); Click("StartButton");
                    await Until(() => window.Runtime.Session.SelectedTargetId != null, "Disconnect fixture did not initially classify", 15000);
                    await Until(() => !window.Runtime.IsConnected && !window.Runtime.IsRecording, "SDK exit did not end session", 7000);
                    Check(window.Runtime.Session.SelectedTargetId == null, "Disconnect left border visible");
                    Check(window.Runtime.Session.State == StudioSessionState.Faulted, "Disconnect must mark incomplete experiment");
                    File.WriteAllText(Path.Combine(output, "result.txt"), "PASS: real WPF controls with synthetic SDK protocol; high/missing/unknown impedance allows start and recognition, high impedance allows resume, stale impedance remains visible without pausing, 500 Hz FBCCA, process exit faults and clears selection. No hardware used.");
                    exit = 0;
                    return;
                }
                ((ComboBox)window.FindName("SourceCombo")).SelectedIndex = 1;
                Click("PrepareNav");
                Click("ConnectButton");
                await Until(() => window.Runtime.IsConnected, "Simulation did not connect");
                await Task.Delay(150);
                Check(window.Runtime.IsSimulation, "Simulation must be explicitly labeled");
                Snapshot(window, Path.Combine(output, "impedance.png"));
                Click("RunNav");
                Click("StartButton");
                Check(window.Runtime.ActiveContext?.Display.Width>0&&window.Runtime.ActiveContext.Display.Height>0,"Display snapshot was captured before layout");
                Check(window.Runtime.FrameRateHz is null && fpsLabel.Text == "实时帧率 统计中", "Start reused old frame history");
                await Until(() => window.Runtime.Session.SelectedTargetId != null, "No synthetic EEG recognition", 15000);
                Check(window.Runtime.Session.SelectedTargetId == "T2", "Classifier must select simulated T2");
                Check(window.Runtime.FrameRateHz is { } rate && double.IsFinite(rate) && rate > 0,
                    "Actual WPF drawing did not produce a frame-rate measurement");
                Check(fpsLabel.Text.EndsWith(" FPS") && !fpsLabel.Text.Contains("--"), "Live frame rate did not reach the UI");
                File.WriteAllText(Path.Combine(output, "frame-rate.txt"), fpsLabel.Text);
                Snapshot(window, Path.Combine(output, "running.png"));
                Click("PauseButton");
                Check(window.Runtime.Session.SelectedTargetId == null, "Pause did not clear border");
                Check(window.Runtime.FrameRateHz is null && fpsLabel.Text == "实时帧率 -- FPS", "Pause left a frozen frame rate visible");
                await Task.Delay(600);
                Check(window.Runtime.Session.SelectedTargetId == null, "Late result restored border");
                Click("StartButton");
                Check(window.Runtime.FrameRateHz is null && fpsLabel.Text == "实时帧率 统计中", "Resume reused pre-pause frame history");
                await Task.Delay(300);
                Check(window.Runtime.Session.SelectedTargetId == null, "Resume reused old window");
                await Until(() => window.Runtime.Session.SelectedTargetId != null, "Resume failed", 15000);
                Click("StopButton");
                await Until(() => !window.Runtime.IsRecording, "Record did not close");
                Check(window.Runtime.Session.SelectedTargetId == null, "Stop did not clear border");
                Check(window.Runtime.FrameRateHz is null && fpsLabel.Text == "实时帧率 -- FPS", "Stop retained a frame rate");
                Click("RecordsNav");
                Snapshot(window, Path.Combine(output, "records.png"));
                var manifests = Directory.GetFiles(Path.Combine(output, "data"), "manifest.json", SearchOption.AllDirectories);
                Check(manifests.Length > 0, "No persisted session");
                File.WriteAllText(Path.Combine(output, "result.txt"), "PASS: six-target editor, actual WPF drawing/live FPS label, simulation FBCCA T2, pause/stop clears FPS, fresh resume resets FPS history, late-result rejection, persisted records, screenshots. No hardware used.");
                exit = 0;
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(output, "result.txt"), ex.ToString()); Console.Error.WriteLine(ex); }
            finally { if (window != null) { await window.Runtime.DisposeAsync(); window.Close(); } app.Shutdown(); }
        };
        app.Run();
        return exit;
    }

    private static async Task Until(Func<bool> condition, string message, int timeout = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeout);
        while (!condition()) { if (DateTime.UtcNow > until) throw new Exception(message); await Task.Delay(50); }
    }

    private static void Snapshot(Window window, string path)
    {
        window.UpdateLayout();
        var visual = (FrameworkElement)window.Content;
        var image = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path); encoder.Save(file);
    }
}
