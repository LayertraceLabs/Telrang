using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Telrang.Core.Ballots;
using Telrang.Core.Ids;
using Telrang.Core.Polls;
using Telrang.Web.Data;
using Telrang.Web.Features.Accounts;
using Telrang.Web.Features.Polls;
using Telrang.Web.Features.Voting;
using Telrang.Web.Security;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>
/// DESIGN.md §34.3, §34.4: a poll with several questions on several pages, voted on the static page without JavaScript.
/// The answers of the pages already filled travel in the protected carry field; the ballot is written once, on Submit;
/// free text is stored apart and encrypted; signed-in voters keep a draft.
/// </summary>
public partial class MultiPageVoteTests
{
    [GeneratedRegex("name=\"_carry\" value=\"([^\"]+)\"")]
    private static partial Regex CarryRegex();

    [GeneratedRegex("name=\"fid\" value=\"([^\"]+)\"")]
    private static partial Regex FormIdRegex();

    [GeneratedRegex("<input type=\"hidden\" name=\"r\" value=\"(\\d+)\"")]
    private static partial Regex ResponseRegex();

    /// <summary>
    /// Two pages: "Attending" (a group "You" with a required choice and a number) and "Food" (checkboxes with Other, a
    /// multi-line text, a ranked question). Keys q1…q5.
    /// </summary>
    public static PollDraft Survey(NamesMode names = NamesMode.Optional) => new()
    {
        Title = "Party",
        Names = names,
        ResultsVisibility = ResultsVisibility.Creator,
        Pages = [new PageDraft { Key = "a", Title = "Attending" }, new PageDraft { Key = "b", Title = "Food", Description = "Pick what you like." }],
        Groups = [new GroupDraft { Key = "g", Title = "You" }],
        Questions =
        [
            new QuestionDraft { Prompt = "Coming?", Type = PollType.Choice, Required = true, Page = "a", Group = "g", Options = Labels("Yes", "No") },
            new QuestionDraft { Prompt = "Guests", Type = PollType.Number, Page = "a", Group = "g", Min = 0, Max = 5, Unit = "people" },
            new QuestionDraft { Prompt = "Diet", Type = PollType.Choice, Page = "b", MaxSelect = 2, AllowOther = true, Options = Labels("Vegan", "Fish", "Meat") },
            new QuestionDraft { Prompt = "Notes", Type = PollType.Text, Page = "b", Multiline = true },
            new QuestionDraft { Prompt = "Dishes", Type = PollType.Ranked, Page = "b", Options = Labels("Soup", "Salad") },
        ],
    };

    private static List<OptionDraft> Labels(params string[] labels) => labels.Select(l => new OptionDraft { Label = l }).ToList();

