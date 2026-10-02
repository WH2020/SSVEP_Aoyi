using System.Text.Json;
using System.Text.Json.Serialization;
using Ssvep.Speller.Core.Stimulus;
using Ssvep.Speller.Core.Studio;

namespace Ssvep.Speller.Core.Research;

public sealed record ResearchFilter
{
    public string Method { get; init; } = "butterworth-sos";
    public int PrototypeOrder { get; init; } = 4;
    public IReadOnlyList<IReadOnlyList<double>> BandsHz { get; init; } = new double[][] { [6, 48], [14, 48], [22, 48] };
    public double? SoftwareNotchHz { get; init; }
    public double NotchQ { get; init; } = 30;
}

public sealed record ResearchDecision(double MinScore = .15, string GapMode = "relative", double MinGap = .15);

public sealed record ResearchProfile
{
    public int SchemaVersion { get; init; } = 1;
    public string ProfileId { get; init; } = Guid.NewGuid().ToString("N");
    public int Revision { get; init; } = 1;
    public string Name { get; init; } = "研发候选";
    public string AlgorithmVersion { get; init; } = "fbcca-research-v1";
    public string ScoreVersion { get; init; } = "weighted-rho2-normalized-v1";
    public IReadOnlyList<double> TargetFrequenciesHz { get; init; } = StudioRuntimeProfile.Targets.Select(t => t.FrequencyHz).ToArray();
    public ResearchFilter Filter { get; init; } = new();
    public int HarmonicCount { get; init; } = 3;
    public IReadOnlyList<double> BandWeights { get; init; } = new[] { 1.25, Math.Pow(2, -1.25) + .25, Math.Pow(3, -1.25) + .25 };
    public ResearchDecision Decision { get; init; } = new();
    [JsonIgnore] public bool IsLegacy => AlgorithmVersion == "fbcca-legacy-0.2.4";
    [JsonIgnore] public string ProfileHash => CanonicalJson.Hash(new
    { SchemaVersion, AlgorithmVersion, ScoreVersion, TargetFrequenciesHz, Filter, HarmonicCount, BandWeights, Decision });

    public static ResearchProfile Legacy() => new()
    {
        ProfileId = "legacy-0.2.4", Name = "原版 0.2.4 基线", AlgorithmVersion = "fbcca-legacy-0.2.4",
        ScoreVersion = "weighted-rho2-raw-legacy", Decision = new(.1, "absolute", .02),
        Filter = new() { Method = "fir-window-129", BandsHz = new double[][] { [8,90], [16,90], [24,90] } }
    };

    public ResearchProfile Freeze()
    {
        Validate();
        return this with
        {
            TargetFrequenciesHz = Array.AsReadOnly(TargetFrequenciesHz.ToArray()),
            BandWeights = Array.AsReadOnly(BandWeights.ToArray()),
            Filter = Filter with { BandsHz = Array.AsReadOnly(Filter.BandsHz.Select(b => (IReadOnlyList<double>)Array.AsReadOnly(b.ToArray())).ToArray()) }
        };
    }

    public IReadOnlyList<StimulusTarget> Targets() => Array.AsReadOnly(StudioRuntimeProfile.Targets
        .Select((t, i) => t with { FrequencyHz = TargetFrequenciesHz[i] }).ToArray());

