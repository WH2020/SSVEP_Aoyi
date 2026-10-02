using System.Text.Json.Nodes;
using Ssvep.Speller.Core.Studio;

namespace Ssvep.Speller.Tests;

public sealed class StudioContentTests
{
    private const string TinyPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Wl8sAAAAASUVORK5CYII=";

    [Fact]
    public void DefaultPackageHasSixChineseTextAndShapeSlots()
    {
        var package = StudioContentService.CreateDefault();
        Assert.Equal(1, package.Version);
        Assert.Equal(new[] { "T1", "T2", "T3", "T4", "T5", "T6" }, package.Targets.Select(t => t.Id));
        Assert.Contains(package.Targets, t => t.Kind == StudioContentKind.Text);
        Assert.Contains(package.Targets, t => t.Kind == StudioContentKind.Shape);
        Assert.All(package.Targets, t => Assert.NotEmpty(t.Name));
        StudioContentService.Validate(package);
    }

    [Fact]
    public void EmbeddedImageSurvivesRemovalOfOriginalFile()
    {
        using var folder = new TemporaryFolder();
        string asset = Path.Combine(folder.Path, "source.png");
        string content = Path.Combine(folder.Path, "portable.json");
        File.WriteAllBytes(asset, Convert.FromBase64String(TinyPng));
        string embedded = StudioContentService.ReadImage(asset);
        var targets = StudioContentService.CreateDefault().Targets.ToArray();
        targets[0] = targets[0] with { Kind = StudioContentKind.Image, Text = "", ImageBase64 = embedded };
        StudioContentService.Save(content, new StudioContentPackage(1, "便携内容", targets));
        File.Delete(asset);
        var loaded = StudioContentService.Load(content);
        Assert.Equal(TinyPng, loaded.Targets[0].ImageBase64);
        Assert.False(File.Exists(asset));
        Assert.Contains("\n", File.ReadAllText(content));
    }

    [Theory]
    [InlineData("count")]
    [InlineData("duplicate")]
    [InlineData("wrong-id")]
    [InlineData("order")]
    public void ValidateRejectsNonCanonicalSlots(string change)
    {
        var targets = StudioContentService.CreateDefault().Targets.ToArray();
        switch (change)
        {
            case "count": targets = targets[..5]; break;
            case "duplicate": targets[1] = targets[1] with { Id = "T1" }; break;
            case "wrong-id": targets[1] = targets[1] with { Id = "T99" }; break;
            case "order": (targets[0], targets[1]) = (targets[1], targets[0]); break;
        }
        Assert.Throws<ArgumentException>(() => StudioContentService.Validate(new StudioContentPackage(1, "测试", targets)));
    }

    [Theory]
    [InlineData(StudioContentKind.Text, "", null)]
    [InlineData(StudioContentKind.Image, "", null)]
    [InlineData(StudioContentKind.Image, "", "%%")]
    [InlineData(StudioContentKind.Image, "", "bm90IGFuIGltYWdl")]
    public void ValidateRejectsMissingOrInvalidRequiredContent(StudioContentKind kind, string text, string? image)
    {
        var targets = StudioContentService.CreateDefault().Targets.ToArray();
        targets[0] = targets[0] with { Kind = kind, Text = text, ImageBase64 = image };
        Assert.Throws<ArgumentException>(() => StudioContentService.Validate(new StudioContentPackage(1, "测试", targets)));
    }

    [Fact]
    public void MultilineTextRoundTrips()
    {
        using var folder = new TemporaryFolder();
        string file = Path.Combine(folder.Path, "multiline.json");
        var targets = StudioContentService.CreateDefault().Targets.ToArray();
        targets[0] = targets[0] with { Kind = StudioContentKind.Text, Text = "第一行\n第二行" };
        StudioContentService.Save(file, new StudioContentPackage(1, "测试", targets));
        Assert.Equal("第一行\n第二行", StudioContentService.Load(file).Targets[0].Text);
    }

    [Theory]
    [InlineData("encoding", false)]
    [InlineData("phase", false)]
    [InlineData("count", false)]
    [InlineData("samplingRate", false)]
    [InlineData("windowDuration", false)]
    [InlineData("sourcePath", true)]
    [InlineData("frequency", true)]
    public void LoadRejectsUnknownFields(string field, bool nested)
    {
        using var folder = new TemporaryFolder();
        string file = Path.Combine(folder.Path, "content.json");
        StudioContentService.Save(file, StudioContentService.CreateDefault());
        var root = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        if (nested) root["targets"]![0]![field] = "extra";
        else root[field] = "extra";
        File.WriteAllText(file, root.ToJsonString());
        Assert.Throws<InvalidDataException>(() => StudioContentService.Load(file));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{ bad json")]
    public void LoadRejectsMalformedStructures(string json)
    {
        using var folder = new TemporaryFolder();
        string file = Path.Combine(folder.Path, "invalid.json");
        File.WriteAllText(file, json);
        Assert.Throws<InvalidDataException>(() => StudioContentService.Load(file));
    }

    [Fact]
    public void ImageLargerThanFourMiBIsRejected()
    {
        var bytes = new byte[4 * 1024 * 1024 + 1];
        Convert.FromBase64String(TinyPng).AsSpan(0, 8).CopyTo(bytes);
        var targets = StudioContentService.CreateDefault().Targets.ToArray();
        targets[0] = targets[0] with { Kind = StudioContentKind.Image, Text = "", ImageBase64 = Convert.ToBase64String(bytes) };
        Assert.Throws<ArgumentException>(() => StudioContentService.Validate(new StudioContentPackage(1, "测试", targets)));
    }

    [Fact]
    public void OversizedPackageIsRejectedBeforeParsing()
    {
        using var folder = new TemporaryFolder();
        string file = Path.Combine(folder.Path, "too-large.json");
        using (var stream = File.Create(file)) stream.SetLength(40L * 1024 * 1024 + 1);
        Assert.Throws<InvalidDataException>(() => StudioContentService.Load(file));
    }

    [Fact]
    public void InvalidSavePreservesExistingDestination()
    {
        using var folder = new TemporaryFolder();
        string file = Path.Combine(folder.Path, "content.json");
        const string original = "original payload";
        File.WriteAllText(file, original);
        var invalid = StudioContentService.CreateDefault() with { Version = 2 };
        Assert.Throws<ArgumentException>(() => StudioContentService.Save(file, invalid));
        Assert.Equal(original, File.ReadAllText(file));
        Assert.Single(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public void ReadImageRejectsNonImageSignature()
    {
        using var folder = new TemporaryFolder();
        string file = Path.Combine(folder.Path, "fake.png");
        File.WriteAllText(file, "this is not PNG");
        Assert.Throws<InvalidDataException>(() => StudioContentService.ReadImage(file));
    }

    private sealed class TemporaryFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ssvep-studio-content-" + Guid.NewGuid().ToString("N"));
        public TemporaryFolder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