    private static async Task<string> PageAsync(HttpClient client, string path)
    {
        var response = await GetPageAsync(client, path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    private static string? Carry(string html) => CarryRegex().Match(html) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>Posts the vote form of <paramref name="html"/> the way a browser would, with the button <paramref name="nav"/>.</summary>
    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string id, string html, string? nav,
        params (string Key, string Value)[] fields) => PostAsync(client, id, html, nav, Carry(html), fields);

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string id, string html, string? nav, string? carry,
        params (string Key, string Value)[] fields)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", AppFactory.ScrapeAntiforgeryToken(html)),
            new("_handler", VotePage.VoteForm),
        };
        if (AppFactory.ScrapeFormStamp(html) is { } stamp) form.Add(new(FormGuard.StampField, stamp));
        if (FormIdRegex().Match(html) is { Success: true } f) form.Add(new(BallotForm.FormIdField, f.Groups[1].Value));
        if (ResponseRegex().Match(html) is { Success: true } r) form.Add(new(BallotForm.ResponseField, r.Groups[1].Value));
        if (carry is not null) form.Add(new(VoteCarry.Field, carry));
        if (nav is not null) form.Add(new(VotePage.NavField, nav));
        form.AddRange(fields.Select(x => new KeyValuePair<string, string>(x.Key, x.Value)));
        return client.PostAsync($"/p/{id}", new FormUrlEncodedContent(form));
    }

    private static async Task<string> OkAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    private static void ExpectSaved(HttpResponseMessage response, string id)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.EndsWith($"/p/{id}?saved=1", response.Headers.Location!.ToString());
    }

    private static bool OnPage(string html, int n) => html.Contains($"aria-current=\"step\">{n} · ");

    private static readonly (string, string)[] Page1 = [("q1.c", "yes"), ("q2.number", "2")];

    private static readonly (string, string)[] Page2 =
        [("q3.c", "vegan"), ("q3.c", BallotForm.OtherValue), ("q3.other", "Halal"), ("q4.text", "Mail me at guest@example.com"), ("q5.o.soup", "1")];

    private static Task<List<ResponseDraft>> DraftsAsync(AppFactory factory) =>
        WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().ResponseDrafts.AsNoTracking().ToListAsync());

    // ---- the whole flow without JS ---------------------------------------------------------------

    [Fact]
    public async Task Next_back_and_submit_write_one_ballot_with_the_free_text_apart_and_encrypted()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, Survey());
        var browser = factory.CreateBrowser();

        var page1 = await PageAsync(browser, $"/p/{id}");
        Assert.True(OnPage(page1, 1));
        Assert.Contains("2 · Food", page1); // the page indicator lists the pages (not links)
        Assert.DoesNotContain("href=\"p/" + id + "?page", page1);
        Assert.Contains("<legend class=\"h5 text-break\">You</legend>", page1);
        Assert.Contains("name=\"q1.c\"", page1);
        Assert.DoesNotContain("name=\"q3.c\"", page1);
        Assert.DoesNotContain("name=\"name\"", page1); // name, notify and Submit belong to the last page
        Assert.NotNull(Carry(page1));

        var page2 = await OkAsync(await PostAsync(browser, id, page1, VotePage.NavNext, Page1));
        Assert.True(OnPage(page2, 2));
        Assert.Contains("Pick what you like.", page2);
        Assert.Contains("name=\"q3.c\"", page2);
        Assert.Contains("name=\"name\"", page2);
        Assert.Contains(">Back</button>", page2);
        Assert.Empty(await BallotsAsync(factory, id)); // nothing is stored before Submit

        // Back keeps what is valid on page 2 (here the diet) and shows page 1 with its answers.
        var back = await OkAsync(await PostAsync(browser, id, page2, VotePage.NavBack, ("q3.c", "fish")));
        Assert.True(OnPage(back, 1));
        Assert.Matches("value=\"yes\"\\s+checked", back);
        Assert.Contains("value=\"2\"", back);
        var again = await OkAsync(await PostAsync(browser, id, back, VotePage.NavNext, Page1));
        Assert.Matches("value=\"fish\"\\s+checked", again);

        ExpectSaved(await PostAsync(browser, id, again, VotePage.NavSubmit, [.. Page2, ("name", "Ann")]), id);
        var row = Assert.Single(await BallotsAsync(factory, id));
        Assert.Equal("Ann", row.DisplayName);
        Assert.DoesNotContain("Halal", row.PayloadJson);
        Assert.DoesNotContain("example", row.PayloadJson);
        Assert.Contains("\"q1\":{\"keys\":[\"yes\"]}", row.PayloadJson);
        Assert.Contains("\"q2\":{\"value\":\"2\"}", row.PayloadJson);
        Assert.Contains("\"q3\":{\"keys\":[\"vegan\"],\"other\":true}", row.PayloadJson);
        Assert.Contains("\"q4\":{}", row.PayloadJson); // a text answer is only "answered" in the scored payload
        Assert.Contains("\"q5\":{\"ranked\":[\"soup\"]", row.PayloadJson);
        Assert.StartsWith(ResponseTextProtection.Prefix, row.TextCipher);
        Assert.DoesNotContain("@", row.TextCipher);
        var texts = factory.Services.GetRequiredService<ResponseTextProtection>().RevealTexts(row.PollId, row.TextCipher)!;
        Assert.Equal("Halal", texts["q3.other"]);
        Assert.Equal("Mail me at guest@example.com", texts["q4"]);
        // Bound to the poll: the cipher does not open as another poll's.
        Assert.Null(factory.Services.GetRequiredService<ResponseTextProtection>().RevealTexts(Guid.NewGuid(), row.TextCipher));

        // The voter sees their own answers, their text included.
        var summary = await PageAsync(browser, $"/p/{id}?saved=1");
        Assert.Contains("Your answers", summary);
        Assert.Contains("Halal", summary);
        Assert.Contains("Mail me at guest@example.com", summary);
        Assert.Contains("2 people", summary);
    }

    [Fact]
    public async Task The_fill_time_counts_from_the_first_page_so_a_quick_last_page_is_not_too_fast()
    {
        var clock = new MutableTime(DateTimeOffset.UtcNow);
        await using var factory = new AppFactory(new Dictionary<string, string?> { ["Abuse:FormMinSeconds"] = "2" },
            services: s => s.AddSingleton<TimeProvider>(clock));
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, Survey());
        var browser = factory.CreateBrowser();

        var page1 = await PageAsync(browser, $"/p/{id}");
        clock.Now += TimeSpan.FromSeconds(30);
        var page2 = await OkAsync(await PostAsync(browser, id, page1, VotePage.NavNext, Page1));
        Assert.Equal(AppFactory.ScrapeFormStamp(page1), AppFactory.ScrapeFormStamp(page2)); // the first page's stamp

        // Submit at once on the last page: 30 s since the form opened, not 0 since page 2 appeared.
        ExpectSaved(await PostAsync(browser, id, page2, VotePage.NavSubmit, [.. Page2, ("name", "Ann")]), id);
        Assert.Single(await BallotsAsync(factory, id));

        // A forged stamp is not kept: the next page gets a new one.
        var other = factory.CreateBrowser();
        var start = await PageAsync(other, $"/p/{id}");
        var form = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", AppFactory.ScrapeAntiforgeryToken(start)),
            new("_handler", VotePage.VoteForm),
            new(FormGuard.StampField, "forged"),
            new(VoteCarry.Field, Carry(start)!),
            new(VotePage.NavField, VotePage.NavNext),
        };
        if (FormIdRegex().Match(start) is { Success: true } f) form.Add(new(BallotForm.FormIdField, f.Groups[1].Value));
        form.AddRange(Page1.Select(x => new KeyValuePair<string, string>(x.Item1, x.Item2)));
        var next = await OkAsync(await other.PostAsync($"/p/{id}", new FormUrlEncodedContent(form)));
        Assert.NotEqual("forged", AppFactory.ScrapeFormStamp(next));
        Assert.False(string.IsNullOrEmpty(AppFactory.ScrapeFormStamp(next)));
    }

    [Fact]
    public async Task Deleting_a_response_deletes_its_draft_so_the_next_response_of_that_number_starts_empty()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var voterId = await CreateUserAsync(factory, "v@example.com");
        var id = await CreatePollAsync(factory, owner, Survey().Many());
        var me = VisitorContext.ForUser(voterId);
        var coming = StructuredResultsTests.Answers(("q1", new QuestionAnswer { Choice = new ChoiceBallot(["yes"]) }));
        await StructuredResultsTests.SubmitAsync(factory, id, me, coming, null);
        await StructuredResultsTests.SubmitAsync(factory, id, me, coming, null);
        Task<T> Ballots<T>(Func<BallotService, Task<T>> f) => WithScope(factory, sp => f(sp.GetRequiredService<BallotService>()));
        Assert.True(await Ballots(b => b.SaveDraftAsync(id, me, 2, new CarryState(2, coming, "Old draft"))));

        Assert.True((await Ballots(b => b.DeleteResponseAsync(id, me, 2))).Succeeded);
        Assert.Empty(await DraftsAsync(factory));

        // A new response gets number 2 again and has no draft.
        await StructuredResultsTests.SubmitAsync(factory, id, me, coming, null);
        var access = await WithScope(factory, async sp => (await sp.GetRequiredService<PollAccessService>().GetAsync(id, me))!);
        Assert.Null(await Ballots(b => b.GetDraftAsync(access, 2)));
    }

    [Fact]
    public async Task Next_checks_the_page_and_keeps_what_was_typed()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, Survey());
        var browser = factory.CreateBrowser();
        var page1 = await PageAsync(browser, $"/p/{id}");

        var missing = await OkAsync(await PostAsync(browser, id, page1, VotePage.NavNext, ("q2.number", "3")));
        Assert.True(OnPage(missing, 1));
        Assert.Contains("Coming?: Pick an option.", missing);
        Assert.Contains("value=\"3\"", missing);

        var bad = await OkAsync(await PostAsync(browser, id, page1, VotePage.NavNext, ("q1.c", "yes"), ("q2.number", "2.5")));
        Assert.True(OnPage(bad, 1));
        Assert.Contains("Guests: " + Telrang.Core.Polls.PollRules.TooManyDecimals(0), bad);
        var tooMany = await OkAsync(await PostAsync(browser, id, page1, VotePage.NavNext, ("q1.c", "yes"), ("q2.number", "9")));
        Assert.Contains("Guests: Enter 5 people or less.", tooMany);
        var notANumber = await OkAsync(await PostAsync(browser, id, page1, VotePage.NavNext, ("q1.c", "yes"), ("q2.number", "1e3")));
        Assert.Contains("Guests: " + BallotForm.NumberFormatMessage, notANumber);

        // Back merges what is valid without the required check: from page 2 with the Other text but no Other picked.
        var page2 = await OkAsync(await PostAsync(browser, id, page1, VotePage.NavNext, Page1));
        var other = await OkAsync(await PostAsync(browser, id, page2, VotePage.NavSubmit, ("q3.other", "Halal")));
        Assert.True(OnPage(other, 2));
        Assert.Contains("Diet: Remove the Other text, or pick Other.", other);
        Assert.Empty(await BallotsAsync(factory, id));
    }

    [Fact]
    public async Task A_carry_field_that_does_not_open_starts_the_form_over_and_writes_nothing()
    {
        var clock = new MutableTime(new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero));
        await using var factory = new AppFactory(services: s => s.AddSingleton<TimeProvider>(clock));
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, Survey().Closes("2026-12-31"));
        var browser = factory.CreateBrowser();
        var page1 = await PageAsync(browser, $"/p/{id}");
        var page2 = await OkAsync(await PostAsync(browser, id, page1, VotePage.NavNext, Page1));
        var carry = Carry(page2)!;

        // Forged: one character changed.
        var forged = carry[..10] + (carry[10] == 'A' ? 'B' : 'A') + carry[11..];
        var restarted = await OkAsync(await PostAsync(browser, id, page2, VotePage.NavSubmit, forged, Page2));
        Assert.Contains(VoteCarry.RestartMessage, restarted);
        Assert.True(OnPage(restarted, 1));
        Assert.DoesNotMatch("value=\"yes\"\\s+checked", restarted);

        // Missing.
        Assert.Contains(VoteCarry.RestartMessage, await OkAsync(await PostAsync(browser, id, page2, VotePage.NavSubmit, (string?)null, Page2)));

        // Another voter's (another browser, another vk): it does not open for them.
        var other = factory.CreateBrowser();
        var otherPage = await PageAsync(other, $"/p/{id}");
        Assert.Contains(VoteCarry.RestartMessage, await OkAsync(await PostAsync(other, id, otherPage, VotePage.NavSubmit, carry, Page2)));

        // Another poll's: it does not open there either.
        var id2 = await CreatePollAsync(factory, owner, Survey().Closes("2026-12-31"));
        var poll2 = await PageAsync(browser, $"/p/{id2}");
        Assert.Contains(VoteCarry.RestartMessage, await OkAsync(await PostAsync(browser, id2, poll2, VotePage.NavSubmit, carry, Page2)));

        // Expired: valid for 24 hours.
        clock.Now += VoteCarry.Lifetime + TimeSpan.FromMinutes(1);
        Assert.Contains(VoteCarry.RestartMessage, await OkAsync(await PostAsync(browser, id, page2, VotePage.NavSubmit, carry, Page2)));
        Assert.Empty(await BallotsAsync(factory, id));
        Assert.Empty(await BallotsAsync(factory, id2));

        // Within the lifetime the same field still works (a replay restores the same voter's own answers, nothing more); the
        // form stamp's 12 hours still apply to Submit.
        clock.Now -= TimeSpan.FromHours(14);
        ExpectSaved(await PostAsync(browser, id, page2, VotePage.NavSubmit, carry, Page2), id);
        Assert.Single(await BallotsAsync(factory, id));
    }

    [Fact]
    public async Task Submit_checks_every_page_again()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, Survey());
        var browser = factory.CreateBrowser();
        var page1 = await PageAsync(browser, $"/p/{id}");
        var page2 = await OkAsync(await PostAsync(browser, id, page1, VotePage.NavNext, Page1));

        // Meanwhile the owner adds a required question to page 1: Submit shows page 1 with its error, and writes nothing.
        var stored = await WithScope(factory, sp => sp.GetRequiredService<PollService>().GetForEditAsync(owner, id));
        var draft = PollDraft.FromDefinition(stored!.Definition);
        draft.Questions.Insert(2, new QuestionDraft { Prompt = "Arrival", Type = PollType.Text, Required = true, Page = draft.Questions[0].Page });
        var saved = await UpdatePollAsync(factory, owner, id, draft);
        Assert.True(saved.Succeeded, string.Join(", ", saved.Errors));

        var shown = await OkAsync(await PostAsync(browser, id, page2, VotePage.NavSubmit, Page2));
        Assert.True(OnPage(shown, 1));
        Assert.Contains("Arrival: Enter an answer.", shown);
        Assert.Matches("value=\"yes\"\\s+checked", shown);
        Assert.Empty(await BallotsAsync(factory, id));

        // The new question's key was issued by the edit (q6); answering it gets through.
        var next = await OkAsync(await PostAsync(browser, id, shown, VotePage.NavNext, [.. Page1, ("q6.text", "Late")]));
        Assert.True(OnPage(next, 2));
        Assert.Matches("value=\"vegan\"\\s+checked", next); // page 2's answers were carried along
        ExpectSaved(await PostAsync(browser, id, next, VotePage.NavSubmit, Page2), id);
    }

    // ---- editing a response, simple polls ----------------------------------------------------------

    [Fact]
    public async Task Changing_a_response_loads_it_into_the_carry_field()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, Survey());
        var browser = factory.CreateBrowser();
        var page1 = await PageAsync(browser, $"/p/{id}");
        var page2 = await OkAsync(await PostAsync(browser, id, page1, VotePage.NavNext, Page1));
        ExpectSaved(await PostAsync(browser, id, page2, VotePage.NavSubmit, Page2), id);

        var edit1 = await PageAsync(browser, $"/p/{id}?edit=1");
        Assert.True(OnPage(edit1, 1));
        Assert.Matches("value=\"yes\"\\s+checked", edit1);
        // Next with page 1 as it is: page 2 comes back filled from the stored response, the text included.
        var edit2 = await OkAsync(await PostAsync(browser, id, edit1, VotePage.NavNext, Page1));
        Assert.Contains("Mail me at guest@example.com", edit2);
        Assert.Contains("value=\"Halal\"", edit2);
        Assert.Contains("Update ballot", edit2);
        var row = Assert.Single(await BallotsAsync(factory, id));

        ExpectSaved(await PostAsync(browser, id, edit2, VotePage.NavSubmit, ("q3.c", "meat"), ("q4.text", "Changed")), id);
        var after = Assert.Single(await BallotsAsync(factory, id));
        Assert.Equal(row.Id, after.Id);
        Assert.Contains("\"q3\":{\"keys\":[\"meat\"]}", after.PayloadJson);
        var texts = factory.Services.GetRequiredService<ResponseTextProtection>().RevealTexts(after.PollId, after.TextCipher)!;
        Assert.Equal(["q4"], texts.Keys);
        Assert.Equal("Changed", texts["q4"]);
    }

    [Theory]
    [InlineData(PollType.Choice)]
    [InlineData(PollType.Text)]
    [InlineData(PollType.Number)]
    public async Task A_simple_poll_of_a_new_type_has_one_submit_and_no_carry_field(PollType type)
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var q = new QuestionDraft { Type = type, Required = true, Options = type == PollType.Choice ? Labels("Red", "Blue") : [] };
        var id = await CreatePollAsync(factory, owner, new PollDraft { Title = "Colour", Questions = [q] });
        var browser = factory.CreateBrowser();
        var html = await PageAsync(browser, $"/p/{id}");
        Assert.Null(Carry(html));
        Assert.DoesNotContain("aria-current=\"step\"", html);
        Assert.DoesNotContain(VotePage.NavField, html);
        var field = type switch { PollType.Choice => ("c", "blue"), PollType.Text => ("text", "Blue, of course"), _ => ("number", "7") };

        var refused = await OkAsync(await PostAsync(browser, id, html, null));
        Assert.Contains(type switch { PollType.Choice => "Pick an option.", PollType.Text => "Enter an answer.", _ => "Enter a number." }, refused);
        ExpectSaved(await PostAsync(browser, id, html, null, field), id);
        var row = Assert.Single(await BallotsAsync(factory, id));
        Assert.Equal(type == PollType.Text, row.TextCipher is not null);
        var summary = await PageAsync(browser, $"/p/{id}?saved=1");
        Assert.Contains(type switch { PollType.Choice => "Blue", PollType.Text => "Blue, of course", _ => "7" }, summary);
        Assert.Contains(type switch { PollType.Choice => "Your choice", _ => "Your answer" }, summary);
        Assert.Equal(1, (await PollRowAsync(factory, id)).QuestionCount);
    }

    [Fact]
    public void The_carry_names_its_page_by_key_so_an_edit_that_renumbers_pages_keeps_the_voter_on_it()
    {
        var def = Definition(Survey());
        var now = DateTimeOffset.UtcNow;
        var json = VoteCarry.Serialize(def, new CarryState(2, new Dictionary<string, QuestionAnswer>()), now);

        Assert.Equal(2, VoteCarry.Deserialize(def, json, now, null)!.Page);
        var reordered = def with { Pages = [def.Pages[1], def.Pages[0]] };
        Assert.Equal(1, VoteCarry.Deserialize(reordered, json, now, null)!.Page);
        var removed = def with { Pages = [def.Pages[0]] };
        Assert.Equal(1, VoteCarry.Deserialize(removed, json, now, null)!.Page); // its page is gone: the first
    }

    [Fact]
    public async Task A_line_break_counts_as_one_character_as_the_browsers_maxlength_counts_it()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var q = new QuestionDraft { Type = PollType.Text, Multiline = true, MaxLength = 5 };
        var id = await CreatePollAsync(factory, owner, new PollDraft { Title = "Lines", Questions = [q] });
        var browser = factory.CreateBrowser();
        var html = await PageAsync(browser, $"/p/{id}");

        // Five characters in the textarea; the browser posts the break as CRLF (six).
        ExpectSaved(await PostAsync(browser, id, html, null, ("text", "ab\r\ncd")), id);
        var row = Assert.Single(await BallotsAsync(factory, id));
        var texts = factory.Services.GetRequiredService<ResponseTextProtection>().RevealTexts(row.PollId, row.TextCipher)!;
        Assert.Equal("ab\ncd", Assert.Single(texts.Values));
    }

    private static Task<Poll> PollRowAsync(AppFactory factory, string id)
    {
        Assert.True(PollId.TryDecode(id, out var pollId));
        return WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().Polls.AsNoTracking().SingleAsync(p => p.Id == pollId));
    }

    [Fact]
    public async Task Question_and_page_counts_are_written_on_save()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var survey = await PollRowAsync(factory, await CreatePollAsync(factory, owner, Survey()));
        Assert.Equal((5, 2, PollType.Choice), (survey.QuestionCount, survey.PageCount, survey.Type));
        var simple = await PollRowAsync(factory, await CreatePollAsync(factory, owner, RankedDraft()));
        Assert.Equal((1, 1), (simple.QuestionCount, simple.PageCount));
    }

    // ---- drafts (signed-in voters) -------------------------------------------------------------------

    [Fact]
    public async Task A_signed_in_voter_resumes_a_draft_and_nobody_else_sees_it()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var voter = await CreateUserAsync(factory, "v@example.com");
        var id = await CreatePollAsync(factory, owner, Survey().Voters(VoterMode.Accounts));
        var browser = await LoggedInBrowserAsync(factory, "v@example.com");

        var page1 = await PageAsync(browser, $"/p/{id}");
        Assert.DoesNotContain("Draft · saved", page1);
        var page2 = await OkAsync(await PostAsync(browser, id, page1, VotePage.NavNext, Page1));
        Assert.True(OnPage(page2, 2));
        var draft = Assert.Single(await DraftsAsync(factory));
        Assert.Equal((voter, 0), (draft.UserId, draft.ResponseNo));
        Assert.StartsWith(ResponseTextProtection.Prefix, draft.Cipher);
        Assert.Empty(await BallotsAsync(factory, id)); // a draft is no ballot

        // Next visit: "Draft · saved <time>" with Continue (the page they left) and Discard.
        var resume = await PageAsync(browser, $"/p/{id}");
        Assert.Contains("Draft · saved", resume);
        Assert.Contains($"href=\"p/{id}?page=2\"", resume);
        Assert.DoesNotContain("name=\"q1.c\"", resume);
        var cont = await PageAsync(browser, $"/p/{id}?page=2");
        Assert.True(OnPage(cont, 2));
        var back = await OkAsync(await PostAsync(browser, id, cont, VotePage.NavBack));
        Assert.Matches("value=\"yes\"\\s+checked", back); // page 1's answers came from the draft

        // The owner sees no trace of it: no ballot count, no response, nothing on the share page.
        var ownerView = await WithScope(factory, sp => sp.GetRequiredService<PollService>().GetOverviewAsync(owner, id));
        Assert.Equal(0, ownerView!.BallotCount);
        var ownerBrowser = await LoggedInBrowserAsync(factory, "o@example.com");
        Assert.DoesNotContain("Draft", await PageAsync(ownerBrowser, $"/polls/{id}/share"));

        // Submit writes the ballot and deletes the draft.
        var page2Again = await OkAsync(await PostAsync(browser, id, back, VotePage.NavNext, Page1));
        ExpectSaved(await PostAsync(browser, id, page2Again, VotePage.NavSubmit, Page2), id);
        Assert.Empty(await DraftsAsync(factory));
        Assert.Single(await BallotsAsync(factory, id));
    }

    [Fact]
    public async Task Discard_close_delete_and_account_deletion_remove_drafts()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var voterId = await CreateUserAsync(factory, "v@example.com");
        var id = await CreatePollAsync(factory, owner, Survey().Voters(VoterMode.Accounts));
        var browser = await LoggedInBrowserAsync(factory, "v@example.com");

        async Task NextAsync(string poll) => await PostAsync(browser, poll, await PageAsync(browser, $"/p/{poll}"), VotePage.NavNext, Page1);

        // Discard.
        await NextAsync(id);
        var notice = await PageAsync(browser, $"/p/{id}");
        var discarded = await PostAsync(browser, id, notice, VotePage.NavDiscard);
        Assert.Equal(HttpStatusCode.Redirect, discarded.StatusCode);
        Assert.Empty(await DraftsAsync(factory));
        Assert.DoesNotContain("Draft · saved", await PageAsync(browser, $"/p/{id}"));

        // The owner closes the poll; a poll closed by its date goes in the sweep.
        await NextAsync(id);
        Assert.True(await WithScope(factory, sp => sp.GetRequiredService<PollService>().SetClosedAsync(owner, id, true)));
        Assert.Empty(await DraftsAsync(factory));
        Assert.True(await WithScope(factory, sp => sp.GetRequiredService<PollService>().SetClosedAsync(owner, id, false)));
        await NextAsync(id);
        Assert.Single(await DraftsAsync(factory));
        await WithScope(factory, async sp =>
        {
            var db = sp.GetRequiredService<ApplicationDbContext>();
            Assert.True(PollId.TryDecode(id, out var pid));
            await db.Polls.Where(p => p.Id == pid).ExecuteUpdateAsync(u => u.SetProperty(p => p.ClosesAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
            return 0;
        });
        Assert.Equal(1, await factory.Services.GetRequiredService<PollSweepService>().DeleteClosedDraftsAsync());
        Assert.Empty(await DraftsAsync(factory));

        // Poll deletion.
        var id2 = await CreatePollAsync(factory, owner, Survey().Voters(VoterMode.Accounts));
        await NextAsync(id2);
        Assert.Single(await DraftsAsync(factory));
        Assert.True(await WithScope(factory, sp => sp.GetRequiredService<PollService>().DeletePollAsync(owner, id2)));
        Assert.Empty(await DraftsAsync(factory));

        // Account deletion.
        var id3 = await CreatePollAsync(factory, owner, Survey().Voters(VoterMode.Accounts));
        await NextAsync(id3);
        Assert.Single(await DraftsAsync(factory));
        await WithScope(factory, async sp =>
        {
            var user = (await sp.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(voterId))!;
            return await sp.GetRequiredService<AccountService>().SoftDeleteAsync(user);
        });
        Assert.Empty(await DraftsAsync(factory));
    }

    [Fact]
    public async Task Cookie_voters_get_no_draft()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, Survey());
        var browser = factory.CreateBrowser();
        var page2 = await OkAsync(await PostAsync(browser, id, await PageAsync(browser, $"/p/{id}"), VotePage.NavNext, Page1));
        Assert.True(OnPage(page2, 2));
        Assert.Empty(await DraftsAsync(factory));
    }

    // ---- limits -------------------------------------------------------------------------------------

    [Fact]
    public void The_form_limits_follow_the_poll_limits()
    {
        Assert.Equal(VoteFormLimits.QuestionsMax, PollLimits.Default.QuestionsMax);
        Assert.Equal(VoteFormLimits.FreeTextBytes, PollLimits.Default.ResponseTextBytesMax);
        Assert.True(VoteFormLimits.MaxBodyBytes < RequestLimits.MaxBodyBytes);
        Assert.True(VoteFormLimits.MaxValueLength >= VoteFormLimits.CarryFieldMax);
    }

    [Fact]
    public async Task A_carry_field_with_the_most_free_text_fits_the_form()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        // Four long text questions on page 1, so 8 KiB of the worst escaping (control characters) travel in the carry.
        var draft = new PollDraft
        {
            Title = "Long",
            Pages = [new PageDraft { Key = "a", Title = "One" }, new PageDraft { Key = "b", Title = "Two" }],
            Questions =
            [
                .. Enumerable.Range(1, 4).Select(i => new QuestionDraft { Prompt = $"T{i}", Type = PollType.Text, Multiline = true, Page = "a" }),
                new QuestionDraft { Prompt = "Last", Type = PollType.Number, Page = "b" },
            ],
        };
        var id = await CreatePollAsync(factory, owner, draft);
        var browser = factory.CreateBrowser();
        var page1 = await PageAsync(browser, $"/p/{id}");
        var text = string.Concat(Enumerable.Repeat("\u0001\"\\x", 500)); // 2 000 characters, 2 000 UTF-8 bytes, ~ 7 000 in JSON
        var page2 = await OkAsync(await PostAsync(browser, id, page1, VotePage.NavNext,
            [.. Enumerable.Range(1, 4).Select(i => ($"q{i}.text", text))]));
        Assert.True(OnPage(page2, 2), page2[..Math.Min(page2.Length, 2000)]);
        Assert.True(Carry(page2)!.Length <= VoteFormLimits.CarryFieldMax);
        ExpectSaved(await PostAsync(browser, id, page2, VotePage.NavSubmit, ("q5.number", "1")), id);
        var row = Assert.Single(await BallotsAsync(factory, id));
        var texts = factory.Services.GetRequiredService<ResponseTextProtection>().RevealTexts(row.PollId, row.TextCipher)!;
        Assert.Equal(4, texts.Count);
        Assert.Equal(text, texts["q1"]);
    }

    // ---- stored forms ---------------------------------------------------------------------------------

    [Fact]
    public void Packing_compresses_only_when_smaller()
    {
        var repetitive = System.Text.Encoding.UTF8.GetBytes(new string('a', 2000));
        var packed = ResponseTextProtection.Pack(repetitive);
        Assert.Equal(ResponseTextProtection.Brotli, packed[0]);
        Assert.True(packed.Length < 100);
        Assert.Equal(repetitive, ResponseTextProtection.Unpack(packed, 4096));
        var tiny = new byte[] { 42 };
        Assert.Equal([ResponseTextProtection.Raw, 42], ResponseTextProtection.Pack(tiny));
        Assert.Equal(tiny, ResponseTextProtection.Unpack(ResponseTextProtection.Pack(tiny), 10));
        Assert.Throws<InvalidDataException>(() => ResponseTextProtection.Unpack(packed, 100)); // larger than allowed
        Assert.Throws<InvalidDataException>(() => ResponseTextProtection.Unpack([7, 1, 2], 100));
    }

    [Fact]
    public void Definitions_with_pages_and_the_new_types_round_trip_and_old_ones_read_as_before()
    {
        var def = Definition(Survey()) with { TimeZone = "UTC" };
        var back = PollJson.DeserializeDefinition(PollJson.Serialize(def));
        Assert.Equal(PollJson.Serialize(def), PollJson.Serialize(back));
        Assert.Equal(["p1", "p2"], back.Pages.Select(p => p.Key));
        Assert.Equal((5m, "people"), (back.Questions[1].Max!.Value, back.Questions[1].Unit));
        Assert.True(back.Questions[2].AllowOther);

        const string old = """{"title":"T","descriptionMarkdown":"","questions":[{"key":"q1","type":"Ranked","options":[{"key":"a","label":"A","descriptionMarkdown":""},{"key":"b","label":"B","descriptionMarkdown":""}]}]}""";
        var read = PollJson.DeserializeDefinition(old);
        Assert.Empty(read.Pages);
        Assert.Empty(read.Groups);
        var q = read.Questions[0];
        Assert.Equal(("", false, null, null), (q.Prompt, q.Required, q.Page, q.Group));
        Assert.True(VoteLayout.IsSimple(read));

        // Old ballots read as before; the new parts read back exactly.
        Assert.Equal(["a"], PollJson.DeserializeRanked("""{"q1":{"ranked":["a"],"banned":[]}}""", "q1").Ranked);
        Assert.Equal("12.50", PollJson.DeserializeNumber("""{"q2":{"value":"12.50"}}""", "q2")!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var choice = PollJson.DeserializeChoice(PollJson.Serialize("q3", new ChoiceBallot(["x"], true)), "q3");
        Assert.Equal(["x"], choice.Keys);
        Assert.True(choice.Other);
        Assert.Null(PollJson.DeserializeNumber("""{"q2":{"value":12.5}}""", "q2")); // a JSON number is no stored value
    }
}
