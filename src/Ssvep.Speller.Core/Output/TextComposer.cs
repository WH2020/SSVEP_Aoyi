using System.Text;
using Ssvep.Speller.Core.Stimulus;

namespace Ssvep.Speller.Core.Output;

public sealed class TextComposer
{
    private readonly StringBuilder builder = new();

    public string Text => builder.ToString();

    public bool IsPaused { get; private set; }

    public void Apply(TargetAction action)
    {
        switch (action)
        {
            case TargetAction.Digit digit:
                if (!IsPaused)
                {
                    builder.Append(digit.Value);
                }

                break;
            case TargetAction.Letter letter:
                if (!IsPaused)
                {
                    builder.Append(letter.Value);
                }

                break;
            case TargetAction.Space:
                if (!IsPaused)
                {
                    builder.Append(' ');
                }

                break;
            case TargetAction.Delete:
                if (!IsPaused && builder.Length > 0)
                {
                    builder.Length--;
                }

                break;
            case TargetAction.Pause:
                IsPaused = !IsPaused;
                break;
            case TargetAction.Clear:
                if (!IsPaused)
                {
                    builder.Clear();
                }

                break;
        }
    }
}
