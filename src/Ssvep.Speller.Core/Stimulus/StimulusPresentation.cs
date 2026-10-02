using Ssvep.Speller.Core.Collection;

namespace Ssvep.Speller.Core.Stimulus;

public static class StimulusPresentation
{
    public static bool ShouldFlicker(
        bool isRunning,
        AppRunMode runMode,
        CollectionPhase collectionPhase)
    {
        if (!isRunning)
        {
            return false;
        }

        return runMode == AppRunMode.Typing || collectionPhase == CollectionPhase.Stimulus;
    }

    public static double CalculateLuminance(
        double frequencyHz,
        double phaseRadians,
        double elapsedSeconds)
    {
        double angle = 2.0 * Math.PI * frequencyHz * elapsedSeconds + phaseRadians;
        return 0.5 * (1.0 + Math.Sin(angle));
    }
}
