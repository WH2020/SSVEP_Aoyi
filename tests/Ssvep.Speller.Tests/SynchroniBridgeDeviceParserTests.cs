using Ssvep.Speller.Core.Devices;

namespace Ssvep.Speller.Tests;

public sealed class SynchroniBridgeDeviceParserTests
{
    [Fact]
    public void TryParseDeviceReadsDeviceJsonLine()
    {
        const string json = """
            {"type":"device","name":"OB6000C(E318)","address":"84:27:12:10:E3:18"}
            """;

        Assert.True(SynchroniBridgeDeviceParser.TryParse(json, out SynchroniDiscoveredDevice? device));

        Assert.NotNull(device);
        Assert.Equal("OB6000C(E318)", device.Name);
        Assert.Equal("84:27:12:10:E3:18", device.Address);
        Assert.Equal("OB6000C(E318) (84:27:12:10:E3:18)", device.DisplayName);
    }

    [Fact]
    public void TryParseDeviceRejectsNonDeviceLines()
    {
        Assert.False(SynchroniBridgeDeviceParser.TryParse("""{"type":"sample","channels":[1]}""", out _));
        Assert.False(SynchroniBridgeDeviceParser.TryParse("""{"type":"device","name":"","address":"AA"}""", out _));
        Assert.False(SynchroniBridgeDeviceParser.TryParse("scan started", out _));
    }
}
