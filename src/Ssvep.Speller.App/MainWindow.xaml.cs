using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Ssvep.Speller.Core.Classification;
using Ssvep.Speller.Core.Collection;
using Ssvep.Speller.Core.Decision;
using Ssvep.Speller.Core.Devices;
using Ssvep.Speller.Core.Feedback;
using Ssvep.Speller.Core.Logging;
using Ssvep.Speller.Core.Output;
using Ssvep.Speller.Core.Signal;
using Ssvep.Speller.Core.Stimulus;

namespace Ssvep.Speller.App;

public partial class MainWindow : Window
{
    private const int SamplingRateHz = 250;
    private const int WindowSamples = 625;

    private readonly IReadOnlyList<StimulusTarget> targets;
    private readonly ISsvepClassifier classifier;
    private readonly TextComposer composer;
    private readonly DispatcherTimer recognitionTimer;
    private readonly DispatcherTimer readinessTimer;
    private readonly object montageGate = new();
    private DecisionSettings decisionSettings = new();
    private HybridDecisionEngine decisionEngine;
    private EegRingBuffer? sdkRingBuffer;
    private CancellationTokenSource? sdkCancellationTokenSource;
    private IEegStream? sdkStream;
    private SynchroniBridgeEegStream? synchroniBridgeStream;
    private SynchroniBridgeOptions? activeSynchroniOptions;
    private IReadOnlyList<ChannelImpedance> latestImpedance = Array.Empty<ChannelImpedance>();
    private IReadOnlyList<SynchroniDiscoveredDevice> discoveredDevices = Array.Empty<SynchroniDiscoveredDevice>();
    private SynchroniBridgeDiagnosticsSnapshot latestDiagnosticsSnapshot = CreateEmptyDiagnosticsSnapshot();
    private ChannelMontage sdkMontage = ChannelMontage.CreateDefault(inputChannelCount: 8, outputChannelCount: 8);
    private Task? sdkReadTask;
    private SessionLogWriter? sessionLogWriter;
    private DataCollectionLogWriter? collectionLogWriter;
    private DataCollectionController? collectionController;
    private IReadOnlyList<DataCollectionTrial> collectionTrials = [];
    private double maxSelectedImpedanceKiloOhms = 500;
    private long windowIndex;
    private bool isRecognizing;
    private bool isSessionActive;
    private bool isStoppingSession;
    private bool collectionNeedsStart;
    private bool isCollectionCompletionScheduled;
    private bool isCollectionAbortScheduled;
    private bool isDisplayAbortScheduled;
    private bool isFullscreen;
    private bool isUpdatingChannelFilterControls;
    private int displayedFilterChannelCount;
    private EegSourceMode sourceMode = EegSourceMode.Simulated;
    private AppRunMode runMode = AppRunMode.Typing;
    private TypingFeedbackMode typingFeedbackMode = TypingFeedbackMode.TrueFeedback;
    private CollectionFeedbackMode collectionFeedbackMode = CollectionFeedbackMode.ShamFeedback;
    private string? activeCollectionFeedbackTargetId;
    private FeedbackSource? activeCollectionFeedbackSource;
    private DateTimeOffset lastDiagnosticsUiUpdate = DateTimeOffset.MinValue;
    private DateTimeOffset lastFrameStatsUiUpdate = DateTimeOffset.MinValue;
    private DateTimeOffset lastCollectionReadinessCheck = DateTimeOffset.MinValue;
    private DateTimeOffset lastPowerStatusCheck = DateTimeOffset.MinValue;
    private AppRunMode? activeRunMode;
    private EegSourceMode activeSourceMode;
    private CancellationTokenSource? sessionCancellationTokenSource;
    private Task? collectionFeedbackTask;
    private PendingCollectionFeedback? pendingCollectionFeedback;
    private long activeSessionId;
    private long collectionFirstLoggedSampleIndex = -1;
    private double expectedRefreshRateHz = 60;

    public MainWindow()
    {
        targets = StimulusLayout.CreateDefaultQwertyTargets();
        classifier = new FbccaClassifier(harmonicCount: 3);
        decisionEngine = new HybridDecisionEngine(decisionSettings);
        composer = new TextComposer();
        recognitionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        recognitionTimer.Tick += RecognitionTimer_Tick;
        readinessTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        readinessTimer.Tick += ReadinessTimer_Tick;

        InitializeComponent();

        StimulusCanvas.Targets = targets;
        StimulusCanvas.StimulusMode = StimulusStyle.Checkerboard;
        StimulusCanvas.FrameRendering += StimulusCanvas_FrameRendering;
        StimulusCanvas.FramePrepared += StimulusCanvas_FramePrepared;
        FocusComboBox.ItemsSource = targets;
        FocusComboBox.SelectedItem = targets.First(target => target.Id == "H");
        TypingShamTargetComboBox.ItemsSource = targets;
        TypingShamTargetComboBox.SelectedItem = targets.First(target => target.Id == "H");
        SourceComboBox.SelectedIndex = 0;
        RunModeComboBox.SelectedIndex = 0;
        TypingFeedbackModeComboBox.SelectedIndex = 0;
        CollectionFeedbackModeComboBox.SelectedIndex = 1;
        ConsistentWindowsComboBox.SelectedIndex = decisionSettings.RequiredConsistentWindows - 1;
        InitializeCollectionTargetControls();
        UpdateThresholdText();
        UpdateRunModeUi();
        UpdateFeedbackModeUi();
        ApplySourceModeUi();
        EnsureChannelFilterControls(inputChannelCount: 8);
        UpdateImpedancePanel(Array.Empty<ChannelImpedance>());
        ShowSettingsPage();
        FrequencyTextBlock.Text = $"{targets.Count} 个目标：{targets[0].FrequencyHz:0.0}-{targets[^1].FrequencyHz:0.0} Hz，步进 0.2 Hz";
        readinessTimer.Start();
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        await StartStimulusSessionAsync();
    }

    private Task StartStimulusSessionAsync()
    {
        if (runMode == AppRunMode.DataCollection)
        {
            return StartDataCollectionSessionAsync();
        }

        if (isSessionActive)
        {
            return Task.CompletedTask;
        }

        StimulusReadinessResult readiness = EvaluateCurrentStimulusReadiness();
        if (!readiness.CanCalculate)
        {
            StatusTextBlock.Text = $"无法开始刺激：{readiness.Message}";
            UpdateStimulusReadinessUi();
            return Task.CompletedTask;
        }

        decisionEngine = new HybridDecisionEngine(decisionSettings);
        windowIndex = 0;
        string logDirectory = System.IO.Path.Combine(AppContext.BaseDirectory, "logs");
        string style = $"{StimulusCanvas.StimulusMode.ToString().ToLowerInvariant()}-{sourceMode.ToString().ToLowerInvariant()}";
        sessionLogWriter = SessionLogWriter.Start(logDirectory, targets, style, TranslateTypingFeedbackMode(typingFeedbackMode));
        BeginSession(AppRunMode.Typing);
        recognitionTimer.Start();
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        StatusTextBlock.Text = sourceMode == EegSourceMode.Simulated
            ? $"正在运行模拟闭环（{TranslateTypingFeedbackMode(typingFeedbackMode)}）"
            : $"正在运行 Synchroni 刺激与分类（{TranslateTypingFeedbackMode(typingFeedbackMode)}）";
        UpdateStimulusReadinessUi();
        return Task.CompletedTask;
    }

    private Task StartDataCollectionSessionAsync()
    {
        if (isSessionActive)
        {
            return Task.CompletedTask;
        }

        StimulusReadinessResult readiness = EvaluateCurrentStimulusReadiness();
        if (!readiness.CanCalculate)
        {
            StatusTextBlock.Text = $"无法开始采集：{readiness.Message}";
            UpdateStimulusReadinessUi();
            return Task.CompletedTask;
        }

        DataCollectionSettings settings;
        try
        {
            settings = BuildDataCollectionSettingsFromControls();
            collectionTrials = DataCollectionScheduler.Generate(targets, settings);
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"采集配置错误：{ex.Message}";
            return Task.CompletedTask;
        }

        string logDirectory = System.IO.Path.Combine(AppContext.BaseDirectory, "logs");
        string style = $"{StimulusCanvas.StimulusMode.ToString().ToLowerInvariant()}-{sourceMode.ToString().ToLowerInvariant()}-collection";
        collectionLogWriter = DataCollectionLogWriter.Start(
            logDirectory,
            targets,
            settings,
            collectionTrials,
            style,
            CreateCollectionAcquisitionInfo());
        collectionController = new DataCollectionController(collectionTrials, settings);
        activeCollectionFeedbackTargetId = null;
        activeCollectionFeedbackSource = null;
        pendingCollectionFeedback = null;
        collectionNeedsStart = true;
        Interlocked.Exchange(ref collectionFirstLoggedSampleIndex, -1);
        BeginSession(AppRunMode.DataCollection);
        UpdateCollectionVisualState();
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        StatusTextBlock.Text = $"正在采集数据：0 / {collectionTrials.Count}";
        UpdateStimulusReadinessUi();
        return Task.CompletedTask;
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        await StopStimulusSessionAsync();
    }

