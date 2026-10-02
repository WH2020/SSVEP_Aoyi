namespace Ssvep.Speller.Core.Stimulus;

public abstract record TargetAction
{
    public sealed record Digit(char Value) : TargetAction;

    public sealed record Letter(char Value) : TargetAction;

    public sealed record Space : TargetAction;

    public sealed record Delete : TargetAction;

    public sealed record Pause : TargetAction;

    public sealed record Clear : TargetAction;
}
