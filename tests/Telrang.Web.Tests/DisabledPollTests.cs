using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Telrang.Core.Ballots;
using Telrang.Core.Ids;
using Telrang.Web.Data;
using Telrang.Web.Features.Accounts;
using Telrang.Web.Features.Admin;
using Telrang.Web.Features.Branding;
using Telrang.Web.Features.Polls;
using Telrang.Web.Features.Results;
using Telrang.Web.Features.Voting;
using Telrang.Web.Security;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>
/// DESIGN.md §34.8: a disabled poll answers 410 with one line on every /p/{id}… route for everyone but its owner and admins;
/// the owner sees it marked Disabled and can still export and delete it, nobody votes, live views end, the owner is mailed.
/// </summary>
public class DisabledPollTests
{
    private static Guid Raw(string id) => PollId.TryDecode(id, out var g) ? g : throw new ArgumentException(id);

    private static Task<AdminOutcome> AdminAsync(AppFactory factory, Func<AdminService, Task<AdminOutcome>> action) =>
        WithScope(factory, sp => action(sp.GetRequiredService<AdminService>()));

    /// <summary>An owner's poll with a look and one ballot, then disabled by an admin.</summary>
    private static async Task<(string Owner, string Poll, ClaimsPrincipal Admin)> DisabledPollAsync(AppFactory factory)
    {
        var owner = await CreateUserAsync(factory, "owner@example.com");
        var voter = await CreateUserAsync(factory, "voter@example.com");
        var admin = await LiveResultsNotifierTests.AdminAsync(factory);
        var id = await CreatePollAsync(factory, owner, RankedDraft(ResultsVisibility.Open).Titled("Secret title"));
        var look = ThemeLook.Create("#5a2a5f", "#d9a62e", "#2a6f2b", "#e0457b", "serif", 12).Look!;
        await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().Polls.Where(p => p.Id == Raw(id))
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.LookJson, look.ToJson())));
        var key = (await WithScope(factory, sp => sp.GetRequiredService<PollService>().GetForEditAsync(owner, id)))!.Definition.Questions[0].Options[0].Key;
        Assert.True((await SubmitRankedAsync(factory, id, VisitorContext.ForUser(voter), new RankedBallot([key], []))).Succeeded);
        var outcome = await AdminAsync(factory, a => a.DisablePollAsync(admin, id));
        Assert.True(outcome.Ok, outcome.Message);
        return (owner, id, admin);
    }

    private static async Task AssertGoneAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(PollAccessService.UnavailableMessage, body);
        Assert.DoesNotContain("Secret title", body);
        Assert.DoesNotContain("look.css", body);
    }

    [Fact]
    public async Task Every_poll_route_answers_410_to_visitors_and_nothing_of_the_poll()
    {
        await using var factory = new AppFactory();
        var (_, id, _) = await DisabledPollAsync(factory);

        var anonymous = factory.CreateBrowser();
        await AssertGoneAsync(await anonymous.GetAsync($"/p/{id}"));
        await AssertGoneAsync(await anonymous.GetAsync($"/p/{id}?t={InviteTokens.Generate().Token}"));
        await AssertGoneAsync(await anonymous.GetAsync($"/p/{id}/results"));
        await AssertGoneAsync(await anonymous.GetAsync($"/p/{id}/look.css"));
        await AssertGoneAsync(await anonymous.GetAsync($"/p/{id}/results/live"));

        // A signed-in voter of the poll gets the same, and the poll leaves "My votes" while it lasts.
        var voterBrowser = await LoggedInBrowserAsync(factory, "voter@example.com");
        await AssertGoneAsync(await voterBrowser.GetAsync($"/p/{id}"));
        await AssertGoneAsync(await voterBrowser.GetAsync($"/p/{id}/results"));
        await AssertGoneAsync(await voterBrowser.GetAsync($"/p/{id}/results/live"));
        Assert.Equal(HttpStatusCode.NotFound, (await voterBrowser.GetAsync($"/polls/{id}/results.csv")).StatusCode); // owner-only, as before
        var voterId = (await WithScope(factory, sp => sp.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync("voter@example.com")))!.Id;
        Assert.Empty(await WithScope(factory, sp => sp.GetRequiredService<BallotService>().ListMyVotesAsync(voterId)));

        // A vote posted anyway: the same 410, nothing stored.
        var token = AppFactory.ScrapeAntiforgeryToken(await (await anonymous.GetAsync("/Account/Login")).Content.ReadAsStringAsync());
        await AssertGoneAsync(await anonymous.PostAsync($"/p/{id}", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["_handler"] = "vote",
        })));
        var refused = await SubmitRankedAsync(factory, id, VisitorContext.ForUser(voterId), new RankedBallot([], []));
        Assert.Equal(SubmitStatus.Denied, refused.Status);
        Assert.Contains(PollAccessService.UnavailableMessage, refused.Errors);
        Assert.Single(await BallotsAsync(factory, id));

        // Unknown ids stay 404.
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/p/AAAAAAAAAAAAAAAAAAAAAA")).StatusCode);
    }

    [Fact]
    public async Task The_owner_sees_it_disabled_cannot_put_it_back_into_use_and_can_export_and_delete_it()
    {
        await using var factory = new AppFactory();
        var (owner, id, _) = await DisabledPollAsync(factory);
        var browser = await LoggedInBrowserAsync(factory, "owner@example.com");

        var vote = await GetPageAsync(browser, $"/p/{id}");
        Assert.Equal(HttpStatusCode.OK, vote.StatusCode);
        var html = await vote.Content.ReadAsStringAsync();
        Assert.Contains(">Disabled<", html);
        Assert.Contains(PollAccessService.DisabledMessage, html);
        Assert.DoesNotContain("data-vote-form", html);
        var results = await (await GetPageAsync(browser, $"/p/{id}/results")).Content.ReadAsStringAsync();
        Assert.Contains(">Disabled<", results);
        Assert.DoesNotContain("Reopen poll", results);
        Assert.Contains(">Disabled<", await (await GetPageAsync(browser, "/polls")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync($"/p/{id}/look.css")).StatusCode);

        // Frozen: no edit, no reopening, no invite links; closing is allowed and stays closed.
        var edit = await UpdatePollAsync(factory, owner, id, RankedDraft(ResultsVisibility.Open).Titled("Changed"));
        Assert.False(edit.Succeeded);
        Assert.Equal(PollAccessService.DisabledMessage, edit.Message);
        Assert.True(await WithScope(factory, sp => sp.GetRequiredService<PollService>().SetClosedAsync(owner, id, true)));
        Assert.Equal((false, PollAccessService.DisabledMessage),
            await WithScope(factory, sp => sp.GetRequiredService<PollService>().ChangeClosedAsync(owner, id, false)));
        var invites = await WithScope(factory, sp => sp.GetRequiredService<PollService>().GenerateInvitesCheckedAsync(owner, id, 2, []));
        Assert.Equal(PollAccessService.DisabledMessage, invites.Error);
        var key = (await WithScope(factory, sp => sp.GetRequiredService<PollService>().GetForEditAsync(owner, id)))!.Definition.Questions[0].Options[0].Key;
        Assert.Equal(SubmitStatus.Denied, (await SubmitRankedAsync(factory, id, VisitorContext.ForUser(owner), new RankedBallot([key], []))).Status);

        // Export: both CSVs and the account data name it disabled.
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync($"/polls/{id}/export.csv")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync($"/polls/{id}/results.csv")).StatusCode);
        var export = await WithScope(factory, async sp =>
        {
            var user = (await sp.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(owner))!;
            return JsonDocument.Parse(await sp.GetRequiredService<AccountService>().ExportAsync(user));
        });
        var exported = export.RootElement.GetProperty("polls")[0];
        Assert.Equal(JsonValueKind.String, exported.GetProperty("disabledAt").ValueKind);

        Assert.True(await WithScope(factory, sp => sp.GetRequiredService<PollService>().DeletePollAsync(owner, id)));
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateBrowser().GetAsync($"/p/{id}")).StatusCode);
    }

    [Fact]
    public async Task An_admin_reviews_the_poll_read_only_and_enabling_restores_it()
    {
        await using var factory = new AppFactory();
        var (_, id, admin) = await DisabledPollAsync(factory);
        var browser = await LoggedInBrowserAsync(factory, "admin@example.com");

        var vote = await GetPageAsync(browser, $"/p/{id}");
        Assert.Equal(HttpStatusCode.OK, vote.StatusCode);
        var html = await vote.Content.ReadAsStringAsync();
        Assert.Contains("Secret title", html);
        Assert.Contains(">Disabled<", html);
        Assert.DoesNotContain("data-vote-form", html);
        Assert.Equal(HttpStatusCode.OK, (await GetPageAsync(browser, $"/p/{id}/results")).StatusCode); // open results, as for any visitor

        // A demoted admin is a visitor again: the database decides, not the principal.
        var adminId = admin.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            return (await users.RemoveFromRoleAsync((await users.FindByIdAsync(adminId))!, AppRoles.Admin)).Succeeded;
        });
        var asVisitor = await WithScope(factory, sp => sp.GetRequiredService<PollAccessService>().GetAsync(id, VisitorContext.ForUser(adminId)));
        Assert.True(asVisitor!.Unavailable);
        await WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            return (await users.AddToRoleAsync((await users.FindByIdAsync(adminId))!, AppRoles.Admin)).Succeeded;
        });

        Assert.False((await AdminAsync(factory, a => a.DisablePollAsync(admin, id))).Ok); // already disabled
        Assert.True((await AdminAsync(factory, a => a.EnablePollAsync(admin, id))).Ok);
        Assert.Equal(HttpStatusCode.OK, (await GetPageAsync(factory.CreateBrowser(), $"/p/{id}")).StatusCode);
        Assert.False((await AdminAsync(factory, a => a.EnablePollAsync(admin, id))).Ok);

        var log = await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().AdminActionLogs.AsNoTracking()
            .Where(l => l.TargetType == "poll").Select(l => l.Action).ToListAsync());
        Assert.Contains("poll.disable", log);
        Assert.Contains("poll.enable", log);
    }

    [Fact]
    public async Task Disabling_ends_live_views_and_mails_the_owner_once_without_report_content()
    {
        await using var factory = new AppFactory();
        await ConfigureMailAsync(factory);
        var owner = await CreateUserAsync(factory, "owner@example.com");
        var admin = await LiveResultsNotifierTests.AdminAsync(factory);
        var id = await CreatePollAsync(factory, owner, RankedDraft().Titled("Bake sale"));
        using var sub = factory.Services.GetRequiredService<PollChangeNotifier>().Subscribe(Raw(id));

        Assert.True((await AdminAsync(factory, a => a.DisablePollAsync(admin, id))).Ok);
        Assert.True(sub.Ended);

        var mails = Revealed(factory, await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().OutboxMessages.AsNoTracking()
            .Where(m => m.Type == MailType.PollDisabled).ToListAsync()));
        var mail = Assert.Single(mails);
        Assert.Equal("owner@example.com", mail.To);
        Assert.Contains("Bake sale", mail.Subject);
        Assert.Contains("https://localhost/polls", mail.TextBody);
    }
}
