using Ssvep.Speller.Core.Collection;
using Ssvep.Speller.Core.Stimulus;

namespace Ssvep.Speller.Tests;

public sealed class StimulusPresentationTests
{
    [Theory]
    [InlineData(CollectionPhase.Rest)]
    [InlineData(CollectionPhase.Cue)]
    [InlineData(CollectionPhase.Feedback)]
    [InlineData(CollectionPhase.InterTrial)]
    [InlineData(CollectionPhase.Complete)]
    public void DataCollectionFlickersOnlyDuringStimulus(CollectionPhase phase)
    {
        Assert.False(StimulusPresentation.ShouldFlicker(
            isRunning: true,
            AppRunMode.DataCollection,
            phase));
    }

    [Fact]
    public void DataCollectionFlickersDuringStimulus()
    {
        Assert.True(StimulusPresentation.ShouldFlicker(
            isRunning: true,
            AppRunMode.DataCollection,
            CollectionPhase.Stimulus));
    }

    [Fact]
    public void TypingModeFlickersWhileSessionIsRunning()
    {
        Assert.True(StimulusPresentation.ShouldFlicker(
            isRunning: true,
            AppRunMode.Typing,
            CollectionPhase.Idle));
        Assert.False(StimulusPresentation.ShouldFlicker(
            isRunning: false,
            AppRunMode.Typing,
            CollectionPhase.Idle));
    }

    [Fact]
    public void SinusoidalLuminanceIsBoundedAndStartsAtConfiguredPhase()
    {
        Assert.Equal(0.5, StimulusPresentation.CalculateLuminance(10, 0, 0), 6);
        Assert.Equal(1.0, StimulusPresentation.CalculateLuminance(10, Math.PI / 2, 0), 6);

        for (int frame = 0; frame < 120; frame++)
        {
            double luminance = StimulusPresentation.CalculateLuminance(
                15.8,
                Math.PI * 1.5,
                frame / 60.0);
            Assert.InRange(luminance, 0.0, 1.0);
        }
    }
}
