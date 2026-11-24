using Telrang.Core.Polls;
using static Telrang.Web.Tests.StructuredResultsTests;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>DESIGN.md §34.3: the vote inputs carry the server's rules as HTML constraints; the server stays authoritative.</summary>
public class VoteInputConstraintTests
{
    [Fact]
    public async Task Vote_inputs_carry_the_server_rules_as_html_constraints()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, new PollDraft
        {
            Title = "Form",
            Pages = [new PageDraft { Key = "a", Title = "One" }],
            Questions =
            [
                new QuestionDraft { Prompt = "Name it", Type = PollType.Text, Required = true, MinLength = 3, MaxLength = 40, Page = "a" },
                new QuestionDraft { Prompt = "Weight", Type = PollType.Number, Required = true, Decimals = 2, Max = 500, Page = "a" },
                new QuestionDraft { Prompt = "Pick", Type = PollType.Choice, Required = true, Page = "a", Options = [new() { Label = "A" }, new() { Label = "B" }] },
                new QuestionDraft { Prompt = "Many", Type = PollType.Choice, Required = true, MaxSelect = 2, Page = "a", Options = [new() { Label = "C" }, new() { Label = "D" }] },
                new QuestionDraft { Prompt = "Optional", Type = PollType.Text, Page = "a" },
            ],
        });
        var html = await PageAsync(factory.CreateBrowser(), $"/p/{id}");
        Assert.Matches("name=\"q1.text\"[^>]*maxlength=\"40\" minlength=\"3\" required", html);
        Assert.Matches("name=\"q2.number\"[^>]*step=\"0.01\" min=\"-1000000000000000\" max=\"500\"\\s+required", html);
        Assert.Matches("name=\"q3.c\" value=\"a\"\\s+required", html);
        Assert.DoesNotMatch("name=\"q4.c\" value=\"c\"\\s+required", html); // "at least one checkbox" is not HTML; the server checks
        Assert.DoesNotMatch("name=\"q5.text\"[^>]*required", html);
    }
}
