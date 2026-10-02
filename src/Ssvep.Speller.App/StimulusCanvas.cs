using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Ssvep.Speller.Core.Collection;
using Ssvep.Speller.Core.Logging;
using Ssvep.Speller.Core.Stimulus;

namespace Ssvep.Speller.App;

public sealed class StimulusCanvas : FrameworkElement
{
    private const int LuminanceLevelCount = 256;
    private static readonly Typeface LabelTypeface = new("Segoe UI");
    private static readonly Brush BackgroundBrush = Freeze(new SolidColorBrush(Color.FromRgb(2, 6, 23)));
    private static readonly Brush LightTextBrush = Freeze(new SolidColorBrush(Color.FromRgb(250, 204, 21)));
    private static readonly Brush DarkTextBrush = Freeze(new SolidColorBrush(Color.FromRgb(15, 23, 42)));
    private static readonly Brush FrequencyTextBrush = Freeze(new SolidColorBrush(Color.FromRgb(203, 213, 225)));
    private static readonly Pen DefaultBorderPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(250, 204, 21))), 1.2));
    private static readonly Pen CueBorderPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(56, 189, 248))), 4.0));
    private static readonly Pen FeedbackBorderPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(34, 197, 94))), 5.0));
    private static readonly Pen PhotodiodeBorderPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(148, 163, 184))), 1.0));
    private static readonly Brush PhotodiodeBrightBrush = Freeze(new SolidColorBrush(Colors.White));
    private static readonly Brush PhotodiodeDarkBrush = Freeze(new SolidColorBrush(Colors.Black));
    private static readonly Brush[] SolidLuminanceBrushes = CreateSolidLuminanceBrushes();
    private static readonly Brush[] CheckerboardBrushes = CreateCheckerboardBrushes();

    private readonly FrameTimingTracker frameTimingTracker = new(expectedRefreshRateHz: 60);
    private readonly Dictionary<TextCacheKey, FormattedText> textCache = [];
    private readonly List<TargetLayout> targetLayouts = [];
    private IReadOnlyList<StimulusTarget> targets = [];
    private IReadOnlyDictionary<string, StimulusTarget> targetsById = new Dictionary<string, StimulusTarget>();
    private IReadOnlyDictionary<string, string> frequencyLabels = new Dictionary<string, string>();
    private TimeSpan? previousRenderingTime;
    private TimeSpan? stimulusStartedAt;
    private double stimulusElapsedSeconds;
    private bool wasFlickering;
    private bool isTrackingSession;
    private long renderFrameIndex = -1;
    private long stimulusFrameIndex = -1;
    private bool photodiodePatchIsBright;
    private double currentFrameIntervalMilliseconds;
    private DrawingGroup? staticOverlay;
    private Size cachedLayoutSize;
    private double cachedPixelsPerDip;

    public StimulusCanvas()
    {
        CompositionTarget.Rendering += OnRendering;
    }

    public event EventHandler<StimulusFrameRenderingEventArgs>? FrameRendering;

    public event EventHandler<StimulusFramePreparedEventArgs>? FramePrepared;

    public IReadOnlyList<StimulusTarget> Targets
    {
        get => targets;
        set
        {
            targets = value ?? [];
            targetsById = targets.ToDictionary(target => target.Id, StringComparer.Ordinal);
            frequencyLabels = targets.ToDictionary(
                target => target.Id,
                target => $"{target.FrequencyHz:0.0}Hz",
                StringComparer.Ordinal);
            textCache.Clear();
            InvalidateLayoutCache();
            InvalidateVisual();
        }
    }

    public StimulusStyle StimulusMode { get; set; } = StimulusStyle.Checkerboard;

    public AppRunMode RunMode { get; set; } = AppRunMode.Typing;

    public bool IsRunning { get; private set; }

    public string? CueTargetId { get; set; }

    public string? FeedbackTargetId { get; set; }

    public CollectionPhase CollectionPhase { get; set; } = CollectionPhase.Idle;

    public double AverageFps => frameTimingTracker.CreateSnapshot().AverageFps;

    public int DroppedFrames => frameTimingTracker.CreateSnapshot().DroppedFrames;

    public void BeginSession(double expectedRefreshRateHz)
    {
        frameTimingTracker.Reset(expectedRefreshRateHz);
        previousRenderingTime = null;
        stimulusStartedAt = null;
        stimulusElapsedSeconds = 0;
        wasFlickering = false;
        renderFrameIndex = -1;
        stimulusFrameIndex = -1;
        photodiodePatchIsBright = false;
        currentFrameIntervalMilliseconds = 0;
        isTrackingSession = true;
        IsRunning = true;
    }

    public void EndSession()
    {
        IsRunning = false;
        isTrackingSession = false;
        previousRenderingTime = null;
        stimulusStartedAt = null;
        stimulusElapsedSeconds = 0;
        wasFlickering = false;
        stimulusFrameIndex = -1;
        photodiodePatchIsBright = false;
        InvalidateVisual();
    }

    public FrameStats GetFrameStats()
    {
        return frameTimingTracker.CreateSnapshot();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(BackgroundBrush, null, new Rect(RenderSize));

        if (Targets.Count == 0)
        {
            return;
        }

        DrawTargets(drawingContext);
        DrawPhotodiodePatch(drawingContext);
    }

    private void DrawTargets(DrawingContext drawingContext)
    {
        EnsureLayoutCache();
        foreach (TargetLayout layout in targetLayouts)
        {
            DrawTargetFill(drawingContext, layout.Target, layout.Bounds);
        }

        if (staticOverlay is not null)
        {
            drawingContext.DrawDrawing(staticOverlay);
        }

        foreach (TargetLayout layout in targetLayouts)
        {
            bool isCue = CueTargetId == layout.Target.Id && CollectionPhase is CollectionPhase.Cue or CollectionPhase.Stimulus;
            bool isFeedback = FeedbackTargetId == layout.Target.Id && CollectionPhase == CollectionPhase.Feedback;
            if (isCue || isFeedback)
            {
                drawingContext.DrawRoundedRectangle(
                    null,
                    isFeedback ? FeedbackBorderPen : CueBorderPen,
                    layout.Bounds,
                    4,
                    4);
            }
        }
    }

    private void EnsureLayoutCache()
    {
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (staticOverlay is not null &&
            cachedLayoutSize == RenderSize &&
            Math.Abs(cachedPixelsPerDip - pixelsPerDip) < 0.001)
        {
            return;
        }

        targetLayouts.Clear();
        double width = ActualWidth;
        double height = ActualHeight;
        double gap = Math.Max(8, width * 0.008);
        double keyHeight = Math.Max(46, (height - 6 * gap) / 5.0);
        double top = Math.Max(gap, (height - keyHeight * 5.0 - gap * 4.0) / 2.0);

        AddRowLayout(StimulusLayout.DigitRow, 10, top, 0.0, keyHeight, gap);
        AddRowLayout(StimulusLayout.TopLetterRow, 10, top + keyHeight + gap, 0.0, keyHeight, gap);
        AddRowLayout(StimulusLayout.HomeLetterRow, 10, top + (keyHeight + gap) * 2.0, 0.5, keyHeight, gap);
        AddRowLayout(StimulusLayout.BottomLetterRow, 10, top + (keyHeight + gap) * 3.0, 1.5, keyHeight, gap);
        AddRowLayout(StimulusLayout.CommandRow, 4, top + (keyHeight + gap) * 4.0, 0.0, keyHeight, gap);

        var overlay = new DrawingGroup();
        using (DrawingContext context = overlay.Open())
        {
            foreach (TargetLayout layout in targetLayouts)
            {
                context.DrawRoundedRectangle(null, DefaultBorderPen, layout.Bounds, 4, 4);
                DrawCenteredText(context, layout.Target.Label, layout.Bounds, useDarkText: false);
                DrawFrequencyText(context, frequencyLabels[layout.Target.Id], layout.Bounds);
            }
        }

        overlay.Freeze();
        staticOverlay = overlay;
        cachedLayoutSize = RenderSize;
        cachedPixelsPerDip = pixelsPerDip;
    }

    private void AddRowLayout(
        IReadOnlyList<string> rowTargetIds,
        int columns,
        double top,
        double offsetColumns,
        double keyHeight,
        double gap)
    {
        double keyWidth = (ActualWidth - gap * (columns + 1)) / columns;
        double left = gap + offsetColumns * (keyWidth + gap);

        for (int i = 0; i < rowTargetIds.Count; i++)
        {
            if (!targetsById.TryGetValue(rowTargetIds[i], out StimulusTarget? target))
            {
                continue;
            }

            Rect rect = new(left + i * (keyWidth + gap), top, keyWidth, keyHeight);
            targetLayouts.Add(new TargetLayout(target, rect));
        }
    }

    private void DrawTargetFill(DrawingContext drawingContext, StimulusTarget target, Rect rect)
    {
        bool isFlickering = StimulusPresentation.ShouldFlicker(IsRunning, RunMode, CollectionPhase);
        double luminance = isFlickering
            ? StimulusPresentation.CalculateLuminance(
                target.FrequencyHz,
                target.PhaseRadians,
                stimulusElapsedSeconds)
            : 0.20;
        int luminanceIndex = Math.Clamp((int)Math.Round(luminance * (LuminanceLevelCount - 1)), 0, LuminanceLevelCount - 1);
        Brush fill = StimulusMode == StimulusStyle.Checkerboard
            ? isFlickering ? CheckerboardBrushes[luminanceIndex] : CheckerboardBrushes[204]
            : SolidLuminanceBrushes[luminanceIndex];
        drawingContext.DrawRoundedRectangle(fill, null, rect, 4, 4);
    }

    private void DrawCenteredText(DrawingContext drawingContext, string label, Rect rect, bool useDarkText)
    {
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText formatted = GetFormattedText(
            label,
            label.Length > 1 ? 22 : 30,
            useDarkText,
            pixelsPerDip);
        drawingContext.DrawText(
            formatted,
            new Point(rect.Left + (rect.Width - formatted.Width) / 2.0, rect.Top + (rect.Height - formatted.Height) / 2.0 - 2));
    }

    private void DrawFrequencyText(DrawingContext drawingContext, string label, Rect rect)
    {
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText formatted = GetFormattedText(label, 11, useDarkText: false, pixelsPerDip, isFrequency: true);
        drawingContext.DrawText(formatted, new Point(rect.Left + 6, rect.Bottom - formatted.Height - 4));
    }

    private FormattedText GetFormattedText(
        string text,
        double size,
        bool useDarkText,
        double pixelsPerDip,
        bool isFrequency = false)
    {
        var key = new TextCacheKey(text, size, useDarkText, isFrequency, pixelsPerDip);
        if (textCache.TryGetValue(key, out FormattedText? formatted))
        {
            return formatted;
        }

        formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            LabelTypeface,
            size,
            isFrequency ? FrequencyTextBrush : useDarkText ? DarkTextBrush : LightTextBrush,
            pixelsPerDip);
        textCache[key] = formatted;
        return formatted;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs renderingEventArgs)
        {
            return;
        }

        if (isTrackingSession)
        {
            bool hasTimingInterruption = false;
            if (previousRenderingTime is not null)
            {
                TimeSpan interval = renderingEventArgs.RenderingTime - previousRenderingTime.Value;
                currentFrameIntervalMilliseconds = interval.TotalMilliseconds;
                hasTimingInterruption = !frameTimingTracker.RecordInterval(interval);
            }
            else
            {
                currentFrameIntervalMilliseconds = 0;
            }

            renderFrameIndex++;
            FrameRendering?.Invoke(
                this,
                new StimulusFrameRenderingEventArgs(
                    DateTimeOffset.UtcNow,
                    renderingEventArgs.RenderingTime,
                    renderFrameIndex,
                    hasTimingInterruption));

            bool isFlickering = StimulusPresentation.ShouldFlicker(IsRunning, RunMode, CollectionPhase);
            if (isFlickering && !wasFlickering)
            {
                stimulusStartedAt = renderingEventArgs.RenderingTime;
                stimulusFrameIndex = 0;
            }
            else if (isFlickering)
            {
                stimulusFrameIndex++;
            }
            else if (!isFlickering)
            {
                stimulusStartedAt = null;
                stimulusFrameIndex = -1;
            }

            stimulusElapsedSeconds = stimulusStartedAt is null
                ? 0
                : (renderingEventArgs.RenderingTime - stimulusStartedAt.Value).TotalSeconds;
            photodiodePatchIsBright = isFlickering && stimulusFrameIndex % 2 == 0;
            wasFlickering = isFlickering;
            previousRenderingTime = renderingEventArgs.RenderingTime;
            FramePrepared?.Invoke(
                this,
                new StimulusFramePreparedEventArgs(
                    DateTimeOffset.UtcNow,
                    renderingEventArgs.RenderingTime,
                    renderFrameIndex,
                    currentFrameIntervalMilliseconds,
                    stimulusElapsedSeconds,
                    isFlickering,
                    photodiodePatchIsBright));
        }

        if (IsVisible)
        {
            InvalidateVisual();
        }
    }

    private void DrawPhotodiodePatch(DrawingContext drawingContext)
    {
        double size = Math.Clamp(Math.Min(ActualWidth, ActualHeight) * 0.055, 28, 64);
        Rect rect = new(
            Math.Max(0, ActualWidth - size - 3),
            Math.Max(0, ActualHeight - size - 3),
            size,
            size);
        drawingContext.DrawRectangle(
            photodiodePatchIsBright ? PhotodiodeBrightBrush : PhotodiodeDarkBrush,
            PhotodiodeBorderPen,
            rect);
    }

    private void InvalidateLayoutCache()
    {
        staticOverlay = null;
        targetLayouts.Clear();
        cachedLayoutSize = default;
        cachedPixelsPerDip = 0;
    }

    private static Brush[] CreateSolidLuminanceBrushes()
    {
        var brushes = new Brush[LuminanceLevelCount];
        for (int index = 0; index < brushes.Length; index++)
        {
            byte level = (byte)Math.Round(8 + index / 255.0 * 247);
            brushes[index] = Freeze(new SolidColorBrush(Color.FromRgb(level, level, level)));
        }

        return brushes;
    }

    private static Brush[] CreateCheckerboardBrushes()
    {
        var brushes = new Brush[LuminanceLevelCount];
        for (int index = 0; index < brushes.Length; index++)
        {
            double contrast = index / 255.0 * 2.0 - 1.0;
            byte first = (byte)Math.Clamp((int)Math.Round(127.5 + 127.5 * contrast), 0, 255);
            byte second = (byte)Math.Clamp((int)Math.Round(127.5 - 127.5 * contrast), 0, 255);
            Brush firstBrush = Freeze(new SolidColorBrush(Color.FromRgb(first, first, first)));
            Brush secondBrush = Freeze(new SolidColorBrush(Color.FromRgb(second, second, second)));
            var group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing(firstBrush, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
            group.Children.Add(new GeometryDrawing(secondBrush, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
            group.Children.Add(new GeometryDrawing(secondBrush, null, new RectangleGeometry(new Rect(8, 8, 8, 8))));
            group.Freeze();
            var brush = new DrawingBrush(group)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, 16, 16),
                ViewportUnits = BrushMappingMode.Absolute
            };
            brushes[index] = Freeze(brush);
        }

        return brushes;
    }

    private static T Freeze<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private readonly record struct TextCacheKey(
        string Text,
        double Size,
        bool UseDarkText,
        bool IsFrequency,
        double PixelsPerDip);

    private readonly record struct TargetLayout(StimulusTarget Target, Rect Bounds);
}
