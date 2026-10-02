using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Ssvep.Speller.Core.Studio;

namespace Ssvep.Studio.App;

public partial class MainWindow : Window
{
    private StudioContentPackage content = StudioContentService.CreateDefault();
    private string? imageBase64;
    private bool binding;
    private bool closing;
    private string? localNotice;
    private readonly Dictionary<int, (CheckBox Check, TextBlock Value, ComboBox Position)> cards = new();
    private bool refreshingChannels;
    private string? channelContext;
    public StudioRuntime Runtime { get; }

    public MainWindow() : this(null) { }

    public MainWindow(Func<Ssvep.Speller.Core.Devices.SynchroniBridgeOptions>? bridgeOptionsFactory)
    {
        Runtime = new StudioRuntime(Dispatcher, bridgeOptionsFactory);
        InitializeComponent();
        StudioWindowPlacement.Attach(this);
        HeadMap.ElectrodeInvoked += HeadMap_Invoked;
        Runtime.Changed += (_, _) => RefreshState();
        RunStimulus.FrameRendered += Runtime.RecordFrame;
        BindContent(0);
        RefreshState();
        PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) { Runtime.Pause(); args.Handled = true; } };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) Runtime.Pause("窗口已最小化，实验暂停"); };
        Deactivated += (_, _) => Runtime.Pause("实验窗口失去焦点，已暂停");
        Closing += Window_Closing;
    }

    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (LayoutRoot is null || e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;
        // Preserve all navigation and editing controls on small/high-DPI work areas.
        double scale = Math.Min(1, Math.Min(e.NewSize.Width / 1000, e.NewSize.Height / 640));
        if (LayoutRoot.LayoutTransform is ScaleTransform current && Math.Abs(current.ScaleX - scale) < .001) return;
        LayoutRoot.LayoutTransform = new ScaleTransform(scale, scale);
    }

    private void BindContent(int selected)
    {
        binding = true;
        SchemeName.Text = content.Name;
        TargetList.ItemsSource = content.Targets;
        TargetList.SelectedIndex = Math.Clamp(selected, 0, 5);
        EditorPreview.SetContent(content); RunStimulus.SetContent(content);
        binding = false;
        LoadTarget();
    }

    private void LoadTarget()
    {
        if (TargetList.SelectedItem is not StudioTargetContent target) return;
        TargetName.Text = target.Name; TargetText.Text = target.Text; imageBase64 = target.ImageBase64;
        KindCombo.SelectedIndex = (int)target.Kind; ShapeCombo.SelectedIndex = (int)target.Shape;
        ImageLabel.Text = imageBase64 is null ? "尚未选择图片" : "图片已嵌入方案";
        UpdateKindFields();
    }

    private void Target_Changed(object sender, SelectionChangedEventArgs e) { if (!binding && IsInitialized) LoadTarget(); }
    private void Kind_Changed(object sender, SelectionChangedEventArgs e) { if (IsInitialized) UpdateKindFields(); }
    private void UpdateKindFields()
    {
        TextFields.Visibility = KindCombo.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShapeFields.Visibility = KindCombo.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        ImageFields.Visibility = KindCombo.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyEditor()
    {
        if (Runtime.ConfigurationLocked) return;
        int index = TargetList.SelectedIndex;
        if (index < 0) return;
        var targets = content.Targets.ToArray();
        targets[index] = targets[index] with { Name = TargetName.Text.Trim(), Text = TargetText.Text,
            Kind = (StudioContentKind)Math.Max(0, KindCombo.SelectedIndex),
            Shape = (StudioShape)Math.Max(0, ShapeCombo.SelectedIndex),
            ImageBase64 = KindCombo.SelectedIndex == 2 ? imageBase64 : null };
        var next = content with { Name = SchemeName.Text.Trim(), Targets = targets };
        StudioContentService.Validate(next);
        // Both previews decode images before replacing the active content snapshot.
        EditorPreview.SetContent(next); RunStimulus.SetContent(next);
        content = next;
        binding = true; TargetList.ItemsSource = content.Targets; TargetList.SelectedIndex = index; binding = false;
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => Attempt(() => { ApplyEditor(); ShowNotice("目标内容已应用。", false); });
    private void Image_Click(object sender, RoutedEventArgs e) => Attempt(() =>
    {
        var dialog = new OpenFileDialog { Title = "选择目标图片", Filter = "PNG / JPEG 图片|*.png;*.jpg;*.jpeg", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        string candidate = StudioContentService.ReadImage(dialog.FileName);
        int index = TargetList.SelectedIndex;
        var targets = content.Targets.ToArray();
        targets[index] = targets[index] with { Kind = StudioContentKind.Image, ImageBase64 = candidate };
        // Verify the real image decoder before retaining an imported file.
        var check = new StudioStimulusView(); check.SetContent(content with { Targets = targets });
        imageBase64 = candidate; ImageLabel.Text = Path.GetFileName(dialog.FileName); KindCombo.SelectedIndex = 2;
    });

    private void Save_Click(object sender, RoutedEventArgs e) => Attempt(() =>
    {
        ApplyEditor();
        var dialog = new SaveFileDialog { Title = "保存刺激方案", Filter = "SSVEP 内容方案|*.ssvep.json", FileName = "实验方案.ssvep.json" };
        if (dialog.ShowDialog(this) == true) { SaveContent(dialog.FileName); ShowNotice("方案与素材已保存。", false); }
    });

    private void Load_Click(object sender, RoutedEventArgs e) => Attempt(() =>
    {
        var dialog = new OpenFileDialog { Title = "载入刺激方案", Filter = "SSVEP 内容方案|*.ssvep.json;*.json" };
        if (dialog.ShowDialog(this) != true) return;
        LoadContent(dialog.FileName); ShowNotice("方案已载入。", false);
    });

    public void LoadContent(string path)
    {
        if (Runtime.ConfigurationLocked) throw new InvalidOperationException("请结束实验后载入内容方案。");
        var candidate = StudioContentService.Load(path);
        var check = new StudioStimulusView(); check.SetContent(candidate);
        content = candidate; BindContent(0);
    }

    public void SaveContent(string path)
    {
        if (Runtime.ConfigurationLocked) throw new InvalidOperationException("请结束实验后保存内容方案。");
        ApplyEditor(); StudioContentService.Save(path, content);
    }

    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        string name = ((Button)sender).Name;
        if (name != "RunNav") Runtime.Pause("已离开运行界面，实验暂停");
        EditorPage.Visibility = name == "EditorNav" ? Visibility.Visible : Visibility.Collapsed;
        PreparePage.Visibility = name == "PrepareNav" ? Visibility.Visible : Visibility.Collapsed;
        RunPage.Visibility = name == "RunNav" ? Visibility.Visible : Visibility.Collapsed;
        RecordsPage.Visibility = name == "RecordsNav" ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = name switch { "PrepareNav" => "设备与阻抗", "RunNav" => "实验运行", "RecordsNav" => "实验记录", _ => "刺激内容" };
        if (name == "RecordsNav") RefreshRecords();
        ClearNotice(); RefreshState();
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    { ClearNotice(); try { await Runtime.ConnectAsync(SourceCombo.SelectedIndex == 1, DeviceAddress.Text); } catch (Exception ex) { ShowNotice(ex.Message); } }
    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    { try { await Runtime.DisconnectAsync(); } catch (Exception ex) { ShowNotice(ex.Message); } }
    private void Start_Click(object sender, RoutedEventArgs e) => Attempt(() => { ApplyEditor(); ClearNotice(); Runtime.Start(content); });
    private void Pause_Click(object sender, RoutedEventArgs e) => Runtime.Pause();
    private async void Stop_Click(object sender, RoutedEventArgs e) { await Runtime.StopAsync(); RefreshRecords(); }
    private void SimulationTarget_Changed(object sender, SelectionChangedEventArgs e)
    { if (Runtime is not null && IsInitialized) Runtime.SimulationTarget = SimulationTargetCombo.SelectedIndex; }

    private void RefreshState()
    {
        if (!IsInitialized) return;
        ConnectionLabel.Text = Runtime.ConnectionMessage;
        LossCountLabel.Text = $"丢包提示 {Runtime.SampleLossEventCount} 次 · 累计丢样 {Runtime.LostSampleCount} 点";
        FrameRateLabel.Text = !Runtime.IsRunning ? "实时帧率 -- FPS"
            : Runtime.FrameRateHz is { } fps ? $"实时帧率 {fps:0.0} FPS" : "实时帧率 统计中";
        RunStatus.Text = Runtime.Session.Message;
        RunStimulus.SetRunning(Runtime.IsRunning);
        RunStimulus.SelectedTargetId = Runtime.Session.SelectedTargetId;
        EditorPage.IsEnabled = !Runtime.ConfigurationLocked;
        SourceCombo.IsEnabled = !Runtime.ConfigurationLocked && !Runtime.IsConnecting && !Runtime.IsConnected;
        DeviceAddress.IsEnabled = SourceCombo.IsEnabled;
        ConnectButton.IsEnabled = !Runtime.ConfigurationLocked && !Runtime.IsConnecting && !Runtime.IsConnected;
        DisconnectButton.IsEnabled = Runtime.IsConnecting || Runtime.IsConnected;
        StartButton.IsEnabled = Runtime.IsConnected && !Runtime.IsRunning && !Runtime.IsConnecting && !Runtime.IsRecording
            || Runtime.IsConnected && Runtime.Session.State == StudioSessionState.Paused;
        StartButton.Content = Runtime.Session.State == StudioSessionState.Paused ? "恢复实验" : "开始实验";
        PauseButton.IsEnabled = Runtime.IsRunning;
        StopButton.IsEnabled = Runtime.IsRunning || Runtime.IsRecording;
        SimulationPanel.Visibility = Runtime.IsSimulation ? Visibility.Visible : Visibility.Collapsed;
        ContactLabel.Text = Runtime.IsSimulation ? Runtime.ContactStatus.Message : Runtime.ContactStatus.Message + " · 仅供参考";
        RefreshImpedance();
        string message = localNotice ?? Runtime.Notice;
        NoticePanel.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        NoticeLabel.Text = message;
    }

    private void RefreshImpedance()
    {
        if (refreshingChannels) return;
        refreshingChannels = true;
        try
        {
            int count = Math.Clamp(Runtime.ChannelCount, 1, 64);
            string context = $"{Runtime.IsSimulation}-{count}";
            if (channelContext != context && !Runtime.ConfigurationLocked)
            {
                channelContext = context;
                var setup = StudioElectrodes.Create(count, Runtime.IsSimulation);
                if (!Runtime.IsSimulation && File.Exists(ChannelSetupPath(count)))
                {
                    try { setup = StudioElectrodes.Load(ChannelSetupPath(count), count); }
                    catch (Exception ex) when (ex is IOException or ArgumentException or JsonException or UnauthorizedAccessException)
                    { localNotice = "已保存的电极对应表无法读取，请重新设置位置：" + ex.Message; }
                }
                Runtime.ApplyChannelSetup(setup);
            }
            if (cards.Count != count)
            {
                cards.Clear(); ImpedanceCards.Children.Clear();
                BindingChannel.ItemsSource = Enumerable.Range(1,count).Select(i => $"CH{i}").ToArray();
                BindingChannel.SelectedIndex = 0;
                for (int i = 0; i < count; i++)
                {
                    int channel = i;
                    var check = new CheckBox { Content = $"CH{i + 1}", Tag=i, IsChecked = Runtime.UsesChannel(i), VerticalAlignment=VerticalAlignment.Center };
                    var position = new ComboBox { Width=116, MinHeight=30, Padding=new Thickness(5), FontSize=12, HorizontalAlignment=HorizontalAlignment.Right,
                        ItemsSource = new[] {"未指定"}.Concat(StudioElectrodes.Positions.Select(p => p.Name)).ToArray(), Tag=i };
                    var row = new DockPanel(); DockPanel.SetDock(position,Dock.Right); row.Children.Add(position); row.Children.Add(check);
                    var value = new TextBlock { Text = "待更新", FontSize = 12, Margin=new Thickness(0,7,0,0) };
                    var stack = new StackPanel(); stack.Children.Add(row); stack.Children.Add(value);
                    ImpedanceCards.Children.Add(new Border { Child = stack, Padding = new Thickness(10), Margin = new Thickness(0,0,5,8), Background = new SolidColorBrush(Color.FromRgb(243,247,250)), CornerRadius = new CornerRadius(8) });
                    cards.Add(i, (check, value, position));
                    check.Checked += ChannelSelectionChanged; check.Unchecked += ChannelSelectionChanged;
                    position.SelectionChanged += (_, _) =>
                    {
                        if (refreshingChannels) return;
                        Attempt(() => AssignElectrode(channel, position.SelectedItem as string is "未指定" ? null : position.SelectedItem as string));
                        RefreshImpedance();
                    };
                }
            }
            bool fresh = Runtime.ImpedanceReceivedAt is not null && DateTimeOffset.UtcNow - Runtime.ImpedanceReceivedAt <= StudioRuntimeProfile.MaximumImpedanceAge;
            var current = Runtime.ChannelSetup;
            foreach (var pair in cards)
            {
                pair.Value.Check.IsEnabled = !Runtime.ConfigurationLocked;
                pair.Value.Check.IsChecked = Runtime.UsesChannel(pair.Key);
                pair.Value.Position.IsEnabled = !Runtime.ConfigurationLocked;
                pair.Value.Position.SelectedItem = current.Bindings.FirstOrDefault(b => b.ChannelIndex == pair.Key)?.Electrode ?? "未指定";
                var value = Runtime.Impedance.FirstOrDefault(v => v.ChannelIndex == pair.Key);
                string label; Brush color;
                if (Runtime.IsSimulation) { label = "模拟 · 未测量"; color = Brushes.SlateGray; }
                else if (!Runtime.IsConnected || !fresh || value is null || value.Quality == Ssvep.Speller.Core.Devices.ImpedanceQuality.Unknown || !double.IsFinite(value.KiloOhms) || value.KiloOhms <= 0)
                { label = "未知 / 待更新"; color = Brushes.SlateGray; }
                else if (value.KiloOhms > StudioRuntimeProfile.MaximumImpedanceKiloOhms)
                { label = $"{value.KiloOhms:0.#} kΩ · 需调整"; color = Brushes.DarkOrange; }
                else { label = $"{value.KiloOhms:0.#} kΩ · 正常"; color = new SolidColorBrush(Color.FromRgb(20,108,99)); }
                pair.Value.Value.Text = label; pair.Value.Value.Foreground = color;
            }
            EditLocations.IsEnabled = BindingChannel.IsEnabled = !Runtime.ConfigurationLocked;
            BindingToolbar.Visibility = EditLocations.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            HeadMap.Refresh(current,Runtime.Impedance,Runtime.IsSimulation,fresh,Runtime.IsConnected,Runtime.ConfigurationLocked,
                EditLocations.IsChecked == true,ShowExtended.IsChecked == true);
            SelectionCount.Text = $"已选择 {current.SelectedChannels.Count} / 8 通道";
            SelectionCount.Foreground = current.SelectedChannels.Count == 8 ? new SolidColorBrush(Color.FromRgb(20,108,99)) : Brushes.DarkOrange;
            int unmapped = count-current.Bindings.Count;
            MappingHint.Text = Runtime.IsSimulation ? "模拟位置示例，仅用于演示；不会覆盖真实设备的电极对应表。"
                : $"{(unmapped > 0 ? $"还有 {unmapped} 个通道未指定位置。" : "所有通道均已指定位置。")}" +
                  "对应关系与通道选择自动保存在本机；更换脑电帽后请重新核对。";
            ImpedanceTimeLabel.Text = Runtime.IsSimulation ? "模拟模式不生成真实阻抗值，也不用于佩戴质量验收。"
                : Runtime.ImpedanceReceivedAt is null ? "等待 SDK 阻抗数据。" : $"最近接收：{Runtime.ImpedanceReceivedAt.Value.ToLocalTime():HH:mm:ss}  ·  设备未提供独立测量时间";
            }
        finally { refreshingChannels = false; }
    }

    private string ChannelSetupPath(int count) => Path.Combine(Runtime.DataDirectory,$"channel-layout-{count}.json");
    private void SaveChannelSetup()
    {
        if (!Runtime.IsSimulation) StudioElectrodes.Save(ChannelSetupPath(Runtime.ChannelCount),Runtime.ChannelSetup);
    }
    private void AssignElectrode(int channel, string? electrode)
    {
        Runtime.ApplyChannelSetup(StudioElectrodes.Assign(Runtime.ChannelSetup,channel,electrode));
        SaveChannelSetup();
    }
    private void MapOptions_Changed(object sender, RoutedEventArgs e)
    { if (IsInitialized && HeadMap is not null) RefreshImpedance(); }
    private void HeadMap_Invoked(string electrode) => Attempt(() =>
    {
        if (Runtime.ConfigurationLocked) return;
        if (EditLocations.IsChecked == true) { AssignElectrode(BindingChannel.SelectedIndex,electrode); return; }
        var mapped = Runtime.ChannelSetup.Bindings.FirstOrDefault(b => b.Electrode == electrode);
        if (mapped is null) return;
        var selection = Runtime.ChannelSetup.SelectedChannels.ToHashSet();
        if (!selection.Remove(mapped.ChannelIndex)) selection.Add(mapped.ChannelIndex);
        Runtime.SetChannels(selection); SaveChannelSetup();
    });
    private void ChannelSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (refreshingChannels) return;
        Attempt(() => { Runtime.SetChannels(cards.Where(p => p.Value.Check.IsChecked == true).Select(p => p.Key)); SaveChannelSetup(); });
    }
    private void RefreshRecords_Click(object sender, RoutedEventArgs e) => RefreshRecords();
    private void RefreshRecords()
    {
        var records = new List<RecordRow>();
        try
        {
            if (Directory.Exists(Runtime.DataDirectory))
                foreach (string path in Directory.EnumerateFiles(Runtime.DataDirectory, "manifest.json", SearchOption.AllDirectories).OrderDescending().Take(500))
                {
                    try
                    {
                        using var json = JsonDocument.Parse(File.ReadAllText(path)); var item = json.RootElement;
                        records.Add(new RecordRow(item.GetProperty("startedAt").GetDateTimeOffset().ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                            item.GetProperty("name").GetString() ?? "未命名", item.GetProperty("dataSource").GetString() == "simulation" ? "模拟脑电" : "真实 SDK",
                            item.GetProperty("completed").GetBoolean() ? "完整" : "未完成 / 异常中断", Path.GetDirectoryName(path)!));
                    }
                    catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
                    { records.Add(new RecordRow("—", Path.GetFileName(Path.GetDirectoryName(path)) ?? "未知记录", "—", "记录无法读取", Path.GetDirectoryName(path)!)); }
                }
            RecordsList.ItemsSource = records;
            RecordsHint.Text = records.Count == 0 ? "暂无实验记录。完成一次实验后，原始数据与运行记录会显示在这里。" : $"共显示 {records.Count} 条记录。记录文件夹包含内容方案、原始数据、阻抗、帧时序与识别记录。";
        }
        catch (Exception ex) { ShowNotice("读取记录失败：" + ex.Message); }
    }

    private void OpenRecord_Click(object sender, RoutedEventArgs e) => Attempt(() =>
    {
        string folder = RecordsList.SelectedItem is RecordRow row ? row.Folder : Runtime.DataDirectory;
        Directory.CreateDirectory(folder); Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    });
    private void Attempt(Action action) { try { action(); } catch (Exception ex) { ShowNotice(ex.Message); } }
    private void ShowNotice(string message, bool error = true) { localNotice = message; RefreshState(); }
    private void ClearNotice() { localNotice = null; RefreshState(); }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        e.Cancel = true; closing = true;
        try { await Runtime.DisposeAsync(); }
        finally { _ = Dispatcher.BeginInvoke(new Action(Close)); }
    }
    private sealed record RecordRow(string Time, string Name, string Source, string Status, string Folder);
}
