using System.Net;
using System.Text;
using Telrang.Core.Ballots;
using Telrang.Core.Polls;
using Telrang.Web.Features.Polls;
using Telrang.Web.Features.Results;
using Telrang.Web.Features.Voting;
using Telrang.Web.Security;
using Microsoft.Extensions.DependencyInjection;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>DESIGN.md §20.3: invite link aliases, owner-only response aliases, owner views and the ballots CSV.</summary>
public class AliasTests
{
    private const string Alias = "Zebra Aunt";
    private static readonly RankedBallot ParisFirst = new(["paris"], []);
    private static readonly RankedBallot RomeFirst = new(["rome"], []);

    private static Task<T> Polls<T>(AppFactory factory, Func<PollService, Task<T>> action) =>
        WithScope(factory, sp => action(sp.GetRequiredService<PollService>()));

    private static async Task<(string Token, string Hash)> InviteAsync(AppFactory factory, string owner, string id, string? alias)
    {
        var invite = (await Polls(factory, p => p.GenerateInvitesAsync(owner, id, 1, alias is null ? [] : [alias])))!.Single();
        Assert.True(InviteTokens.TryHash(invite.Token, out var hash));
        return (invite.Token, hash);
    }

    private static Task<(AliasStatus Status, string? Error)> SetAliasAsync(AppFactory factory, string userId, string id, Guid ballotId, string? alias) =>
        Polls(factory, p => p.SetResponseAliasAsync(userId, id, ballotId, alias));

    private static async Task<string> PageAsync(HttpClient client, string path) =>
        WebUtility.HtmlDecode(await (await GetPageAsync(client, path)).Content.ReadAsStringAsync());

