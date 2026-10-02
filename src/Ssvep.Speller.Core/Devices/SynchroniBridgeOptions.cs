namespace Ssvep.Speller.Core.Devices;

public sealed record SynchroniBridgeOptions(
    string PythonExecutablePath,
    IReadOnlyList<string> PythonPrefixArguments,
    string BridgeScriptPath,
    int ScanPeriodMs,
    string? DeviceAddress,
    string? DeviceNamePrefix,
    int PackageSampleCount,
    int PowerRefreshIntervalMs,
    bool Enable50HzNotch,
    bool EnableHighPass,
    bool EnableLowPass)
{
    public static SynchroniBridgeOptions CreateDefault()
    {
        return new SynchroniBridgeOptions(
            PythonExecutablePath: ResolvePythonExecutable(out IReadOnlyList<string> prefixArguments),
            PythonPrefixArguments: prefixArguments,
            BridgeScriptPath: Path.Combine(AppContext.BaseDirectory, "Bridges", "synchroni_eeg_bridge.py"),
            ScanPeriodMs: 6000,
            DeviceAddress: null,
            DeviceNamePrefix: null,
            PackageSampleCount: 10,
            PowerRefreshIntervalMs: 60_000,
            Enable50HzNotch: true,
            EnableHighPass: true,
            EnableLowPass: true);
    }

    private static string ResolvePythonExecutable(out IReadOnlyList<string> prefixArguments)
    {
        string? uv = FindOnPath("uv.exe") ?? FindOnPath("uv");
        if (uv is not null)
        {
            prefixArguments =
            [
                "run",
                "--python",
                "3.11",
                "--with",
                "synchroni-sensor-sdk",
                "python"
            ];
            return uv;
        }

        prefixArguments = [];
        return "python";
    }

    private static string? FindOnPath(string fileName)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (string directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            string candidate = Path.Combine(directory.Trim(), fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
