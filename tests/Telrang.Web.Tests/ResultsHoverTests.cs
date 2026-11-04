using System.Text.RegularExpressions;
using Telrang.Web.Features.Polls;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>DESIGN.md §33: every results bar names its value on hover.</summary>
public partial class ResultsHoverTests
{
    [Theory]
    [InlineData(1, "1st")]
    [InlineData(2, "2nd")]
    [InlineData(3, "3rd")]
    [InlineData(4, "4th")]
    [InlineData(11, "11th")]
    [InlineData(12, "12th")]
    [InlineData(13, "13th")]
    [InlineData(21, "21st")]
    [InlineData(102, "102nd")]
    [InlineData(112, "112th")]
    public void Ordinals(int n, string expected) => Assert.Equal(expected, PollText.Ordinal(n));

    [Fact]
    public void Voter_share_leaves_out_the_percentage_without_voters()
    {
        Assert.Equal("3 voters (43%)", PollText.VotersShare(3, 7));
        Assert.Equal("1 voter (14%)", PollText.VotersShare(1, 7));
        Assert.Equal("0 voters", PollText.VotersShare(0, 0));
    }

    [GeneratedRegex(@"<title>(?<t>[^<]*)</title>\s*<rect class=""hist-hit""")]
    private static partial Regex HitTitle();

    /// <summary>The home page examples render the real results views (7 ballots each).</summary>
    [Fact]
    public async Task Bars_and_histogram_positions_carry_their_values()
    {
        await using var factory = new AppFactory();
        var html = await (await GetPageAsync(factory.CreateBrowser(), "/")).Content.ReadAsStringAsync();

        Assert.Matches(@"<title>Score [\d.]+ · 1st: \d+ · \d+ bans?</title>", html);
        Assert.Matches(@"<title>Total \d+ · mean [\d.]+ · \d+ supporters?</title>", html);
        // The whole ranks histogram has a summary; used positions and every points bucket a full-height hit area.
        Assert.Matches(@"<title>(1st: \d+ voters? \(\d+%\)|Not ranked)", html);
        var hits = HitTitle().Matches(html).Select(m => m.Groups["t"].Value).ToList();
        Assert.Contains(hits, t => Regex.IsMatch(t, @"^\d+(st|nd|rd|th): \d+ voters? \(\d+%\)$"));
        Assert.Contains(hits, t => Regex.IsMatch(t, @"^0 points: \d+ voters? \(\d+%\)$"));
    }
}
