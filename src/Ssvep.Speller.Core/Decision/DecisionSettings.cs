namespace Ssvep.Speller.Core.Decision;

public sealed record DecisionSettings(
    int RequiredConsistentWindows = 3,
    double MinBestScore = 0.55,
    double MinScoreMargin = 0.08,
    int CooldownWindows = 2);
