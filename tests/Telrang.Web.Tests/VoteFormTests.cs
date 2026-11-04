using System.Net;
using System.Text.RegularExpressions;
using Telrang.Core.Ballots;
using Telrang.Core.Polls;
using Telrang.Web.Features.Polls;
using Telrang.Web.Features.Voting;
using Telrang.Web.Security;
using Microsoft.Extensions.DependencyInjection;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>DESIGN.md §21.1: the vote page is a static form posted back to itself (post/redirect/get).</summary>
public partial class VoteFormTests
{
    [GeneratedRegex("name=\"fid\" value=\"([^\"]+)\"")]
    private static partial Regex FormIdRegex();

    [GeneratedRegex("<input type=\"hidden\" name=\"r\" value=\"(\\d+)\"")]
    private static partial Regex ResponseRegex();

    /// <summary>The vote page after the one-time cookie check.</summary>
    private static async Task<string> PageAsync(HttpClient client, string path)
    {
        var response = await GetPageAsync(client, path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>Posts the page's named form the way a browser would: its antiforgery token, stamp, form id and response.</summary>
    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string id, string html, string handler, IDictionary<string, string> fields)
    {
        var form = new Dictionary<string, string>(fields)
        {
            ["__RequestVerificationToken"] = AppFactory.ScrapeAntiforgeryToken(html),
            ["_handler"] = handler,
        };
        if (handler == VotePage.VoteForm)
        {
            if (AppFactory.ScrapeFormStamp(html) is { } stamp) form.TryAdd(FormGuard.StampField, stamp);
            if (FormIdRegex().Match(html) is { Success: true } f) form.TryAdd(BallotForm.FormIdField, f.Groups[1].Value);
            if (ResponseRegex().Match(html) is { Success: true } r) form.TryAdd(BallotForm.ResponseField, r.Groups[1].Value);
        }
        return client.PostAsync($"/p/{id}", new FormUrlEncodedContent(form));
    }

    private static Task<HttpResponseMessage> VoteAsync(HttpClient client, string id, string html, params (string Key, string Value)[] fields) =>
        PostAsync(client, id, html, VotePage.VoteForm, fields.ToDictionary(f => f.Key, f => f.Value));

    private static async Task<string> ExpectPageAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    private static void ExpectSaved(HttpResponseMessage response, string id)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.EndsWith($"/p/{id}?saved=1", response.Headers.Location!.ToString());
    }

    private static async Task<RankedBallot> RankedOfAsync(AppFactory factory, string id, int responseNo = 1)
    {
        var row = Assert.Single(await BallotsAsync(factory, id), b => b.ResponseNo == responseNo);
        return PollJson.DeserializeRanked(row.PayloadJson, "q1")!;
    }

    // ---- ranked -------------------------------------------------------------------------------------

