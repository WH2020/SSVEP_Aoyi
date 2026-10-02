using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ssvep.Speller.Core.Studio;

public sealed record StudioElectrode(string Name, double X, double Y, bool Extended = false);
public sealed record StudioElectrodeBinding(int ChannelIndex, string Electrode);
public sealed record StudioChannelSetup(int ChannelCount, IReadOnlyList<StudioElectrodeBinding> Bindings,
    IReadOnlyList<int> SelectedChannels);

public static class StudioElectrodes
{
    // Schematic top view, subject's left on the left. Coordinates are for UI placement, not digitized anatomy.
    // 19 conventional scalp sites plus explicitly marked 10-10 intermediate sites for posterior/32-channel caps.
    public static IReadOnlyList<StudioElectrode> Positions { get; } = Array.AsReadOnly(new StudioElectrode[]
    {
        new("Fp1",190,72), new("Fp2",330,72),
        new("F7",93,137), new("F3",174,158), new("Fz",260,148), new("F4",346,158), new("F8",427,137),
        new("T7",53,260), new("C3",153,260), new("Cz",260,260), new("C4",367,260), new("T8",467,260),
        new("P7",93,383), new("P3",174,362), new("Pz",260,372), new("P4",346,362), new("P8",427,383),
        new("O1",190,448), new("O2",330,448),
        new("Fpz",260,60,true),
        new("FC5",108,205,true), new("FC1",211,207,true), new("FC2",309,207,true), new("FC6",412,205,true),
        new("CP5",108,315,true), new("CP1",211,313,true), new("CP2",309,313,true), new("CP6",412,315,true),
        new("PO3",219,410,true), new("POz",260,421,true), new("PO4",301,410,true), new("Oz",260,470,true)
    });

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static StudioChannelSetup Create(int count, bool simulation = false)
    {
        if (count is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(count));
        string[] demo = ["O1", "Oz", "O2", "PO3", "POz", "PO4", "P3", "P4"];
        return new(count, simulation && count == 8
            ? demo.Select((name, index) => new StudioElectrodeBinding(index, name)).ToArray() : [],
            Enumerable.Range(0, Math.Min(8, count)).ToArray());
    }

    public static string Normalize(string name)
    {
        string value = name.Trim();
        value = value.ToUpperInvariant() switch { "T3" => "T7", "T4" => "T8", "T5" => "P7", "T6" => "P8", _ => value };
        return Positions.FirstOrDefault(p => p.Name.Equals(value, StringComparison.OrdinalIgnoreCase))?.Name
            ?? throw new ArgumentException("请选择图中已有的电极位置。");
    }

    public static StudioChannelSetup Validate(StudioChannelSetup setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        if (setup.ChannelCount is < 1 or > 64 || setup.Bindings is null || setup.SelectedChannels is null)
            throw new ArgumentException("通道配置无效。");
        var bindings = setup.Bindings.Select(b => b is null || string.IsNullOrWhiteSpace(b.Electrode)
            ? throw new ArgumentException("电极位置不能为空。")
            : b with { Electrode = Normalize(b.Electrode) }).OrderBy(b => b.ChannelIndex).ToArray();
        if (bindings.Any(b => b.ChannelIndex < 0 || b.ChannelIndex >= setup.ChannelCount)
            || bindings.Select(b => b.ChannelIndex).Distinct().Count() != bindings.Length
            || bindings.Select(b => b.Electrode).Distinct().Count() != bindings.Length)
            throw new ArgumentException("每个通道和电极位置只能对应一次，请先解除原对应关系。");
        int[] selected = setup.SelectedChannels.ToArray();
        if (selected.Any(i => i < 0 || i >= setup.ChannelCount) || selected.Distinct().Count() != selected.Length)
            throw new ArgumentException("选中的通道编号无效。");
        return new(setup.ChannelCount, Array.AsReadOnly(bindings), Array.AsReadOnly(selected.Order().ToArray()));
    }

    public static StudioChannelSetup Assign(StudioChannelSetup setup, int channel, string? electrode)
    {
        if (channel < 0 || channel >= setup.ChannelCount) throw new ArgumentOutOfRangeException(nameof(channel));
        var bindings = setup.Bindings.Where(b => b.ChannelIndex != channel).ToList();
        if (!string.IsNullOrWhiteSpace(electrode)) bindings.Add(new(channel, electrode));
        return Validate(setup with { Bindings = bindings });
    }

    public static StudioChannelSetup Load(string path, int expectedCount)
    {
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("通道配置文件过大。");
        var setup = Validate(JsonSerializer.Deserialize<StudioChannelSetup>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("通道配置为空。"));
        if (setup.ChannelCount != expectedCount) throw new InvalidDataException("通道配置与当前设备通道数不一致。");
        return setup;
    }

    public static void Save(string path, StudioChannelSetup setup)
    {
        string text = JsonSerializer.Serialize(Validate(setup), Json);
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temp = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, text); File.Move(temp, fullPath, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
