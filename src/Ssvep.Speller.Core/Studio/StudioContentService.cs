using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ssvep.Speller.Core.Studio;

public static class StudioContentService
{
    private const int CurrentVersion = 1;
    private const int TargetCount = 6;
    private const int MaxNameLength = 80;
    private const int MaxTextLength = 120;
    private const int MaxImageBytes = 4 * 1024 * 1024;
    private const int MaxPackageBytes = 40 * 1024 * 1024;
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static StudioContentPackage CreateDefault() => new(
        CurrentVersion,
        "默认刺激内容",
        [
            new("T1", "目标一", StudioContentKind.Text, "开始", StudioShape.Rectangle, null),
            new("T2", "目标二", StudioContentKind.Text, "暂停", StudioShape.Rectangle, null),
            new("T3", "目标三", StudioContentKind.Text, "确认", StudioShape.Rectangle, null),
            new("T4", "目标四", StudioContentKind.Shape, "", StudioShape.Circle, null),
            new("T5", "目标五", StudioContentKind.Shape, "", StudioShape.Diamond, null),
            new("T6", "目标六", StudioContentKind.Shape, "", StudioShape.Arrow, null)
        ]);

    public static void Validate(StudioContentPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.Version != CurrentVersion)
            throw new ArgumentException("Unsupported content package version.", nameof(package));
        ValidateLabel(package.Name, MaxNameLength, "Package name", nameof(package));
        if (package.Targets is null || package.Targets.Count != TargetCount)
            throw new ArgumentException("A package must contain exactly six targets.", nameof(package));

        for (int index = 0; index < TargetCount; index++)
        {
            var target = package.Targets[index];
            if (target is null)
                throw new ArgumentException("Targets cannot be null.", nameof(package));
            if (target.Id != $"T{index + 1}")
                throw new ArgumentException("Targets must be ordered T1 through T6 with unique canonical IDs.", nameof(package));
            ValidateLabel(target.Name, MaxNameLength, "Target name", nameof(package));
            if (!Enum.IsDefined(target.Kind) || !Enum.IsDefined(target.Shape))
                throw new ArgumentException("Unknown content kind or shape.", nameof(package));
            if (target.Text is null || target.Text.Length > MaxTextLength)
                throw new ArgumentException("Target text is missing or too long.", nameof(package));
            if (target.Kind == StudioContentKind.Text && string.IsNullOrWhiteSpace(target.Text))
                throw new ArgumentException("Text targets require nonempty text.", nameof(package));
            if (target.Kind == StudioContentKind.Image)
                DecodeImage(target.ImageBase64, nameof(package));
            else if (target.ImageBase64 is not null)
                throw new ArgumentException("Only image targets may contain image data.", nameof(package));
        }
    }

    public static StudioContentPackage Load(string path)
    {
        ValidatePath(path);
        using var stream = File.OpenRead(path);
        if (stream.Length > MaxPackageBytes)
            throw new InvalidDataException("Content package exceeds 40 MiB.");
        try
        {
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            RequireObject(root, "package");
            RequireFields(root, ["version", "name", "targets"]);
            int version = RequireInt(root, "version");
            string name = RequireString(root, "name");
            var targetElements = RequireProperty(root, "targets");
            if (targetElements.ValueKind != JsonValueKind.Array || targetElements.GetArrayLength() != TargetCount)
                throw new InvalidDataException("Targets must be an array of six objects.");
            var targets = new List<StudioTargetContent>(TargetCount);
            foreach (var element in targetElements.EnumerateArray())
            {
                RequireObject(element, "target");
                RequireFields(element, ["id", "name", "kind", "text", "shape", "imageBase64"]);
                targets.Add(new StudioTargetContent(
                    RequireString(element, "id"),
                    RequireString(element, "name"),
                    RequireEnum<StudioContentKind>(element, "kind"),
                    RequireString(element, "text"),
                    RequireEnum<StudioShape>(element, "shape"),
                    RequireNullableString(element, "imageBase64")));
            }
            var package = new StudioContentPackage(version, name, targets);
            try { Validate(package); }
            catch (ArgumentException error) { throw new InvalidDataException("Invalid content package.", error); }
            return package;
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Malformed content package JSON.", error);
        }
    }

    public static void Save(string path, StudioContentPackage package)
    {
        ValidatePath(path);
        Validate(package);
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(package, JsonOptions) + Environment.NewLine);
        if (bytes.Length > MaxPackageBytes)
            throw new ArgumentException("Content package exceeds 40 MiB.", nameof(package));
        string destination = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(destination);
        if (directory is null)
            throw new ArgumentException("Invalid package destination.", nameof(path));
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static string ReadImage(string path)
    {
        ValidatePath(path);
        using var stream = File.OpenRead(path);
        if (stream.Length > MaxImageBytes)
            throw new InvalidDataException("Image exceeds 4 MiB.");
        byte[] bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (!HasImageSignature(bytes))
            throw new InvalidDataException("Only PNG and JPEG images are supported.");
        return Convert.ToBase64String(bytes);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A file path is required.", nameof(path));
    }

    private static void ValidateLabel(string? value, int maximum, string label, string argument)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum)
            throw new ArgumentException($"{label} must contain 1 to {maximum} characters.", argument);
    }

    private static byte[] DecodeImage(string? base64, string argument)
    {
        if (string.IsNullOrWhiteSpace(base64) || base64.Length > ((MaxImageBytes + 2) / 3) * 4)
            throw new ArgumentException("Image is missing or exceeds 4 MiB.", argument);
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException error) { throw new ArgumentException("Invalid image base64.", argument, error); }
        if (bytes.Length > MaxImageBytes || !HasImageSignature(bytes))
            throw new ArgumentException("Image must be PNG or JPEG and no more than 4 MiB.", argument);
        return bytes;
    }

    private static bool HasImageSignature(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith(PngSignature) ||
        (bytes.Length >= 4 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff && bytes[^2] == 0xff && bytes[^1] == 0xd9);

    private static void RequireObject(JsonElement element, string label)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{label} must be an object.");
    }

    private static void RequireFields(JsonElement element, IReadOnlyList<string> allowed)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) || !seen.Add(property.Name))
                throw new InvalidDataException($"Unknown or duplicate field: {property.Name}.");
        }
        if (seen.Count != allowed.Count)
            throw new InvalidDataException("Required package fields are missing.");
    }

    private static JsonElement RequireProperty(JsonElement element, string key) => element.GetProperty(key);

    private static string RequireString(JsonElement element, string key)
    {
        var value = RequireProperty(element, key);
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"{key} must be a string.");
        return value.GetString()!;
    }

    private static string? RequireNullableString(JsonElement element, string key)
    {
        var value = RequireProperty(element, key);
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"{key} must be a string or null.");
        return value.GetString();
    }

    private static int RequireInt(JsonElement element, string key)
    {
        var value = RequireProperty(element, key);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int number))
            throw new InvalidDataException($"{key} must be an integer.");
        return number;
    }

    private static T RequireEnum<T>(JsonElement element, string key) where T : struct, Enum
    {
        string value = RequireString(element, key);
        if (!Enum.TryParse<T>(value, ignoreCase: false, out var parsed) || !Enum.IsDefined(parsed) || value != parsed.ToString())
            throw new InvalidDataException($"Unknown {key} value.");
        return parsed;
    }
}
