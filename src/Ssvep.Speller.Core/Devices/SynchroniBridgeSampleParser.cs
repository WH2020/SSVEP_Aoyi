using System.Text.Json;
using Ssvep.Speller.Core.Signal;

namespace Ssvep.Speller.Core.Devices;

public static class SynchroniBridgeSampleParser
{
    private const long EarliestPlausibleUnixTimestampMilliseconds = 946_684_800_000;

    public static bool TryParse(
        string line,
        out EegSample? sample,
        out SynchroniBridgeMetadata? metadata)
    {
        bool parsed = TryParseFrame(line, out SynchroniBridgeSampleFrame? frame, out metadata);
        sample = frame?.Sample;
        return parsed;
    }

    public static bool TryParseFrame(
        string line,
        out SynchroniBridgeSampleFrame? frame,
        out SynchroniBridgeMetadata? metadata)
    {
        frame = null;
        metadata = null;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("type", out JsonElement typeElement))
            {
                return false;
            }

            string? type = typeElement.GetString();
            if (string.Equals(type, "meta", StringComparison.OrdinalIgnoreCase))
            {
                metadata = ParseMetadata(root);
                return metadata is not null;
            }

            if (string.Equals(type, "sample", StringComparison.OrdinalIgnoreCase))
            {
                frame = ParseSampleFrame(root);
                return frame is not null;
            }
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        return false;
    }

    private static SynchroniBridgeMetadata? ParseMetadata(JsonElement root)
    {
        if (!root.TryGetProperty("samplingRateHz", out JsonElement rateElement) ||
            !root.TryGetProperty("channelCount", out JsonElement channelCountElement))
        {
            return null;
        }

        string deviceName = root.TryGetProperty("deviceName", out JsonElement nameElement)
            ? nameElement.GetString() ?? string.Empty
            : string.Empty;
        string deviceAddress = root.TryGetProperty("deviceAddress", out JsonElement addressElement)
            ? addressElement.GetString() ?? string.Empty
            : string.Empty;
        return new SynchroniBridgeMetadata(
            deviceName,
            deviceAddress,
            rateElement.GetInt32(),
            channelCountElement.GetInt32());
    }

    private static SynchroniBridgeSampleFrame? ParseSampleFrame(JsonElement root)
    {
        if (!root.TryGetProperty("sampleIndex", out JsonElement indexElement) ||
            !root.TryGetProperty("channels", out JsonElement channelsElement) ||
            channelsElement.ValueKind != JsonValueKind.Array ||
            channelsElement.GetArrayLength() == 0)
        {
            return null;
        }

        var channels = new double[channelsElement.GetArrayLength()];
        for (int i = 0; i < channels.Length; i++)
        {
            JsonElement channel = channelsElement[i];
            if (channel.ValueKind != JsonValueKind.Number)
            {
                return null;
            }

            channels[i] = channel.GetDouble();
        }

        bool isLost = root.TryGetProperty("lost", out JsonElement lostElement) && lostElement.GetBoolean();
        (DateTimeOffset timestamp, double? deviceElapsedMilliseconds) = ParseTimestamps(root);
        int samplingRateHz = root.TryGetProperty("samplingRateHz", out JsonElement rateElement)
            ? rateElement.GetInt32()
            : 250;
        int channelCount = root.TryGetProperty("channelCount", out JsonElement channelCountElement)
            ? channelCountElement.GetInt32()
            : channels.Length;
        IReadOnlyList<int> impedanceOhms = ParseImpedanceOhms(root);

        return new SynchroniBridgeSampleFrame(
            new EegSample(
                indexElement.GetInt64(),
                timestamp,
                channels,
                deviceElapsedMilliseconds,
                isLost),
            isLost,
            samplingRateHz,
            channelCount,
            impedanceOhms);
    }

    private static (DateTimeOffset Timestamp, double? DeviceElapsedMilliseconds) ParseTimestamps(JsonElement root)
    {
        if (root.TryGetProperty("hostReceivedUnixMs", out JsonElement hostTimestampElement))
        {
            DateTimeOffset hostTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(hostTimestampElement.GetInt64());
            double? deviceElapsed = root.TryGetProperty("deviceElapsedMs", out JsonElement deviceTimestampElement) &&
                deviceTimestampElement.ValueKind == JsonValueKind.Number
                    ? deviceTimestampElement.GetDouble()
                    : null;
            return (hostTimestamp, deviceElapsed);
        }

        if (!root.TryGetProperty("timestampUnixMs", out JsonElement legacyTimestampElement))
        {
            return (DateTimeOffset.UtcNow, null);
        }

        long legacyTimestamp = legacyTimestampElement.GetInt64();
        if (legacyTimestamp >= EarliestPlausibleUnixTimestampMilliseconds)
        {
            return (DateTimeOffset.FromUnixTimeMilliseconds(legacyTimestamp), null);
        }

        return (DateTimeOffset.UtcNow, legacyTimestamp);
    }

    private static IReadOnlyList<int> ParseImpedanceOhms(JsonElement root)
    {
        if (!root.TryGetProperty("impedance", out JsonElement impedanceElement) ||
            impedanceElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<int>();
        }

        var impedanceOhms = new int[impedanceElement.GetArrayLength()];
        for (int i = 0; i < impedanceOhms.Length; i++)
        {
            JsonElement impedance = impedanceElement[i];
            if (impedance.ValueKind != JsonValueKind.Number)
            {
                return Array.Empty<int>();
            }

            impedanceOhms[i] = impedance.GetInt32();
        }

        return impedanceOhms;
    }
}
