using System.Net;
using Telrang.Core.Ballots;
using Telrang.Core.Polls;
using Telrang.Web.Features.Polls;
using Telrang.Web.Features.Results;
using Telrang.Web.Features.Voting;
using Telrang.Web.Security;
using Microsoft.Extensions.DependencyInjection;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>DESIGN.md §33: the ballots view of the results page (<c>?view=ballots</c>).</summary>
public class BallotsViewTests
{
    private const string Alias = "Zebra Aunt";

    private static async Task<string> PageAsync(HttpClient client, string path) =>
        WebUtility.HtmlDecode(await (await GetPageAsync(client, path)).Content.ReadAsStringAsync());

    private static Task<BallotTable?> TableAsync(AppFactory factory, string id, VisitorContext viewer) =>
        WithScope(factory, async sp =>
            await sp.GetRequiredService<ResultsService>().BallotsAsync((await sp.GetRequiredService<PollAccessService>().GetAsync(id, viewer))!, 1));

    /// <summary>An invite poll (results for voters) with two links, the first aliased; one ballot through each: "Ann"
    /// on the aliased link, no name on the other.</summary>
    private static async Task<(string Id, string Owner, VisitorContext Aliased, VisitorContext Plain)> PollAsync(AppFactory factory, PollType type, bool showBallots)
    {
        var owner = await CreateUserAsync(factory, "o@example.com");
        var draft = (type switch
        {
            PollType.Ranked => RankedDraft(ResultsVisibility.AfterVote, VoterMode.Invite),
            PollType.Points => PointsDraft(ResultsVisibility.AfterVote, VoterMode.Invite),
            _ => Dates("Meetup", "2026-11-03", "2026-11-05").Results(ResultsVisibility.AfterVote).Voters(VoterMode.Invite),
        }).With(d => d.ShowBallots = showBallots);
        var id = await CreatePollAsync(factory, owner, draft);
        var invites = (await WithScope(factory, sp => sp.GetRequiredService<PollService>().GenerateInvitesAsync(owner, id, 2, [Alias])))!;
        Assert.True(InviteTokens.TryHash(invites[0].Token, out var aliasedHash));
        Assert.True(InviteTokens.TryHash(invites[1].Token, out var plainHash));
        var aliased = new VisitorContext(null, null, aliasedHash);
        var plain = new VisitorContext(null, null, plainHash);
        switch (type)
        {
            case PollType.Ranked:
                Assert.True((await SubmitRankedAsync(factory, id, aliased, new RankedBallot(["rome", "paris"], ["berlin"]), "Ann")).Succeeded);
                Assert.True((await SubmitRankedAsync(factory, id, plain, new RankedBallot(["berlin", "paris", "rome"], []))).Succeeded);
                break;
            case PollType.Points:
                Assert.True((await SubmitPointsAsync(factory, id, aliased, new() { ["paris"] = 7, ["rome"] = 3 }, "Ann")).Succeeded);
                Assert.True((await SubmitPointsAsync(factory, id, plain, new() { ["berlin"] = 10 })).Succeeded);
                break;
            default:
                var yes = new DatesBallot(new Dictionary<string, Availability> { ["2026-11-03"] = Availability.Yes });
                Assert.True((await WithScope(factory, sp => sp.GetRequiredService<BallotService>().SubmitDatesAsync(id, aliased, yes, "Ann"))).Succeeded);
                Assert.True((await WithScope(factory, sp => sp.GetRequiredService<BallotService>().SubmitDatesAsync(id, plain, yes, null))).Succeeded);
                break;
        }
        return (id, owner, aliased, plain);
    }

    [Theory]
    [InlineData(PollType.Ranked, "ballots-ranked")]
    [InlineData(PollType.Points, "ballots-points")]
    [InlineData(PollType.Dates, "dates-grid")]
    public async Task Owner_sees_the_ballots_view_with_aliases_and_the_summary_has_no_table(PollType type, string table)
    {
        await using var factory = new AppFactory();
        var (id, owner, _, _) = await PollAsync(factory, type, showBallots: false);
        var browser = await LoggedInBrowserAsync(factory, "o@example.com");

        var summary = await PageAsync(browser, $"/p/{id}/results");
        Assert.Contains($"href=\"p/{id}/results?view=ballots\" data-enhance-nav=\"false\">Ballots<", summary);
        Assert.DoesNotContain(table, summary);
        Assert.DoesNotContain(Alias, summary);
        Assert.DoesNotContain("Anonymous", summary);

        var html = await PageAsync(browser, $"/p/{id}/results?view=ballots");
        Assert.Contains(table, html);
        Assert.Contains($">{Alias}<", html);
        Assert.Contains(">Anonymous<", html);
        Assert.DoesNotContain(">Ann<", html); // the alias wins over the name as entered
        Assert.Contains("Summary<", html);
        Assert.DoesNotContain(" style=\"", html);
        // The owner's live updates (DESIGN.md §21.2) stay on: js/results-live.js refetches this same URL.
        Assert.Contains($"data-live-src=\"p/{id}/results/live\"", html);
        Assert.Contains("results-live", html);

        var t = (await TableAsync(factory, id, VisitorContext.ForUser(owner)))!;
        Assert.Equal(2, t.Window.Total);
    }

