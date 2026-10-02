using System.Diagnostics;

namespace Ssvep.Speller.Core.Devices;

public sealed class SynchroniBridgeDeviceScanner
{
    private readonly SynchroniBridgeOptions options;

    public SynchroniBridgeDeviceScanner(SynchroniBridgeOptions options)
    {
        this.options = options;
    }

    public async Task<IReadOnlyList<SynchroniDiscoveredDevice>> ScanAsync(CancellationToken cancellationToken)
    {
        using Process process = StartProcess();
        Task<string> errorOutputTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var devices = new List<SynchroniDiscoveredDevice>();
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (SynchroniBridgeDeviceParser.TryParse(line, out SynchroniDiscoveredDevice? device) &&
                device is not null &&
                devices.All(item => !string.Equals(item.Address, device.Address, StringComparison.OrdinalIgnoreCase)))
            {
                devices.Add(device);
            }
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        string errorOutput = (await errorOutputTask.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(errorOutput)
                ? $"设备搜索进程退出异常，退出码 {process.ExitCode}。"
                : errorOutput);
        }

        return devices;
    }

    private Process StartProcess()
    {
        if (!File.Exists(options.BridgeScriptPath))
        {
            throw new FileNotFoundException("未找到 Synchroni Python 桥接脚本。", options.BridgeScriptPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = options.PythonExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string prefixArgument in options.PythonPrefixArguments)
        {
            startInfo.ArgumentList.Add(prefixArgument);
        }

        startInfo.ArgumentList.Add("-u");
        startInfo.ArgumentList.Add(options.BridgeScriptPath);
        startInfo.ArgumentList.Add("--list-devices");
        startInfo.ArgumentList.Add("--scan-ms");
        startInfo.ArgumentList.Add(options.ScanPeriodMs.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (!string.IsNullOrWhiteSpace(options.DeviceNamePrefix))
        {
            startInfo.ArgumentList.Add("--device-name-prefix");
            startInfo.ArgumentList.Add(options.DeviceNamePrefix);
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException("启动 Synchroni Python 桥接进程失败。");
    }
}
