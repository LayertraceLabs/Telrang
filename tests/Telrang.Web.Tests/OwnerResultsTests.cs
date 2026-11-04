using System.Net;
using Telrang.Web.Data;
using Telrang.Web.Features.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>Owners land on results and close or reopen there (DESIGN.md §24.3).</summary>
public class OwnerResultsTests
{
    private static async Task<string> Html(HttpClient client, string path)
    {
        var response = await GetPageAsync(client, path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static Task<DateTimeOffset?> ClosedAtAsync(AppFactory factory, string encodedId) =>
        WithScope(factory, async sp =>
        {
            Assert.True(Telrang.Core.Ids.PollId.TryDecode(encodedId, out var id));
            return (await sp.GetRequiredService<ApplicationDbContext>().Polls.AsNoTracking().SingleAsync(p => p.Id == id)).ClosedAt;
        });

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string id, string form, string? tokenSource = null) =>
        AppFactory.SubmitFormAsync(client, $"/p/{id}/results", form, new Dictionary<string, string>(), tokenSource);

    [Fact]
    public async Task Owner_closes_and_reopens_on_the_results_page_with_post_redirect_get()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "owner@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft());
        var browser = await LoggedInBrowserAsync(factory, "owner@example.com");

        var page = await Html(browser, $"/p/{id}/results");
        Assert.Contains("class=\"owner-bar", page);
        Assert.Contains($"href=\"polls/{id}/edit\"", page);
        Assert.Contains($"href=\"polls/{id}/share\"", page);
        Assert.Contains($"href=\"polls/new?from={id}\"", page);
        Assert.Contains($"href=\"p/{id}\" data-enhance-nav=\"false\">Open poll</a>", page);
        Assert.Contains($"value=\"{ResultsPage.CloseForm}\"", page);
        Assert.Contains("Close poll now", page);
        Assert.DoesNotContain("Reopen poll", page);
        Assert.DoesNotContain("Back to the poll", page);

        var closed = await PostAsync(browser, id, ResultsPage.CloseForm);
        Assert.Equal(HttpStatusCode.Redirect, closed.StatusCode);
        Assert.EndsWith($"/p/{id}/results", closed.Headers.Location!.OriginalString);
        Assert.NotNull(await ClosedAtAsync(factory, id));

        page = await Html(browser, $"/p/{id}/results");
        Assert.Contains("Reopen poll", page);
        Assert.DoesNotContain("Close poll now", page);
        Assert.Contains($"value=\"{ResultsPage.ReopenForm}\"", page);

        var reopened = await PostAsync(browser, id, ResultsPage.ReopenForm);
        Assert.Equal(HttpStatusCode.Redirect, reopened.StatusCode);
        Assert.Null(await ClosedAtAsync(factory, id));

        // A stale form (already open, "reopen" posted again) changes nothing and is no error page.
        var stale = await PostAsync(browser, id, ResultsPage.ReopenForm, tokenSource: $"/p/{id}/results");
        Assert.True(stale.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.OK, stale.StatusCode.ToString());
        Assert.Null(await ClosedAtAsync(factory, id));
    }

    [Fact]
    public async Task Others_get_no_owner_bar_and_their_posts_change_nothing()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "owner@example.com");
        await CreateUserAsync(factory, "other@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft(results: ResultsVisibility.Open));

        var other = await LoggedInBrowserAsync(factory, "other@example.com");
        var anonymous = factory.CreateBrowser();
        foreach (var client in new[] { other, anonymous })
        {
            var page = await Html(client, $"/p/{id}/results");
            Assert.DoesNotContain("owner-bar", page);
            Assert.DoesNotContain("Close poll now", page);
            Assert.Contains("Back to the poll", page);

            await GetPageAsync(client, $"/p/{id}"); // the vote page's cookie check
            // A valid antiforgery token for this visitor (from the vote page): still refused, as for a poll they cannot edit.
            var post = await PostAsync(client, id, ResultsPage.CloseForm, tokenSource: $"/p/{id}");
            Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
            Assert.Null(await ClosedAtAsync(factory, id));
        }
    }

    [Fact]
    public async Task A_post_without_a_valid_antiforgery_token_is_refused()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "owner@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft());
        var browser = await LoggedInBrowserAsync(factory, "owner@example.com");
        await Html(browser, $"/p/{id}/results"); // the antiforgery cookie is set

        foreach (var token in new[] { null, "forged" })
        {
            var fields = new Dictionary<string, string> { ["_handler"] = ResultsPage.CloseForm };
            if (token is not null) fields["__RequestVerificationToken"] = token;
            var post = await browser.PostAsync($"/p/{id}/results", new FormUrlEncodedContent(fields));
            Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
            Assert.Null(await ClosedAtAsync(factory, id));
        }

        // A token issued to another account (the form rendered for someone else) fails too.
        await CreateUserAsync(factory, "other@example.com");
        var other = await LoggedInBrowserAsync(factory, "other@example.com");
        var foreignToken = AppFactory.ScrapeAntiforgeryToken(await Html(other, $"/p/{id}"));
        var crossed = await browser.PostAsync($"/p/{id}/results", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = ResultsPage.CloseForm,
            ["__RequestVerificationToken"] = foreignToken,
        }));
        Assert.Equal(HttpStatusCode.BadRequest, crossed.StatusCode);
        Assert.Null(await ClosedAtAsync(factory, id));
    }

    [Fact]
    public async Task A_poll_closed_by_its_date_offers_neither_close_nor_reopen()
    {
        var clock = new MutableTime(DateTimeOffset.UtcNow);
        await using var factory = new AppFactory(services: s => s.AddSingleton<TimeProvider>(clock));
        var owner = await CreateUserAsync(factory, "owner@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft());
        clock.Now += TimeSpan.FromDays(200); // past the default maximum duration (180 days)
        var browser = await LoggedInBrowserAsync(factory, "owner@example.com");

        var page = await Html(browser, $"/p/{id}/results");
        Assert.Contains("class=\"owner-bar", page);
        Assert.Contains(">Closed</span>", page);
        Assert.DoesNotContain("Close poll now", page);
        Assert.DoesNotContain("Reopen poll", page);
        // The share page has the same rule.
        var share = await Html(browser, $"/polls/{id}/share");
        Assert.DoesNotContain("Close poll now", share);
        Assert.DoesNotContain("Reopen poll", share);
    }

    [Fact]
    public async Task My_polls_title_opens_the_results_and_vote_opens_the_ballot()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "owner@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft(title: "Lunch"));
        var browser = await LoggedInBrowserAsync(factory, "owner@example.com");

        var html = await Html(browser, "/polls");
        Assert.Contains($"href=\"p/{id}/results\" data-enhance-nav=\"false\">Lunch</a>", html);
        Assert.Contains($"href=\"p/{id}\" data-enhance-nav=\"false\">Vote</a>", html);
        Assert.DoesNotContain(">Results</a>", html);
    }
}