    private async void SearchDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sourceMode != EegSourceMode.SynchroniSdk || sdkStream is not null)
        {
            return;
        }

        SearchDeviceButton.IsEnabled = false;
        ConnectDeviceButton.IsEnabled = false;
        DeviceComboBox.ItemsSource = null;
        StatusTextBlock.Text = "正在搜索 Synchroni 设备...";
        DiagnosticMessageTextBlock.Text = "消息：正在搜索附近的 Synchroni 设备...";

        try
        {
            var scanner = new SynchroniBridgeDeviceScanner(SynchroniBridgeOptions.CreateDefault());
            discoveredDevices = await scanner.ScanAsync(CancellationToken.None);
            DeviceComboBox.ItemsSource = discoveredDevices;
            DeviceComboBox.SelectedIndex = discoveredDevices.Count > 0 ? 0 : -1;
            StatusTextBlock.Text = discoveredDevices.Count > 0
                ? $"已找到 {discoveredDevices.Count} 台设备"
                : "未找到设备";
            DiagnosticMessageTextBlock.Text = discoveredDevices.Count > 0
                ? $"消息：已找到 {discoveredDevices.Count} 台设备，请选择后连接"
                : "消息：未找到 Synchroni 设备，请确认设备已开机并靠近电脑";
        }
        catch (Exception ex)
        {
            discoveredDevices = Array.Empty<SynchroniDiscoveredDevice>();
            DeviceComboBox.ItemsSource = discoveredDevices;
            DeviceComboBox.SelectedIndex = -1;
            StatusTextBlock.Text = $"搜索设备失败：{ex.Message}";
            DiagnosticMessageTextBlock.Text = $"消息：搜索设备失败：{ex.Message}";
        }
        finally
        {
            SearchDeviceButton.IsEnabled = sourceMode == EegSourceMode.SynchroniSdk && sdkStream is null;
            UpdateDeviceConnectionButtons();
        }
    }

    private async void ConnectDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sourceMode != EegSourceMode.SynchroniSdk)
        {
            StatusTextBlock.Text = "模拟脑电已就绪";
            UpdateStimulusReadinessUi();
            return;
        }

        if (DeviceComboBox.SelectedItem is not SynchroniDiscoveredDevice)
        {
            StatusTextBlock.Text = "请先搜索并选择设备";
            DiagnosticMessageTextBlock.Text = "消息：请先搜索并选择设备";
            UpdateDeviceConnectionButtons();
            return;
        }

        await StartSdkStreamAsync();
    }

    private async void DisconnectDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        if (isSessionActive)
        {
            await StopStimulusSessionAsync();
        }

        await StopSdkStreamAsync();
    }

    private void StyleButton_Click(object sender, RoutedEventArgs e)
    {
        StimulusCanvas.StimulusMode = StimulusCanvas.StimulusMode == StimulusStyle.Checkerboard
            ? StimulusStyle.Solid
            : StimulusStyle.Checkerboard;
        StyleButton.Content = StimulusCanvas.StimulusMode == StimulusStyle.Checkerboard ? "棋盘格" : "纯色";
    }

    private void FullscreenButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleFullscreen();
    }

    private async void SourceComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        EegSourceMode nextMode = SourceComboBox.SelectedIndex == 1 ? EegSourceMode.SynchroniSdk : EegSourceMode.Simulated;
        if (isSessionActive)
        {
            await StopStimulusSessionAsync();
        }

        if (sourceMode == EegSourceMode.SynchroniSdk && nextMode != EegSourceMode.SynchroniSdk)
        {
            await StopSdkStreamAsync();
        }

        sourceMode = nextMode;
        ApplySourceModeUi();
    }

    private void ApplySourceModeUi()
    {
        FocusComboBox.IsEnabled = sourceMode == EegSourceMode.Simulated;
        UpdateDeviceConnectionButtons();
        ClassifierTextBlock.Text = sourceMode == EegSourceMode.Simulated
            ? "FBCCA，模拟 8 通道 250 Hz，2.5 秒窗口，0.25 秒步长"
            : "FBCCA，Synchroni SDK，2.5 秒窗口，0.25 秒步长";
        if (sourceMode == EegSourceMode.SynchroniSdk && synchroniBridgeStream is not null)
        {
            UpdateDiagnosticsPanel(synchroniBridgeStream.DiagnosticsSnapshot);
        }
        else
        {
            ResetDiagnosticsPanel();
            UpdateImpedancePanel(Array.Empty<ChannelImpedance>());
        }

        UpdateStimulusReadinessUi();
    }

    private void RunModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        runMode = RunModeComboBox.SelectedIndex == 1 ? AppRunMode.DataCollection : AppRunMode.Typing;
        UpdateRunModeUi();
    }

    private void TypingFeedbackModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        typingFeedbackMode = TypingFeedbackModeComboBox.SelectedIndex == 1
            ? TypingFeedbackMode.ShamFeedback
            : TypingFeedbackMode.TrueFeedback;
        decisionEngine = new HybridDecisionEngine(decisionSettings);
        UpdateFeedbackModeUi();
    }

    private void CollectionFeedbackModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        collectionFeedbackMode = CollectionFeedbackModeComboBox.SelectedIndex switch
        {
            0 => CollectionFeedbackMode.TrueFeedback,
            2 => CollectionFeedbackMode.Mixed,
            _ => CollectionFeedbackMode.ShamFeedback
        };
        UpdateFeedbackModeUi();
    }

    private void CollectionDefaultTargetsButton_Click(object sender, RoutedEventArgs e)
    {
        SetCollectionTargetSelection(StimulusLayout.DefaultCollectionTargetIds);
    }

    private void CollectionAllTargetsButton_Click(object sender, RoutedEventArgs e)
    {
        SetCollectionTargetSelection(targets.Select(target => target.Id));
    }

    private void InitializeCollectionTargetControls()
    {
        CollectionTargetPanel.Children.Clear();
        HashSet<string> defaultTargets = StimulusLayout.DefaultCollectionTargetIds.ToHashSet(StringComparer.Ordinal);
        foreach (StimulusTarget target in targets.OrderBy(target => target.Id, StringComparer.Ordinal))
        {
            var checkBox = new CheckBox
            {
                Content = $"{target.Id} {target.FrequencyHz:0.0}Hz",
                Tag = target.Id,
                IsChecked = defaultTargets.Contains(target.Id),
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 12, 6),
                MinWidth = 88
            };
            checkBox.Checked += CollectionTargetCheckBox_Changed;
            checkBox.Unchecked += CollectionTargetCheckBox_Changed;
            CollectionTargetPanel.Children.Add(checkBox);
        }

        UpdateCollectionTargetStatus();
    }

    private void CollectionTargetCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        UpdateCollectionTargetStatus();
    }

    private void SetCollectionTargetSelection(IEnumerable<string> targetIds)
    {
        HashSet<string> selectedIds = targetIds.ToHashSet(StringComparer.Ordinal);
        foreach (CheckBox checkBox in CollectionTargetPanel.Children.OfType<CheckBox>())
        {
            checkBox.IsChecked = checkBox.Tag is string id && selectedIds.Contains(id);
        }

        UpdateCollectionTargetStatus();
    }

    private void UpdateCollectionTargetStatus()
    {
        if (CollectionTargetStatusTextBlock is null || CollectionTargetPanel is null)
        {
            return;
        }

        string[] selected = ReadSelectedCollectionTargetIds();
        CollectionTargetStatusTextBlock.Text = selected.Length == 0
            ? "采集目标：未选择，将使用默认 8 目标"
            : $"采集目标：{selected.Length} 个（每目标重复 {ReadPositiveInt(CollectionRepeatsTextBox, 5)} 次）";
    }

    private void UpdateRunModeUi()
    {
        if (StartButton is null || StopButton is null || CollectionStatusTextBlock is null)
        {
            return;
        }

        StartButton.Content = runMode == AppRunMode.DataCollection ? "开始采集" : "开始刺激";
        StopButton.Content = runMode == AppRunMode.DataCollection ? "停止采集" : "停止刺激";
        CollectionStatusTextBlock.Visibility = runMode == AppRunMode.DataCollection
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (runMode == AppRunMode.DataCollection)
        {
            CollectionStatusTextBlock.Text = "采集：未开始";
        }
    }

    private void UpdateFeedbackModeUi()
    {
        if (TypingShamTargetComboBox is null ||
            CollectionShamTrialRatioTextBox is null ||
            CollectionShamPositiveRatioTextBox is null ||
            FeedbackModeStatusTextBlock is null)
        {
            return;
        }

        bool configurationEnabled = !isSessionActive;
        TypingShamTargetComboBox.IsEnabled = configurationEnabled && typingFeedbackMode == TypingFeedbackMode.ShamFeedback;
        CollectionShamTrialRatioTextBox.IsEnabled = configurationEnabled && collectionFeedbackMode == CollectionFeedbackMode.Mixed;
        CollectionShamPositiveRatioTextBox.IsEnabled = configurationEnabled && collectionFeedbackMode != CollectionFeedbackMode.TrueFeedback;
        FeedbackModeStatusTextBlock.Text =
            $"反馈：打字{TranslateTypingFeedbackMode(typingFeedbackMode)}；采集{TranslateCollectionFeedbackMode(collectionFeedbackMode)}";
    }

    private DataCollectionSettings BuildDataCollectionSettingsFromControls()
    {
        string[] selectedTargetIds = ReadSelectedCollectionTargetIds();
        if (selectedTargetIds.Length == 0)
        {
            selectedTargetIds = StimulusLayout.DefaultCollectionTargetIds.ToArray();
        }

        double shamPositiveRatio = Math.Clamp(ReadDouble(CollectionShamPositiveRatioTextBox, 0.5), 0.0, 1.0);
        double shamTrialRatio = collectionFeedbackMode switch
        {
            CollectionFeedbackMode.TrueFeedback => 0.0,
            CollectionFeedbackMode.ShamFeedback => 1.0,
            _ => Math.Clamp(ReadDouble(CollectionShamTrialRatioTextBox, 0.5), 0.0, 1.0)
        };

        return new DataCollectionSettings(
            selectedTargetIds,
            ReadPositiveInt(CollectionRepeatsTextBox, 5),
            ReadInt(CollectionSeedTextBox, 20260623),
            TimeSpan.FromSeconds(ReadPositiveDouble(CollectionRestTextBox, 1.0)),
            TimeSpan.FromSeconds(ReadPositiveDouble(CollectionCueTextBox, 0.8)),
            TimeSpan.FromSeconds(ReadPositiveDouble(CollectionStimulusTextBox, 2.5)),
            TimeSpan.FromSeconds(ReadPositiveDouble(CollectionFeedbackTextBox, 0.6)),
            TimeSpan.FromSeconds(ReadPositiveDouble(CollectionInterTrialTextBox, 0.8)),
            shamPositiveRatio,
            collectionFeedbackMode,
            shamTrialRatio);
    }

    private CollectionAcquisitionInfo CreateCollectionAcquisitionInfo()
    {
        SynchroniBridgeOptions? options = activeSynchroniOptions;
        return new CollectionAcquisitionInfo(
            SourceMode: sourceMode.ToString(),
            DeviceName: sourceMode == EegSourceMode.SynchroniSdk
                ? latestDiagnosticsSnapshot.DeviceName
                : "Simulated EEG",
            DeviceAddress: sourceMode == EegSourceMode.SynchroniSdk
                ? latestDiagnosticsSnapshot.DeviceAddress
                : string.Empty,
            SamplingRateHz: sourceMode == EegSourceMode.SynchroniSdk
                ? latestDiagnosticsSnapshot.SamplingRateHz
                : SamplingRateHz,
            InputChannelCount: sourceMode == EegSourceMode.SynchroniSdk
                ? latestDiagnosticsSnapshot.ChannelCount
                : 8,
            SelectedChannelIndices: sourceMode == EegSourceMode.SynchroniSdk
                ? GetSelectedChannelIndices()
                : Enumerable.Range(0, 8).ToArray(),
            ImpedanceThresholdKiloOhms: maxSelectedImpedanceKiloOhms,
            Impedance: latestImpedance.ToArray(),
            Enable50HzNotch: options?.Enable50HzNotch ?? false,
            EnableHighPass: options?.EnableHighPass ?? false,
            EnableLowPass: options?.EnableLowPass ?? false);
    }

    private string[] ReadSelectedCollectionTargetIds()
    {
        return CollectionTargetPanel.Children
            .OfType<CheckBox>()
            .Where(checkBox => checkBox.IsChecked == true && checkBox.Tag is string)
            .Select(checkBox => (string)checkBox.Tag)
            .ToArray();
    }

    private static int ReadPositiveInt(TextBox textBox, int fallback)
    {
        int value = ReadInt(textBox, fallback);
        return value > 0 ? value : fallback;
    }

    private static int ReadInt(TextBox textBox, int fallback)
    {
        return int.TryParse(textBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;
    }

    private static double ReadPositiveDouble(TextBox textBox, double fallback)
    {
        double value = ReadDouble(textBox, fallback);
        return value > 0 ? value : fallback;
    }

    private static double ReadDouble(TextBox textBox, double fallback)
    {
        return double.TryParse(textBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : fallback;
    }

    private void UpdateCollectionVisualState()
    {
        if (collectionController is null)
        {
            return;
        }

        DataCollectionTrial? trial = collectionController.CurrentTrial;
        CollectionPhase phase = collectionController.CurrentPhase;
        StimulusCanvas.CollectionPhase = phase;
        StimulusCanvas.CueTargetId = phase is CollectionPhase.Cue or CollectionPhase.Stimulus
            ? trial?.TargetId
            : null;
        StimulusCanvas.FeedbackTargetId = phase == CollectionPhase.Feedback
            ? activeCollectionFeedbackTargetId ?? trial?.FeedbackTargetId
            : null;

        int currentTrialNumber = trial is null
            ? collectionTrials.Count
            : Math.Min(collectionController.CurrentTrialIndex + 1, collectionTrials.Count);
        CollectionStatusTextBlock.Text = trial is null
            ? $"采集：{phase}"
            : $"采集：{currentTrialNumber}/{collectionTrials.Count}，阶段 {TranslateCollectionPhase(phase)}，目标 {trial.TargetId}，" +
              $"反馈 {TranslateFeedbackSource(activeCollectionFeedbackSource ?? trial.FeedbackSource)} " +
              $"{activeCollectionFeedbackTargetId ?? trial.FeedbackTargetId}";
    }

    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && isFullscreen)
        {
            ToggleFullscreen();
        }

        if (e.Key == Key.F5)
        {
            if (isSessionActive)
            {
                await StopStimulusSessionAsync();
            }
            else
            {
                await StartStimulusSessionAsync();
            }
        }
    }

    private async void RecognitionTimer_Tick(object? sender, EventArgs e)
    {
        if (isRecognizing ||
            activeRunMode != AppRunMode.Typing ||
            FocusComboBox.SelectedItem is not StimulusTarget target)
        {
            return;
        }

        long sessionId = activeSessionId;
        CancellationToken cancellationToken = sessionCancellationTokenSource?.Token ?? CancellationToken.None;
        isRecognizing = true;
        try
        {
            ClassificationResult? classification;
            if (activeSourceMode == EegSourceMode.Simulated)
            {
                sessionLogWriter?.RecordGroundTruth(target.Id, DateTimeOffset.UtcNow);
                classification = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    double[,] window = SyntheticSsvepSignal.GenerateWindow(
                        target.FrequencyHz,
                        SamplingRateHz,
                        WindowSamples,
                        channelCount: 8,
                        noiseAmplitude: 0.15,
                        seed: unchecked((int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + windowIndex)));
                    return classifier.Classify(window, targets, SamplingRateHz);
                }, cancellationToken);
            }
            else
            {
                StimulusReadinessResult readiness = EvaluateCurrentStimulusReadiness();
                if (!readiness.CanCalculate)
                {
                    await AbortSessionAsync(sessionId, $"刺激已中止：{readiness.Message}");
                    return;
                }

                if (sdkRingBuffer is null ||
                    !sdkRingBuffer.TryReadLatestWindowSnapshot(WindowSamples, out EegWindowSnapshot? snapshot) ||
                    snapshot is null)
                {
                    StatusTextBlock.Text = "正在等待足够的 Synchroni 脑电样本...";
                    UpdateBufferDiagnostics();
                    return;
                }

                if (!snapshot.IsUsable)
                {
                    StatusTextBlock.Text = "当前脑电窗口包含丢包或样本断号，已跳过本次计算。";
                    return;
                }

                int sdkRate = sdkStream?.SamplingRateHz ?? SamplingRateHz;
                classification = await Task.Run(
                    () => classifier.Classify(snapshot.Samples, targets, sdkRate),
                    cancellationToken);
            }

            if (classification is null || !IsCurrentSession(sessionId, AppRunMode.Typing))
            {
                return;
            }

            sessionLogWriter?.RecordClassification(classification);
            StimulusTarget shamTarget = GetTypingShamTarget();
            TypingFeedbackDecisionInput feedbackInput = TypingFeedbackRouter.Resolve(
                typingFeedbackMode,
                classification,
                shamTarget.Id,
                targets.Select(t => t.Id).ToArray());
            DecisionResult? decision = decisionEngine.Update(
                feedbackInput.Classification,
                windowIndex++,
                feedbackInput.Source);
            StatusTextBlock.Text =
                $"真实最佳 {classification.BestTargetId}，得分 {classification.BestScore:0.000}，分差 {classification.Margin:0.000}；" +
                $"反馈 {TranslateFeedbackSource(feedbackInput.Source)} {feedbackInput.FeedbackTargetId}";

            if (decision is null)
            {
                return;
            }

            sessionLogWriter?.RecordDecision(decision);
            StimulusTarget committed = targets.First(t => t.Id == decision.TargetId);
            composer.Apply(committed.Action);
            OutputTextBox.Text = composer.Text;
            OutputTextBox.CaretIndex = OutputTextBox.Text.Length;
            OutputTextBox.ScrollToEnd();
            sessionLogWriter?.RecordText(composer.Text);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (IsCurrentSession(sessionId, AppRunMode.Typing))
            {
                UpdateFrameStatsText();
            }

            isRecognizing = false;
        }
    }

    private void StimulusCanvas_FrameRendering(object? sender, StimulusFrameRenderingEventArgs e)
    {
        if (!isSessionActive)
        {
            return;
        }

        if (e.HasTimingInterruption && !isDisplayAbortScheduled)
        {
            isDisplayAbortScheduled = true;
            StopStimulusPresentationImmediately();
            _ = AbortSessionAsync(
                activeSessionId,
                "刺激已中止：检测到超过 250 ms 的显示中断，请保持窗口前台显示后重新开始。");
            return;
        }

        if (e.Timestamp - lastPowerStatusCheck >= TimeSpan.FromMilliseconds(500))
        {
            lastPowerStatusCheck = e.Timestamp;
            if (!PowerStatusProvider.IsOnAcPower() && !isDisplayAbortScheduled)
            {
                isDisplayAbortScheduled = true;
                StopStimulusPresentationImmediately();
                _ = AbortSessionAsync(
                    activeSessionId,
                    "刺激已中止：电脑已切换为电池供电，显示刷新稳定性可能下降。");
                return;
            }
        }

        if (e.Timestamp - lastFrameStatsUiUpdate >= TimeSpan.FromMilliseconds(500))
        {
            lastFrameStatsUiUpdate = e.Timestamp;
            UpdateFrameStatsText();
        }

        if (activeRunMode != AppRunMode.DataCollection || collectionController is null)
        {
            return;
        }

        if (activeSourceMode == EegSourceMode.SynchroniSdk &&
            e.Timestamp - lastCollectionReadinessCheck >= TimeSpan.FromMilliseconds(100))
        {
            lastCollectionReadinessCheck = e.Timestamp;
            StimulusReadinessResult readiness = EvaluateCurrentStimulusReadiness();
            if (!readiness.CanCalculate && !isCollectionAbortScheduled)
            {
                isCollectionAbortScheduled = true;
                StimulusCanvas.CollectionPhase = CollectionPhase.Idle;
                StimulusCanvas.CueTargetId = null;
                StimulusCanvas.FeedbackTargetId = null;
                StimulusCanvas.EndSession();
                _ = AbortSessionAsync(activeSessionId, $"数据采集已中止：{readiness.Message}");
                return;
            }
        }

        if (ApplyPendingCollectionFeedback(e))
        {
            return;
        }

        long? sampleIndex = GetCollectionMarkerSampleIndex(e.Timestamp);
        CollectionMarker? marker;
        if (collectionNeedsStart)
        {
            marker = collectionController.Start(
                e.RenderingTime,
                e.Timestamp,
                sampleIndex,
                e.FrameIndex);
            collectionNeedsStart = false;
        }
        else
        {
            marker = collectionController.Advance(
                e.RenderingTime,
                e.Timestamp,
                sampleIndex,
                e.FrameIndex);
        }

        if (marker is null)
        {
            return;
        }

        if (marker.Phase == CollectionPhase.Feedback &&
            collectionController.CurrentTrial is DataCollectionTrial feedbackTrial)
        {
            if (feedbackTrial.FeedbackSource == FeedbackSource.ShamPreset)
            {
                activeCollectionFeedbackSource = FeedbackSource.ShamPreset;
                activeCollectionFeedbackTargetId = feedbackTrial.FeedbackTargetId;
                marker = marker with
                {
                    FeedbackSource = FeedbackSource.ShamPreset,
                    FeedbackTargetId = feedbackTrial.FeedbackTargetId,
                    FeedbackMatchesCue = feedbackTrial.FeedbackTargetId == feedbackTrial.TargetId
                };
            }
            else
            {
                activeCollectionFeedbackSource = FeedbackSource.TrueClassifier;
                activeCollectionFeedbackTargetId = null;
                marker = marker with
                {
                    FeedbackSource = FeedbackSource.TrueClassifier,
                    FeedbackTargetId = null,
                    FeedbackMatchesCue = null
                };
                StartCollectionFeedbackClassification(feedbackTrial);
            }
        }
        else if (marker.Phase is CollectionPhase.Rest or CollectionPhase.Cue or CollectionPhase.Stimulus)
        {
            activeCollectionFeedbackTargetId = null;
            activeCollectionFeedbackSource = null;
            pendingCollectionFeedback = null;
        }

        collectionLogWriter?.RecordMarker(marker);
        UpdateCollectionVisualState();

        if (collectionController.IsComplete && !isCollectionCompletionScheduled)
        {
            isCollectionCompletionScheduled = true;
            _ = CompleteCollectionSessionAsync(activeSessionId);
        }
    }

    private void StimulusCanvas_FramePrepared(object? sender, StimulusFramePreparedEventArgs e)
    {
        if (!isSessionActive ||
            activeRunMode != AppRunMode.DataCollection ||
            collectionController is null ||
            collectionLogWriter is null)
        {
            return;
        }

        DataCollectionTrial? trial = collectionController.CurrentTrial;
        collectionLogWriter.RecordFrame(new StimulusFrameRecord(
            e.Timestamp,
            e.FrameIndex,
            e.RenderingTime.Ticks,
            e.FrameIntervalMilliseconds,
            trial?.TrialIndex ?? -1,
            collectionController.CurrentPhase,
            trial?.TargetId ?? string.Empty,
            e.StimulusElapsedSeconds,
            e.IsFlickering,
            e.PhotodiodePatchIsBright));
    }

    private void StartCollectionFeedbackClassification(DataCollectionTrial trial)
    {
        CancellationToken token = sessionCancellationTokenSource?.Token ?? CancellationToken.None;
        long sessionId = activeSessionId;
        Task<ClassificationResult?> classificationTask = ClassifyCollectionFeedbackAsync(trial, token);
        collectionFeedbackTask = classificationTask;
        _ = CaptureCollectionFeedbackAsync(classificationTask, trial, sessionId, token);
    }

    private async Task CaptureCollectionFeedbackAsync(
        Task<ClassificationResult?> classificationTask,
        DataCollectionTrial trial,
        long sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            ClassificationResult? classification = await classificationTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await Dispatcher.InvokeAsync(() =>
            {
                if (IsCurrentSession(sessionId, AppRunMode.DataCollection))
                {
                    pendingCollectionFeedback = new PendingCollectionFeedback(trial, classification);
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool ApplyPendingCollectionFeedback(StimulusFrameRenderingEventArgs e)
    {
        PendingCollectionFeedback? pending = pendingCollectionFeedback;
        if (pending is null ||
            collectionController?.CurrentPhase != CollectionPhase.Feedback ||
            collectionController.CurrentTrialIndex != pending.Trial.TrialIndex)
        {
            return false;
        }

        pendingCollectionFeedback = null;
        ClassificationResult? classification = pending.Classification;
        activeCollectionFeedbackSource = FeedbackSource.TrueClassifier;
        activeCollectionFeedbackTargetId = classification?.BestTargetId;
        if (classification is not null)
        {
            collectionLogWriter?.RecordClassification(
                pending.Trial.TargetId,
                FeedbackSource.TrueClassifier,
                classification);
        }

        collectionLogWriter?.RecordMarker(new CollectionMarker(
            e.Timestamp,
            GetCollectionMarkerSampleIndex(e.Timestamp),
            pending.Trial.TrialIndex,
            CollectionPhase.Feedback,
            classification is null ? "FeedbackUnavailable" : "FeedbackTargetShown",
            pending.Trial.TargetId,
            FeedbackSource.TrueClassifier,
            classification?.BestTargetId,
            classification is null ? null : classification.BestTargetId == pending.Trial.TargetId,
            e.FrameIndex,
            e.RenderingTime.Ticks));
        collectionController.RestartCurrentPhase(e.RenderingTime);
        UpdateCollectionVisualState();
        return true;
    }

    private Task<ClassificationResult?> ClassifyCollectionFeedbackAsync(
        DataCollectionTrial trial,
        CancellationToken cancellationToken)
    {
        if (activeSourceMode == EegSourceMode.Simulated)
        {
            StimulusTarget? target = targets.FirstOrDefault(t => t.Id == trial.TargetId);
            if (target is null)
            {
                return Task.FromResult<ClassificationResult?>(null);
            }

            return Task.Run<ClassificationResult?>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                double[,] window = SyntheticSsvepSignal.GenerateWindow(
                    target.FrequencyHz,
                    SamplingRateHz,
                    WindowSamples,
                    channelCount: 8,
                    noiseAmplitude: 0.12,
                    seed: unchecked((int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + trial.TrialIndex)));
                return classifier.Classify(window, targets, SamplingRateHz);
            }, cancellationToken);
        }

        if (sdkRingBuffer is null ||
            !sdkRingBuffer.TryReadLatestWindowSnapshot(WindowSamples, out EegWindowSnapshot? snapshot) ||
            snapshot is null ||
            !snapshot.IsUsable)
        {
            return Task.FromResult<ClassificationResult?>(null);
        }

        int sdkRate = sdkStream?.SamplingRateHz ?? SamplingRateHz;
        return Task.Run<ClassificationResult?>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return classifier.Classify(snapshot.Samples, targets, sdkRate);
        }, cancellationToken);
    }

    private async Task CompleteCollectionSessionAsync(long sessionId)
    {
        if (!IsCurrentSession(sessionId, AppRunMode.DataCollection))
        {
            return;
        }

        await StopStimulusSessionAsync();
        StatusTextBlock.Text = "数据采集完成";
        CollectionStatusTextBlock.Text = $"采集完成：{collectionTrials.Count} / {collectionTrials.Count}";
    }

    private async Task AbortSessionAsync(long sessionId, string message)
    {
        if (!isSessionActive || activeSessionId != sessionId)
        {
            return;
        }

        await StopStimulusSessionAsync();
        StatusTextBlock.Text = message;
        CollectionStatusTextBlock.Text = message;
    }

    private async Task StopStimulusSessionAsync()
    {
        if (isStoppingSession)
        {
            return;
        }

        isStoppingSession = true;
        try
        {
        bool hadActiveSession = isSessionActive;
        FrameStats frameStats = StimulusCanvas.GetFrameStats();
        CancellationTokenSource? sessionCancellation = sessionCancellationTokenSource;
        isSessionActive = false;
        activeSessionId++;
        recognitionTimer.Stop();
        if (sessionCancellation is not null)
        {
            await sessionCancellation.CancelAsync();
        }

        StimulusCanvas.EndSession();
        StimulusCanvas.CueTargetId = null;
        StimulusCanvas.FeedbackTargetId = null;
        StimulusCanvas.CollectionPhase = CollectionPhase.Idle;
        activeCollectionFeedbackTargetId = null;
        activeCollectionFeedbackSource = null;
        pendingCollectionFeedback = null;
        collectionNeedsStart = false;
        isCollectionCompletionScheduled = false;
        isCollectionAbortScheduled = false;
        isDisplayAbortScheduled = false;
        StopButton.IsEnabled = false;
        if (hadActiveSession)
        {
            StatusTextBlock.Text = "刺激已停止";
        }

        if (collectionFeedbackTask is not null)
        {
            try
            {
                await collectionFeedbackTask;
            }
            catch (OperationCanceledException)
            {
            }

            collectionFeedbackTask = null;
        }

        if (sessionLogWriter is not null)
        {
            sessionLogWriter.RecordFrameStats(frameStats);
            await sessionLogWriter.DisposeAsync();
            sessionLogWriter = null;
        }

        if (collectionLogWriter is not null)
        {
            collectionLogWriter.RecordFrameStats(frameStats);
            await collectionLogWriter.DisposeAsync();
            collectionLogWriter = null;
        }

        }
        finally
        {
            collectionController = null;
            activeRunMode = null;
            sessionCancellationTokenSource?.Dispose();
            sessionCancellationTokenSource = null;
            SetConfigurationControlsEnabled(true);
            isStoppingSession = false;
            UpdateStimulusReadinessUi();
        }
    }

    private void BeginSession(AppRunMode mode)
    {
        sessionCancellationTokenSource?.Dispose();
        sessionCancellationTokenSource = new CancellationTokenSource();
        activeSessionId++;
        activeRunMode = mode;
        activeSourceMode = sourceMode;
        isSessionActive = true;
        isCollectionCompletionScheduled = false;
        isCollectionAbortScheduled = false;
        isDisplayAbortScheduled = false;
        lastCollectionReadinessCheck = DateTimeOffset.MinValue;
        lastPowerStatusCheck = DateTimeOffset.MinValue;
        lastFrameStatsUiUpdate = DateTimeOffset.MinValue;
        expectedRefreshRateHz = DisplayRefreshRateProvider.GetPrimaryRefreshRateHz();
        ShowStimulusPage();
        SetConfigurationControlsEnabled(false);
        StimulusCanvas.RunMode = mode;
        StimulusCanvas.CollectionPhase = CollectionPhase.Idle;
        StimulusCanvas.BeginSession(expectedRefreshRateHz);
    }

    private bool IsCurrentSession(long sessionId, AppRunMode expectedMode)
    {
        return isSessionActive &&
               activeSessionId == sessionId &&
               activeRunMode == expectedMode &&
               sessionCancellationTokenSource?.IsCancellationRequested == false;
    }

    private void SetConfigurationControlsEnabled(bool isEnabled)
    {
        SettingsNavButton.IsEnabled = isEnabled;
        SourceComboBox.IsEnabled = isEnabled;
        RunModeComboBox.IsEnabled = isEnabled;
        StyleButton.IsEnabled = isEnabled;
        TypingFeedbackModeComboBox.IsEnabled = isEnabled;
        TypingShamTargetComboBox.IsEnabled = isEnabled && typingFeedbackMode == TypingFeedbackMode.ShamFeedback;
        CollectionFeedbackModeComboBox.IsEnabled = isEnabled;
        CollectionRepeatsTextBox.IsEnabled = isEnabled;
        CollectionSeedTextBox.IsEnabled = isEnabled;
        CollectionRestTextBox.IsEnabled = isEnabled;
        CollectionCueTextBox.IsEnabled = isEnabled;
        CollectionStimulusTextBox.IsEnabled = isEnabled;
        CollectionFeedbackTextBox.IsEnabled = isEnabled;
        CollectionInterTrialTextBox.IsEnabled = isEnabled;
        CollectionTargetPanel.IsEnabled = isEnabled;
        CollectionShamTrialRatioTextBox.IsEnabled = isEnabled && collectionFeedbackMode == CollectionFeedbackMode.Mixed;
        CollectionShamPositiveRatioTextBox.IsEnabled = isEnabled && collectionFeedbackMode != CollectionFeedbackMode.TrueFeedback;
        ChannelFilterPanel.IsEnabled = isEnabled;
        ImpedanceThresholdSlider.IsEnabled = isEnabled;
        ScoreThresholdSlider.IsEnabled = isEnabled;
        MarginThresholdSlider.IsEnabled = isEnabled;
        ConsistentWindowsComboBox.IsEnabled = isEnabled;
        FocusComboBox.IsEnabled = isEnabled;
        UpdateDeviceConnectionButtons();
    }

    private void UpdateFrameStatsText()
    {
        FrameStats stats = StimulusCanvas.GetFrameStats();
        FrameStatsTextBlock.Text =
            $"刷新率：{stats.AverageFps:0.0}/{expectedRefreshRateHz:0} 帧/秒；" +
            $"漏帧：{stats.DroppedFrames}；中断：{stats.TimingInterruptions}；" +
            $"P95：{stats.P95FrameIntervalMs:0.0} ms";
    }

    private long? GetCollectionMarkerSampleIndex(DateTimeOffset markerTimestamp)
    {
        if (activeSourceMode != EegSourceMode.SynchroniSdk ||
            Interlocked.Read(ref collectionFirstLoggedSampleIndex) < 0 ||
            sdkRingBuffer is null ||
            !sdkRingBuffer.TryGetLatestSampleMetadata(out EegSampleMetadata metadata))
        {
            return null;
        }

        TimeSpan age = markerTimestamp - metadata.Timestamp;
        return age >= TimeSpan.Zero && age <= TimeSpan.FromMilliseconds(500)
            ? metadata.SampleIndex
            : null;
    }

    private void ToggleFullscreen()
    {
        isFullscreen = !isFullscreen;
        WindowStyle = isFullscreen ? WindowStyle.None : WindowStyle.SingleBorderWindow;
        WindowState = isFullscreen ? WindowState.Maximized : WindowState.Normal;
        ResizeMode = isFullscreen ? ResizeMode.NoResize : ResizeMode.CanResize;
        NavigationColumn.Width = isFullscreen ? new GridLength(0) : new GridLength(132);
        NavigationBorder.Visibility = isFullscreen ? Visibility.Collapsed : Visibility.Visible;
        StimulusPage.Margin = isFullscreen ? new Thickness(0) : new Thickness(10);
        StimulusToolbar.Visibility = isFullscreen ? Visibility.Collapsed : Visibility.Visible;
        StimulusStatusPanel.Visibility = isFullscreen ? Visibility.Collapsed : Visibility.Visible;
        StimulusCanvasBorder.BorderThickness = isFullscreen ? new Thickness(0) : new Thickness(1);
        StimulusCanvasBorder.Padding = isFullscreen ? new Thickness(0) : new Thickness(4);
        StimulusCanvasBorder.CornerRadius = isFullscreen ? new CornerRadius(0) : new CornerRadius(4);
    }

    protected override async void OnClosed(EventArgs e)
    {
        readinessTimer.Stop();
        await StopStimulusSessionAsync();
        await StopSdkStreamAsync();
        base.OnClosed(e);
    }

    private void ReadinessTimer_Tick(object? sender, EventArgs e)
    {
        if (!isSessionActive)
        {
            UpdateStimulusReadinessUi();
        }
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (!isSessionActive || WindowState != WindowState.Minimized || isDisplayAbortScheduled)
        {
            return;
        }

        isDisplayAbortScheduled = true;
        StopStimulusPresentationImmediately();
        _ = AbortSessionAsync(
            activeSessionId,
            "刺激已中止：窗口被最小化，当前会话的显示时序已失效。");
    }

    private async Task StartSdkStreamAsync()
    {
        if (sdkStream is not null)
        {
            UpdateStimulusReadinessUi();
            return;
        }

        sdkRingBuffer = new EegRingBuffer(channelCount: 8, capacitySamples: SamplingRateHz * 20);
        sdkCancellationTokenSource = new CancellationTokenSource();
        var selectedDevice = (SynchroniDiscoveredDevice?)DeviceComboBox.SelectedItem;
        SynchroniBridgeOptions options = SynchroniBridgeOptions.CreateDefault() with
        {
            DeviceAddress = selectedDevice?.Address
        };
        activeSynchroniOptions = options;
        synchroniBridgeStream = new SynchroniBridgeEegStream(options);
        synchroniBridgeStream.DiagnosticsUpdated += SynchroniBridgeStream_DiagnosticsUpdated;
        sdkStream = synchroniBridgeStream;
        latestDiagnosticsSnapshot = synchroniBridgeStream.DiagnosticsSnapshot;
        UpdateDeviceConnectionButtons();
        StatusTextBlock.Text = selectedDevice is null
            ? "正在连接 Synchroni 设备..."
            : $"正在连接 {selectedDevice.DisplayName}...";
        UpdateDiagnosticsPanel(latestDiagnosticsSnapshot);
        sdkReadTask = Task.Run(async () =>
        {
            try
            {
                await foreach (EegSample sample in sdkStream.ReadSamplesAsync(sdkCancellationTokenSource.Token))
                {
                    DataCollectionLogWriter? activeCollectionLogWriter = collectionLogWriter;
                    if (activeCollectionLogWriter is not null)
                    {
                        activeCollectionLogWriter.RecordSample(sample);
                        Interlocked.CompareExchange(
                            ref collectionFirstLoggedSampleIndex,
                            sample.SampleIndex,
                            -1);
                    }

                    ChannelMontage? montage = GetMontageForSample(sample.Channels.Length);
                    if (montage is null)
                    {
                        continue;
                    }

                    double[] projectedChannels = montage.Project(sample.Channels);
                    EegSample projectedSample = sample with { Channels = projectedChannels };
                    sdkRingBuffer?.Append(projectedSample);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() => StatusTextBlock.Text = $"Synchroni 桥接已停止：{ex.Message}");
            }
        });
        await Task.CompletedTask;
    }

    private async Task StopSdkStreamAsync()
    {
        if (sdkCancellationTokenSource is not null)
        {
            await sdkCancellationTokenSource.CancelAsync();
        }

        if (sdkReadTask is not null)
        {
            try
            {
                await sdkReadTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (sdkStream is not null)
        {
            await sdkStream.DisposeAsync();
        }

        if (synchroniBridgeStream is not null)
        {
            synchroniBridgeStream.DiagnosticsUpdated -= SynchroniBridgeStream_DiagnosticsUpdated;
        }

        sdkReadTask = null;
        sdkStream = null;
        synchroniBridgeStream = null;
        activeSynchroniOptions = null;
        sdkCancellationTokenSource?.Dispose();
        sdkCancellationTokenSource = null;
        sdkRingBuffer = null;
        latestDiagnosticsSnapshot = CreateEmptyDiagnosticsSnapshot();
        latestImpedance = Array.Empty<ChannelImpedance>();
        UpdateDeviceConnectionButtons();
        ResetDiagnosticsPanel();
        UpdateStimulusReadinessUi();
    }

    private async void SynchroniBridgeStream_DiagnosticsUpdated(object? sender, SynchroniBridgeDiagnosticsSnapshot snapshot)
    {
        latestDiagnosticsSnapshot = snapshot;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool isImportant = snapshot.LastMessageKind is
            SynchroniBridgeDiagnosticKind.Error or
            SynchroniBridgeDiagnosticKind.SensorError or
            SynchroniBridgeDiagnosticKind.ShutdownError or
            SynchroniBridgeDiagnosticKind.State;
        if (!isImportant && (now - lastDiagnosticsUiUpdate).TotalMilliseconds < 250)
        {
            return;
        }

        lastDiagnosticsUiUpdate = now;
        await Dispatcher.InvokeAsync(() => UpdateDiagnosticsPanel(snapshot));
    }

    private void ResetDiagnosticsPanel()
    {
        DiagnosticModeTextBlock.Text = sourceMode == EegSourceMode.Simulated ? "模式：模拟脑电" : "模式：Synchroni 设备";
        DiagnosticDeviceTextBlock.Text = "设备：-";
        DiagnosticRateTextBlock.Text = "脑电：250 Hz，8 通道";
        DiagnosticSamplesTextBlock.Text = "样本：0 / 丢包：0";
        DiagnosticBufferTextBlock.Text = $"缓冲：0 / {WindowSamples}";
        DiagnosticLastSampleTextBlock.Text = "最新样本：-";
        DiagnosticMessageTextBlock.Text = sourceMode == EegSourceMode.Simulated
            ? "消息：已选择模拟脑电"
            : "消息：请先搜索设备，然后选择设备并连接";
        latestImpedance = Array.Empty<ChannelImpedance>();
        latestDiagnosticsSnapshot = CreateEmptyDiagnosticsSnapshot();
    }

    private void UpdateDiagnosticsPanel(SynchroniBridgeDiagnosticsSnapshot snapshot)
    {
        latestDiagnosticsSnapshot = snapshot;
        latestImpedance = snapshot.Impedance;
        EnsureChannelFilterControls(snapshot.ChannelCount);
        DiagnosticModeTextBlock.Text = "模式：Synchroni 设备";
        DiagnosticDeviceTextBlock.Text = snapshot.HasMetadata
            ? $"设备：{snapshot.DeviceName} ({snapshot.DeviceAddress})"
            : "设备：搜索中 / 未连接";
        DiagnosticRateTextBlock.Text = $"脑电：{snapshot.SamplingRateHz} Hz，{snapshot.ChannelCount} 通道";
        DiagnosticSamplesTextBlock.Text = $"样本：{snapshot.SampleCount} / 丢包：{snapshot.LostSampleCount}";
        UpdateBufferDiagnostics();
        DiagnosticLastSampleTextBlock.Text = snapshot.LastSampleIndex is null
            ? "最新样本：-"
            : $"最新样本：#{snapshot.LastSampleIndex}，时间 {snapshot.LastSampleAt:HH:mm:ss.fff}";
        DiagnosticMessageTextBlock.Text = $"消息：[{TranslateDiagnosticKind(snapshot.LastMessageKind)}] {TranslateDiagnosticMessage(snapshot.LastMessage)}";
        UpdateImpedancePanel(snapshot.Impedance);
        UpdateStimulusReadinessUi();
    }

    private void UpdateBufferDiagnostics()
    {
        int buffered = sdkRingBuffer?.Count ?? 0;
        DiagnosticBufferTextBlock.Text = $"缓冲：{Math.Min(buffered, WindowSamples)} / {WindowSamples}";
        UpdateStimulusReadinessUi();
    }

    private ChannelMontage? GetMontageForSample(int inputChannelCount)
    {
        if (inputChannelCount < 8)
        {
            return null;
        }

        lock (montageGate)
        {
            if (sdkMontage.InputChannelCount == inputChannelCount)
            {
                return sdkMontage;
            }

            sdkMontage = ChannelMontage.CreateDefault(inputChannelCount, outputChannelCount: 8);
            return sdkMontage;
        }
    }

    private int[] GetSelectedChannelIndices()
    {
        lock (montageGate)
        {
            return sdkMontage.SelectedChannelIndices.ToArray();
        }
    }

    private void EnsureChannelFilterControls(int inputChannelCount)
    {
        if (inputChannelCount < 8 ||
            displayedFilterChannelCount == inputChannelCount &&
            ChannelFilterPanel.Children.Count == inputChannelCount)
        {
            return;
        }

        ChannelMontage montage;
        lock (montageGate)
        {
            if (sdkMontage.InputChannelCount != inputChannelCount)
            {
                sdkMontage = ChannelMontage.CreateDefault(inputChannelCount, outputChannelCount: 8);
            }

            montage = sdkMontage;
        }

        isUpdatingChannelFilterControls = true;
        displayedFilterChannelCount = inputChannelCount;
        ChannelFilterPanel.Children.Clear();
        var selected = montage.SelectedChannelIndices.ToHashSet();

        for (int channelIndex = 0; channelIndex < inputChannelCount; channelIndex++)
        {
            var checkBox = new CheckBox
            {
                Content = $"通道 {channelIndex + 1}",
                Tag = channelIndex,
                IsChecked = selected.Contains(channelIndex),
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 12, 6),
                MinWidth = 72
            };
            checkBox.Checked += ChannelFilterCheckBox_Changed;
            checkBox.Unchecked += ChannelFilterCheckBox_Changed;
            ChannelFilterPanel.Children.Add(checkBox);
        }

        isUpdatingChannelFilterControls = false;
        UpdateChannelFilterStatus(montage.SelectedChannelIndices, isValid: true);
    }

    private void ChannelFilterCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (isUpdatingChannelFilterControls)
        {
            return;
        }

        int[] selected = ReadCheckedChannelIndices();
        if (selected.Length > 8 && sender is CheckBox checkBox)
        {
            isUpdatingChannelFilterControls = true;
            checkBox.IsChecked = false;
            isUpdatingChannelFilterControls = false;
            selected = ReadCheckedChannelIndices();
        }

        if (selected.Length == 8)
        {
            var montage = new ChannelMontage(selected, displayedFilterChannelCount);
            lock (montageGate)
            {
                sdkMontage = montage;
            }

            ResetSdkBufferAfterChannelFilterChange();
            UpdateChannelFilterStatus(selected, isValid: true);
            UpdateImpedancePanel(latestImpedance);
            UpdateStimulusReadinessUi();
            return;
        }

        UpdateChannelFilterStatus(selected, isValid: false);
        UpdateImpedancePanel(latestImpedance);
        UpdateStimulusReadinessUi();
    }

    private int[] ReadCheckedChannelIndices()
    {
        return ChannelFilterPanel.Children
            .OfType<CheckBox>()
            .Where(checkBox => checkBox.IsChecked == true)
            .Select(checkBox => (int)checkBox.Tag)
            .OrderBy(index => index)
            .ToArray();
    }

    private void ResetSdkBufferAfterChannelFilterChange()
    {
        if (sdkStream is null)
        {
            return;
        }

        sdkRingBuffer = new EegRingBuffer(channelCount: 8, capacitySamples: SamplingRateHz * 20);
        StatusTextBlock.Text = "通道筛选已更新，正在重新积累缓冲样本。";
        UpdateBufferDiagnostics();
    }

    private void UpdateChannelFilterStatus(IReadOnlyList<int> selectedIndices, bool isValid)
    {
        string selectedText = selectedIndices.Count == 0
            ? "-"
            : string.Join("、", selectedIndices.Select(index => (index + 1).ToString()));
        ChannelFilterStatusTextBlock.Text = isValid
            ? $"已选择 {selectedIndices.Count}/8：通道 {selectedText}"
            : $"已勾选 {selectedIndices.Count}/8；将继续使用上一组有效通道";
        ChannelFilterStatusTextBlock.Foreground = isValid
            ? new SolidColorBrush(Color.FromRgb(167, 243, 208))
            : new SolidColorBrush(Color.FromRgb(253, 224, 71));
    }

    private void DeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateDeviceConnectionButtons();
    }

    private void UpdateDeviceConnectionButtons()
    {
        if (SearchDeviceButton is null ||
            DeviceComboBox is null ||
            ConnectDeviceButton is null ||
            DisconnectDeviceButton is null)
        {
            return;
        }

        bool isSynchroniMode = sourceMode == EegSourceMode.SynchroniSdk;
        bool isConnected = sdkStream is not null;
        bool canConfigure = !isSessionActive;
        SearchDeviceButton.IsEnabled = canConfigure && isSynchroniMode && !isConnected;
        DeviceComboBox.IsEnabled = canConfigure && isSynchroniMode && !isConnected;
        ConnectDeviceButton.IsEnabled = canConfigure && isSynchroniMode && !isConnected && DeviceComboBox.SelectedItem is SynchroniDiscoveredDevice;
        DisconnectDeviceButton.IsEnabled = canConfigure && isSynchroniMode && isConnected;
    }

    private void SettingsNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSettingsPage();
    }

    private void StimulusNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowStimulusPage();
    }

    private void ShowSettingsPage()
    {
        SettingsPage.Visibility = Visibility.Visible;
        StimulusPage.Visibility = Visibility.Collapsed;
        SettingsNavButton.Background = new SolidColorBrush(Color.FromRgb(30, 64, 175));
        StimulusNavButton.Background = new SolidColorBrush(Color.FromRgb(31, 41, 55));
    }

    private void ShowStimulusPage()
    {
        SettingsPage.Visibility = Visibility.Collapsed;
        StimulusPage.Visibility = Visibility.Visible;
        SettingsNavButton.Background = new SolidColorBrush(Color.FromRgb(31, 41, 55));
        StimulusNavButton.Background = new SolidColorBrush(Color.FromRgb(30, 64, 175));
        UpdateStimulusReadinessUi();
    }

    private void ImpedanceThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        maxSelectedImpedanceKiloOhms = Math.Round(e.NewValue);
        UpdateThresholdText();
        UpdateImpedancePanel(latestImpedance);
        UpdateStimulusReadinessUi();
    }

    private void ScoreThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateDecisionSettingsFromControls();
    }

    private void MarginThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateDecisionSettingsFromControls();
    }

    private void ConsistentWindowsComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateDecisionSettingsFromControls();
    }

    private void UpdateDecisionSettingsFromControls()
    {
        if (ScoreThresholdSlider is null ||
            MarginThresholdSlider is null ||
            ConsistentWindowsComboBox is null)
        {
            return;
        }

        int consistentWindows = ConsistentWindowsComboBox.SelectedIndex >= 0
            ? ConsistentWindowsComboBox.SelectedIndex + 1
            : decisionSettings.RequiredConsistentWindows;
        decisionSettings = decisionSettings with
        {
            RequiredConsistentWindows = consistentWindows,
            MinBestScore = Math.Round(ScoreThresholdSlider.Value, 2),
            MinScoreMargin = Math.Round(MarginThresholdSlider.Value, 2)
        };
        decisionEngine = new HybridDecisionEngine(decisionSettings);
        UpdateThresholdText();
    }

    private void UpdateThresholdText()
    {
        if (ImpedanceThresholdValueTextBlock is not null)
        {
            ImpedanceThresholdValueTextBlock.Text = $"{maxSelectedImpedanceKiloOhms:0} 千欧";
        }

        if (ScoreThresholdValueTextBlock is not null && ScoreThresholdSlider is not null)
        {
            ScoreThresholdValueTextBlock.Text = $"{ScoreThresholdSlider.Value:0.00}";
        }

        if (MarginThresholdValueTextBlock is not null && MarginThresholdSlider is not null)
        {
            MarginThresholdValueTextBlock.Text = $"{MarginThresholdSlider.Value:0.00}";
        }
    }

    private StimulusTarget GetTypingShamTarget()
    {
        return TypingShamTargetComboBox.SelectedItem as StimulusTarget
            ?? targets.First(target => target.Id == "H");
    }

    private static string TranslateTypingFeedbackMode(TypingFeedbackMode mode)
    {
        return mode switch
        {
            TypingFeedbackMode.ShamFeedback => "假反馈",
            _ => "真反馈"
        };
    }

    private static string TranslateCollectionFeedbackMode(CollectionFeedbackMode mode)
    {
        return mode switch
        {
            CollectionFeedbackMode.TrueFeedback => "真反馈",
            CollectionFeedbackMode.Mixed => "混合反馈",
            _ => "假反馈"
        };
    }

    private static string TranslateFeedbackSource(FeedbackSource source)
    {
        return source switch
        {
            FeedbackSource.ShamPreset => "假反馈",
            _ => "真反馈"
        };
    }

    private static string TranslateCollectionPhase(CollectionPhase phase)
    {
        return phase switch
        {
            CollectionPhase.Rest => "休息",
            CollectionPhase.Cue => "提示",
            CollectionPhase.Stimulus => "刺激",
            CollectionPhase.Feedback => "反馈",
            CollectionPhase.InterTrial => "间隔",
            CollectionPhase.Complete => "完成",
            _ => "空闲"
        };
    }

    private StimulusReadinessResult EvaluateCurrentStimulusReadiness()
    {
        bool isOnAcPower = PowerStatusProvider.IsOnAcPower();
        if (!isOnAcPower)
        {
            return new StimulusReadinessResult(
                CanCalculate: false,
                StimulusReadinessState.PowerSaving,
                "请连接电脑电源后再开始刺激，电池节能会降低显示刷新稳定性。");
        }

        if (sourceMode == EegSourceMode.Simulated)
        {
            return new StimulusReadinessResult(
                CanCalculate: true,
                StimulusReadinessState.Ready,
                "模拟脑电已就绪。");
        }

        bool? isLatestWindowUsable = sdkRingBuffer is not null &&
            sdkRingBuffer.TryGetLatestWindowQuality(WindowSamples, out EegWindowQuality windowQuality)
                ? windowQuality.IsUsable
                : null;
        return StimulusReadinessEvaluator.Evaluate(
            latestDiagnosticsSnapshot,
            GetSelectedChannelIndices(),
            maxSelectedImpedanceKiloOhms,
            DateTimeOffset.UtcNow,
            maxSampleAge: TimeSpan.FromMilliseconds(500),
            bufferedSampleCount: sdkRingBuffer?.Count ?? 0,
            requiredBufferedSampleCount: WindowSamples,
            isLatestWindowUsable: isLatestWindowUsable,
            isOnAcPower: isOnAcPower);
    }

    private void StopStimulusPresentationImmediately()
    {
        StimulusCanvas.CollectionPhase = CollectionPhase.Idle;
        StimulusCanvas.CueTargetId = null;
        StimulusCanvas.FeedbackTargetId = null;
        StimulusCanvas.EndSession();
    }

    private void UpdateStimulusReadinessUi()
    {
        if (StimulusReadyTextBlock is null || StartButton is null || StopButton is null)
        {
            return;
        }

        StimulusReadinessResult readiness = EvaluateCurrentStimulusReadiness();
        StimulusReadyTextBlock.Text = $"刺激就绪：{readiness.Message}";
        StimulusReadyTextBlock.Foreground = readiness.CanCalculate
            ? new SolidColorBrush(Color.FromRgb(134, 239, 172))
            : new SolidColorBrush(Color.FromRgb(253, 224, 71));

        if (!isSessionActive)
        {
            StartButton.IsEnabled = readiness.CanCalculate;
        }

        StopButton.IsEnabled = isSessionActive;
    }

    private void UpdateImpedancePanel(IReadOnlyList<ChannelImpedance> impedance)
    {
        if (ImpedancePanel is null)
        {
            return;
        }

        ImpedancePanel.Children.Clear();

        if (impedance.Count == 0)
        {
            ImpedancePanel.Children.Add(new TextBlock
            {
                Text = sourceMode == EegSourceMode.Simulated ? "模拟模式没有阻抗数据" : "尚未收到阻抗数据",
                Foreground = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                Margin = new Thickness(0, 3, 0, 0)
            });
            return;
        }

        HashSet<int> selectedChannels = GetSelectedChannelIndices().ToHashSet();
        foreach (ChannelImpedance channel in impedance)
        {
            bool isSelected = selectedChannels.Contains(channel.ChannelIndex);
            bool exceedsSelectedThreshold = isSelected &&
                channel.Quality != ImpedanceQuality.Unknown &&
                channel.KiloOhms > maxSelectedImpedanceKiloOhms;
            Brush foreground = exceedsSelectedThreshold
                ? new SolidColorBrush(Color.FromRgb(252, 165, 165))
                : QualityForeground(channel.Quality);
            var textBlock = new TextBlock
            {
                Text = FormatImpedance(channel),
                FontSize = 12,
                Foreground = foreground
            };
            var border = new Border
            {
                Child = textBlock,
                BorderBrush = exceedsSelectedThreshold
                    ? foreground
                    : isSelected ? new SolidColorBrush(Color.FromRgb(56, 189, 248)) : QualityForeground(channel.Quality),
                BorderThickness = isSelected ? new Thickness(2) : new Thickness(1),
                Background = isSelected
                    ? new SolidColorBrush(Color.FromRgb(15, 23, 42))
                    : new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 3, 6, 3),
                Margin = new Thickness(0, 0, 6, 6),
                ToolTip = $"通道 {channel.ChannelIndex + 1}；{TranslateImpedanceQuality(channel.Quality)}；{channel.Ohms} 欧姆；阈值 {maxSelectedImpedanceKiloOhms:0} 千欧"
            };
            ImpedancePanel.Children.Add(border);
        }
    }

    private static string FormatImpedance(ChannelImpedance channel)
    {
        return channel.Quality == ImpedanceQuality.Unknown
            ? $"通道 {channel.ChannelIndex + 1}：--"
            : $"通道 {channel.ChannelIndex + 1}：{channel.KiloOhms:0} 千欧";
    }

    private static string TranslateImpedanceQuality(ImpedanceQuality quality)
    {
        return quality switch
        {
            ImpedanceQuality.Good => "良好",
            ImpedanceQuality.Warning => "警告",
            ImpedanceQuality.Bad => "较差",
            _ => "未知"
        };
    }

    private static string TranslateDiagnosticKind(SynchroniBridgeDiagnosticKind kind)
    {
        return kind switch
        {
            SynchroniBridgeDiagnosticKind.Error => "错误",
            SynchroniBridgeDiagnosticKind.SensorError => "传感器错误",
            SynchroniBridgeDiagnosticKind.ShutdownError => "关闭错误",
            SynchroniBridgeDiagnosticKind.State => "状态",
            SynchroniBridgeDiagnosticKind.Battery => "电量",
            _ => "信息"
        };
    }

    private static string TranslateDiagnosticMessage(string message)
    {
        return message switch
        {
            "DeviceStateEx.Ready" => "设备已就绪",
            "DeviceStateEx.Disconnected" => "设备已断开",
            "DeviceStateEx.Connecting" => "设备连接中",
            "DeviceStateEx.Connected" => "设备已连接",
            _ => message
        };
    }

    private static Brush QualityForeground(ImpedanceQuality quality)
    {
        return quality switch
        {
            ImpedanceQuality.Good => new SolidColorBrush(Color.FromRgb(134, 239, 172)),
            ImpedanceQuality.Warning => new SolidColorBrush(Color.FromRgb(253, 224, 71)),
            ImpedanceQuality.Bad => new SolidColorBrush(Color.FromRgb(252, 165, 165)),
            _ => new SolidColorBrush(Color.FromRgb(148, 163, 184))
        };
    }

    private static SynchroniBridgeDiagnosticsSnapshot CreateEmptyDiagnosticsSnapshot()
    {
        return new SynchroniBridgeDiagnosticsSnapshot(
            DeviceName: string.Empty,
            DeviceAddress: string.Empty,
            SamplingRateHz: SamplingRateHz,
            ChannelCount: 8,
            SampleCount: 0,
            LostSampleCount: 0,
            LastSampleIndex: null,
            LastMessage: "未连接",
            LastMessageKind: SynchroniBridgeDiagnosticKind.Info,
            LastSampleAt: null,
            LastEventAt: null,
            HasMetadata: false,
            Impedance: Array.Empty<ChannelImpedance>());
    }

    private sealed record PendingCollectionFeedback(
        DataCollectionTrial Trial,
        ClassificationResult? Classification);
}
