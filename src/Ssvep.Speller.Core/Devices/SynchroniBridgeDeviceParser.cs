using System.Text.Json;

namespace Ssvep.Speller.Core.Devices;

public static class SynchroniBridgeDeviceParser
{
    public static bool TryParse(string line, out SynchroniDiscoveredDevice? device)
    {
        device = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("type", out JsonElement typeElement) ||
                !string.Equals(typeElement.GetString(), "device", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string name = root.TryGetProperty("name", out JsonElement nameElement)
                ? nameElement.GetString() ?? string.Empty
                : string.Empty;
            string address = root.TryGetProperty("address", out JsonElement addressElement)
                ? addressElement.GetString() ?? string.Empty
                : string.Empty;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(address))
            {
                return false;
            }

            device = new SynchroniDiscoveredDevice(name.Trim(), address.Trim());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
