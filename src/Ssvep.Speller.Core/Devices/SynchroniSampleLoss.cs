using System.Globalization;

namespace Ssvep.Speller.Core.Devices;

/// <summary>COUNT in the SDK diagnostic is missing samples, not BLE packets.</summary>
public sealed record SynchroniSampleLoss(int DataType, long SampleCount)
{
    public bool IsEeg => DataType == 0x10;

    public static bool TryParse(SynchroniBridgeDiagnosticKind kind, string message, out SynchroniSampleLoss? loss)
    {
        loss = null;
        if (kind != SynchroniBridgeDiagnosticKind.SensorError) return false;
        string[] fields = message.Split('|');
        if (fields.Length != 8 || fields[0] != "MSG" || fields[1] != "LOST SAMPLE"
            || fields[2] != "MAC" || string.IsNullOrWhiteSpace(fields[3])
            || fields[4] != "TYPE" || fields[6] != "COUNT"
            || !int.TryParse(fields[5], NumberStyles.None, CultureInfo.InvariantCulture, out int type) || type <= 0
            || !long.TryParse(fields[7], NumberStyles.None, CultureInfo.InvariantCulture, out long count) || count <= 0)
            return false;
        loss = new(type, count);
        return true;
    }
}
