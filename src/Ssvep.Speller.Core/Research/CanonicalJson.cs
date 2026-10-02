using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Org.Webpki.JsonCanonicalizer;

namespace Ssvep.Speller.Core.Research;

public static class CanonicalJson
{
    public const string Version = "jcs-sha256-v1";
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    public static byte[] Encode(string json)
    {
        using var document = JsonDocument.Parse(json);
        Validate(document.RootElement);
        return new JsonCanonicalizer(json).GetEncodedUTF8();
    }

    public static string HashJson(string json) => Convert.ToHexString(SHA256.HashData(Encode(json))).ToLowerInvariant();
    public static string Hash(object value) => HashJson(JsonSerializer.Serialize(value, Options));
    public static string BytesHash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static void Validate(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("配置包含重复字段：" + property.Name);
                ValidateString(property.Name);
                Validate(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var element in value.EnumerateArray()) Validate(element);
        else if (value.ValueKind == JsonValueKind.Number && (!value.TryGetDouble(out double number) || !double.IsFinite(number)))
            throw new JsonException("配置包含非有限数值。");
        else if (value.ValueKind == JsonValueKind.String) ValidateString(value.GetString()!);
    }

    private static void ValidateString(string value)
    {
        // Strict UTF-8 conversion rejects isolated UTF-16 surrogates. No Unicode normalization.
        _ = new UTF8Encoding(false, true).GetBytes(value);
    }
}
