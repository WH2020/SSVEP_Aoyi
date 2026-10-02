using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ssvep.Speller.Core.Devices;
using Ssvep.Speller.Core.Studio;
using Ssvep.Studio.App;

internal static class HeadMapChecks
{
    public static async Task<int> Run(string output,string python)
    {
        MainWindow? window=null;
        var results = new List<string>();
        void Check(bool condition,string message) { if(!condition) throw new Exception(message); }
        try
        {
            window=new MainWindow();
            T Control<T>(string name) where T:FrameworkElement => (T)window.FindName(name);
            void Click(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            void Electrode(string name) => Control<StudioHeadMap>("HeadMap").ElectrodeButton(name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            ComboBox Position(int channel) => Descendants<ComboBox>(Control<StackPanel>("ImpedanceCards")).Single(c => Equals(c.Tag,channel));
            CheckBox Selected(int channel) => Descendants<CheckBox>(Control<StackPanel>("ImpedanceCards")).Single(c => Equals(c.Tag,channel));
            Click("PrepareNav");
            Check(window.Runtime.ChannelSetup.Bindings.Count==0,"Real mode must not infer cap wiring");
            Check(!Control<StudioHeadMap>("HeadMap").ElectrodeButton("O1").IsEnabled,"Unbound site must not toggle an arbitrary channel");
            Control<CheckBox>("EditLocations").IsChecked=true;
            Control<ComboBox>("BindingChannel").SelectedIndex=0; Electrode("O1");
            Check(window.Runtime.ChannelSetup.Bindings.Single().Electrode=="O1","Map assignment did not map CH1");
            Control<ComboBox>("BindingChannel").SelectedIndex=1; Electrode("O1");
            Check(window.Runtime.ChannelSetup.Bindings.Count==1,"Duplicate location silently stole existing channel");
            Check(Control<TextBlock>("NoticeLabel").Text.Contains("只能对应一次"),"Duplicate position did not explain correction");
            Position(1).SelectedItem="O2";
            Control<CheckBox>("EditLocations").IsChecked=false;
            Electrode("O1");
            Check(!window.Runtime.UsesChannel(0) && Selected(0).IsChecked==false,"Map toggle not synchronized to SDK channel selection/list");
            Selected(0).IsChecked=true;
            Check(Control<StudioHeadMap>("HeadMap").ElectrodeButton("O1").IsChecked==true,"List toggle not synchronized to map");
            results.Add("PASS manual assignment on map/list, duplicate rejection and bidirectional channel selection");
            string stored=Path.Combine(window.Runtime.DataDirectory,"channel-layout-8.json");
            string realSetup=File.ReadAllText(stored);
            await window.Runtime.DisposeAsync(); window.Close();
            window=new MainWindow(); Click("PrepareNav");
            Check(window.Runtime.ChannelSetup.Bindings.Count==2 && window.Runtime.ChannelSetup.Bindings[1].Electrode=="O2","Manual positions not restored after restart");
            Check(window.Runtime.UsesChannel(0),"Selection not restored");
            Control<ComboBox>("SourceCombo").SelectedIndex=1;
            await window.Runtime.ConnectAsync(true,null);
            await Until(()=>window.Runtime.IsConnected);
            Check(window.Runtime.ChannelSetup.Bindings.Count==8,"Demonstration positions not installed");
            Control<CheckBox>("ShowExtended").IsChecked=false;
            Check(Control<StudioHeadMap>("HeadMap").ElectrodeButton("Oz").Visibility==Visibility.Visible,"Mapped extension disappeared");
            Check(Control<StudioHeadMap>("HeadMap").ElectrodeButton("FC1").Visibility==Visibility.Collapsed,"Unbound extension hide switch failed");
            Control<CheckBox>("ShowExtended").IsChecked=true;
            Electrode("O1");
            bool blocked=false;
            try { window.Runtime.Start(StudioContentService.CreateDefault()); } catch(InvalidOperationException) { blocked=true; }
            Check(blocked && !window.Runtime.IsRecording,"Seven channels incorrectly started experiment");
            Check(File.ReadAllText(stored)==realSetup,"Simulation changed real cap configuration");
            Electrode("O1");
            results.Add("PASS persistence/restart, simulation isolation, extension visibility and eight-channel start guard");
            foreach(var size in new[]{new Size(1280,900),new Size(1000,680)}) Snapshot(window,output,$"headmap-{size.Width}.png",size);
            Control<ScrollViewer>("PreparePage").ScrollToBottom();
            Snapshot(window,output,"headmap-1000-scrolled.png",new Size(1000,680));
            window.Runtime.Start(StudioContentService.CreateDefault());
            window.Runtime.RecordFrame(0);
            Check(!Control<StudioHeadMap>("HeadMap").ElectrodeButton("O1").IsEnabled && !Position(0).IsEnabled,"Running configuration remains editable");
            Electrode("O1");
            Check(window.Runtime.UsesChannel(0),"Synthetic UI click bypassed running guard");
            bool locked=false;
            try { window.Runtime.ApplyChannelSetup(StudioElectrodes.Create(8)); } catch(InvalidOperationException) { locked=true; }
            Check(locked,"Runtime configuration API bypassed running lock");
            window.Runtime.Pause();
            Check(!Position(0).IsEnabled,"Paused recording unlocked channel assignments");
            await window.Runtime.StopAsync();
            Check(Position(0).IsEnabled,"Stop did not unlock assignments");
            string manifestPath=Directory.GetFiles(window.Runtime.DataDirectory,"manifest.json",SearchOption.AllDirectories).Single();
            using(var manifest=JsonDocument.Parse(File.ReadAllText(manifestPath)))
                Check(manifest.RootElement.GetProperty("electrodeBindings").GetArrayLength()==8,"Experiment omitted frozen electrode mapping");
            results.Add("PASS running/paused lock, stop unlock and experiment manifest electrode mapping");
            await window.Runtime.DisposeAsync(); window.Close();

            window=new MainWindow(()=>new SynchroniBridgeOptions(python,[],Path.GetFullPath("tests/Ssvep.Studio.Smoke/sdk_fixture.py"),100,null,null,10,60000,false,false,false));
            Click("PrepareNav");
            await window.Runtime.ConnectAsync(false,"32");
            await Until(()=>window.Runtime.IsConnected && window.Runtime.ChannelCount==32);
            Check(window.Runtime.ChannelSetup.Bindings.Count==0,"8-channel mapping leaked into 32-channel device");
            Check(Control<ComboBox>("BindingChannel").Items.Count==32,"Device channel count did not rebuild assignment controls");
            Position(31).SelectedItem="O1";
            Position(24).SelectedItem="O2";
            Electrode("O1");
            Check(window.Runtime.UsesChannel(31),"Mapped CH32 did not select SDK index31");
            Selected(0).IsChecked=false;
            Check(window.Runtime.ChannelSetup.SelectedChannels.Count==8 && !window.Runtime.UsesChannel(0),"Eight-channel subset did not reflect map/list changes");
            var map=Control<StudioHeadMap>("HeadMap");
            Check(map.ElectrodeButton("O1").ToolTip!.ToString()!.Contains("CH32"),"Map label lost high channel index");
            map.Refresh(window.Runtime.ChannelSetup,[ChannelImpedance.FromOhms(31,600000)],false,true,true,false,false,true);
            Check(map.ElectrodeButton("O1").ToolTip!.ToString()!.Contains("600 kΩ"),"Map did not associate impedance with assigned SDK index");
            map.Refresh(window.Runtime.ChannelSetup,[ChannelImpedance.FromOhms(31,100000)],false,false,true,false,false,true);
            Check(map.ElectrodeButton("O1").ToolTip!.ToString()!.Contains("待更新"),"Stale measurement still shown as valid on map");
            results.Add("PASS 32-channel input/subset, correct SDK index mapping and stale impedance display");
            File.WriteAllLines(Path.Combine(output,"headmap-result.txt"),results);
            return 0;
        }
        catch(Exception ex) { File.WriteAllLines(Path.Combine(output,"headmap-result.txt"),results.Append(ex.ToString())); Console.Error.WriteLine(ex); return 1; }
        finally { if(window is not null) { await window.Runtime.DisposeAsync(); window.Close(); } }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T:DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
        { var child=VisualTreeHelper.GetChild(parent,i); if(child is T value) yield return value; foreach(var next in Descendants<T>(child)) yield return next; }
    }
    private static async Task Until(Func<bool> condition)
    {
        var deadline=DateTime.UtcNow.AddSeconds(10);
        while(!condition()) { if(DateTime.UtcNow>deadline) throw new Exception("Connection timed out"); await Task.Delay(50); }
    }
    private static void Snapshot(MainWindow window,string directory,string name,Size size)
    {
        var root=(FrameworkElement)window.Content;
        root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
        var bitmap=new RenderTargetBitmap((int)size.Width,(int)size.Height,96,96,PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file=File.Create(Path.Combine(directory,name)); encoder.Save(file);
    }
}
