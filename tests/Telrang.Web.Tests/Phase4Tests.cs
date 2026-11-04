using System.Net;
using System.Security.Claims;
using System.Text;
using Telrang.Core.Ballots;
using Telrang.Core.Ids;
using Telrang.Web.Data;
using Telrang.Web.Features.Admin;
using Telrang.Web.Features.Polls;
using Telrang.Web.Features.Results;
using Telrang.Web.Features.Voting;
using Telrang.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>Phase 4: points voting and results, CSV export, admin page, owner conveniences.</summary>
public class Phase4Tests
{
    // ---- points voting ------------------------------------------------------------------------

    [Fact]
    public async Task Points_vote_page_prerenders_the_editor_and_rules()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, PointsDraft().Q(q => { q.Budget = 100; q.MaxPerOption = 60; q.SpendAll = true; }));

        var html = await (await GetPageAsync(factory.CreateBrowser(), $"/p/{id}")).Content.ReadAsStringAsync();
        Assert.Contains("Spread 100 points.", html);
        Assert.Contains("At most 60 per option", html);
        Assert.Contains("Spend all.", html);
        Assert.Contains("How scoring works", html);
        Assert.Contains("id=\"pts-paris\"", html);
        Assert.Contains("id=\"rng-paris\"", html);
        Assert.Contains("max=\"60\"", html);
        Assert.Contains("Remaining:", html);
        Assert.Contains("must spend all", html);
        Assert.Contains("aria-live=\"polite\"", html);
        Assert.DoesNotContain("not available yet", html);
    }

    [Fact]
    public async Task Points_ballots_are_validated_and_revisable()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, PointsDraft(results: ResultsVisibility.AfterVote).Q(q => { q.Budget = 10; q.MaxPerOption = 6; }));
        var (client, jar) = factory.CreateBrowserWithJar();
        await client.GetAsync($"/p/{id}");
        var visitor = VisitorFrom(factory, jar, id);

        Assert.Equal(SubmitStatus.Invalid, (await SubmitPointsAsync(factory, id, visitor, new() { ["paris"] = 6, ["rome"] = 5 })).Status); // 11 > 10
        Assert.Equal(SubmitStatus.Invalid, (await SubmitPointsAsync(factory, id, visitor, new() { ["paris"] = 7 })).Status);              // > max
        Assert.Equal(SubmitStatus.Invalid, (await SubmitPointsAsync(factory, id, visitor, new() { ["madrid"] = 1 })).Status);
        Assert.Equal(SubmitStatus.Invalid, (await SubmitPointsAsync(factory, id, visitor, new() { ["paris"] = -1 })).Status);
        Assert.Empty(await BallotsAsync(factory, id));

        var ok = await SubmitPointsAsync(factory, id, visitor, new() { ["paris"] = 6, ["rome"] = 4 });
        Assert.True(ok.Succeeded);
        Assert.True(ok.Access!.CanSeeResults);
        Assert.True((await SubmitPointsAsync(factory, id, visitor, new() { ["berlin"] = 3 })).Succeeded);
        var ballot = Assert.Single(await BallotsAsync(factory, id));
        Assert.Equal(3, PollJson.DeserializePoints(ballot.PayloadJson, "q1").Points["berlin"]);

        // The voter's page now shows the confirmation with their points.
        var html = await (await client.GetAsync($"/p/{id}")).Content.ReadAsStringAsync();
        Assert.Contains("You have already voted", html);
        Assert.Contains("3 points", html);
    }

    [Fact]
    public async Task Spend_all_requires_the_full_budget()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, PointsDraft().Q(q => { q.Budget = 10; q.SpendAll = true; }));
        var visitor = new VisitorContext(null, "browser-1", null);
        Assert.Equal(SubmitStatus.Invalid, (await SubmitPointsAsync(factory, id, visitor, new() { ["paris"] = 9 })).Status);
        Assert.True((await SubmitPointsAsync(factory, id, visitor, new() { ["paris"] = 9, ["rome"] = 1 })).Succeeded);
    }

    [Fact]
    public async Task Points_results_show_totals_supporters_and_distribution()
    {
        await using var factory = new AppFactory();
        var ownerBrowser = factory.CreateBrowser();
        await AppFactory.RegisterAsync(ownerBrowser, "owner@example.com", Password);
        var owner = await ConfirmEmailAsync(factory, "owner@example.com");
        await AppFactory.LoginAsync(ownerBrowser, "owner@example.com", Password);
        var id = await CreatePollAsync(factory, owner, PointsDraft().Q(q => { q.Budget = 100; q.MaxPerOption = 60; }));

        Assert.True((await SubmitPointsAsync(factory, id, new(null, "v1", null), new() { ["paris"] = 60, ["rome"] = 40 })).Succeeded);
        Assert.True((await SubmitPointsAsync(factory, id, new(null, "v2", null), new() { ["paris"] = 10, ["berlin"] = 15 })).Succeeded);

        var html = WebUtility.HtmlDecode(await (await ownerBrowser.GetAsync($"/p/{id}/results")).Content.ReadAsStringAsync());
        Assert.Contains("points-results", html);
        Assert.Contains("<strong>2</strong> voters", html);
        Assert.Contains("<td class=\"text-end font-monospace\">70</td>", html);   // Paris total
        Assert.Contains("<td class=\"text-end font-monospace\">35.0</td>", html); // Paris mean
        Assert.Contains("(100%)", html);                                          // Paris supporters
        Assert.Contains("1–15 points: 1 voter,", html);                           // Paris 10 / 60 -> first quarter
        Assert.Contains("46–60 points: 1 voter<", html);                          // Paris 60 -> last quarter
        Assert.DoesNotContain("not supported yet", html);
        Assert.Contains("href=\"polls/" + id + "/results.csv\"", html);
        Assert.Contains("Refresh", html);
    }

    [Theory]
    [InlineData(0, 60, 0)]
    [InlineData(1, 60, 1)]
    [InlineData(15, 60, 1)]
    [InlineData(16, 60, 2)]
    [InlineData(30, 60, 2)]
    [InlineData(31, 60, 3)]
    [InlineData(45, 60, 3)]
    [InlineData(46, 60, 4)]
    [InlineData(60, 60, 4)]
    [InlineData(1, 3, 2)]  // 1/3 = 33% -> 26–50%
    [InlineData(3, 3, 4)]
    public void Points_buckets(int value, int cap, int bucket) => Assert.Equal(bucket, PointsBuckets.Of(value, cap));

    [Fact]
    public void Points_bucket_labels()
    {
        Assert.Equal(["0", "1–25", "26–50", "51–75", "76–100"], Enumerable.Range(0, 5).Select(k => PointsBuckets.Label(k, 100)));
        Assert.Equal(["0", "–", "1", "2", "3"], Enumerable.Range(0, 5).Select(k => PointsBuckets.Label(k, 3)));
    }

    // ---- CSV export ---------------------------------------------------------------------------

    private const string TrickyOptions = "- Paris, \"the city\"\n- Rome\n- =Berlin";

    [Fact]
    public async Task Owner_downloads_ballots_csv_with_rfc4180_quoting()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft().OptionsText(TrickyOptions).Names(NamesMode.Optional).Titled("Trip: 2027 / Ünïcode"));
        var def = (await WithScope(factory, sp => sp.GetRequiredService<PollService>().GetForEditAsync(owner, id)))!.Definition;
        var (paris, rome, berlin) = (def.Questions[0].Options[0].Key, def.Questions[0].Options[1].Key, def.Questions[0].Options[2].Key);
        Assert.True((await SubmitRankedAsync(factory, id, new(null, "v1", null), new RankedBallot([rome, paris], [berlin]), "Ann \"A\", Jr")).Succeeded);
        Assert.True((await SubmitRankedAsync(factory, id, new(null, "v2", null), new RankedBallot([paris], []), "@cmd")).Succeeded);

        var browser = await LoggedInBrowserAsync(factory, "o@example.com");
        var response = await browser.GetAsync($"/polls/{id}/export.csv");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType.CharSet);
        var disposition = response.Content.Headers.ContentDisposition!;
        Assert.Equal("attachment", disposition.DispositionType);
        Assert.Equal("Trip-2027-Unicode-ballots.csv", disposition.FileName?.Trim('"'));
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());

        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n");
        Assert.Equal("Name,Alias,SubmittedAt,UpdatedAt,\"Paris, \"\"the city\"\"\",Rome,'=Berlin", lines[0]);
        Assert.StartsWith("\"Ann \"\"A\"\", Jr\",,", lines[1]);
        Assert.EndsWith(",2,1,BAN", lines[1]);
        Assert.Matches(@"^'@cmd,,\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ,\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ,1,,$", lines[2]);
        Assert.Equal("", lines[3]); // trailing CRLF
        Assert.Equal(4, lines.Length);
    }

    [Fact]
    public async Task Results_csv_for_ranked_and_points()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var ranked = await CreatePollAsync(factory, owner, RankedDraft().OptionsText(TrickyOptions));
        var def = (await WithScope(factory, sp => sp.GetRequiredService<PollService>().GetForEditAsync(owner, ranked)))!.Definition;
        Assert.True((await SubmitRankedAsync(factory, ranked, new(null, "v1", null), new RankedBallot([def.Questions[0].Options[0].Key], [def.Questions[0].Options[2].Key]))).Succeeded);
        var points = await CreatePollAsync(factory, owner, PointsDraft().Q(q => q.Budget = 10));
        Assert.True((await SubmitPointsAsync(factory, points, new(null, "v1", null), new() { ["rome"] = 7, ["paris"] = 3 })).Succeeded);

        var browser = await LoggedInBrowserAsync(factory, "o@example.com");
        var text = Encoding.UTF8.GetString((await browser.GetByteArrayAsync($"/polls/{ranked}/results.csv"))[3..]);
        var lines = text.Split("\r\n");
        Assert.Equal("Place,Option,Score,FirstChoices,Bans,BanPercent", lines[0]);
        Assert.Equal("1,\"Paris, \"\"the city\"\"\",3,1,0,0", lines[1]);
        Assert.Equal("2,Rome,0,0,0,0", lines[2]);
        Assert.Equal("2,'=Berlin,0,0,1,100", lines[3]);

        var response = await browser.GetAsync($"/polls/{points}/results.csv");
        Assert.Equal("Budget-results.csv", response.Content.Headers.ContentDisposition!.FileName?.Trim('"'));
        lines = Encoding.UTF8.GetString((await response.Content.ReadAsByteArrayAsync())[3..]).Split("\r\n");
        Assert.StartsWith("Place,Option,Total,Mean,Supporters,SupporterPercent,Max,Voters 0 pts,", lines[0]);
        Assert.Equal("1,Rome,7,7,1,100,7,0,0,0,1,0", lines[1]);  // 7/10 -> 51–75%
        Assert.Equal("2,Paris,3,3,1,100,3,0,0,1,0,0", lines[2]); // 3/10 -> 26–50%
        Assert.Equal("3,Berlin,0,0,0,0,0,1,0,0,0,0", lines[3]);

        var ballots = Encoding.UTF8.GetString((await browser.GetByteArrayAsync($"/polls/{points}/export.csv"))[3..]).Split("\r\n");
        Assert.Equal("Name,Alias,SubmittedAt,UpdatedAt,Paris,Rome,Berlin", ballots[0]);
        Assert.EndsWith(",3,7,0", ballots[1]);
    }

    [Fact]
    public async Task Csv_exports_are_owner_only()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        await CreateUserAsync(factory, "s@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft(results: ResultsVisibility.Open));

        foreach (var path in new[] { $"/polls/{id}/export.csv", $"/polls/{id}/results.csv" })
        {
            var anonymous = await factory.CreateBrowser().GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
            Assert.Contains("/Account/Login", anonymous.Headers.Location!.ToString());

            var stranger = await LoggedInBrowserAsync(factory, "s@example.com");
            Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(path.Replace(id, PollId.Encode(Guid.NewGuid())))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(path.Replace(id, "garbage"))).StatusCode);
        }

        // Non-owners do not get the CSV links on the (open) results page.
        var visitor = factory.CreateBrowser();
        var html = await (await visitor.GetAsync($"/p/{id}/results")).Content.ReadAsStringAsync();
        Assert.Contains("results-table", html);
        Assert.DoesNotContain("export.csv", html);
    }

    [Theory]
    [InlineData("Trip", "Trip-x.csv")]
    [InlineData("  ¿¿¿  ", "poll-x.csv")]
    [InlineData("a/b\\c:d*e?f\"g<h>i|j", "a-b-c-d-e-f-g-h-i-j-x.csv")]
    [InlineData("Café crème", "Cafe-creme-x.csv")]
    public void Csv_file_names_are_safe(string title, string expected) => Assert.Equal(expected, Csv.SafeFileName(title, "x"));

    // ---- owner conveniences -------------------------------------------------------------------

    [Fact]
    public async Task Owner_can_delete_a_poll_with_cascade_and_strangers_cannot()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var stranger = await CreateUserAsync(factory, "s@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft(voters: VoterMode.Invite));
        var invites = await WithScope(factory, sp => sp.GetRequiredService<PollService>().GenerateInvitesAsync(owner, id, 2, []));
        Assert.True(InviteTokens.TryHash(invites![0].Token, out var hash));
        Assert.True((await SubmitRankedAsync(factory, id, new(null, null, hash), new RankedBallot(["paris"], []))).Succeeded);

        Assert.False(await WithScope(factory, sp => sp.GetRequiredService<PollService>().DeletePollAsync(stranger, id)));
        Assert.Single(await BallotsAsync(factory, id));

        Assert.True(await WithScope(factory, sp => sp.GetRequiredService<PollService>().DeletePollAsync(owner, id)));
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateBrowser().GetAsync($"/p/{id}")).StatusCode);
        Assert.True(PollId.TryDecode(id, out var pollId));
        await WithScope(factory, async sp =>
        {
            var db = sp.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(0, await db.Ballots.CountAsync(b => b.PollId == pollId));
            Assert.Equal(0, await db.InviteTokens.CountAsync(t => t.PollId == pollId));
            return true;
        });
    }

    [Fact]
    public async Task Owner_can_revoke_unused_invites_only()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var stranger = await CreateUserAsync(factory, "s@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft(voters: VoterMode.Invite));
        var polls = (Func<IServiceProvider, PollService>)(sp => sp.GetRequiredService<PollService>());
        var invites = await WithScope(factory, sp => polls(sp).GenerateInvitesAsync(owner, id, 0, ["Used", "Unused"]));
        Assert.True(InviteTokens.TryHash(invites![0].Token, out var usedHash));
        Assert.True((await SubmitRankedAsync(factory, id, new(null, null, usedHash), new RankedBallot(["paris"], []))).Succeeded);

        var list = (await WithScope(factory, sp => polls(sp).ListInvitesAsync(owner, id)))!;
        var used = list.Single(i => i.Alias == "Used");
        var unused = list.Single(i => i.Alias == "Unused");

        Assert.False(await WithScope(factory, sp => polls(sp).RevokeInviteAsync(stranger, id, unused.Id)));
        Assert.False(await WithScope(factory, sp => polls(sp).RevokeInviteAsync(owner, id, used.Id)));
        Assert.True(await WithScope(factory, sp => polls(sp).RevokeInviteAsync(owner, id, unused.Id)));
        Assert.Equal(["Used"], (await WithScope(factory, sp => polls(sp).ListInvitesAsync(owner, id)))!.Select(i => i.Alias));

        // The revoked link is dead; the used one still works.
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateBrowser().GetAsync($"/p/{id}?t={invites[1].Token}")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await factory.CreateBrowser().GetAsync($"/p/{id}?t={invites[0].Token}")).StatusCode);
    }

    // ---- admin --------------------------------------------------------------------------------

    private static async Task<(string Id, ClaimsPrincipal Principal)> UserWithPrincipalAsync(AppFactory factory, string email, bool admin)
    {
        var id = await CreateUserAsync(factory, email);
        var principal = await WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(id))!;
            if (admin) Assert.True((await users.AddToRoleAsync(user, AppRoles.Admin)).Succeeded);
            return await sp.GetRequiredService<SignInManager<ApplicationUser>>().CreateUserPrincipalAsync(user);
        });
        return (id, principal);
    }

    private static Task<T> Admin<T>(AppFactory factory, Func<AdminService, Task<T>> action) =>
        WithScope(factory, sp => action(sp.GetRequiredService<AdminService>()));

    [Fact]
    public async Task Admin_operations_are_denied_to_non_admins()
    {
        await using var factory = new AppFactory();
        var (_, adminPrincipal) = await UserWithPrincipalAsync(factory, "admin@example.com", admin: true);
        var (userId, user) = await UserWithPrincipalAsync(factory, "user@example.com", admin: false);
        var id = await CreatePollAsync(factory, userId, RankedDraft());

        Assert.Null(await Admin(factory, a => a.ListPollsAsync(user)));
        Assert.Null(await Admin(factory, a => a.ListUsersAsync(user)));
        Assert.False((await Admin(factory, a => a.DeletePollAsync(user, id))).Ok);
        Assert.False((await Admin(factory, a => a.SetPollClosedAsync(user, id, true))).Ok);
        Assert.False((await Admin(factory, a => a.DisableTwoFactorAsync(user, userId))).Ok);
        Assert.False((await Admin(factory, a => a.DeleteUserAsync(user, userId))).Ok);
        Assert.Single((await Admin(factory, a => a.ListPollsAsync(adminPrincipal)))!);

        var browser = await LoggedInBrowserAsync(factory, "user@example.com");
        var denied = await browser.GetAsync("/admin?tab=polls");
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        Assert.Contains("/Account/AccessDenied", denied.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Admin_lists_closes_and_deletes_polls_with_cascade()
    {
        await using var factory = new AppFactory();
        var (_, admin) = await UserWithPrincipalAsync(factory, "admin@example.com", admin: true);
        var owner = await CreateUserAsync(factory, "owner@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft(voters: VoterMode.Invite));
        var invites = await WithScope(factory, sp => sp.GetRequiredService<PollService>().GenerateInvitesAsync(owner, id, 2, []));
        Assert.True(InviteTokens.TryHash(invites![0].Token, out var hash));
        Assert.True((await SubmitRankedAsync(factory, id, new(null, null, hash), new RankedBallot(["paris"], []))).Succeeded);

        var row = Assert.Single((await Admin(factory, a => a.ListPollsAsync(admin)))!);
        Assert.Equal(("Trip", "owner@example.com", 1, false), (row.Title, row.OwnerEmail, row.BallotCount, row.IsClosed));

        var html = await (await (await LoggedInBrowserAsync(factory, "admin@example.com")).GetAsync("/admin?tab=polls")).Content.ReadAsStringAsync();
        Assert.Contains("owner@example.com", html);
        Assert.Contains($"href=\"p/{id}\"", html);

        Assert.True((await Admin(factory, a => a.SetPollClosedAsync(admin, id, true))).Ok);
        Assert.True((await Admin(factory, a => a.ListPollsAsync(admin)))![0].IsClosed);
        Assert.True((await Admin(factory, a => a.SetPollClosedAsync(admin, id, false))).Ok);
        Assert.False((await Admin(factory, a => a.ListPollsAsync(admin)))![0].IsClosed);

        Assert.True((await Admin(factory, a => a.DeletePollAsync(admin, id))).Ok);
        Assert.Empty((await Admin(factory, a => a.ListPollsAsync(admin)))!);
        Assert.False((await Admin(factory, a => a.DeletePollAsync(admin, id))).Ok);
        await WithScope(factory, async sp =>
        {
            var db = sp.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(0, await db.Ballots.CountAsync());
            Assert.Equal(0, await db.InviteTokens.CountAsync());
            return true;
        });
    }

    [Fact]
    public async Task Admin_reset_2fa_clears_key_and_recovery_codes()
    {
        await using var factory = new AppFactory();
        var (_, admin) = await UserWithPrincipalAsync(factory, "admin@example.com", admin: true);
        var userId = await CreateUserAsync(factory, "user@example.com");
        var stampBefore = await WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(userId))!;
            await users.ResetAuthenticatorKeyAsync(user);
            await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
            await users.SetTwoFactorEnabledAsync(user, true);
            return await users.GetSecurityStampAsync(user);
        });

        var listed = (await Admin(factory, a => a.ListUsersAsync(admin)))!.Single(u => u.Id == userId);
        Assert.True(listed.TwoFactorEnabled);

        Assert.True((await Admin(factory, a => a.DisableTwoFactorAsync(admin, userId))).Ok);
        await WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(userId))!;
            Assert.False(await users.GetTwoFactorEnabledAsync(user));
            Assert.Null(await users.GetAuthenticatorKeyAsync(user));
            Assert.Equal(0, await users.CountRecoveryCodesAsync(user));
            Assert.NotEqual(stampBefore, await users.GetSecurityStampAsync(user));
            return true;
        });
    }

    [Fact]
    public async Task Admin_delete_user_removes_their_polls_and_refuses_the_last_admin()
    {
        await using var factory = new AppFactory();
        var (adminId, admin) = await UserWithPrincipalAsync(factory, "admin@example.com", admin: true);
        var (otherAdminId, _) = await UserWithPrincipalAsync(factory, "admin2@example.com", admin: true);
        var userId = await CreateUserAsync(factory, "user@example.com");
        var id = await CreatePollAsync(factory, userId, RankedDraft());
        Assert.True((await SubmitRankedAsync(factory, id, new(null, "v", null), new RankedBallot(["paris"], []))).Succeeded);
        var adminsPoll = await CreatePollAsync(factory, adminId, RankedDraft());

        // Yourself: refused.
        Assert.False((await Admin(factory, a => a.DeleteUserAsync(admin, adminId))).Ok);

        // A normal user: deleted with their polls and ballots.
        var deleted = await Admin(factory, a => a.DeleteUserAsync(admin, userId));
        Assert.True(deleted.Ok, deleted.Message);
        Assert.Contains("1 poll", deleted.Message);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateBrowser().GetAsync($"/p/{id}")).StatusCode);
        Assert.Empty(await BallotsAsync(factory, id));
        // Soft delete: the row stays, without email, for references (DESIGN.md §14.3).
        await WithScope(factory, async sp =>
        {
            var row = await sp.GetRequiredService<ApplicationDbContext>().Users.AsNoTracking().SingleAsync(u => u.Id == userId);
            Assert.NotNull(row.DeletedAt);
            Assert.Null(row.Email);
            Assert.Equal(userId, Reveal(factory, row.UserName));
            return true;
        });

        // Another admin can be deleted while one admin remains...
        Assert.True((await Admin(factory, a => a.DeleteUserAsync(admin, otherAdminId))).Ok);
        // A principal issued while "third" was an admin, demoted in the database afterwards: refused (pentest R2-ACC-2),
        // so the last admin stays.
        var (thirdId, third) = await UserWithPrincipalAsync(factory, "admin3@example.com", admin: true);
        await WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.True((await users.RemoveFromRoleAsync((await users.FindByIdAsync(thirdId))!, AppRoles.Admin)).Succeeded);
            return true;
        });
        var refused = await Admin(factory, a => a.DeleteUserAsync(third, adminId));
        Assert.False(refused.Ok);
        Assert.Equal(AdminOutcome.Denied, refused); // the demoted caller is refused before the last-admin check (pentest R2-ACC-2)
        Assert.Single((await Admin(factory, a => a.ListPollsAsync(admin)))!, p => p.EncodedId == adminsPoll);
    }
}
