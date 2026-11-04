using System.Net;
using Telrang.Core.Ballots;
using Telrang.Core.Ids;
using Telrang.Core.Polls;
using Telrang.Web.Data;
using Telrang.Web.Features.Polls;
using Telrang.Web.Features.Results;
using Telrang.Web.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>DESIGN.md §25: results for (owner / voters / anyone), show results (while voting / after closing), show ballots.</summary>
public class ResultsSettingsTests
{
    private static readonly RankedBallot ParisFirst = new(["paris", "rome"], ["berlin"]);
    private const string Alias = "Zebra Aunt";

    private static PollDraft Draft(VoterMode voters, ResultsVisibility results, ResultsTiming timing, bool ballots = false) =>
        RankedDraft(results, voters).With(d => { d.ResultsTiming = timing; d.ShowBallots = ballots; });

    private static Task<PollAccess?> AccessAsync(AppFactory factory, string id, VisitorContext visitor) =>
        WithScope(factory, sp => sp.GetRequiredService<PollAccessService>().GetAsync(id, visitor));

    private static async Task<string> PageAsync(HttpClient client, string path) =>
        WebUtility.HtmlDecode(await (await GetPageAsync(client, path)).Content.ReadAsStringAsync());

    /// <summary>
    /// Every audience × timing in each voter mode: who sees results before and after the close. "voter" has a ballot, "other"
    /// is a voter identity of the poll without one.
    /// </summary>
    [Theory]
    [InlineData(VoterMode.Public, ResultsVisibility.Creator, ResultsTiming.WhileOpen, "", "")]
    [InlineData(VoterMode.Public, ResultsVisibility.AfterVote, ResultsTiming.WhileOpen, "voter", "voter")]
    [InlineData(VoterMode.Public, ResultsVisibility.AfterVote, ResultsTiming.AfterClose, "", "voter")]
    [InlineData(VoterMode.Public, ResultsVisibility.Open, ResultsTiming.WhileOpen, "voter,other,anon", "voter,other,anon")]
    [InlineData(VoterMode.Public, ResultsVisibility.Open, ResultsTiming.AfterClose, "", "voter,other,anon")]
    [InlineData(VoterMode.Invite, ResultsVisibility.Creator, ResultsTiming.WhileOpen, "", "")]
    [InlineData(VoterMode.Invite, ResultsVisibility.AfterVote, ResultsTiming.WhileOpen, "voter", "voter")]
    [InlineData(VoterMode.Invite, ResultsVisibility.AfterVote, ResultsTiming.AfterClose, "", "voter")]
    [InlineData(VoterMode.Accounts, ResultsVisibility.Creator, ResultsTiming.WhileOpen, "", "")]
    [InlineData(VoterMode.Accounts, ResultsVisibility.AfterVote, ResultsTiming.WhileOpen, "voter", "voter")]
    [InlineData(VoterMode.Accounts, ResultsVisibility.AfterVote, ResultsTiming.AfterClose, "", "voter")]
    public async Task Results_audience_and_timing(VoterMode mode, ResultsVisibility results, ResultsTiming timing, string open, string closed)
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var voterId = await CreateUserAsync(factory, "v@example.com");
        var otherId = await CreateUserAsync(factory, "x@example.com");
        var id = await CreatePollAsync(factory, owner, Draft(mode, results, timing));

        VisitorContext voter, other;
        if (mode == VoterMode.Invite)
        {
            var links = (await WithScope(factory, sp => sp.GetRequiredService<PollService>().GenerateInvitesAsync(owner, id, 2, [])))!;
            Assert.True(InviteTokens.TryHash(links[0].Token, out var h1));
            Assert.True(InviteTokens.TryHash(links[1].Token, out var h2));
            (voter, other) = (new VisitorContext(null, null, h1), new VisitorContext(null, null, h2));
        }
        else
        {
            (voter, other) = (VisitorContext.ForUser(voterId), VisitorContext.ForUser(otherId));
        }
        Assert.True((await SubmitRankedAsync(factory, id, voter, ParisFirst)).Succeeded);
        var anon = new VisitorContext(null, mode == VoterMode.Public ? "anonymous-browser-key-0000000000000" : null, null);

        async Task<string> WhoSees()
        {
            Assert.True((await AccessAsync(factory, id, VisitorContext.ForUser(owner)))!.CanSeeResults);
            var who = new List<string>();
            foreach (var (name, visitor) in new[] { ("voter", voter), ("other", other), ("anon", anon) })
                if ((await AccessAsync(factory, id, visitor))!.CanSeeResults) who.Add(name);
            return string.Join(",", who);
        }

