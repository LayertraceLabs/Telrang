namespace Telrang.Core.Polls;

/// <summary>
/// Size caps checked by <see cref="PollDraftValidator"/> and <see cref="OptionListParser"/> (DESIGN.md §14.4, §20.4).
/// Character counts are UTF-16 string lengths.
/// </summary>
public sealed record PollLimits(
    int TitleMax = 120,
    int OptionLabelMax = 80,
    int OptionDescriptionMax = 1000,
    int DescriptionMax = 2000,
    int OptionsMax = 100)
{
    public static readonly PollLimits Default = new();
}