    [Theory]
    [InlineData(PollType.Ranked)]
    [InlineData(PollType.Points)]
    [InlineData(PollType.Dates)]
    public async Task Voters_with_show_ballots_see_names_never_aliases(PollType type)
    {
        await using var factory = new AppFactory();
        var (id, _, aliased, _) = await PollAsync(factory, type, showBallots: true);
        var t = (await TableAsync(factory, id, aliased))!;
        IEnumerable<string> names = type switch
        {
            PollType.Ranked => t.Ranked!.Select(c => c.Name),
            PollType.Points => t.Points!.Columns.Select(c => c.Name),
            _ => t.Dates!.Select(r => r.Name),
        };
        Assert.Equal(["Ann", "Anonymous"], names);
    }

    [Theory]
    [InlineData(PollType.Ranked)]
    [InlineData(PollType.Points)]
    [InlineData(PollType.Dates)]
    public async Task Voters_without_show_ballots_get_no_link_and_no_ballots(PollType type)
    {
        await using var factory = new AppFactory();
        var (id, _, aliased, _) = await PollAsync(factory, type, showBallots: false);
        Assert.Null(await TableAsync(factory, id, aliased));
        Assert.Null(await TableAsync(factory, id, VisitorContext.ForUser(null)));
    }

    [Fact]
    public async Task Pages_follow_the_voters_link_cookie()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, PointsDraft(ResultsVisibility.AfterVote, VoterMode.Invite));
        var invite = (await WithScope(factory, sp => sp.GetRequiredService<PollService>().GenerateInvitesAsync(owner, id, 1, [Alias])))!.Single();
        Assert.True(InviteTokens.TryHash(invite.Token, out var hash));
        Assert.True((await SubmitPointsAsync(factory, id, new VisitorContext(null, null, hash), new() { ["rome"] = 10 }, "Ann")).Succeeded);

        // Without Show ballots: no link, and asking for the view shows the summary.
        var (browser, _) = factory.CreateBrowserWithJar();
        Assert.Equal(HttpStatusCode.OK, (await GetPageAsync(browser, $"/p/{id}?t={invite.Token}")).StatusCode);
        var summary = await PageAsync(browser, $"/p/{id}/results");
        Assert.Contains("Total", summary);
        Assert.DoesNotContain("view=ballots", summary);
        var asked = await PageAsync(browser, $"/p/{id}/results?view=ballots");
        Assert.DoesNotContain("ballots-points", asked);
        Assert.DoesNotContain("Ann", asked);
        Assert.DoesNotContain(Alias, asked);
        Assert.DoesNotContain("Summary<", asked);
    }

    [Fact]
    public async Task Ranked_columns_list_the_ranking_to_the_longest_and_the_bans()
    {
        await using var factory = new AppFactory();
        var (id, owner, _, _) = await PollAsync(factory, PollType.Ranked, showBallots: false);
        var columns = (await TableAsync(factory, id, VisitorContext.ForUser(owner)))!.Ranked!;
        Assert.Equal(["Rome", "Paris"], columns[0].Ranked);
        Assert.Equal(["Berlin"], columns[0].Banned);
        Assert.Equal(["Berlin", "Paris", "Rome"], columns[1].Ranked);
        Assert.Empty(columns[1].Banned);

        var html = await PageAsync(await LoggedInBrowserAsync(factory, "o@example.com"), $"/p/{id}/results?view=ballots");
        Assert.Contains(">1st<", html);
        Assert.Contains(">3rd<", html);
        Assert.DoesNotContain(">4th<", html);
        Assert.Contains(">Banned<", html);
        Assert.Contains("ballots-sticky", html);
    }

    [Fact]
    public async Task Points_rows_follow_the_results_order_and_zero_is_empty()
    {
        await using var factory = new AppFactory();
        var (id, owner, _, _) = await PollAsync(factory, PollType.Points, showBallots: false);
        var points = (await TableAsync(factory, id, VisitorContext.ForUser(owner)))!.Points!;
        // Totals: Berlin 10, Paris 7, Rome 3.
        Assert.Equal(["Berlin", "Paris", "Rome"], points.Options);
        Assert.Equal([0, 7, 3], points.Columns[0].Points);
        Assert.Equal([10, 0, 0], points.Columns[1].Points);

        var html = await PageAsync(await LoggedInBrowserAsync(factory, "o@example.com"), $"/p/{id}/results?view=ballots");
        Assert.Contains("<td class=\"ballots-cell ballots-number\"></td>", html);
        Assert.Contains("<td class=\"ballots-cell ballots-number\">7</td>", html);
    }
}
