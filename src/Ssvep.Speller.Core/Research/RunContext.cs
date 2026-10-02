using Ssvep.Speller.Core.Studio;

namespace Ssvep.Speller.Core.Research;

public sealed record DisplayGeometry(double Width, double Height, double DpiX = 1, double DpiY = 1);

public sealed class RunContext
{
    public ResearchProfile Profile { get; }
    public StudioContentPackage Content { get; }
    public IReadOnlyList<int> Channels { get; }
    public IReadOnlyList<StudioElectrodeBinding> ElectrodeBindings { get; }
    public int SamplingRateHz { get; }
    public int ChannelCount { get; }
    public DisplayGeometry Display { get; }
    public string RunConfigHash { get; }
    public int WarmupSamples { get; }
    public string SampleUnit { get; }
    public double? ScaleToVolts { get; }
    public string UnitSource { get; }

    public RunContext(ResearchProfile profile, StudioContentPackage content, int rate, int channelCount,
        IEnumerable<int> channels, IEnumerable<StudioElectrodeBinding> bindings, DisplayGeometry? display = null,
        string sampleUnit = "unknown", double? scaleToVolts = null, string unitSource = "unknown")
    {
        profile.Validate(rate); StudioContentService.Validate(content);
        if(channelCount is <8 or >64) throw new ArgumentException("当前记录格式支持 8–64 个采集通道。");
        var selection = channels.ToArray();
        if (selection.Length != 8 || selection.Distinct().Count() != 8 || selection.Any(c => c < 0 || c >= channelCount)) throw new ArgumentException("请选择八个不同的有效识别通道。");
        if (scaleToVolts is double scale && (!double.IsFinite(scale) || scale <= 0)) throw new ArgumentException("物理单位比例无效。");
        Profile = profile.Freeze(); Content = content with { Targets = Array.AsReadOnly(content.Targets.ToArray()) };
        Channels = Array.AsReadOnly(selection); ElectrodeBindings = Array.AsReadOnly(bindings.ToArray());
        SamplingRateHz = rate; ChannelCount = channelCount; Display = display ?? new(0, 0);
        SampleUnit = sampleUnit; ScaleToVolts = scaleToVolts; UnitSource = unitSource;
        WarmupSamples = Profile.IsLegacy ? 0 : new StreamingFilterBank(Profile.Filter, rate, 8).WarmupSamples;
        RunConfigHash = CanonicalJson.Hash(new
        {
            Profile.ProfileHash, content = new { Content.Name, targets = Content.Targets.Select(t => new { t.Id, t.Name, t.Kind, t.Text, t.Shape,
                imageHash = t.ImageBase64 is null ? null : CanonicalJson.BytesHash(Convert.FromBase64String(t.ImageBase64)) }) },
            Channels, ElectrodeBindings, SamplingRateHz, ChannelCount, Display, SampleUnit, ScaleToVolts, UnitSource,
            fixedSpec = new { targetCount = 6, phaseRadians = 0, windowSeconds = 2.5, encoding = "sinusoidal-luminance" },
            schedule = SampleWindowScheduler.Version, warmupVersion = StreamingFilterBank.Version, WarmupSamples
        });
    }
}
