using System.Net;
using System.Text.Json;
using Telrang.Core.Ballots;
using Telrang.Core.Ids;
using Telrang.Core.Polls;
using Telrang.Web.Data;
using Telrang.Web.Features.Polls;
using Telrang.Web.Features.Settings;
using Telrang.Web.Features.Voting;
using Telrang.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>
/// Phase 5b part 1: voter modes (accounts | public | invite), Ballot.UserId, responses: one | many,
/// read-only ballots after close, /my/votes, parser limits, poll time zones, the Phase5bVoters migration.
/// </summary>
public class Phase5bTests
{
    private static readonly RankedBallot ParisFirst = new(["paris", "rome"], []);
    private static readonly RankedBallot RomeFirst = new(["rome", "paris"], ["berlin"]);
    private static readonly RankedBallot BerlinFirst = new(["berlin"], []);

    private static PollDraft Draft(string title = "Trip", VoterMode voters = VoterMode.Public) =>
        RankedDraft(ResultsVisibility.AfterVote, voters, title).Describe("Where?");

    private static Task<PollSaveResult> CreateRawAsync(AppFactory factory, string ownerId, PollDraft source) =>
        WithScope(factory, sp => sp.GetRequiredService<PollService>().CreatePollAsync(ownerId, source));

    private static Task<PollSaveResult> UpdateAsync(AppFactory factory, string ownerId, string id, PollDraft source) =>
        WithScope(factory, sp => sp.GetRequiredService<PollService>().UpdatePollAsync(ownerId, id, source));

    private static Task<SubmitResult> SubmitAsync(AppFactory factory, string id, VisitorContext visitor, RankedBallot ballot,
        string? name = null, int? responseNo = null) =>
        WithScope(factory, sp => sp.GetRequiredService<BallotService>().SubmitRankedAsync(id, visitor, ballot, name, responseNo));

    private static Task<SubmitResult> DeleteAsync(AppFactory factory, string id, VisitorContext visitor, int responseNo) =>
        WithScope(factory, sp => sp.GetRequiredService<BallotService>().DeleteResponseAsync(id, visitor, responseNo));

    private static Task<VoteState?> StateAsync(AppFactory factory, string id, VisitorContext visitor) =>
        WithScope(factory, sp => sp.GetRequiredService<BallotService>().GetVoteStateAsync(id, visitor));