    [Fact]
    public async Task Invite_alias_is_copied_onto_a_new_response_and_the_owner_edit_wins()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft(voters: VoterMode.Invite).Many());
        var (_, hash) = await InviteAsync(factory, owner, id, "  " + Alias + "  ");
        Assert.Equal(Alias, Assert.Single((await Polls(factory, p => p.ListInvitesAsync(owner, id)))!).Alias);

        var visitor = new VisitorContext(null, null, hash);
        var first = await SubmitRankedAsync(factory, id, visitor, ParisFirst, "Ann");
        Assert.True(first.Succeeded);
        var ballot = Assert.Single(await BallotsAsync(factory, id));
        Assert.Equal((Alias, "Ann"), (ballot.Alias, ballot.DisplayName));

        // The owner clears it; a revision by the voter does not bring it back.
        Assert.Equal(AliasStatus.Ok, (await SetAliasAsync(factory, owner, id, ballot.Id, " ")).Status);
        Assert.True((await WithScope(factory, sp => sp.GetRequiredService<BallotService>()
            .SubmitRankedAsync(id, visitor, RomeFirst, "Ann B", first.ResponseNo))).Succeeded);
        ballot = Assert.Single(await BallotsAsync(factory, id));
        Assert.Equal((null, "Ann B"), (ballot.Alias, ballot.DisplayName));

        // A second response through the same link (claimed by an account by now) starts with the link's alias again.
        var me = await CreateUserAsync(factory, "me@example.com");
        Assert.True((await SubmitRankedAsync(factory, id, new VisitorContext(me, null, hash), ParisFirst, "Me")).Succeeded);
        var ballots = await BallotsAsync(factory, id);
        Assert.Equal(2, ballots.Count);
        Assert.All(ballots, b => Assert.Equal(me, b.UserId));
        Assert.Equal(Alias, ballots.Single(b => b.DisplayName == "Me").Alias);
        Assert.Null(ballots.Single(b => b.DisplayName == "Ann B").Alias);
    }

    [Fact]
    public async Task Long_invite_aliases_are_cut_to_the_voter_name_limit()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft(voters: VoterMode.Invite));
        await InviteAsync(factory, owner, id, new string('x', 200));
        var max = BallotService.NameLimit(factory.Settings.Current.Limits);
        Assert.Equal(max, Assert.Single((await Polls(factory, p => p.ListInvitesAsync(owner, id)))!).Alias!.Length);
    }

    [Fact]
    public async Task Owner_sets_and_clears_aliases_in_any_poll_and_nobody_else_can()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var other = await CreateUserAsync(factory, "x@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft(voters: VoterMode.Public));
        var otherPoll = await CreatePollAsync(factory, other, RankedDraft(voters: VoterMode.Public));
        Assert.True((await SubmitRankedAsync(factory, id, new VisitorContext(null, "browser-1", null), ParisFirst, "Ann")).Succeeded);
        var ballot = Assert.Single(await BallotsAsync(factory, id));
        Assert.Null(ballot.Alias); // public poll: no link, no alias

        Assert.Equal(AliasStatus.Ok, (await SetAliasAsync(factory, owner, id, ballot.Id, "  " + Alias + " ")).Status);
        Assert.Equal(Alias, Assert.Single(await BallotsAsync(factory, id)).Alias);
        var listed = Assert.Single((await Polls(factory, p => p.ListResponsesAsync(owner, id)))!);
        Assert.Equal((ballot.Id, "Ann", Alias, Alias), (listed.Id, listed.Name, listed.Alias, listed.Shown));

        // Too long: refused with the limit; the stored alias stays.
        var max = BallotService.NameLimit(factory.Settings.Current.Limits);
        var tooLong = await SetAliasAsync(factory, owner, id, ballot.Id, new string('y', max + 1));
        Assert.Equal(AliasStatus.Invalid, tooLong.Status);
        Assert.Equal($"Shorten the alias to {max} characters.", tooLong.Error);
        Assert.Equal(Alias, Assert.Single(await BallotsAsync(factory, id)).Alias);

        // Not the owner, a ballot of another poll, an unknown ballot: not found, nothing changes.
        Assert.Equal(AliasStatus.NotFound, (await SetAliasAsync(factory, other, id, ballot.Id, "Mallory")).Status);
        Assert.Equal(AliasStatus.NotFound, (await SetAliasAsync(factory, other, otherPoll, ballot.Id, "Mallory")).Status);
        Assert.Equal(AliasStatus.NotFound, (await SetAliasAsync(factory, owner, id, Guid.NewGuid(), "Mallory")).Status);
        Assert.Null(await Polls(factory, p => p.ListResponsesAsync(other, id)));
        Assert.Equal(Alias, Assert.Single(await BallotsAsync(factory, id)).Alias);

        // The owner's share page lists it; another account gets a 404 there.
        var ownerShare = await PageAsync(await LoggedInBrowserAsync(factory, "o@example.com"), $"/polls/{id}/share");
        Assert.Contains("Responses", ownerShare);
        Assert.Contains(Alias, ownerShare);
        Assert.Contains("entered as Ann", ownerShare);
        Assert.Equal(HttpStatusCode.NotFound, (await (await LoggedInBrowserAsync(factory, "x@example.com")).GetAsync($"/polls/{id}/share")).StatusCode);

        Assert.Equal(AliasStatus.Ok, (await SetAliasAsync(factory, owner, id, ballot.Id, null)).Status);
        Assert.Null(Assert.Single(await BallotsAsync(factory, id)).Alias);
    }

    [Fact]
    public async Task Ballots_csv_has_name_and_alias_columns()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft(voters: VoterMode.Invite));
        var (_, aliased) = await InviteAsync(factory, owner, id, "=Aunt");
        var (_, plain) = await InviteAsync(factory, owner, id, null);
        Assert.True((await SubmitRankedAsync(factory, id, new VisitorContext(null, null, aliased), ParisFirst, "Ann")).Succeeded);
        Assert.True((await SubmitRankedAsync(factory, id, new VisitorContext(null, null, plain), RomeFirst, "Bob")).Succeeded);

        var browser = await LoggedInBrowserAsync(factory, "o@example.com");
        var lines = Encoding.UTF8.GetString((await browser.GetByteArrayAsync($"/polls/{id}/export.csv"))[3..]).Split("\r\n");
        Assert.StartsWith("Name,Alias,SubmittedAt,UpdatedAt,", lines[0]);
        var rows = lines.Skip(1).Where(l => l.Length > 0).ToList();
        Assert.Contains(rows, l => l.StartsWith("Ann,'=Aunt,")); // user text, guarded against formulas
        Assert.Contains(rows, l => l.StartsWith("Bob,,"));
    }

    [Fact]
    public async Task Dates_grid_shows_the_alias_even_without_names_and_voters_never_see_it()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner,
            Dates("Meetup", "2026-11-03", "2026-11-05").Results(ResultsVisibility.AfterVote).Voters(VoterMode.Invite).Names(NamesMode.None));
        var (token, hash) = await InviteAsync(factory, owner, id, Alias);
        var (_, plainHash) = await InviteAsync(factory, owner, id, null);

        // The voter opens the link in a browser and votes (through the service, with the browser's identity).
        var (voterBrowser, jar) = factory.CreateBrowserWithJar();
        Assert.Equal(HttpStatusCode.OK, (await GetPageAsync(voterBrowser, $"/p/{id}?t={token}")).StatusCode);
        var visitor = VisitorFrom(factory, jar, id);
        Assert.Equal(hash, visitor.InviteTokenHash);
        var answers = new DatesBallot(new Dictionary<string, Availability> { ["2026-11-03"] = Availability.Yes });
        Assert.True((await WithScope(factory, sp => sp.GetRequiredService<BallotService>().SubmitDatesAsync(id, visitor, answers, null))).Succeeded);
        Assert.True((await WithScope(factory, sp => sp.GetRequiredService<BallotService>()
            .SubmitDatesAsync(id, new VisitorContext(null, null, plainHash), answers, null))).Succeeded);

        // Owner: names: none, but the aliased response gets a grid row with its alias.
        var grid = (await WithScope(factory, async sp =>
        {
            var access = await sp.GetRequiredService<PollAccessService>().GetAsync(id, VisitorContext.ForUser(owner));
            return await sp.GetRequiredService<ResultsService>().BallotsAsync(access!, 1);
        }))!.Dates!;
        Assert.Equal([Alias, DatesGridRow.AnonymousName], grid.Select(g => g.Name));
        var ownerBrowser = await LoggedInBrowserAsync(factory, "o@example.com");
        var ownerResults = await PageAsync(ownerBrowser, $"/p/{id}/results?view=ballots");
        Assert.Contains("Who can when", ownerResults);
        Assert.Contains(Alias, ownerResults);
        // DESIGN.md §33: the summary carries no ballot table.
        Assert.DoesNotContain("Who can when", await PageAsync(ownerBrowser, $"/p/{id}/results"));

        // The voter: vote page and results page never carry it.
        var votePage = await PageAsync(voterBrowser, $"/p/{id}");
        Assert.Contains("Meetup", votePage);
        Assert.DoesNotContain(Alias, votePage);
        var voterResults = await PageAsync(voterBrowser, $"/p/{id}/results");
        Assert.Contains("Best:", voterResults);
        Assert.DoesNotContain(Alias, voterResults);
        Assert.DoesNotContain("Who can when", voterResults);
        Assert.DoesNotContain("view=ballots", voterResults);
        var voterBallots = await PageAsync(voterBrowser, $"/p/{id}/results?view=ballots");
        Assert.DoesNotContain(Alias, voterBallots);
        Assert.DoesNotContain("Who can when", voterBallots);
    }

    [Fact]
    public async Task Ranked_results_and_my_votes_never_show_the_alias_to_the_voter()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        await CreateUserAsync(factory, "me@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft(voters: VoterMode.Invite, results: ResultsVisibility.AfterVote));
        var (token, _) = await InviteAsync(factory, owner, id, Alias);

        var me = await LoggedInBrowserAsync(factory, "me@example.com");
        Assert.Equal(HttpStatusCode.OK, (await GetPageAsync(me, $"/p/{id}?t={token}")).StatusCode);
        var meId = await WithScope(factory, async sp =>
            (await sp.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Telrang.Web.Data.ApplicationUser>>().FindByEmailAsync("me@example.com"))!.Id);
        Assert.True(InviteTokens.TryHash(token, out var hash));
        Assert.True((await SubmitRankedAsync(factory, id, new VisitorContext(meId, null, hash), ParisFirst, "Me")).Succeeded);
        Assert.Equal(Alias, Assert.Single(await BallotsAsync(factory, id)).Alias);

        foreach (var path in new[] { $"/p/{id}", $"/p/{id}/results", "/my/votes" })
        {
            var html = await PageAsync(me, path);
            Assert.DoesNotContain(Alias, html);
        }
    }
}