        Assert.Equal(open, await WhoSees());
        Assert.True(await WithScope(factory, sp => sp.GetRequiredService<PollService>().SetClosedAsync(owner, id, true)));
        Assert.Equal(closed, await WhoSees());
    }

    [Fact]
    public async Task Results_page_explains_when_results_open()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        await CreateUserAsync(factory, "v@example.com");
        var id = await CreatePollAsync(factory, owner, Draft(VoterMode.Public, ResultsVisibility.AfterVote, ResultsTiming.AfterClose));
        var browser = await LoggedInBrowserAsync(factory, "v@example.com");
        var vote = await PageAsync(browser, $"/p/{id}");
        Assert.Contains("Results: voters, after closing.", vote);
        Assert.Contains("Results open to voters when the poll closes.", await PageAsync(browser, $"/p/{id}/results"));

        var voterId = await WithScope(factory, async sp => (await sp.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>()
            .FindByEmailAsync("v@example.com"))!.Id);
        Assert.True((await SubmitRankedAsync(factory, id, VisitorContext.ForUser(voterId), ParisFirst)).Succeeded);
        var hidden = await GetPageAsync(browser, $"/p/{id}/results");
        Assert.Equal(HttpStatusCode.Forbidden, hidden.StatusCode);
        Assert.Contains("Results open when the poll closes.", WebUtility.HtmlDecode(await hidden.Content.ReadAsStringAsync()));
        Assert.DoesNotContain("See results", await PageAsync(browser, $"/p/{id}"));

        Assert.True(await WithScope(factory, sp => sp.GetRequiredService<PollService>().SetClosedAsync(owner, id, true)));
        Assert.Equal(HttpStatusCode.OK, (await GetPageAsync(browser, $"/p/{id}/results")).StatusCode);

        var share = await PageAsync(await LoggedInBrowserAsync(factory, "o@example.com"), $"/polls/{id}/share");
        Assert.Contains("Results: voters, after closing.", share);
    }

    [Fact]
    public async Task Ballot_rows_show_names_as_entered_and_never_the_alias()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, Draft(VoterMode.Invite, ResultsVisibility.AfterVote, ResultsTiming.WhileOpen, ballots: true));
        var invites = (await WithScope(factory, sp => sp.GetRequiredService<PollService>().GenerateInvitesAsync(owner, id, 2, [Alias])))!;
        Assert.True(InviteTokens.TryHash(invites[0].Token, out var aliased));
        Assert.True(InviteTokens.TryHash(invites[1].Token, out var plain));
        Assert.True((await SubmitRankedAsync(factory, id, new VisitorContext(null, null, aliased), ParisFirst, "Ann")).Succeeded);
        Assert.True((await SubmitRankedAsync(factory, id, new VisitorContext(null, null, plain), new RankedBallot(["berlin"], []))).Succeeded);
        Assert.Equal(Alias, (await BallotsAsync(factory, id)).Single(b => b.DisplayName == "Ann").Alias);

        var voter = await AccessAsync(factory, id, new VisitorContext(null, null, aliased));
        Assert.True(voter!.CanSeeBallots);
        var table = (await WithScope(factory, sp => sp.GetRequiredService<ResultsService>().BallotsAsync(voter, 1)))!;
        var columns = table.Ranked!;
        Assert.Equal(["Ann", "Anonymous"], columns.Select(r => r.Name));
        Assert.Equal(["Paris", "Rome"], columns[0].Ranked);
        Assert.Equal(["Berlin"], columns[0].Banned);
        Assert.Equal(["Berlin"], columns[1].Ranked);
        Assert.Empty(columns[1].Banned);

        // The owner sees the alias (DESIGN.md §33), else the name as entered.
        var ownerAccess = await AccessAsync(factory, id, VisitorContext.ForUser(owner));
        Assert.Equal([Alias, "Anonymous"], (await WithScope(factory, sp => sp.GetRequiredService<ResultsService>().BallotsAsync(ownerAccess!, 1)))!
            .Ranked!.Select(c => c.Name));

        // The page: through the voter's own link cookie. The summary links to the ballots view and has no ballot table.
        var (browser, _) = factory.CreateBrowserWithJar();
        Assert.Equal(HttpStatusCode.OK, (await GetPageAsync(browser, $"/p/{id}?t={invites[0].Token}")).StatusCode);
        var summary = await PageAsync(browser, $"/p/{id}/results");
        Assert.Contains($"href=\"p/{id}/results?view=ballots\"", summary);
        Assert.DoesNotContain("ballots-table", summary);
        Assert.DoesNotContain(">Ann<", summary);
        var html = await PageAsync(browser, $"/p/{id}/results?view=ballots");
        Assert.Contains("ballots-ranked", html);
        Assert.Contains($"href=\"p/{id}/results\" data-enhance-nav=\"false\">Summary<", html);
        Assert.Contains(">Ann<", html);
        Assert.Contains(">Anonymous<", html);
        Assert.Contains(">1st<", html);
        Assert.Contains(">2nd<", html);
        Assert.Contains(">Banned<", html);
        Assert.DoesNotContain(Alias, html);
        Assert.DoesNotContain(" style=\"", html);

        // Not shown: a stranger without a ballot can see neither.
        Assert.False((await AccessAsync(factory, id, VisitorContext.ForUser(null)))!.CanSeeBallots);
    }

    [Fact]
    public async Task Dates_ballots_show_voters_the_grid_with_names_only()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, Dates("Meetup", "2026-11-03", "2026-11-05").Voters(VoterMode.Invite)
            .Results(ResultsVisibility.AfterVote).With(d => d.ShowBallots = true));
        var invites = (await WithScope(factory, sp => sp.GetRequiredService<PollService>().GenerateInvitesAsync(owner, id, 1, [Alias])))!;
        Assert.True(InviteTokens.TryHash(invites[0].Token, out var hash));
        var visitor = new VisitorContext(null, null, hash);
        Assert.True((await WithScope(factory, sp => sp.GetRequiredService<Telrang.Web.Features.Voting.BallotService>().SubmitDatesAsync(id, visitor,
            new DatesBallot(new Dictionary<string, Availability> { ["2026-11-03"] = Availability.Yes }), "Bea"))).Succeeded);

        var voterResults = await WithScope(factory, async sp =>
            await sp.GetRequiredService<ResultsService>().BallotsAsync((await sp.GetRequiredService<PollAccessService>().GetAsync(id, visitor))!, 1));
        var row = Assert.Single(voterResults!.Dates!);
        Assert.Equal("Bea", row.Name);
        Assert.Equal([Availability.Yes, Availability.No], row.Answers);
        var ownerResults = await WithScope(factory, async sp =>
            await sp.GetRequiredService<ResultsService>().BallotsAsync((await sp.GetRequiredService<PollAccessService>().GetAsync(id, VisitorContext.ForUser(owner)))!, 1));
        Assert.Equal(Alias, Assert.Single(ownerResults!.Dates!).Name);
    }

    [Fact]
    public async Task Without_show_ballots_voters_get_no_rows()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var voterId = await CreateUserAsync(factory, "v@example.com");
        var id = await CreatePollAsync(factory, owner, Draft(VoterMode.Public, ResultsVisibility.Open, ResultsTiming.WhileOpen));
        Assert.True((await SubmitRankedAsync(factory, id, VisitorContext.ForUser(voterId), ParisFirst, "Vic")).Succeeded);
        var access = await AccessAsync(factory, id, VisitorContext.ForUser(voterId));
        Assert.True(access!.CanSeeResults);
        Assert.False(access.CanSeeBallots);
        Assert.Null(await WithScope(factory, sp => sp.GetRequiredService<ResultsService>().BallotsAsync(access, 1)));
        var browser = await LoggedInBrowserAsync(factory, "v@example.com");
        var summary = await PageAsync(browser, $"/p/{id}/results");
        Assert.DoesNotContain("Vic", summary);
        Assert.DoesNotContain("view=ballots", summary);
        // Asking for the view anyway shows the summary.
        var asked = await PageAsync(browser, $"/p/{id}/results?view=ballots");
        Assert.Contains("1 voter", asked.Replace("<strong>", "").Replace("</strong>", ""));
        Assert.DoesNotContain("Vic", asked);
        Assert.DoesNotContain("ballots-table", asked);
    }

    [Fact]
    public async Task Invite_poll_stored_with_results_for_anyone_reads_as_voters()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, Draft(VoterMode.Invite, ResultsVisibility.AfterVote, ResultsTiming.WhileOpen));
        Assert.True(PollId.TryDecode(id, out var pollId));
        // As stored before §25: "results": "Open" on an invite poll, without the new fields.
        await WithScope(factory, async sp =>
        {
            var db = sp.GetRequiredService<ApplicationDbContext>();
            var poll = await db.Polls.SingleAsync(p => p.Id == pollId);
            poll.DefinitionJson = poll.DefinitionJson.Replace("\"results\":\"AfterVote\"", "\"results\":\"Open\"", StringComparison.Ordinal)
                .Replace(",\"resultsTiming\":\"WhileOpen\"", "", StringComparison.Ordinal).Replace(",\"showBallots\":false", "", StringComparison.Ordinal);
            poll.ResultsVisibility = ResultsVisibility.Open;
            Assert.Contains("\"results\":\"Open\"", poll.DefinitionJson);
            Assert.DoesNotContain("resultsTiming", poll.DefinitionJson);
            return await db.SaveChangesAsync();
        });
        var stranger = await AccessAsync(factory, id, VisitorContext.ForUser(null));
        Assert.Equal(ResultsVisibility.AfterVote, stranger!.Definition.Results);
        Assert.Equal((ResultsTiming.WhileOpen, false), (stranger.Definition.ResultsTiming, stranger.Definition.ShowBallots));
        Assert.False(stranger.CanSeeResults);
    }

    [Fact]
    public async Task Migration_moves_invite_and_account_polls_from_anyone_to_voters()
    {
        await using var factory = new AppFactory();
        var path = Path.Combine(factory.DataDir, "upgrade-results.db");
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={path}")
            .UseApplicationServiceProvider(factory.Services)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;
        var polls = new Dictionary<string, (Guid Id, VoterMode Mode, ResultsVisibility Results)>
        {
            ["invite-open"] = (Guid.NewGuid(), VoterMode.Invite, ResultsVisibility.Open),
            ["accounts-open"] = (Guid.NewGuid(), VoterMode.Accounts, ResultsVisibility.Open),
            ["public-open"] = (Guid.NewGuid(), VoterMode.Public, ResultsVisibility.Open),
            ["invite-creator"] = (Guid.NewGuid(), VoterMode.Invite, ResultsVisibility.Creator),
        };
        await using (var db = new ApplicationDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync("20261101165631_HideIdentity");
            await InsertLegacyUserAsync(db, "owner", "o@example.com");
            foreach (var (_, (pollId, mode, results)) in polls)
            {
                // A definition as stored before §25: no resultsTiming / showBallots.
                var json = $$"""{"title":"Old","descriptionMarkdown":"","questions":[{"key":"q1","type":"Ranked","options":[{"key":"a","label":"A","descriptionMarkdown":""},{"key":"b","label":"B","descriptionMarkdown":""}],"scoring":"Borda","maxBans":0,"rankAll":false,"budget":null,"maxPerOption":null,"spendAll":false,"maybe":true}],"results":"{{results}}","voters":"{{mode}}","names":"Optional","responses":"One","closesOn":null,"timeZone":"UTC"}""";
                await db.Database.ExecuteSqlAsync(
                    $"INSERT INTO \"Polls\" (\"Id\", \"OwnerId\", \"Title\", \"Type\", \"Scoring\", \"DefinitionJson\", \"ResultsVisibility\", \"VoterMode\", \"AutoClose\", \"CreatedAt\", \"UpdatedAt\") VALUES ({pollId}, 'owner', 'Old', 'Ranked', 'Borda', {json}, {results.ToString()}, {mode.ToString()}, 0, '2026-10-01 10:00:00+00:00', '2026-10-01 10:00:00+00:00')");
            }
        }
        await using (var db = new ApplicationDbContext(options))
        {
            await db.Database.MigrateAsync();
            var rows = await db.Polls.AsNoTracking().ToDictionaryAsync(p => p.Id);
            ResultsVisibility Stored(string name) => PollJson.DeserializeDefinition(rows[polls[name].Id].DefinitionJson).Results;
            Assert.Equal((ResultsVisibility.AfterVote, ResultsVisibility.AfterVote), (Stored("invite-open"), rows[polls["invite-open"].Id].ResultsVisibility));
            Assert.Equal((ResultsVisibility.AfterVote, ResultsVisibility.AfterVote), (Stored("accounts-open"), rows[polls["accounts-open"].Id].ResultsVisibility));
            Assert.Equal((ResultsVisibility.Open, ResultsVisibility.Open), (Stored("public-open"), rows[polls["public-open"].Id].ResultsVisibility));
            Assert.Equal((ResultsVisibility.Creator, ResultsVisibility.Creator), (Stored("invite-creator"), rows[polls["invite-creator"].Id].ResultsVisibility));
            // The raw JSON itself was rewritten, not only read differently.
            Assert.Contains("\"results\":\"AfterVote\"", rows[polls["invite-open"].Id].DefinitionJson);
            var def = PollJson.DeserializeDefinition(rows[polls["invite-open"].Id].DefinitionJson);
            Assert.Equal((ResultsTiming.WhileOpen, false, "B"), (def.ResultsTiming, def.ShowBallots, def.Questions[0].Options[1].Label));
        }
        SqliteConnection.ClearAllPools();
    }
}