    private static async Task<Poll> PollRowAsync(AppFactory factory, string id)
    {
        Assert.True(PollId.TryDecode(id, out var pollId));
        return await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().Polls.AsNoTracking().SingleAsync(p => p.Id == pollId));
    }

    private static async Task<string> CreateUnverifiedUserAsync(AppFactory factory, string email)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = false };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        return user.Id;
    }

    private static async Task SetTimeZoneAsync(AppFactory factory, string userId, string zone)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await users.FindByIdAsync(userId))!;
        user.TimeZone = zone;
        Assert.True((await users.UpdateAsync(user)).Succeeded);
    }

    /// <summary>These tests use fixed far-future close dates; lift the max poll duration (180 days by default, Phase5b2Tests).</summary>
    private static Task NoDurationLimitAsync(AppFactory factory) =>
        factory.Settings.SetAsync(new Dictionary<string, string?> { [SettingKeys.LimitPollDurationDays] = "0" }, null);

    private static bool SetsVoterCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(c => c.StartsWith("vk="));

    // ---- voters: accounts ---------------------------------------------------------------------------

    [Fact]
    public async Task Accounts_mode_refuses_anonymous_and_unverified_and_records_the_user()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var voter = await CreateUserAsync(factory, "v@example.com");
        var unverified = await CreateUnverifiedUserAsync(factory, "u@example.com");
        var id = await CreatePollAsync(factory, owner, Draft(voters: VoterMode.Accounts));

        // Anonymous: the poll is visible, no vk, no editor, a sign-in link that returns to the poll.
        var anonymous = factory.CreateBrowser();
        var page = await anonymous.GetAsync($"/p/{id}");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.False(SetsVoterCookie(page));
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Sign in to respond.", html);
        Assert.Contains($"Account/Login?ReturnUrl=%2Fp%2F{id}", html);
        Assert.Contains("Paris", html);
        Assert.DoesNotContain("Your ranking", html);
        var refused = await SubmitAsync(factory, id, new VisitorContext(null, "some-browser-key", null), ParisFirst);
        Assert.Equal(SubmitStatus.Denied, refused.Status);
        Assert.Equal(AccessDenial.SignInRequired, refused.Access!.Denial);

        // Signed in without a verified email: refused.
        var unverifiedHtml = await (await (await LoggedInBrowserAsync(factory, "u@example.com")).GetAsync($"/p/{id}")).Content.ReadAsStringAsync();
        Assert.Contains("Verify your email to respond.", unverifiedHtml);
        Assert.DoesNotContain("Your ranking", unverifiedHtml);
        var notVerified = await SubmitAsync(factory, id, VisitorContext.ForUser(unverified), ParisFirst);
        Assert.Equal(AccessDenial.EmailNotVerified, notVerified.Access!.Denial);

        // Verified: the editor is there and the ballot carries the user, not a voter key.
        var voterBrowser = await LoggedInBrowserAsync(factory, "v@example.com");
        var signedIn = await voterBrowser.GetAsync($"/p/{id}");
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        Assert.False(SetsVoterCookie(signedIn));
        Assert.Contains("Your ranking", await signedIn.Content.ReadAsStringAsync());
        Assert.True((await SubmitAsync(factory, id, VisitorContext.ForUser(voter), ParisFirst)).Succeeded);
        var ballot = Assert.Single(await BallotsAsync(factory, id));
        Assert.Equal(voter, ballot.UserId);
        Assert.Null(ballot.VoterKey);
        Assert.Equal("u:" + voter, ballot.VoterIdentity);
        Assert.Equal(1, ballot.ResponseNo);

        // The creator votes under the same rules.
        Assert.True((await SubmitAsync(factory, id, VisitorContext.ForUser(owner), RomeFirst)).Succeeded);
        Assert.Equal(2, (await BallotsAsync(factory, id)).Count);
    }

    // ---- voters: public -----------------------------------------------------------------------------

    [Fact]
    public async Task Public_mode_records_signed_in_users_by_account_and_anonymous_visitors_by_voter_key()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var voter = await CreateUserAsync(factory, "v@example.com");
        var id = await CreatePollAsync(factory, owner, Draft());

        // Anonymous: vk cookie round trip, ballot keyed by the per-poll HMAC.
        var (anon, jar) = factory.CreateBrowserWithJar();
        await GetPageAsync(anon, $"/p/{id}");
        var visitor = VisitorFrom(factory, jar, id);
        Assert.NotNull(visitor.LinkVoterKey);
        Assert.True((await SubmitAsync(factory, id, visitor, ParisFirst)).Succeeded);

        // Signed in: no vk, no cookie-check redirect, ballot keyed by the account.
        var signedIn = await LoggedInBrowserAsync(factory, "v@example.com");
        var page = await signedIn.GetAsync($"/p/{id}");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.False(SetsVoterCookie(page));
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Your ranking", html);
        Assert.DoesNotContain("Editable in this browser only", html); // the cookie line is for anonymous voters only
        Assert.True((await SubmitAsync(factory, id, VisitorContext.ForUser(voter), RomeFirst)).Succeeded);
        Assert.Contains("You have already voted", await (await signedIn.GetAsync($"/p/{id}")).Content.ReadAsStringAsync());

        var ballots = await BallotsAsync(factory, id);
        Assert.Equal(2, ballots.Count);
        var anonymousBallot = Assert.Single(ballots, b => b.UserId is null);
        Assert.Equal("k:" + anonymousBallot.VoterKey, anonymousBallot.VoterIdentity);
        Assert.DoesNotContain(visitor.LinkVoterKey!, anonymousBallot.VoterKey);
        var accountBallot = Assert.Single(ballots, b => b.UserId == voter);
        Assert.Null(accountBallot.VoterKey);

        // Same account with a stale vk in the visitor context is still the account (no second ballot, no merge).
        Assert.True((await SubmitAsync(factory, id, new VisitorContext(voter, visitor.LinkVoterKey, null), BerlinFirst)).Succeeded);
        Assert.Equal(2, (await BallotsAsync(factory, id)).Count);
        Assert.Equal(["paris", "rome"], (await StateAsync(factory, id, visitor))!.Ballot!.Ranked!.Ranked);
    }

    [Fact]
    public async Task Invite_mode_votes_by_token_and_a_signed_in_voter_claims_it()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var voter = await CreateUserAsync(factory, "v@example.com");
        var id = await CreatePollAsync(factory, owner, Draft(voters: VoterMode.Invite));
        var invite = (await WithScope(factory, sp => sp.GetRequiredService<PollService>().GenerateInvitesAsync(owner, id, 1, [])))!.Single();
        Assert.True(InviteTokens.TryHash(invite.Token, out var hash));

        // Signed in without a token: still invite-only.
        Assert.Equal(AccessDenial.InviteRequired, (await SubmitAsync(factory, id, VisitorContext.ForUser(voter), ParisFirst)).Access!.Denial);

        // First write while signed in claims the token: the ballot belongs to the account (DESIGN.md §16).
        var withToken = new VisitorContext(voter, null, hash);
        Assert.True((await SubmitAsync(factory, id, withToken, ParisFirst)).Succeeded);
        var ballot = Assert.Single(await BallotsAsync(factory, id));
        Assert.Null(ballot.VoterKey);
        Assert.Equal(voter, ballot.UserId);
        Assert.Equal("u:" + voter, ballot.VoterIdentity);

        // Signed out, the token is refused; the account revises its ballot without the token.
        var anonymous = await SubmitAsync(factory, id, new VisitorContext(null, null, hash), RomeFirst);
        Assert.Equal(AccessDenial.InviteClaimed, anonymous.Access!.Denial);
        Assert.Equal([PollAccessService.InviteClaimedMessage], anonymous.Errors);
        Assert.True((await SubmitAsync(factory, id, VisitorContext.ForUser(voter), RomeFirst)).Succeeded);
        ballot = Assert.Single(await BallotsAsync(factory, id));
        Assert.Equal(voter, ballot.UserId);
        Assert.Equal(["rome", "paris"], (await StateAsync(factory, id, VisitorContext.ForUser(voter)))!.Ballot!.Ranked!.Ranked);
    }

    // ---- responses ----------------------------------------------------------------------------------

    [Fact]
    public async Task Responses_many_three_responses_edit_the_second_delete_the_third()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, Draft().Many().Names(NamesMode.Required));
        var (browser, jar) = factory.CreateBrowserWithJar();
        await GetPageAsync(browser, $"/p/{id}");
        var visitor = VisitorFrom(factory, jar, id);

        var first = await SubmitAsync(factory, id, visitor, ParisFirst, "Ann");
        var second = await SubmitAsync(factory, id, visitor, RomeFirst, "Ben");
        var third = await SubmitAsync(factory, id, visitor, BerlinFirst, "Cat");
        Assert.Equal([1, 2, 3], new[] { first.ResponseNo, second.ResponseNo, third.ResponseNo });
        Assert.Equal(3, third.Access!.ResponseCount);
        var ballots = await BallotsAsync(factory, id);
        Assert.Equal(3, ballots.Count);
        Assert.Single(ballots.Select(b => b.VoterIdentity).Distinct());

        // The page lists them; ?r=2&edit=1 opens the second for editing (the form carries the number).
        var list = await (await browser.GetAsync($"/p/{id}")).Content.ReadAsStringAsync();
        Assert.Contains("Your responses", list);
        Assert.Contains("Add another response", list);
        Assert.Contains($"href=\"p/{id}?r=2&amp;edit=1\"", list);
        foreach (var name in new[] { "Ann", "Ben", "Cat" }) Assert.Contains(name, list);
        var edit = await (await browser.GetAsync($"/p/{id}?r=2&edit=1")).Content.ReadAsStringAsync();
        Assert.Contains("Update response", edit);
        Assert.Contains("value=\"Ben\"", edit);
        Assert.Contains("name=\"r\" value=\"2\"", edit);
        Assert.True(PollId.TryDecode(id, out var pollId));

        // Edit the second.
        var edited = await SubmitAsync(factory, id, visitor, BerlinFirst, "Ben B.", responseNo: 2);
        Assert.True(edited.Succeeded);
        Assert.Equal(2, edited.ResponseNo);
        ballots = await BallotsAsync(factory, id);
        Assert.Equal(3, ballots.Count);
        var ben = Assert.Single(ballots, b => b.ResponseNo == 2);
        Assert.Equal("Ben B.", ben.DisplayName);
        Assert.Equal(["berlin"], PollJson.DeserializeRanked(ben.PayloadJson, "q1").Ranked);

        // Delete the third.
        Assert.True((await DeleteAsync(factory, id, visitor, 3)).Succeeded);
        Assert.Equal([1, 2], (await BallotsAsync(factory, id)).Select(b => b.ResponseNo).Order());
        Assert.Equal(SubmitStatus.Invalid, (await DeleteAsync(factory, id, visitor, 3)).Status);
        Assert.Equal(SubmitStatus.Invalid, (await SubmitAsync(factory, id, visitor, ParisFirst, "X", responseNo: 3)).Status);

        // Another browser cannot touch these responses: its number 1 is its own.
        var (other, otherJar) = factory.CreateBrowserWithJar();
        await GetPageAsync(other, $"/p/{id}");
        var stranger = VisitorFrom(factory, otherJar, id);
        Assert.Equal(SubmitStatus.Invalid, (await DeleteAsync(factory, id, stranger, 1)).Status);
        Assert.Equal(SubmitStatus.Invalid, (await SubmitAsync(factory, id, stranger, ParisFirst, "Eve", responseNo: 1)).Status);
        Assert.Equal(2, (await BallotsAsync(factory, id)).Count);

        // The unique index (poll, identity, response number) holds.
        var duplicate = new Ballot
        {
            Id = Guid.NewGuid(), PollId = pollId, VoterKey = ben.VoterKey, VoterIdentity = ben.VoterIdentity, ResponseNo = 2,
            PayloadJson = ben.PayloadJson, SubmittedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        await Assert.ThrowsAsync<DbUpdateException>(() => WithScope(factory, async sp =>
        {
            var db = sp.GetRequiredService<ApplicationDbContext>();
            db.Ballots.Add(duplicate);
            return await db.SaveChangesAsync();
        }));

        // Closed: no more edits or deletes.
        Assert.True(await WithScope(factory, sp => sp.GetRequiredService<PollService>().SetClosedAsync(owner, id, true)));
        Assert.Equal(SubmitStatus.Denied, (await DeleteAsync(factory, id, visitor, 1)).Status);
    }

    [Fact]
    public async Task Responses_one_never_creates_a_second_ballot()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var voter = await CreateUserAsync(factory, "v@example.com");
        var id = await CreatePollAsync(factory, owner, Draft().Responses(ResponsePolicy.One));
        var me = VisitorContext.ForUser(voter);

        Assert.True((await SubmitAsync(factory, id, me, ParisFirst)).Succeeded);
        Assert.True((await SubmitAsync(factory, id, me, RomeFirst)).Succeeded);               // a "new" submit revises
        Assert.True((await SubmitAsync(factory, id, me, BerlinFirst, responseNo: 2)).Succeeded); // a response number is ignored
        var ballot = Assert.Single(await BallotsAsync(factory, id));
        Assert.Equal(1, ballot.ResponseNo);
        Assert.Equal(["berlin"], PollJson.DeserializeRanked(ballot.PayloadJson, "q1").Ranked);
        Assert.Equal(SubmitStatus.Denied, (await DeleteAsync(factory, id, me, 1)).Status);
        Assert.Single(await BallotsAsync(factory, id));
    }

    [Fact]
    public async Task Responses_and_time_zone_are_fixed_and_the_close_date_moves_with_ballots_kept()
    {
        await using var factory = new AppFactory();
        await NoDurationLimitAsync(factory);
        var owner = await CreateUserAsync(factory, "o@example.com");
        var voter = await CreateUserAsync(factory, "v@example.com");
        var id = await CreatePollAsync(factory, owner, Draft().Many());
        var me = VisitorContext.ForUser(voter);
        Assert.True((await SubmitAsync(factory, id, me, ParisFirst)).Succeeded);
        Assert.True((await SubmitAsync(factory, id, me, RomeFirst)).Succeeded);

        // DESIGN.md §26: responses and the time zone are fixed at creation.
        var one = await UpdateAsync(factory, owner, id, Draft().Responses(ResponsePolicy.One));
        Assert.Equal(PollSaveStatus.Invalid, one.Status);
        Assert.Contains(new DraftError("responses", PollDraftValidator.FixedMessage("Responses")), one.Errors);
        var zone = await UpdateAsync(factory, owner, id, Draft().Many().Zone("Asia/Tokyo"));
        Assert.Contains(new DraftError("timezone", PollDraftValidator.FixedMessage("The time zone")), zone.Errors);
        Assert.Equal(2, (await BallotsAsync(factory, id)).Count);

        // The closing date moves; the ballots stay.
        Assert.True((await UpdateAsync(factory, owner, id, Draft().Many().Closes("2031-05-01"))).Succeeded);
        Assert.Equal(2, (await BallotsAsync(factory, id)).Count);
    }

    // ---- after close ------------------------------------------------------------------------------

    [Fact]
    public async Task After_close_the_vote_page_shows_the_own_ballot_read_only()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, Draft());
        var (browser, jar) = factory.CreateBrowserWithJar();
        await GetPageAsync(browser, $"/p/{id}");
        Assert.True((await SubmitAsync(factory, id, VisitorFrom(factory, jar, id), RomeFirst, "Ann")).Succeeded);
        Assert.True(await WithScope(factory, sp => sp.GetRequiredService<PollService>().SetClosedAsync(owner, id, true)));

        var html = await (await browser.GetAsync($"/p/{id}")).Content.ReadAsStringAsync();
        Assert.Contains("Poll closed.", html);
        Assert.Contains("Your ballot", html);
        Assert.Matches("<ol[^>]*>\\s*<li[^>]*>Rome</li>\\s*<li[^>]*>Paris</li>\\s*</ol>", html);
        Assert.Contains("Banned:</span> Berlin", html);
        Assert.Contains("Ann", html);
        Assert.Contains($"href=\"p/{id}/results\"", html); // after-vote: visible
        Assert.DoesNotContain("Change ballot", html);
        Assert.DoesNotContain("Submit ballot", html);
        Assert.DoesNotContain("Your ranking", html);       // no ballot editor

        // A visitor without a ballot just sees the closed poll.
        var other = await (await factory.CreateBrowser().GetAsync($"/p/{id}")).Content.ReadAsStringAsync();
        Assert.Contains("Poll closed.", other);
        Assert.DoesNotContain("Your ballot", other);
    }

    [Fact]
    public async Task After_close_responses_many_are_listed_read_only()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        await CreateUserAsync(factory, "v@example.com");
        var voter = await CreateUserAsync(factory, "w@example.com");
        var id = await CreatePollAsync(factory, owner, Draft(voters: VoterMode.Accounts).Many());
        Assert.True((await SubmitAsync(factory, id, VisitorContext.ForUser(voter), ParisFirst, "Mum Smith")).Succeeded);
        Assert.True((await SubmitAsync(factory, id, VisitorContext.ForUser(voter), BerlinFirst, "Dad")).Succeeded);
        Assert.True(await WithScope(factory, sp => sp.GetRequiredService<PollService>().SetClosedAsync(owner, id, true)));

        var html = await (await (await LoggedInBrowserAsync(factory, "w@example.com")).GetAsync($"/p/{id}")).Content.ReadAsStringAsync();
        Assert.Contains("Poll closed.", html);
        Assert.Contains("Your responses", html);
        Assert.Contains("Mum Smith", html);
        Assert.Contains("Dad", html);
        Assert.DoesNotContain("?r=1", html);              // read-only: no edit links
        Assert.DoesNotContain("Add another response", html);

        // Someone else's account sees none of them.
        var stranger = await (await (await LoggedInBrowserAsync(factory, "v@example.com")).GetAsync($"/p/{id}")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("Mum Smith", stranger); // a space: never part of a random base64 token in the page
    }

    // ---- /my/votes ------------------------------------------------------------------------------------

    [Fact]
    public async Task My_votes_lists_the_users_polls_and_nothing_of_others()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var me = await CreateUserAsync(factory, "me@example.com");
        var other = await CreateUserAsync(factory, "other@example.com");
        var publicPoll = await CreatePollAsync(factory, owner, Draft(title: "Alpha poll"));
        var accountsPoll = await CreatePollAsync(factory, owner, Draft(title: "Beta poll", voters: VoterMode.Accounts).Many());
        var invitePoll = await CreatePollAsync(factory, owner, Draft(title: "Gamma poll", voters: VoterMode.Invite).Results(ResultsVisibility.Creator));
        var othersPoll = await CreatePollAsync(factory, owner, Draft(title: "Delta poll"));
        var untouched = await CreatePollAsync(factory, owner, Draft(title: "Epsilon poll"));

        Assert.True((await SubmitAsync(factory, publicPoll, VisitorContext.ForUser(me), ParisFirst)).Succeeded);
        Assert.True((await SubmitAsync(factory, accountsPoll, VisitorContext.ForUser(me), ParisFirst)).Succeeded);
        Assert.True((await SubmitAsync(factory, accountsPoll, VisitorContext.ForUser(me), RomeFirst)).Succeeded);
        var invite = (await WithScope(factory, sp => sp.GetRequiredService<PollService>().GenerateInvitesAsync(owner, invitePoll, 1, [])))!.Single();
        Assert.True(InviteTokens.TryHash(invite.Token, out var hash));
        Assert.True((await SubmitAsync(factory, invitePoll, new VisitorContext(me, null, hash), ParisFirst)).Succeeded);
        Assert.True((await SubmitAsync(factory, othersPoll, VisitorContext.ForUser(other), ParisFirst)).Succeeded);
        // An anonymous ballot in the same poll is not the user's.
        Assert.True((await SubmitAsync(factory, untouched, new VisitorContext(null, "anon-key", null), ParisFirst)).Succeeded);
        Assert.True(await WithScope(factory, sp => sp.GetRequiredService<PollService>().SetClosedAsync(owner, publicPoll, true)));

        var list = await WithScope(factory, sp => sp.GetRequiredService<BallotService>().ListMyVotesAsync(me));
        Assert.Equal(["Alpha poll", "Beta poll", "Gamma poll"], list.Select(v => v.Title).Order());
        Assert.Equal(2, list.Single(v => v.Title == "Beta poll").ResponseCount);
        Assert.True(list.Single(v => v.Title == "Alpha poll").IsClosed);
        Assert.True(list.Single(v => v.Title == "Alpha poll").ResultsLink);   // after-vote
        Assert.False(list.Single(v => v.Title == "Gamma poll").ResultsLink);  // creator only

        var browser = await LoggedInBrowserAsync(factory, "me@example.com");
        var page = await browser.GetAsync("/my/votes");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        foreach (var title in new[] { "Alpha poll", "Beta poll", "Gamma poll" }) Assert.Contains(title, html);
        Assert.DoesNotContain("Delta poll", html);
        Assert.DoesNotContain("Epsilon poll", html);
        Assert.Contains($"href=\"p/{publicPoll}/results\"", html);
        Assert.DoesNotContain($"href=\"p/{invitePoll}/results\"", html);

        var othersList = await WithScope(factory, sp => sp.GetRequiredService<BallotService>().ListMyVotesAsync(other));
        Assert.Equal(["Delta poll"], othersList.Select(v => v.Title));

        // Anonymous: login redirect.
        var anonymous = await factory.CreateBrowser().GetAsync("/my/votes");
        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        Assert.Contains("/Account/Login", anonymous.Headers.Location!.ToString());

        // The data export carries the same ballots.
        var token = AppFactory.ScrapeAntiforgeryToken(await (await browser.GetAsync("/Account/Manage/PersonalData")).Content.ReadAsStringAsync());
        var export = await browser.PostAsync("/Account/Manage/DownloadPersonalData",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token, ["Secret"] = Password }));
        using var json = JsonDocument.Parse(await export.Content.ReadAsStringAsync());
        var ballots = json.RootElement.GetProperty("ballots").EnumerateArray().ToList();
        Assert.Equal(4, ballots.Count);
        Assert.Equal(2, ballots.Count(b => b.GetProperty("poll").GetString() == "Beta poll"));
        Assert.Equal("paris", ballots[0].GetProperty("ballot").GetProperty("q1").GetProperty("ranked")[0].GetString());
    }

    // ---- limits ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Size_caps_are_enforced_by_PollService_with_field_errors()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");

        var tooLong = await CreateRawAsync(factory, owner, Draft(title: new string('t', 121)));
        Assert.Equal(PollSaveStatus.Invalid, tooLong.Status);
        var error = Assert.Single(tooLong.Errors);
        Assert.Equal("title", error.Field);
        Assert.Contains("120", error.Message);
        Assert.True((await CreateRawAsync(factory, owner, Draft(title: new string('t', 120)))).Succeeded);

        var many = await CreateRawAsync(factory, owner, Ranked("T", Enumerable.Range(1, 101).Select(i => $"Option {i}").ToArray()));
        Assert.Contains(many.Errors, e => e.Field == "questions[0].options" && e.Message.Contains("100"));

        // Edits are checked too.
        var id = await CreatePollAsync(factory, owner, Draft());
        var edit = await UpdateAsync(factory, owner, id, Draft().Q(q => q.Options[2].Label = new string('b', 81)));
        Assert.Contains(edit.Errors, e => e.Field == "questions[0].options[2].label" && e.Message.Contains("80"));

        // The admin's limit settings apply.
        await factory.Settings.SetAsync(new Dictionary<string, string?> { [SettingKeys.LimitTitle] = "10" }, null);
        var custom = await CreateRawAsync(factory, owner, Draft(title: "Eleven char"));
        Assert.Contains(custom.Errors, e => e.Field == "title" && e.Message.Contains("10"));
        Assert.True((await CreateRawAsync(factory, owner, Draft(title: "Ten chars!"))).Succeeded);
    }

    // ---- time zones -----------------------------------------------------------------------------------

    [Fact]
    public async Task Timezone_is_validated_and_closes_at_the_end_of_the_local_day()
    {
        await using var factory = new AppFactory();
        await NoDurationLimitAsync(factory);
        var owner = await CreateUserAsync(factory, "o@example.com");

        var unknown = await CreateRawAsync(factory, owner, Draft().Names(NamesMode.Optional).Zone("Mars/Olympus"));
        Assert.Equal(PollSaveStatus.Invalid, unknown.Status);
        var error = Assert.Single(unknown.Errors);
        Assert.Equal("timezone", error.Field);
        Assert.Equal("Pick a time zone from the list, such as Europe/Berlin.", error.Message);
        // Windows ids are not IANA ids.
        Assert.Equal(PollSaveStatus.Invalid, (await CreateRawAsync(factory, owner, Draft().Zone("W. Europe Standard Time"))).Status);

        var id = await CreatePollAsync(factory, owner, Draft().Zone("Europe/Berlin").Closes("2030-01-31"));
        var poll = await PollRowAsync(factory, id);
        Assert.Equal(Stored(new DateTimeOffset(2030, 1, 31, 23, 0, 0, TimeSpan.Zero).AddTicks(-1)), poll.ClosesAt); // CET = UTC+1
        Assert.Equal("Europe/Berlin", PollJson.DeserializeDefinition(poll.DefinitionJson).TimeZone);

        // Summer time (CEST = UTC+2).
        Assert.True((await UpdateAsync(factory, owner, id, Draft().Zone("Europe/Berlin").Closes("2030-07-15"))).Succeeded);
        Assert.Equal(Stored(new DateTimeOffset(2030, 7, 15, 22, 0, 0, TimeSpan.Zero).AddTicks(-1)), (await PollRowAsync(factory, id)).ClosesAt);
    }

    [Fact]
    public async Task Default_timezone_is_the_creators_and_is_frozen_at_creation()
    {
        await using var factory = new AppFactory();
        await NoDurationLimitAsync(factory);
        var owner = await CreateUserAsync(factory, "o@example.com");
        var plain = await CreatePollAsync(factory, owner, Draft().Closes("2030-01-31"));
        Assert.Equal(Stored(new DateTimeOffset(2030, 2, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(-1)), (await PollRowAsync(factory, plain)).ClosesAt); // no zone: UTC

        await SetTimeZoneAsync(factory, owner, "America/New_York");
        var id = await CreatePollAsync(factory, owner, Draft().Closes("2030-01-31"));
        var expected = Stored(new DateTimeOffset(2030, 2, 1, 5, 0, 0, TimeSpan.Zero).AddTicks(-1)); // EST = UTC-5
        var poll = await PollRowAsync(factory, id);
        Assert.Equal(expected, poll.ClosesAt);
        Assert.Equal("America/New_York", PollJson.DeserializeDefinition(poll.DefinitionJson).TimeZone);

        // A later change of the account setting does not move the close, not even on edit.
        await SetTimeZoneAsync(factory, owner, "Asia/Tokyo");
        Assert.True((await UpdateAsync(factory, owner, id, Draft(title: "Renamed").Closes("2030-01-31"))).Succeeded);
        poll = await PollRowAsync(factory, id);
        Assert.Equal(expected, poll.ClosesAt);
        Assert.Equal("America/New_York", PollJson.DeserializeDefinition(poll.DefinitionJson).TimeZone);

        // The vote page shows the close time in the viewer's zone, else in the poll's, with the zone name.
        var anonymous = await (await GetPageAsync(factory.CreateBrowser(), $"/p/{id}")).Content.ReadAsStringAsync();
        Assert.Contains("Closes 2030-01-31 23:59 America/New_York<", anonymous);
        var tokyo = await (await (await LoggedInBrowserAsync(factory, "o@example.com")).GetAsync($"/p/{id}")).Content.ReadAsStringAsync();
        Assert.Contains("Closes 2030-02-01 13:59 Asia/Tokyo<", tokyo);
    }

    // ---- compatibility --------------------------------------------------------------------------------

    /// <summary>
    /// Seeds a database in the pre-5b shape (migrated to Phase5aAccounts, rows written as that schema
    /// stored them: VoterMode "Link", a ballot with only a VoterKey),
    /// then lets the app migrate it at startup.
    /// </summary>
    [SqliteOnlyFact]
    public async Task Phase5bVoters_migrations_backfill_existing_ballots_and_polls()
    {
        var dir = Path.Combine(Path.GetTempPath(), "telrang-tests-" + Guid.NewGuid().ToString("N"));
        var settings = new Dictionary<string, string?> { ["App:DataDir"] = dir };
        var pollId = PollId.NewId();
        var id = PollId.Encode(pollId);
        const string rawVk = "pre-migration-browser";
        try
        {
            // 1. First start: creates the key ring and the voter-key secret; compute the old ballot's key.
            string voterKey;
            await using (var first = new AppFactory(settings))
            {
                voterKey = first.Services.GetRequiredService<VoterKeys>().ForPoll(rawVk, pollId);
            }
            SqliteConnection.ClearAllPools();
            foreach (var file in Directory.GetFiles(dir, "app.db*")) File.Delete(file);

            // 2. A database in the Phase 5a shape with one poll and one ballot.
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite($"Data Source={Path.Combine(dir, "app.db")}").Options;
            await using (var db = new ApplicationDbContext(options))
            {
                await db.GetService<IMigrator>().MigrateAsync("20261025171736_Phase5aAccounts");
                var owner = Guid.NewGuid().ToString();
                var poll = pollId.ToString().ToUpperInvariant();
                await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO "AspNetUsers" ("Id", "UserName", "NormalizedUserName", "Email", "NormalizedEmail", "EmailConfirmed",
                        "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount", "CreatedAt", "PendingSetup", "MustEnrolTwoFactor")
                    VALUES ({0}, 'old@example.com', 'OLD@EXAMPLE.COM', 'old@example.com', 'OLD@EXAMPLE.COM', 1, 0, 0, 1, 0, '2026-10-01 10:00:00+00:00', 0, 0);
                    """, owner);
                // The definition and payload use today's shape (DefinitionJson is never migrated; no release had another).
                var definition = PollJson.Serialize(Definition(Ranked("Old trip", "Paris", "Rome").Results(ResultsVisibility.AfterVote).Closes("2099-01-31")));
                await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO "Polls" ("Id", "OwnerId", "Title", "Type", "Scoring", "Source", "DefinitionJson", "PasswordVersion",
                        "ResultsVisibility", "VoterMode", "ClosesAt", "CreatedAt", "UpdatedAt")
                    VALUES ({0}, {1}, 'Old trip', 'Ranked', 'Borda', {2}, {3}, 0, 'AfterVote', 'Link', '2099-01-31 23:59:59.9999999+00:00',
                        '2026-10-01 10:00:00+00:00', '2026-10-01 10:00:00+00:00');
                    """, poll, owner, "title: Old trip\ntype: ranked\nresults: after-vote\nvoters: link\ncloses: 2099-01-31\n---\n- Paris\n- Rome\n", definition);
                await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO "Ballots" ("Id", "PollId", "VoterKey", "DisplayName", "SubmittedAt", "UpdatedAt", "PayloadJson")
                    VALUES ({0}, {1}, {2}, 'Old voter', '2026-10-02 10:00:00+00:00', '2026-10-02 10:00:00+00:00', '{{"q1":{{"ranked":["rome","paris"],"banned":[]}}}}');
                    """, Guid.NewGuid().ToString().ToUpperInvariant(), poll, voterKey);
            }
            SqliteConnection.ClearAllPools();

            // 3. Second start migrates to the current schema.
            await using var factory = new AppFactory(settings);
            var ballot = Assert.Single(await BallotsAsync(factory, id));
            Assert.Equal("k:" + voterKey, ballot.VoterIdentity);
            Assert.Equal(1, ballot.ResponseNo);
            Assert.Null(ballot.UserId);
            var row = await PollRowAsync(factory, id);
            Assert.Equal(VoterMode.Public, row.VoterMode);

            // The old browser still owns its ballot and can revise it; the definition reads as public, UTC.
            var visitor = new VisitorContext(null, rawVk, null);
            var state = (await StateAsync(factory, id, visitor))!;
            Assert.True(state.Access.HasBallot);
            Assert.True(state.Access.CanSeeResults);
            Assert.Equal(["rome", "paris"], state.Ballot!.Ranked!.Ranked);
            Assert.Equal("UTC", state.Access.Definition.TimeZone);
            Assert.Equal(new DateOnly(2099, 1, 31), state.Access.Definition.ClosesOn);
            Assert.True((await SubmitAsync(factory, id, visitor, ParisFirst)).Succeeded);
            Assert.Single(await BallotsAsync(factory, id));
            var page = await (await GetPageAsync(factory.CreateBrowser(), $"/p/{id}")).Content.ReadAsStringAsync();
            Assert.Contains("Old trip", page);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
