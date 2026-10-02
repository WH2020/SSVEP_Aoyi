using Ssvep.Speller.Core.Output;
using Ssvep.Speller.Core.Stimulus;

namespace Ssvep.Speller.Tests;

public sealed class TextComposerTests
{
    [Fact]
    public void ComposerAppliesDigitsLettersSpaceDeleteAndClear()
    {
        var composer = new TextComposer();

        composer.Apply(new TargetAction.Digit('1'));
        composer.Apply(new TargetAction.Letter('H'));
        composer.Apply(new TargetAction.Letter('I'));
        composer.Apply(new TargetAction.Space());
        composer.Apply(new TargetAction.Letter('X'));
        composer.Apply(new TargetAction.Delete());
        composer.Apply(new TargetAction.Letter('A'));

        Assert.Equal("1HI A", composer.Text);

        composer.Apply(new TargetAction.Clear());

        Assert.Equal(string.Empty, composer.Text);
    }
}