    [Fact]
    public async Task Ranked_form_saves_compacts_gaps_and_redirects_to_the_summary()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft());
        var browser = factory.CreateBrowser();

        var html = await PageAsync(browser, $"/p/{id}");
        Assert.Contains("method=\"post\"", html);
        Assert.Contains("name=\"o.paris\"", html);
        Assert.Contains("name=\"b.berlin\"", html);
        Assert.Contains("data-rank-board hidden", html);
        Assert.DoesNotContain("<!--Blazor:", html);          // no server-component marker: no circuit

        var saved = await VoteAsync(browser, id, html, ("o.rome", "2"), ("o.paris", "9"), ("b.berlin", "1"), ("o.unknown", "1"));
        ExpectSaved(saved, id);
        var ballot = await RankedOfAsync(factory, id);
        Assert.Equal(["rome", "paris"], ballot.Ranked);
        Assert.Equal(["berlin"], ballot.Banned);

        var summary = await PageAsync(browser, $"/p/{id}?saved=1");
        Assert.Contains("Your ballot was saved.", summary);
        Assert.Contains($"href=\"p/{id}?r=1&amp;edit=1\"", summary);
        Assert.Contains("You have already voted.", await PageAsync(browser, $"/p/{id}"));

        // Change ballot: the editor holds the saved positions; the next save updates the one ballot.
        var edit = await PageAsync(browser, $"/p/{id}?r=1&edit=1");
        Assert.Contains("Update ballot", edit);
        Assert.Matches("name=\"o.rome\"[^>]*value=\"1\"", edit);
        ExpectSaved(await VoteAsync(browser, id, edit, ("o.paris", "1")), id);
        Assert.Equal(["paris"], (await RankedOfAsync(factory, id)).Ranked);
        Assert.Single(await BallotsAsync(factory, id));
    }

    [Fact]
    public async Task Ranked_ties_and_ranked_and_banned_are_refused_with_the_values_kept()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft().Names(NamesMode.Optional));
        var browser = factory.CreateBrowser();
        var html = await PageAsync(browser, $"/p/{id}");

        var tie = await ExpectPageAsync(await VoteAsync(browser, id, html, ("o.paris", "1"), ("o.rome", "1"), ("name", "Ann")));
        Assert.Contains(BallotForm.TiesMessage, tie);
        Assert.Matches("name=\"o.paris\"[^>]*value=\"1\"", tie);
        Assert.Matches("name=\"o.rome\"[^>]*value=\"1\"", tie);
        Assert.Contains("value=\"Ann\"", tie);

        var both = await ExpectPageAsync(await VoteAsync(browser, id, html, ("o.paris", "1"), ("b.paris", "1")));
        Assert.Contains(BallotForm.RankedAndBannedMessage, both);

        var text = await ExpectPageAsync(await VoteAsync(browser, id, html, ("o.paris", "first")));
        Assert.Contains(BallotForm.PositionFormatMessage, text);

        // Too many bans: the validator (the authority) refuses.
        Assert.Equal(HttpStatusCode.OK, (await VoteAsync(browser, id, html, ("b.paris", "1"), ("b.rome", "1"))).StatusCode);
        Assert.Empty(await BallotsAsync(factory, id));
    }

    // ---- points and dates ---------------------------------------------------------------------------

    [Fact]
    public async Task Points_form_saves_and_refuses_over_budget_with_the_values_kept()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, PointsDraft());
        var browser = factory.CreateBrowser();
        var html = await PageAsync(browser, $"/p/{id}");
        Assert.Contains("Remaining: <strong data-remaining-value>10</strong> of 10", html);

        var over = await ExpectPageAsync(await VoteAsync(browser, id, html, ("o.paris", "8"), ("o.rome", "5")));
        Assert.Matches("name=\"o.paris\"[^>]*value=\"8\"", over);
        Assert.Contains("Remaining: <strong data-remaining-value>-3</strong>", over);
        Assert.Contains("alert-danger", over);
        Assert.Contains(BallotForm.PointsFormatMessage, await ExpectPageAsync(await VoteAsync(browser, id, html, ("o.paris", "-1"))));
        Assert.Empty(await BallotsAsync(factory, id));

        ExpectSaved(await VoteAsync(browser, id, html, ("o.paris", "6"), ("o.rome", "4"), ("o.berlin", "")), id);
        var row = Assert.Single(await BallotsAsync(factory, id));
        Assert.Equal(new Dictionary<string, int> { ["paris"] = 6, ["rome"] = 4 }, PollJson.DeserializePoints(row.PayloadJson, "q1")!.Points);
    }

    [Fact]
    public async Task Dates_form_saves_and_the_layout_switch_saves_nothing()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var draft = Dates("Meet", "2026-11-03", "2026-11-05 19:00", "2026-11-06");
        var id = await CreatePollAsync(factory, owner, draft);
        var keys = Definition(draft).Questions[0].Options.Select(o => o.Key).ToArray();
        var browser = factory.CreateBrowser();
        var html = await PageAsync(browser, $"/p/{id}");
        Assert.Contains("data-panel=\"calendar\"", html);
        Assert.Contains("data-panel=\"list\" hidden", html);

        // No JS: "List" re-renders with the posted answers checked in the list, the calendar hidden, nothing saved.
        var list = await ExpectPageAsync(await VoteAsync(browser, id, html, ($"o.{keys[0]}", "yes"), ($"o.{keys[1]}", "maybe"), ("view", "list")));
        Assert.Contains("data-panel=\"calendar\" hidden", list);
        Assert.DoesNotContain("data-panel=\"list\" hidden", list);
        Assert.Matches($"id=\"d-list-{keys[0]}-yes\"[^>]*checked", list);
        Assert.Matches($"id=\"d-list-{keys[1]}-maybe\"[^>]*checked", list);
        Assert.DoesNotMatch($"id=\"d-calendar-{keys[0]}-yes\"[^>]*checked", list);
        Assert.Contains("name=\"shown\" value=\"list\"", list);
        Assert.Contains("1 yes · 1 maybe", list);
        Assert.Empty(await BallotsAsync(factory, id));

        ExpectSaved(await VoteAsync(browser, id, list, ($"o.{keys[0]}", "yes"), ($"o.{keys[2]}", "maybe"), ($"o.{keys[1]}", "bogus")), id);
        var row = Assert.Single(await BallotsAsync(factory, id));
        var answers = PollJson.DeserializeDates(row.PayloadJson, "q1")!.Answers;
        Assert.Equal(Availability.Yes, answers[keys[0]]);
        Assert.Equal(Availability.Maybe, answers[keys[2]]);
        Assert.False(answers.ContainsKey(keys[1]));
    }

    // ---- responses: many, delete, double submit, tampering -----------------------------------------

    [Fact]
    public async Task Double_submit_of_one_form_creates_one_response()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft().Many());
        var browser = factory.CreateBrowser();
        var html = await PageAsync(browser, $"/p/{id}");

        ExpectSaved(await VoteAsync(browser, id, html, ("o.paris", "1")), id);
        ExpectSaved(await VoteAsync(browser, id, html, ("o.paris", "1")), id); // same form id: the first response
        Assert.Single(await BallotsAsync(factory, id));

        // ?new=1 renders a new form (new id): a second response.
        var again = await PageAsync(browser, $"/p/{id}?new=1");
        Assert.Contains("Submit response", again);
        ExpectSaved(await VoteAsync(browser, id, again, ("o.rome", "1")), id);
        Assert.Equal(2, (await BallotsAsync(factory, id)).Count);
        var summary = await PageAsync(browser, $"/p/{id}?saved=1");
        Assert.Contains("Response saved.", summary);
        Assert.Contains("Your responses", summary);
    }

    [Fact]
    public async Task Delete_asks_first_then_deletes_only_this_voters_response()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft().Many());
        var browser = factory.CreateBrowser();
        ExpectSaved(await VoteAsync(browser, id, await PageAsync(browser, $"/p/{id}"), ("o.paris", "1")), id);
        ExpectSaved(await VoteAsync(browser, id, await PageAsync(browser, $"/p/{id}?new=1"), ("o.rome", "1")), id);

        var list = await PageAsync(browser, $"/p/{id}");
        Assert.Contains("name=\"r\" value=\"2\"", list);
        var ask = await ExpectPageAsync(await PostAsync(browser, id, list, VotePage.DeleteForm, new Dictionary<string, string> { ["r"] = "2" }));
        Assert.Contains("Delete this response?", ask);
        Assert.Contains("name=\"confirm\" value=\"2\"", ask);
        Assert.Contains("_handler\" value=\"vote\"", ask);   // both named forms on a POST render
        Assert.Equal(2, (await BallotsAsync(factory, id)).Count);

        var deleted = await PostAsync(browser, id, ask, VotePage.DeleteForm, new Dictionary<string, string> { ["confirm"] = "2" });
        Assert.Equal(HttpStatusCode.Redirect, deleted.StatusCode);
        Assert.EndsWith($"/p/{id}", deleted.Headers.Location!.ToString());
        Assert.Equal([1], (await BallotsAsync(factory, id)).Select(b => b.ResponseNo));

        // Another browser's number 1 is its own: deleting "1" there touches nothing of ours.
        var other = factory.CreateBrowser();
        ExpectSaved(await VoteAsync(other, id, await PageAsync(other, $"/p/{id}"), ("o.berlin", "1")), id);
        var otherList = await PageAsync(other, $"/p/{id}");
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(other, id, otherList, VotePage.DeleteForm, new Dictionary<string, string> { ["confirm"] = "1" })).StatusCode);
        var left = await BallotsAsync(factory, id);
        Assert.Single(left);
        Assert.Equal(["paris"], PollJson.DeserializeRanked(left[0].PayloadJson, "q1")!.Ranked);

        // A number that is not this voter's: "not found", nothing deleted.
        var gone = await ExpectPageAsync(await PostAsync(browser, id, await PageAsync(browser, $"/p/{id}"), VotePage.DeleteForm,
            new Dictionary<string, string> { ["confirm"] = "7" }));
        Assert.Contains(BallotService.ResponseGoneMessage, gone);
        Assert.Single(await BallotsAsync(factory, id));
    }

    [Fact]
    public async Task A_tampered_response_number_is_not_found_and_changes_nothing()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft().Many());
        var victim = factory.CreateBrowser();
        ExpectSaved(await VoteAsync(victim, id, await PageAsync(victim, $"/p/{id}"), ("o.paris", "1")), id);

        var attacker = factory.CreateBrowser();
        var html = await PageAsync(attacker, $"/p/{id}");
        var refused = await ExpectPageAsync(await VoteAsync(attacker, id, html, ("r", "1"), ("o.berlin", "1")));
        Assert.Contains(BallotService.ResponseGoneMessage, refused);
        Assert.Matches("name=\"o.berlin\"[^>]*value=\"1\"", refused);
        Assert.Equal(["paris"], (await RankedOfAsync(factory, id)).Ranked);
        Assert.Single(await BallotsAsync(factory, id));
    }

    // ---- state changes between GET and POST -----------------------------------------------------------

    [Fact]
    public async Task Poll_closed_between_get_and_post_is_refused_with_the_values_kept()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft());
        var browser = factory.CreateBrowser();
        var html = await PageAsync(browser, $"/p/{id}");
        Assert.True(await WithScope(factory, sp => sp.GetRequiredService<PollService>().SetClosedAsync(owner, id, true)));

        var page = await ExpectPageAsync(await VoteAsync(browser, id, html, ("o.rome", "1"), ("o.paris", "2")));
        Assert.Contains("Poll closed.", page);
        Assert.Matches("name=\"o.rome\"[^>]*value=\"1\"", page);
        Assert.DoesNotContain("data-submit", page);
        Assert.Contains("_handler\" value=\"delete\"", page);   // both named forms on a POST render
        Assert.Empty(await BallotsAsync(factory, id));
    }

    [Fact]
    public async Task Voter_cookie_gone_between_get_and_post_keeps_the_values_and_mints_nothing()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft());
        var (browser, jar) = factory.CreateBrowserWithJar();
        var html = await PageAsync(browser, $"/p/{id}");
        Assert.NotNull(VisitorFrom(factory, jar, id).LinkVoterKey);
        foreach (System.Net.Cookie c in jar.GetCookies(new Uri("https://localhost/"))) if (c.Name == PollCookies.VoterCookie) c.Expired = true;

        var response = await VoteAsync(browser, id, html, ("o.berlin", "1"));
        var page = await ExpectPageAsync(response);
        Assert.Contains(VotePage.NoCookieMessage, page);
        Assert.Matches("name=\"o.berlin\"[^>]*value=\"1\"", page);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var set) && set.Any(c => c.StartsWith("vk=")));
        Assert.Empty(await BallotsAsync(factory, id));
    }

    [Fact]
    public async Task Stamp_older_than_12_hours_is_refused_with_the_values_and_a_fresh_stamp()
    {
        var clock = new MutableTime(DateTimeOffset.UtcNow);
        // One signal would block: the refusal must not be one.
        await using var factory = new AppFactory(new Dictionary<string, string?> { ["Abuse:Threshold"] = "1" },
            services: s => { s.AddSingleton<TimeProvider>(clock); AddFakePeer(s); });
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft());
        var browser = factory.CreateBrowser();
        browser.DefaultRequestHeaders.Add(PeerHeader, "203.0.113.20");
        var html = await PageAsync(browser, $"/p/{id}");

        clock.Now += TimeSpan.FromHours(12) + TimeSpan.FromMinutes(1);
        var page = await ExpectPageAsync(await VoteAsync(browser, id, html, ("o.rome", "1")));
        Assert.Contains(BallotService.StampTooOldMessage, page);
        Assert.Matches("name=\"o.rome\"[^>]*value=\"1\"", page);
        Assert.NotEqual(AppFactory.ScrapeFormStamp(html), AppFactory.ScrapeFormStamp(page));
        Assert.Empty(await BallotsAsync(factory, id));

        // The re-rendered form works.
        ExpectSaved(await VoteAsync(browser, id, page, ("o.rome", "1")), id);
    }

    [Fact]
    public async Task Honeypot_is_refused_with_the_values_kept()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft());
        var browser = factory.CreateBrowser();
        var html = await PageAsync(browser, $"/p/{id}");
        Assert.Contains("name=\"website\"", html);
        var page = await ExpectPageAsync(await VoteAsync(browser, id, html, ("o.rome", "1"), (FormGuard.HoneypotField, "http://spam.example")));
        Assert.Contains(FormGuard.Message(FormCheck.Honeypot), page);
        Assert.Matches("name=\"o.rome\"[^>]*value=\"1\"", page);
        Assert.Empty(await BallotsAsync(factory, id));
    }

    // ---- antiforgery binds the form to who it was rendered for -------------------------------------

    private static async Task ExpectExpiredAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Form expired", await response.Content.ReadAsStringAsync());
    }

    private static void SignOut(System.Net.CookieContainer jar)
    {
        foreach (System.Net.Cookie c in jar.GetCookies(new Uri("https://localhost/")))
            if (c.Name.StartsWith(".AspNetCore.Identity.", StringComparison.Ordinal)) c.Expired = true;
    }

    [Fact]
    public async Task A_form_rendered_for_one_account_fails_after_another_signs_in()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        await CreateUserAsync(factory, "a@example.com");
        await CreateUserAsync(factory, "b@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft());
        var (browser, jar) = factory.CreateBrowserWithJar();
        Assert.Equal(HttpStatusCode.Redirect, (await AppFactory.LoginAsync(browser, "a@example.com", Password)).StatusCode);
        var html = await PageAsync(browser, $"/p/{id}");

        SignOut(jar);
        Assert.Equal(HttpStatusCode.Redirect, (await AppFactory.LoginAsync(browser, "b@example.com", Password)).StatusCode);
        await ExpectExpiredAsync(await VoteAsync(browser, id, html, ("o.paris", "1")));
        Assert.Empty(await BallotsAsync(factory, id));
    }

    [Fact]
    public async Task An_anonymous_form_fails_after_signing_in()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        await CreateUserAsync(factory, "a@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft());
        var (browser, _) = factory.CreateBrowserWithJar();
        var html = await PageAsync(browser, $"/p/{id}");

        Assert.Equal(HttpStatusCode.Redirect, (await AppFactory.LoginAsync(browser, "a@example.com", Password)).StatusCode);
        await ExpectExpiredAsync(await VoteAsync(browser, id, html, ("o.paris", "1")));
        Assert.Empty(await BallotsAsync(factory, id));
    }

    [Fact]
    public async Task A_signed_in_form_fails_after_signing_out()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        await CreateUserAsync(factory, "a@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft());
        var (browser, jar) = factory.CreateBrowserWithJar();
        Assert.Equal(HttpStatusCode.Redirect, (await AppFactory.LoginAsync(browser, "a@example.com", Password)).StatusCode);
        var html = await PageAsync(browser, $"/p/{id}");

        SignOut(jar);
        await ExpectExpiredAsync(await VoteAsync(browser, id, html, ("o.paris", "1")));
        Assert.Empty(await BallotsAsync(factory, id));

        // The same anonymous browser posting its own fresh form is fine (no binding needed: same vk, same voter).
        var fresh = await PageAsync(browser, $"/p/{id}");
        ExpectSaved(await VoteAsync(browser, id, fresh, ("o.paris", "1")), id);
        ExpectSaved(await VoteAsync(browser, id, await PageAsync(browser, $"/p/{id}?r=1&edit=1"), ("o.rome", "1")), id);
        Assert.Single(await BallotsAsync(factory, id));
    }

    [Fact]
    public async Task Signed_in_voters_vote_by_account_through_the_form()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var voter = await CreateUserAsync(factory, "a@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft().Voters(VoterMode.Accounts));
        var browser = await LoggedInBrowserAsync(factory, "a@example.com");
        var html = await PageAsync(browser, $"/p/{id}");
        ExpectSaved(await VoteAsync(browser, id, html, ("o.berlin", "1")), id);
        var row = Assert.Single(await BallotsAsync(factory, id));
        Assert.Equal(voter, row.UserId);
        Assert.Null(row.VoterKey);
    }

    // ---- limits, caching ----------------------------------------------------------------------------

    [Fact]
    public async Task An_oversized_body_is_413_and_too_many_values_400_never_500()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft());
        var browser = factory.CreateBrowser();
        var html = await PageAsync(browser, $"/p/{id}");

        // 70 values of 1 kB: under the value count, over the 64 kB body.
        var big = Enumerable.Range(0, 70).ToDictionary(i => $"x{i}", _ => new string('a', 1024));
        var tooLarge = await PostAsync(browser, id, html, VotePage.VoteForm, big);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        Assert.Contains("Form too large", await tooLarge.Content.ReadAsStringAsync());

        var many = Enumerable.Range(0, VoteFormLimits.MaxValues + 10).ToDictionary(i => $"x{i}", _ => "1");
        var tooMany = await PostAsync(browser, id, html, VotePage.VoteForm, many);
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Contains("Form too large", await tooMany.Content.ReadAsStringAsync());

        var longValue = await PostAsync(browser, id, html, VotePage.VoteForm,
            new Dictionary<string, string> { ["name"] = new string('a', VoteFormLimits.MaxValueLength + 1) });
        Assert.Equal(HttpStatusCode.BadRequest, longValue.StatusCode);
        Assert.Empty(await BallotsAsync(factory, id));

        // A full-size legitimate ballot still fits.
        ExpectSaved(await VoteAsync(browser, id, html, ("o.paris", "1")), id);
    }

    [Fact]
    public async Task Poll_pages_are_never_cached()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft(ResultsVisibility.Open));
        var browser = factory.CreateBrowser();

        var get = await GetPageAsync(browser, $"/p/{id}");
        Assert.True(get.Headers.CacheControl!.NoStore);
        var refused = await VoteAsync(browser, id, await get.Content.ReadAsStringAsync(), ("o.paris", "x"));
        Assert.True(refused.Headers.CacheControl!.NoStore);
        var saved = await VoteAsync(browser, id, await PageAsync(browser, $"/p/{id}"), ("o.paris", "1"));
        Assert.True(saved.Headers.CacheControl!.NoStore);
        Assert.True((await browser.GetAsync($"/p/{id}/results")).Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task Vote_rate_limit_refusals_are_no_abuse_signal()
    {
        await using var factory = new AppFactory(new Dictionary<string, string?>
        {
            ["RateLimiting:VotePermitPerMinute"] = "2",
            ["Abuse:Threshold"] = "1", // one signal would block
        }, services: AddFakePeer);
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft().Many());
        var browser = factory.CreateBrowser();
        browser.DefaultRequestHeaders.Add(PeerHeader, "203.0.113.30");
        var html = await PageAsync(browser, $"/p/{id}");

        ExpectSaved(await VoteAsync(browser, id, html, ("o.paris", "1")), id);
        ExpectSaved(await VoteAsync(browser, id, await PageAsync(browser, $"/p/{id}?new=1"), ("o.rome", "1")), id);
        var third = await VoteAsync(browser, id, await PageAsync(browser, $"/p/{id}?new=1"), ("o.berlin", "1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal(2, (await BallotsAsync(factory, id)).Count);

        // Not blocked: pages still load from this address.
        Assert.Equal(HttpStatusCode.OK, (await GetPageAsync(browser, $"/p/{id}")).StatusCode);
        Assert.Empty(factory.Services.GetRequiredService<AbuseTracker>().List());
    }
}
