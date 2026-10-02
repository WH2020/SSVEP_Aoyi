using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ssvep.Speller.Core.Stimulus;
using Ssvep.Speller.Core.Studio;
using Ssvep.Speller.Core.Research;

namespace Ssvep.Studio.App;

public sealed class StudioStimulusView : FrameworkElement
{
    private StudioContentPackage? content;
    private readonly Dictionary<string, BitmapSource> images = new();
    private readonly PresentationClock clock = new();
    private IReadOnlyList<StimulusTarget> targets = StudioRuntimeProfile.Targets;
    private bool running;
    private string? selected;
    private TimeSpan lastRenderTime = TimeSpan.MinValue;
    private TimeSpan lastRecordedRenderTime = TimeSpan.MinValue;
    private string cueText = "";
    private long cueVersion;
    private long drawnCueVersion = -1;
    public string CueText { get => cueText; set { cueText=value; cueVersion++; InvalidateVisual(); } }
    public event Action<string,double,double>? CuePresented;
    public event Action<double>? FrameRendered;
    public event Action<double,double,string?,long>? PresentationRendered;
    public void SetTargets(IReadOnlyList<StimulusTarget> value)
    {
        if (running) return;
        targets = Array.AsReadOnly(value.ToArray()); InvalidateVisual();
    }
    public string? SelectedTargetId { get => selected; set { if (selected != value) { selected = value; InvalidateVisual(); } } }

    public StudioStimulusView()
    {
        MinHeight = 260;
        Loaded += (_, _) => CompositionTarget.Rendering += Rendering;
        Unloaded += (_, _) => { CompositionTarget.Rendering -= Rendering; SetRunning(false); };
    }

