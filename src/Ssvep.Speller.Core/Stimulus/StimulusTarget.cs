namespace Ssvep.Speller.Core.Stimulus;

public sealed record StimulusTarget(
    string Id,
    string Label,
    double FrequencyHz,
    double PhaseRadians,
    TargetAction Action);
