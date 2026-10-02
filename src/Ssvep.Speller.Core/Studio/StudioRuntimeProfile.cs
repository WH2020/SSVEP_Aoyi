using Ssvep.Speller.Core.Stimulus;

namespace Ssvep.Speller.Core.Studio;

/// <summary>Developer-maintained profile; never deserialized from a content package.</summary>
public static class StudioRuntimeProfile
{
    public const string Version = "studio-six-v1";
    public static TimeSpan WindowDuration => TimeSpan.FromSeconds(2.5);
    public static TimeSpan MaximumSampleAge => TimeSpan.FromMilliseconds(500);
    public static TimeSpan MaximumImpedanceAge => TimeSpan.FromSeconds(5);
    // Retains the current device integration's provisional contact limit; verify for the supplied electrodes.
    public const double MaximumImpedanceKiloOhms = 500;
    public static IReadOnlyList<StimulusTarget> Targets { get; } = Array.AsReadOnly(
        new[] { 8.0, 9.2, 10.4, 11.6, 12.8, 14.0 }.Select((frequency, i) =>
            new StimulusTarget($"T{i + 1}", $"目标 {i + 1}", frequency, 0,
                new TargetAction.Letter((char)('A' + i)))).ToArray());

    public static int WindowSamples(int samplingRateHz) => samplingRateHz switch
    {
        250 or 500 => checked((int)(samplingRateHz * WindowDuration.TotalSeconds)),
        _ => throw new ArgumentException("设备数据速率不匹配当前内部方案。", nameof(samplingRateHz))
    };
}