    public void SetContent(StudioContentPackage package)
    {
        if (running) throw new InvalidOperationException("请结束实验后修改内容。");
        var decoded = new Dictionary<string, BitmapSource>();
        foreach (var target in package.Targets.Where(t => t.Kind == StudioContentKind.Image))
        {
            byte[] bytes = Convert.FromBase64String(target.ImageBase64!);
            using var input = new MemoryStream(bytes, false);
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count != 1 || decoder.Frames[0].PixelWidth > 4096 || decoder.Frames[0].PixelHeight > 4096
                || (long)decoder.Frames[0].PixelWidth * decoder.Frames[0].PixelHeight > 8_000_000)
                throw new InvalidDataException("图片过大，请使用不超过 4096 像素且总像素不超过 800 万的静态图片。");
            var frame = decoder.Frames[0]; frame.Freeze(); decoded.Add(target.Id, frame);
        }
        content = package;
        images.Clear(); foreach (var pair in decoded) images.Add(pair.Key, pair.Value);
        InvalidateVisual();
    }

    public void SetRunning(bool value)
    {
        if (running == value) return;
        running = value;
        if (running) { clock.Reset(); lastRenderTime = TimeSpan.MinValue; lastRecordedRenderTime = TimeSpan.MinValue; }
        else { clock.Reset(); SelectedTargetId = null; }
        InvalidateVisual();
    }

    private void Rendering(object? sender, EventArgs args)
    {
        if (!running || args is not RenderingEventArgs frame || frame.RenderingTime == lastRenderTime) return;
        lastRenderTime = frame.RenderingTime;
        clock.Advance(frame.RenderingTime.TotalSeconds);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(16, 25, 43)), null, new Rect(RenderSize));
        if (content is null || ActualWidth < 100 || ActualHeight < 100) return;
        double seconds = clock.PhaseSeconds;
        double gap = 28, pad = 32;
        double width = (ActualWidth - pad * 2 - gap * 2) / 3;
        double height = (ActualHeight - pad * 2 - gap) / 2;
        for (int i = 0; i < 6; i++)
        {
            var item = content.Targets[i];
            var cell = new Rect(pad + i % 3 * (width + gap), pad + i / 3 * (height + gap), width, height);
            var flicker = new Rect(cell.X + 6, cell.Y + 6, cell.Width - 12, Math.Max(32, cell.Height - 44));
            double luminance = running ? StimulusPresentation.CalculateLuminance(
                targets[i].FrequencyHz, targets[i].PhaseRadians, seconds) : .88;
            byte grey = (byte)Math.Round(30 + luminance * 215);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(grey, grey, grey)), null, flicker, 8, 8);
            dc.PushClip(new RectangleGeometry(new Rect(flicker.X + 10, flicker.Y + 10, flicker.Width - 20, flicker.Height - 20)));
            if (item.Kind == StudioContentKind.Text)
                DrawText(dc, item.Text, flicker, Brushes.Black, Math.Clamp(width / 7.0, 16, 34));
            else if (item.Kind == StudioContentKind.Shape)
                DrawShape(dc, item.Shape, flicker);
            else if (images.TryGetValue(item.Id, out var bitmap))
            {
                double scale = Math.Min((flicker.Width - 30) / bitmap.PixelWidth, (flicker.Height - 24) / bitmap.PixelHeight);
                var bounds = new Rect(flicker.X + (flicker.Width - bitmap.PixelWidth * scale) / 2,
                    flicker.Y + (flicker.Height - bitmap.PixelHeight * scale) / 2, bitmap.PixelWidth * scale, bitmap.PixelHeight * scale);
                dc.PushOpacity(running ? .15 + .85 * luminance : 1); dc.DrawImage(bitmap, bounds); dc.Pop();
            }
            dc.Pop();
            DrawText(dc, item.Name, new Rect(cell.X, cell.Bottom - 32, cell.Width, 28), Brushes.LightGray, 13);
            if (item.Id == selected)
                dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(64, 224, 190)), 4),
                    new Rect(cell.X, cell.Y, cell.Width, cell.Height - 32), 12, 12);
        }
        // Selection borders or layout updates can redraw within the same composition frame.
        string drawnCue=cueText; long drawnVersion=cueVersion;
        if (!string.IsNullOrEmpty(drawnCue))
            DrawText(dc,drawnCue,new Rect(0,ActualHeight/2-19,ActualWidth,38),Brushes.White,16);
        // A cue may redraw within one composition frame. Acknowledge the text that was
        // actually drawn, separately from the deduplicated frame/FPS callback.
        if(running && lastRenderTime != TimeSpan.MinValue && drawnVersion != drawnCueVersion)
        {
            drawnCueVersion=drawnVersion;
            CuePresented?.Invoke(drawnCue,lastRenderTime.TotalSeconds,Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency);
        }
        if (running && lastRenderTime != TimeSpan.MinValue && lastRecordedRenderTime != lastRenderTime)
        {
            lastRecordedRenderTime = lastRenderTime;
            FrameRendered?.Invoke(seconds);
            PresentationRendered?.Invoke(lastRenderTime.TotalSeconds, seconds, selected, clock.Epoch);
        }
    }

    private void DrawText(DrawingContext dc, string value, Rect bounds, Brush brush, double size)
    {
        var text = new FormattedText(value, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxTextWidth = Math.Max(10, bounds.Width - 20), MaxTextHeight = Math.Max(10, bounds.Height - 8),
            TextAlignment = TextAlignment.Center, Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawText(text, new Point(bounds.X + 10, bounds.Y + Math.Max(4, (bounds.Height - text.Height) / 2)));
    }

    private static void DrawShape(DrawingContext dc, StudioShape shape, Rect bounds)
    {
        var brush = new SolidColorBrush(Color.FromRgb(15, 30, 47));
        double size = Math.Min(bounds.Width, bounds.Height) * .48;
        var center = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        if (shape == StudioShape.Circle) dc.DrawEllipse(brush, null, center, size / 2, size / 2);
        else if (shape == StudioShape.Rectangle) dc.DrawRectangle(brush, null, new Rect(center.X - size / 2, center.Y - size / 2, size, size));
        else
        {
            Point[] points = shape == StudioShape.Diamond
                ? [new(center.X, center.Y-size/2), new(center.X+size/2,center.Y), new(center.X,center.Y+size/2), new(center.X-size/2,center.Y)]
                : [new(center.X-size/2,center.Y-size/6), new(center.X,center.Y-size/6), new(center.X,center.Y-size/2),
                    new(center.X+size/2,center.Y), new(center.X,center.Y+size/2), new(center.X,center.Y+size/6), new(center.X-size/2,center.Y+size/6)];
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open()) { ctx.BeginFigure(points[0], true, true); ctx.PolyLineTo(points.Skip(1).ToArray(), true, false); }
            geometry.Freeze(); dc.DrawGeometry(brush, null, geometry);
        }
    }
}
