namespace Ssvep.Speller.Core.Stimulus;

public static class StimulusLayout
{
    public static readonly IReadOnlyList<string> DigitRow =
    [
        "1", "2", "3", "4", "5", "6", "7", "8", "9", "0"
    ];

    public static readonly IReadOnlyList<string> TopLetterRow =
    [
        "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P"
    ];

    public static readonly IReadOnlyList<string> HomeLetterRow =
    [
        "A", "S", "D", "F", "G", "H", "J", "K", "L"
    ];

    public static readonly IReadOnlyList<string> BottomLetterRow =
    [
        "Z", "X", "C", "V", "B", "N", "M"
    ];

    public static readonly IReadOnlyList<string> CommandRow =
    [
        "SPACE", "DEL", "PAUSE", "CLEAR"
    ];

    public static readonly IReadOnlyList<string> DefaultCollectionTargetIds =
    [
        "1", "T", "O", "D", "J", "X", "N", "PAUSE"
    ];

    private static readonly IReadOnlyList<string> FrequencyOrder =
    [
        "1", "2", "3", "4", "T", "5", "6", "7", "O", "8",
        "9", "0", "D", "Q", "W", "E", "J", "R", "Y", "U",
        "X", "I", "P", "A", "N", "S", "F", "G", "H", "K",
        "L", "Z", "C", "V", "B", "M", "SPACE", "DEL", "PAUSE", "CLEAR"
    ];

    public static IReadOnlyList<StimulusTarget> CreateDefaultQwertyTargets()
    {
        var targets = new List<StimulusTarget>(40);
        for (int i = 0; i < FrequencyOrder.Count; i++)
        {
            string id = FrequencyOrder[i];
            targets.Add(new StimulusTarget(
                Id: id,
                Label: LabelForId(id),
                FrequencyHz: FrequencyForIndex(i),
                PhaseRadians: (i % 4) * Math.PI / 2.0,
                Action: ActionForId(id)));
        }

        return targets;
    }

    public static IReadOnlyList<StimulusTarget> CreateDefaultCollectionTargets(IReadOnlyList<StimulusTarget> targets)
    {
        Dictionary<string, StimulusTarget> byId = targets.ToDictionary(target => target.Id);
        return DefaultCollectionTargetIds.Select(id => byId[id]).ToArray();
    }

    public static IReadOnlyList<IReadOnlyList<string>> CreateVisualRows()
    {
        return [DigitRow, TopLetterRow, HomeLetterRow, BottomLetterRow, CommandRow];
    }

    private static double FrequencyForIndex(int index)
    {
        return 8.0 + index * 0.2;
    }

    private static string LabelForId(string id)
    {
        return id;
    }

    private static TargetAction ActionForId(string id)
    {
        return id switch
        {
            "SPACE" => new TargetAction.Space(),
            "DEL" => new TargetAction.Delete(),
            "PAUSE" => new TargetAction.Pause(),
            "CLEAR" => new TargetAction.Clear(),
            { Length: 1 } value when char.IsDigit(value[0]) => new TargetAction.Digit(value[0]),
            { Length: 1 } value when char.IsLetter(value[0]) => new TargetAction.Letter(value[0]),
            _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown stimulus target id.")
        };
    }
}
