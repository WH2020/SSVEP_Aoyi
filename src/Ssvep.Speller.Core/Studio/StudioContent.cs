namespace Ssvep.Speller.Core.Studio;

public enum StudioContentKind { Text, Shape, Image }

public enum StudioShape { Rectangle, Circle, Diamond, Arrow }

public sealed record StudioTargetContent(
    string Id,
    string Name,
    StudioContentKind Kind,
    string Text,
    StudioShape Shape,
    string? ImageBase64);

public sealed record StudioContentPackage(
    int Version,
    string Name,
    IReadOnlyList<StudioTargetContent> Targets);
