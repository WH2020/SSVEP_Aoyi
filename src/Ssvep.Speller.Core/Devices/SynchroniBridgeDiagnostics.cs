namespace Ssvep.Speller.Core.Devices;

public sealed class SynchroniBridgeDiagnostics
{
    private readonly object gate = new();
    private string deviceName = string.Empty;
    private string deviceAddress = string.Empty;
    private int samplingRateHz = 250;
    private int channelCount = 8;
    private long sampleCount;
    private long lostSampleCount;
    private long sampleLossEventCount;
    private long reportedLostSampleCount;
    private long? lastSampleIndex;
    private string lastMessage = "Not started";
    private SynchroniBridgeDiagnosticKind lastMessageKind = SynchroniBridgeDiagnosticKind.Info;
    private DateTimeOffset? lastSampleAt;
    private DateTimeOffset? lastEventAt;
    private bool hasMetadata;
    private IReadOnlyList<ChannelImpedance> impedance = Array.Empty<ChannelImpedance>();
    private DateTimeOffset? impedanceReceivedAt;

    public void RecordMetadata(SynchroniBridgeMetadata metadata)
    {
        lock (gate)
        {
            deviceName = metadata.DeviceName;
            deviceAddress = metadata.DeviceAddress;
            samplingRateHz = metadata.SamplingRateHz;
            channelCount = metadata.ChannelCount;
            hasMetadata = true;
            lastMessage = $"已连接 {metadata.DeviceName}（{metadata.SamplingRateHz} Hz，{metadata.ChannelCount} 通道）";
            lastMessageKind = SynchroniBridgeDiagnosticKind.Info;
            lastEventAt = DateTimeOffset.UtcNow;
        }
    }

    public void RecordEvent(SynchroniBridgeDiagnosticEvent diagnosticEvent)
    {
        lock (gate)
        {
            // Count each stderr event once here; sample snapshots can repeat the last message.
            if (SynchroniSampleLoss.TryParse(diagnosticEvent.Kind, diagnosticEvent.Message, out var loss) && loss!.IsEeg)
            {
                sampleLossEventCount++;
                reportedLostSampleCount += loss.SampleCount;
            }
            lastMessage = diagnosticEvent.Message;
            lastMessageKind = diagnosticEvent.Kind;
            lastEventAt = diagnosticEvent.Timestamp;
        }
    }

    public void RecordSample(SynchroniBridgeSampleFrame frame)
    {
        lock (gate)
        {
            sampleCount++;
            if (frame.IsLost)
            {
                lostSampleCount++;
            }

            samplingRateHz = frame.SamplingRateHz;
            channelCount = frame.ChannelCount;
            lastSampleIndex = frame.Sample.SampleIndex;
            lastSampleAt = frame.Sample.Timestamp;
            if (frame.ImpedanceOhms.Count > 0)
            {
                impedanceReceivedAt = frame.Sample.Timestamp;
                impedance = frame.ImpedanceOhms
                    .Select((ohms, index) => ChannelImpedance.FromOhms(index, ohms))
                    .ToArray();
            }
        }
    }

    public SynchroniBridgeDiagnosticsSnapshot CreateSnapshot()
    {
        lock (gate)
        {
            return new SynchroniBridgeDiagnosticsSnapshot(
                deviceName,
                deviceAddress,
                samplingRateHz,
                channelCount,
                sampleCount,
                lostSampleCount,
                lastSampleIndex,
                lastMessage,
                lastMessageKind,
                lastSampleAt,
                lastEventAt,
                hasMetadata,
                impedance)
            {
                ImpedanceReceivedAt = impedanceReceivedAt,
                SampleLossEventCount = sampleLossEventCount,
                ReportedLostSampleCount = reportedLostSampleCount
            };
        }
    }
}
