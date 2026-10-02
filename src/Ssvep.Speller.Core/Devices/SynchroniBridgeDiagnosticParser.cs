using System.Text.Json;

namespace Ssvep.Speller.Core.Devices;

public static class SynchroniBridgeDiagnosticParser
{
    public static bool TryParse(string line, out SynchroniBridgeDiagnosticEvent? diagnosticEvent)
    {
        diagnosticEvent = null;
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

            string type = typeElement.GetString() ?? string.Empty;
            string message = CreateMessage(type, root);
            SynchroniBridgeDiagnosticKind kind = type.ToLowerInvariant() switch
            {
                "error" => SynchroniBridgeDiagnosticKind.Error,
                "sensor_error" => SynchroniBridgeDiagnosticKind.SensorError,
                "shutdown_error" => SynchroniBridgeDiagnosticKind.ShutdownError,
                "state" => SynchroniBridgeDiagnosticKind.State,
                "battery" => SynchroniBridgeDiagnosticKind.Battery,
                _ => SynchroniBridgeDiagnosticKind.Info
            };

            diagnosticEvent = new SynchroniBridgeDiagnosticEvent(kind, message, DateTimeOffset.UtcNow);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string CreateMessage(string type, JsonElement root)
    {
        if (root.TryGetProperty("message", out JsonElement messageElement))
        {
            return messageElement.GetString() ?? string.Empty;
        }

        if (string.Equals(type, "state", StringComparison.OrdinalIgnoreCase) &&
            root.TryGetProperty("state", out JsonElement stateElement))
        {
            return stateElement.GetString() ?? string.Empty;
        }

        if (string.Equals(type, "battery", StringComparison.OrdinalIgnoreCase) &&
            root.TryGetProperty("power", out JsonElement powerElement))
        {
            return $"{powerElement.GetInt32()}%";
        }

        return type;
    }
}
