using System.Net;
using Telrang.Core.Ballots;
using Telrang.Core.Polls;
using Telrang.Core.Ids;
using Telrang.Web.Data;
using Telrang.Web.Features.Polls;
using Telrang.Web.Features.Voting;
using Telrang.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Telrang.Web.Tests;

/// <summary>
/// Phase 3: polls, access control, voter identity. Mutations go through the service layer
/// (the vote/edit UI is a SignalR circuit); cookies, gates and visibility are checked over HTTP.
/// </summary>
public class PollTests
{
    private const string Password = "correct horse battery staple";

    private static PollDraft Draft(ResultsVisibility results = ResultsVisibility.Creator, VoterMode voters = VoterMode.Public) =>
        TestUtil.RankedDraft(results, voters);

    private static readonly RankedBallot ParisFirst = new(["paris", "rome"], ["berlin"]);
    private static readonly RankedBallot RomeFirst = new(["rome", "paris"], []);

    // ---- helpers -----------------------------------------------------------------------------

    private static async Task<string> CreateUserAsync(AppFactory factory, string email)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
        var result = await users.CreateAsync(user, Password);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Description)));
        return user.Id;
    }

    private static async Task<T> WithScope<T>(AppFactory factory, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    private static async Task<string> CreatePollAsync(AppFactory factory, string ownerId, PollDraft source)
    {
        var result = await WithScope(factory, sp => sp.GetRequiredService<PollService>().CreatePollAsync(ownerId, source));
        Assert.True(result.Succeeded, result.Message + string.Join(", ", result.Errors));
        return result.EncodedId!;
    }

    /// <summary>The visitor context the server would build from this cookie jar for poll <paramref name="id"/>.</summary>
    private static VisitorContext VisitorFrom(AppFactory factory, CookieContainer jar, string id, string? userId = null)
    {
        Assert.True(PollId.TryDecode(id, out var pollId));
        var cookies = jar.GetCookies(new Uri($"https://localhost/p/{id}"));
        return factory.Services.GetRequiredService<PollCookies>().Read(name => cookies[name]?.Value, pollId, userId);
    }

    private static Task<SubmitResult> SubmitAsync(AppFactory factory, string id, VisitorContext visitor, RankedBallot ballot, string? name = null) =>
        WithScope(factory, sp => sp.GetRequiredService<BallotService>().SubmitRankedAsync(id, visitor, ballot, name));

    private static async Task<List<Ballot>> BallotsAsync(AppFactory factory, string id)
    {
        Assert.True(PollId.TryDecode(id, out var pollId));
        return await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().Ballots.AsNoTracking().Where(b => b.PollId == pollId).ToListAsync());
    }

    // ---- 404s --------------------------------------------------------------------------------

    [Fact]
    public async Task Unknown_or_malformed_ids_are_404()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, Draft());
        var client = factory.CreateBrowser();
        var missing = PollId.Encode(Guid.NewGuid());

        foreach (var path in new[]
                 {
                     "/p/nope", $"/p/{missing}", $"/p/{missing}/results", "/p/" + id[..21] + "!", $"/p/{id}x",
                     $"/p/{missing}?t=" + InviteTokens.Generate().Token,
                 })
        {
            var response = await client.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{path} -> {response.StatusCode}");
        }

        Assert.Equal(HttpStatusCode.OK, (await TestUtil.GetPageAsync(client, $"/p/{id}")).StatusCode);
    }

    // ---- results visibility ------------------------------------------------------------------

    [Theory]
    [InlineData(ResultsVisibility.Creator, true, false, false)]
    [InlineData(ResultsVisibility.AfterVote, true, true, false)]
    [InlineData(ResultsVisibility.Open, true, true, true)]
    public async Task Results_visibility(ResultsVisibility mode, bool owner, bool voter, bool anonymous)
    {
        await using var factory = new AppFactory();
        var ownerBrowser = factory.CreateBrowser();
        Assert.Equal(HttpStatusCode.Redirect, (await AppFactory.RegisterAsync(ownerBrowser, "owner@example.com", Password)).StatusCode);
        var ownerId = await TestUtil.ConfirmEmailAsync(factory, "owner@example.com");
        Assert.Equal(HttpStatusCode.Redirect, (await AppFactory.LoginAsync(ownerBrowser, "owner@example.com", Password)).StatusCode);
        var id = await CreatePollAsync(factory, ownerId, Draft(results: mode));

        var (voterClient, voterJar) = factory.CreateBrowserWithJar();
        await voterClient.GetAsync($"/p/{id}");
        Assert.True((await SubmitAsync(factory, id, VisitorFrom(factory, voterJar, id), ParisFirst)).Succeeded);

        var anonClient = factory.CreateBrowser();
        await anonClient.GetAsync($"/p/{id}");

        async Task<bool> CanSee(HttpClient c)
        {
            var response = await c.GetAsync($"/p/{id}/results");
            var html = await response.Content.ReadAsStringAsync();
            var visible = response.StatusCode == HttpStatusCode.OK && html.Contains("results-table");
            Assert.True(visible || response.StatusCode == HttpStatusCode.Forbidden, $"{response.StatusCode}");
            return visible;
        }

        Assert.Equal(owner, await CanSee(ownerBrowser));
        Assert.Equal(voter, await CanSee(voterClient));
        Assert.Equal(anonymous, await CanSee(anonClient));

        if (owner)
        {
            var html = await (await ownerBrowser.GetAsync($"/p/{id}/results")).Content.ReadAsStringAsync();
            Assert.Contains("<strong>1</strong> voter", html);
        }
    }

    // ---- link mode ---------------------------------------------------------------------------

    [Fact]
    public async Task Link_mode_one_ballot_per_browser_and_revisions_replace()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, Draft().Names(NamesMode.Required));

        var (a, jarA) = factory.CreateBrowserWithJar();
        var (b, jarB) = factory.CreateBrowserWithJar();
        var first = await a.GetAsync($"/p/{id}");
        var vk = Assert.Single(first.Headers.GetValues("Set-Cookie"), c => c.StartsWith("vk="));
        Assert.Contains("httponly", vk, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/;", vk, StringComparison.OrdinalIgnoreCase);
        var html = await (await TestUtil.GetPageAsync(a, first.Headers.Location!.ToString())).Content.ReadAsStringAsync();
        Assert.Contains("Editable in this browser only", html);
        Assert.Contains("Your ranking", html); // the ballot editor is part of the static page
        await b.GetAsync($"/p/{id}");

        var visitorA = VisitorFrom(factory, jarA, id);
        var visitorB = VisitorFrom(factory, jarB, id);
        Assert.NotNull(visitorA.LinkVoterKey);
        Assert.NotEqual(visitorA.LinkVoterKey, visitorB.LinkVoterKey);

        // Names are required by this poll.
        var noName = await SubmitAsync(factory, id, visitorA, ParisFirst, "   ");
        Assert.Equal(SubmitStatus.Invalid, noName.Status);
        var tooLong = await SubmitAsync(factory, id, visitorA, ParisFirst, new string('x', 61));
        Assert.Equal(SubmitStatus.Invalid, tooLong.Status);

        Assert.True((await SubmitAsync(factory, id, visitorA, ParisFirst, "  Ann  ")).Succeeded);
        Assert.True((await SubmitAsync(factory, id, visitorB, ParisFirst, "Bob")).Succeeded);
        Assert.Equal(2, (await BallotsAsync(factory, id)).Count);

        // Same browser again: revises, does not add.
        Assert.True((await SubmitAsync(factory, id, visitorA, RomeFirst, "Ann")).Succeeded);
        var ballots = await BallotsAsync(factory, id);
        Assert.Equal(2, ballots.Count);
        var annBallot = Assert.Single(ballots, x => x.DisplayName == "Ann");
        Assert.Equal(["rome", "paris"], PollJson.DeserializeRanked(annBallot.PayloadJson, "q1").Ranked);
        Assert.True(annBallot.UpdatedAt >= annBallot.SubmittedAt);

        // The second visit reuses the cookie (no new vk).
        var again = await a.GetAsync($"/p/{id}");
        Assert.False(again.Headers.TryGetValues("Set-Cookie", out var set) && set.Any(c => c.StartsWith("vk=")));
        Assert.Contains("You have already voted", await again.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Server_validates_ballots()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, Draft());
        var (client, jar) = factory.CreateBrowserWithJar();
        await client.GetAsync($"/p/{id}");
        var visitor = VisitorFrom(factory, jar, id);

        Assert.Equal(SubmitStatus.Invalid, (await SubmitAsync(factory, id, visitor, new RankedBallot(["paris"], ["paris"]))).Status);
        Assert.Equal(SubmitStatus.Invalid, (await SubmitAsync(factory, id, visitor, new RankedBallot([], ["paris", "rome"]))).Status); // bans: 1
        Assert.Equal(SubmitStatus.Invalid, (await SubmitAsync(factory, id, visitor, new RankedBallot(["madrid"], []))).Status);
        Assert.Empty(await BallotsAsync(factory, id));

        // A visitor without any voter cookie cannot vote.
        Assert.Equal(SubmitStatus.Denied, (await SubmitAsync(factory, id, VisitorContext.ForUser(null), ParisFirst)).Status);
        // Unknown poll.
        Assert.Equal(SubmitStatus.NotFound, (await SubmitAsync(factory, PollId.Encode(Guid.NewGuid()), visitor, ParisFirst)).Status);
    }


    // ---- invite mode -------------------------------------------------------------------------

    [Fact]
    public async Task Invite_mode_requires_a_known_token_and_tokens_revise_their_own_ballot()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, Draft(voters: VoterMode.Invite, results: ResultsVisibility.AfterVote));
        var invites = await WithScope(factory, sp => sp.GetRequiredService<PollService>()
            .GenerateInvitesAsync(owner, id, 1, ["Alice", "Bob"]));
        Assert.Equal(2, invites!.Count);
        Assert.Equal("Alice", invites[0].Alias);

        // No token: the poll is visible but voting is refused.
        var (plain, plainJar) = factory.CreateBrowserWithJar();
        var html = await (await plain.GetAsync($"/p/{id}")).Content.ReadAsStringAsync();
        Assert.Contains("invite-only", html);
        Assert.Contains("Paris", html);
        Assert.Equal(SubmitStatus.Denied, (await SubmitAsync(factory, id, VisitorFrom(factory, plainJar, id), ParisFirst)).Status);

        // The owner cannot vote without a token either.
        Assert.Equal(SubmitStatus.Denied, (await SubmitAsync(factory, id, VisitorContext.ForUser(owner), ParisFirst)).Status);

        // Unknown or malformed tokens: 404.
        Assert.Equal(HttpStatusCode.NotFound, (await plain.GetAsync($"/p/{id}?t=" + InviteTokens.Generate().Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await plain.GetAsync($"/p/{id}?t=garbage")).StatusCode);

        // Valid token: stored in a per-poll cookie, URL cleaned by redirect.
        var (alice, aliceJar) = factory.CreateBrowserWithJar();
        var landing = await alice.GetAsync($"/p/{id}?t={invites[0].Token}");
        Assert.Equal(HttpStatusCode.Redirect, landing.StatusCode);
        Assert.EndsWith($"/p/{id}", landing.Headers.Location!.ToString());
        Assert.Contains(landing.Headers.GetValues("Set-Cookie"), c => c.StartsWith(PollCookies.InviteCookieName(id) + "="));
        html = await (await alice.GetAsync($"/p/{id}")).Content.ReadAsStringAsync();
        Assert.Contains("Your ranking", html);

        var aliceVisitor = VisitorFrom(factory, aliceJar, id);
        Assert.True((await SubmitAsync(factory, id, aliceVisitor, ParisFirst)).Succeeded);
        var revised = await SubmitAsync(factory, id, aliceVisitor, RomeFirst);
        Assert.True(revised.Succeeded);
        Assert.True(revised.Access!.CanSeeResults); // after-vote

        var ballots = await BallotsAsync(factory, id);
        var ballot = Assert.Single(ballots);
        Assert.StartsWith(PollAccessService.InviteVoterPrefix, ballot.VoterKey);
        Assert.Equal(["rome", "paris"], PollJson.DeserializeRanked(ballot.PayloadJson, "q1").Ranked);

        var list = await WithScope(factory, sp => sp.GetRequiredService<PollService>().ListInvitesAsync(owner, id));
        Assert.Equal(1, list!.Count(i => i.Used));
        Assert.True(list!.Single(i => i.Alias == "Alice").Used);

        // Results: Alice (voted) sees them, the token-less visitor does not.
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync($"/p/{id}/results")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync($"/p/{id}/results")).StatusCode);

        // Tokens are per poll.
        var other = await CreatePollAsync(factory, owner, Draft(voters: VoterMode.Invite));
        Assert.Equal(HttpStatusCode.NotFound, (await plain.GetAsync($"/p/{other}?t={invites[1].Token}")).StatusCode);
    }

    // ---- closing -----------------------------------------------------------------------------

    [Fact]
    public async Task Closed_poll_refuses_submit()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, Draft());
        var (client, jar) = factory.CreateBrowserWithJar();
        await client.GetAsync($"/p/{id}");
        var visitor = VisitorFrom(factory, jar, id);

        var polls = (Func<IServiceProvider, PollService>)(sp => sp.GetRequiredService<PollService>());
        Assert.True(await WithScope(factory, sp => polls(sp).SetClosedAsync(owner, id, true)));
        var refused = await SubmitAsync(factory, id, visitor, ParisFirst);
        Assert.Equal(SubmitStatus.Denied, refused.Status);
        Assert.Contains("closed", refused.Errors.Single());
        Assert.Contains("Poll closed", await (await client.GetAsync($"/p/{id}")).Content.ReadAsStringAsync());

        Assert.True(await WithScope(factory, sp => polls(sp).SetClosedAsync(owner, id, false)));
        Assert.True((await SubmitAsync(factory, id, visitor, ParisFirst)).Succeeded);

        // A closing date in the past closes it too.
        var update = await WithScope(factory, sp => polls(sp).UpdatePollAsync(owner, id, Draft().Closes("2020-01-01")));
        Assert.True(update.Succeeded);
        Assert.Equal(SubmitStatus.Denied, (await SubmitAsync(factory, id, visitor, RomeFirst)).Status);

        // Only the owner can close.
        var stranger = await CreateUserAsync(factory, "s@example.com");
        Assert.False(await WithScope(factory, sp => polls(sp).SetClosedAsync(stranger, id, false)));
    }

    // ---- editing -----------------------------------------------------------------------------

    [Fact]
    public async Task Edit_changing_type_or_scoring_is_refused()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, Draft());

        var result = await WithScope(factory, sp => sp.GetRequiredService<PollService>()
            .UpdatePollAsync(owner, id, Draft().Q(q => q.Scoring = ScoringScheme.Dowdall)));
        Assert.Equal(PollSaveStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, e => e.Field == "questions[0].scoring");

        var points = Draft().Q(q => { q.Type = PollType.Points; q.Budget = 10; });
        result = await WithScope(factory, sp => sp.GetRequiredService<PollService>()
            .UpdatePollAsync(owner, id, points));
        Assert.Equal(PollSaveStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, e => e.Field == "questions[0].type");

        var edit = await WithScope(factory, sp => sp.GetRequiredService<PollService>().GetForEditAsync(owner, id));
        Assert.Equal(PollType.Ranked, edit!.Definition.Questions[0].Type);
        Assert.Equal(ScoringScheme.Borda, edit.Definition.Questions[0].Scoring);

        // Non-owners get "not found".
        var stranger = await CreateUserAsync(factory, "s@example.com");
        result = await WithScope(factory, sp => sp.GetRequiredService<PollService>()
            .UpdatePollAsync(stranger, id, Draft()));
        Assert.Equal(PollSaveStatus.NotFound, result.Status);
        Assert.Null(await WithScope(factory, sp => sp.GetRequiredService<PollService>().GetForEditAsync(stranger, id)));
    }

    [Fact]
    public async Task Edit_changing_options_with_ballots_keeps_them_and_the_links()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, Draft(voters: VoterMode.Invite));
        var invites = await WithScope(factory, sp => sp.GetRequiredService<PollService>().GenerateInvitesAsync(owner, id, 2, []));
        foreach (var invite in invites!)
        {
            Assert.True(InviteTokens.TryHash(invite.Token, out var hash));
            var v = new VisitorContext(null, null, hash);
            Assert.True((await SubmitAsync(factory, id, v, ParisFirst)).Succeeded);
        }
        var stored = (await BallotsAsync(factory, id)).Select(b => b.PayloadJson).ToList();
        var service = (Func<IServiceProvider, PollService>)(sp => sp.GetRequiredService<PollService>());

        // Details, an added option, a renamed one (Text mode: by label, so a new key), a removed one: all saved directly.
        foreach (var text in new[] { "- Paris\n  Lovely.\n- Rome\n- Berlin", "- Paris\n- Rome\n- Berlin\n- Lisbon", "- Paris, France\n- Rome\n- Lisbon" })
            Assert.True((await WithScope(factory, sp => service(sp).UpdatePollAsync(owner, id, Draft(voters: VoterMode.Invite).OptionsText(text)))).Succeeded);

        // DESIGN.md §26: ballots are never rewritten or deleted by an edit, and the links stay used.
        Assert.Equal(stored, (await BallotsAsync(factory, id)).Select(b => b.PayloadJson).ToList());
        Assert.All((await WithScope(factory, sp => service(sp).ListInvitesAsync(owner, id)))!, i => Assert.True(i.Used));
        var edit = await WithScope(factory, sp => service(sp).GetForEditAsync(owner, id));
        // Keys added by edits are numbered (DESIGN.md §31): Lisbon first, then "Paris, France" (a new label in Text mode).
        Assert.Equal(["_2", "rome", "_1"], edit!.Definition.Questions[0].Options.Select(o => o.Key));
        Assert.Equal(2, edit.Definition.Questions[0].KeysIssued);
    }

    // ---- misc --------------------------------------------------------------------------------

    [Fact]
    public async Task My_polls_lists_only_own_polls()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var other = await CreateUserAsync(factory, "x@example.com");
        var id = await CreatePollAsync(factory, owner, Draft());
        await CreatePollAsync(factory, other, Draft());

        var list = await WithScope(factory, sp => sp.GetRequiredService<PollService>().ListOwnedAsync(owner));
        var row = Assert.Single(list);
        Assert.Equal(id, row.EncodedId);
        Assert.Equal("Trip", row.Title);
        Assert.False(row.IsClosed);
    }

    [Fact]
    public void Markdown_escapes_html_and_neutralises_script_links()
    {
        var html = MarkdownRenderer.Render("<script>alert(1)</script> [x](javascript:alert(1)) [ok](https://example.com) <img src=x onerror=alert(1)>").Value;
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("javascript:", html);
        Assert.Contains("href=\"https://example.com\"", html);
        Assert.Contains("rel=\"noopener noreferrer\"", html);
        Assert.Contains("target=\"_blank\"", html);
    }
}
