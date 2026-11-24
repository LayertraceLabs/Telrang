using Microsoft.Extensions.DependencyInjection;
using Telrang.Core.Polls;
using Telrang.Web.Features.Polls;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>
/// DESIGN.md §34 step 1 lets Core hold several questions, pages, groups and the new types; the vote page takes them since
/// step 2, the editor and the results not yet, so <see cref="PollParsing.Validate"/> refuses them unless the temporary switch
/// <see cref="PollParsing.TemporaryStructuredPollsSwitch"/> is on (tests and e2e; removed in step 4).
/// </summary>
public class MultiQuestionGateTests
{
    private static QuestionDraft Q(PollType type = PollType.Ranked, string prompt = "Q") => new()
    {
        Type = type,
        Prompt = prompt,
        Budget = 10,
        Options = [new OptionDraft { Label = "A" }, new OptionDraft { Label = "B" }],
    };

    private static IReadOnlyList<DraftError> Refused(PollDraft d)
    {
        var r = PollParsing.Validate(d, PollLimits.Default);
        Assert.False(r.Success);
        return r.Errors;
    }

    [Fact]
    public void A_single_ranked_or_points_question_still_passes()
    {
        foreach (var type in new[] { PollType.Ranked, PollType.Points })
            Assert.True(PollParsing.Validate(new PollDraft { Title = "T", Questions = [Q(type)] }, PollLimits.Default).Success);
    }

    [Fact]
    public void More_than_one_question_is_refused()
    {
        var errors = Refused(new PollDraft { Title = "T", Questions = [Q(), Q()] });
        Assert.Contains(errors, e => e.Field == "questions");
    }

    [Theory]
    [InlineData(PollType.Choice)]
    [InlineData(PollType.Text)]
    [InlineData(PollType.Number)]
    public void The_new_types_are_refused(PollType type)
    {
        var errors = Refused(new PollDraft { Title = "T", Questions = [Q(type)] });
        Assert.Contains(errors, e => e.Field == "questions[0].type");
    }

    [Fact]
    public void Pages_and_groups_are_refused()
    {
        var q = Q();
        q.Page = "p";
        q.Group = "g";
        var errors = Refused(new PollDraft
        {
            Title = "T",
            Pages = [new PageDraft { Key = "p", Title = "One" }],
            Groups = [new GroupDraft { Key = "g", Title = "Box" }],
            Questions = [q],
        });
        Assert.Contains(errors, e => e.Field == "pages");
        Assert.Contains(errors, e => e.Field == "groups");
    }

    [Fact]
    public void The_temporary_switch_lets_them_through()
    {
        Assert.True(PollParsing.Validate(MultiPageVoteTests.Survey(), PollLimits.Default, allowStructured: true).Success);
        Assert.True(PollParsing.Validate(new PollDraft { Title = "T", Questions = [Q(PollType.Number)] }, PollLimits.Default, allowStructured: true).Success);
    }

    [Fact]
    public async Task A_normal_instance_cannot_create_them()
    {
        await using var factory = new AppFactory(new Dictionary<string, string?> { [PollParsing.TemporaryStructuredPollsSwitch] = "false" });
        var owner = await CreateUserAsync(factory, "o@example.com");
        var result = await WithScope(factory, sp => sp.GetRequiredService<PollService>().CreatePollAsync(owner, MultiPageVoteTests.Survey()));
        Assert.Equal(PollSaveStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, e => e.Field == "questions");
    }
}
