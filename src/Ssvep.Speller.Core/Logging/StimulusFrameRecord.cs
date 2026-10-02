using Ssvep.Speller.Core.Collection;

namespace Ssvep.Speller.Core.Logging;

public sealed record StimulusFrameRecord(
    DateTimeOffset Timestamp,
    long RenderFrameIndex,
    long RenderingTimeTicks,
    double FrameIntervalMilliseconds,
    int TrialIndex,
    CollectionPhase Phase,
    string TargetId,
    double StimulusElapsedSeconds,
    bool IsFlickering,
    bool PhotodiodePatchIsBright);
