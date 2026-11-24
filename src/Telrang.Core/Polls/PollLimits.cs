namespace Telrang.Core.Polls;

/// <summary>
/// Size caps checked by <see cref="PollDraftValidator"/> and <see cref="OptionListParser"/> (DESIGN.md §14.4, §20.4).
/// Character counts are UTF-16 string lengths; <see cref="ResponseTextBytesMax"/> counts UTF-8 bytes (DESIGN.md §34.5).
/// </summary>
public sealed record PollLimits(
    int TitleMax = 120,
    int OptionLabelMax = 80,
    int OptionDescriptionMax = 1000,
    int DescriptionMax = 2000,
    int OptionsMax = 100,
    int QuestionsMax = 50,
    int PagesMax = 20,
    int GroupsMax = 30,
    int PromptMax = 200,
    int HelpMax = 1000,
    int TextAnswerMax = 2000,
    int ResponseTextBytesMax = 8192)
{
    public static readonly PollLimits Default = new();
}