    public IReadOnlyList<string> Validate(int samplingRate = 500)
    {
        _ = StudioRuntimeProfile.WindowSamples(samplingRate);
        if (SchemaVersion != 1 || string.IsNullOrWhiteSpace(ProfileId) || ProfileId.Length > 80 || Revision < 1 || string.IsNullOrWhiteSpace(Name) || Name.Length > 80)
            throw new ArgumentException("方案版本、编号或名称无效。");
        if (AlgorithmVersion is not ("fbcca-research-v1" or "fbcca-legacy-0.2.4")) throw new ArgumentException("不支持该算法版本。");
        if (ScoreVersion != (IsLegacy ? "weighted-rho2-raw-legacy" : "weighted-rho2-normalized-v1")) throw new ArgumentException("算法与评分单位不匹配。");
        if (TargetFrequenciesHz is null || TargetFrequenciesHz.Count != 6 || TargetFrequenciesHz.Any(f => !double.IsFinite(f) || f < 8 || f > 16 || Math.Abs(f * 10 - Math.Round(f * 10)) > 1e-8)
            || TargetFrequenciesHz.Distinct().Count() != 6) throw new ArgumentException("六频率必须为 8–16 Hz、步长 0.1 Hz 且不重复。");
        if (HarmonicCount is not (2 or 3) || TargetFrequenciesHz.Max() * HarmonicCount >= samplingRate / 2.0) throw new ArgumentException("谐波须为二或三次，且低于奈奎斯特频率。");
        if (Filter is null || Filter.Method != (IsLegacy ? "fir-window-129" : "butterworth-sos") || Filter.PrototypeOrder != 4
            || Filter.BandsHz is null || Filter.BandsHz.Count != 3 || Filter.BandsHz.Any(b => b is null || b.Count != 2 || b.Any(x => !double.IsFinite(x)) || b[0] < 1 || b[1] <= b[0] || b[1] >= samplingRate / 2.0))
            throw new ArgumentException("滤波参数无效：三子带须满足 1 Hz ≤ 下限 < 上限 < 奈奎斯特频率，内部原型阶数固定为 4。");
        if (Filter.BandsHz[0][0] > TargetFrequenciesHz.Min() || Filter.BandsHz[0][1] < TargetFrequenciesHz.Max()) throw new ArgumentException("第一子带必须覆盖全部目标基频。");
        if (Filter.SoftwareNotchHz is not (null or 50 or 60) || !double.IsFinite(Filter.NotchQ) || Filter.NotchQ < 10 || Filter.NotchQ > 60) throw new ArgumentException("陷波只能关闭、50 或 60 Hz；Q 为 10–60。");
        if (BandWeights is null || BandWeights.Count != 3 || BandWeights.Any(w => !double.IsFinite(w) || w < 0) || !double.IsFinite(BandWeights.Sum()) || BandWeights.Sum() <= 0) throw new ArgumentException("三个子带权重须有限、非负且总和大于零。");
        if (Decision is null || !double.IsFinite(Decision.MinScore) || Decision.MinScore < 0 || Decision.MinScore > (IsLegacy ? BandWeights.Sum() : 1)
            || !double.IsFinite(Decision.MinGap) || Decision.MinGap < 0 || Decision.MinGap > 1 || Decision.GapMode is not ("relative" or "absolute")) throw new ArgumentException("判决阈值或分差模式无效。");
        if (IsLegacy && ProfileHash != Legacy().ProfileHash) throw new ArgumentException("原版基线只读，请建立研发候选。");
        var warnings = new List<string>();
        if (TargetFrequenciesHz.Order().Zip(TargetFrequenciesHz.Order().Skip(1), (a,b) => b-a).Any(g => g < .999)) warnings.Add("存在间隔小于 1 Hz 的目标，需采集对照验证。");
        if (Filter.SoftwareNotchHz is double notch && TargetFrequenciesHz.Any(f => Enumerable.Range(1,HarmonicCount).Any(h => Math.Abs(f*h-notch) < 1))) warnings.Add("陷波接近参考谐波，可能削弱响应。");
        var references=TargetFrequenciesHz.SelectMany((f,i)=>Enumerable.Range(1,HarmonicCount).Select(h=>(Target:i,Harmonic:h,Frequency:f*h))).ToArray();
        if(references.Any(a=>references.Any(b=>a.Target!=b.Target&&(a.Harmonic>1||b.Harmonic>1)&&Math.Abs(a.Frequency-b.Frequency)<.5)))
            warnings.Add("不同目标的低阶谐波接近，建议另采对照数据检查混淆。");
        if(!IsLegacy&&references.Any(r=>Filter.BandsHz.All(b=>StreamingFilterBank.Response(StreamingFilterBank.DesignBandpass(b[0],b[1],samplingRate),r.Frequency,samplingRate).Magnitude<.5)))
            warnings.Add("部分参考谐波在所有子带中被明显衰减，请结合实测比较滤波范围。");
        return warnings;
    }

    public static ResearchProfile Parse(string json)
    {
        _ = CanonicalJson.Encode(json);
        var profile = JsonSerializer.Deserialize<ResearchProfile>(json, CanonicalJson.Options) ?? throw new JsonException("空方案。");
        return profile.Freeze();
    }

    public string ToJson() => JsonSerializer.Serialize(this, CanonicalJson.Options);
}
