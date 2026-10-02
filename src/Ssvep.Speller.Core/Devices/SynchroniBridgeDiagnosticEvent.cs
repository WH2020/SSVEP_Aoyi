namespace Ssvep.Speller.Core.Devices;

public sealed record SynchroniBridgeDiagnosticEvent(
    SynchroniBridgeDiagnosticKind Kind,
    string Message,
    DateTimeOffset Timestamp);
