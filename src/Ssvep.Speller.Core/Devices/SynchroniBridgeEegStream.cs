using System.Diagnostics;
using System.Runtime.CompilerServices;
using Ssvep.Speller.Core.Signal;

namespace Ssvep.Speller.Core.Devices;

public sealed class SynchroniBridgeEegStream : IEegStream
{
    private readonly SynchroniBridgeOptions options;
    private readonly SynchroniBridgeDiagnostics diagnostics = new();
    private Process? process;
    private int samplingRateHz = 250;
    private int channelCount = 8;

    public SynchroniBridgeEegStream(SynchroniBridgeOptions options)
    {
        this.options = options;
    }

    public int SamplingRateHz => samplingRateHz;

    public int ChannelCount => channelCount;

    public event EventHandler<SynchroniBridgeDiagnosticsSnapshot>? DiagnosticsUpdated;

    public SynchroniBridgeDiagnosticsSnapshot DiagnosticsSnapshot => diagnostics.CreateSnapshot();

    public async IAsyncEnumerable<EegSample> ReadSamplesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        process = StartProcess();
        RecordEvent(new SynchroniBridgeDiagnosticEvent(
            SynchroniBridgeDiagnosticKind.Info,
            "Synchroni 桥接进程已启动，正在扫描蓝牙设备...",
            DateTimeOffset.UtcNow));
        _ = Task.Run(() => DrainErrorsAsync(process, cancellationToken), cancellationToken);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (!SynchroniBridgeSampleParser.TryParse(line, out EegSample? sample, out SynchroniBridgeMetadata? metadata))
                {
                    continue;
                }

                if (metadata is not null)
                {
                    samplingRateHz = metadata.SamplingRateHz;
                    channelCount = metadata.ChannelCount;
                    diagnostics.RecordMetadata(metadata);
                    RaiseDiagnosticsUpdated();
                    continue;
                }

                if (sample is not null)
                {
                    if (SynchroniBridgeSampleParser.TryParseFrame(line, out SynchroniBridgeSampleFrame? frame, out _) &&
                        frame is not null)
                    {
                        diagnostics.RecordSample(frame);
                        RaiseDiagnosticsUpdated();
                    }

                    yield return sample;
                }
            }
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested && process is not null)
            {
                try
                {
                    if (process.HasExited)
                    {
                        RecordEvent(new SynchroniBridgeDiagnosticEvent(
                            process.ExitCode == 0 ? SynchroniBridgeDiagnosticKind.Info : SynchroniBridgeDiagnosticKind.Error,
                            $"Synchroni 桥接进程已退出，退出码 {process.ExitCode}",
                            DateTimeOffset.UtcNow));
                    }
                }
                catch (InvalidOperationException)
                {
                }
            }

            await StopProcessAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopProcessAsync().ConfigureAwait(false);
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
        startInfo.ArgumentList.Add("--scan-ms");
        startInfo.ArgumentList.Add(options.ScanPeriodMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--package-count");
        startInfo.ArgumentList.Add(options.PackageSampleCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--power-refresh-ms");
        startInfo.ArgumentList.Add(options.PowerRefreshIntervalMs.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (!string.IsNullOrWhiteSpace(options.DeviceAddress))
        {
            startInfo.ArgumentList.Add("--device-address");
            startInfo.ArgumentList.Add(options.DeviceAddress);
        }

        if (!string.IsNullOrWhiteSpace(options.DeviceNamePrefix))
        {
            startInfo.ArgumentList.Add("--device-name-prefix");
            startInfo.ArgumentList.Add(options.DeviceNamePrefix);
        }

        if (options.Enable50HzNotch)
        {
            startInfo.ArgumentList.Add("--filter-50hz");
        }

        if (options.EnableHighPass)
        {
            startInfo.ArgumentList.Add("--filter-hpf");
        }

        if (options.EnableLowPass)
        {
            startInfo.ArgumentList.Add("--filter-lpf");
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException("启动 Synchroni Python 桥接进程失败。");
    }

    private async Task DrainErrorsAsync(Process process, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !process.HasExited)
        {
            string? line = await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }

            if (SynchroniBridgeDiagnosticParser.TryParse(line, out SynchroniBridgeDiagnosticEvent? diagnosticEvent) &&
                diagnosticEvent is not null)
            {
                RecordEvent(diagnosticEvent);
            }
            else
            {
                RecordEvent(new SynchroniBridgeDiagnosticEvent(
                    SynchroniBridgeDiagnosticKind.Info,
                    line,
                    DateTimeOffset.UtcNow));
            }
        }
    }

    private void RecordEvent(SynchroniBridgeDiagnosticEvent diagnosticEvent)
    {
        diagnostics.RecordEvent(diagnosticEvent);
        RaiseDiagnosticsUpdated();
    }

    private void RaiseDiagnosticsUpdated()
    {
        DiagnosticsUpdated?.Invoke(this, diagnostics.CreateSnapshot());
    }

    private async Task StopProcessAsync()
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            process.Dispose();
            process = null;
        }
    }
}
