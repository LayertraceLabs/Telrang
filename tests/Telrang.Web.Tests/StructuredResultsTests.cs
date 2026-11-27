using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Telrang.Core.Ballots;
using Telrang.Core.Polls;
using Telrang.Web.Data;
using Telrang.Web.Features.Polls;
using Telrang.Web.Features.Results;
using Telrang.Web.Features.Voting;
using Telrang.Web.Security;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>
/// DESIGN.md §34.3, §34.5 (step 3): results, the ballots view and the CSV for every question type and for polls with several
/// questions, pages and groups. Free text is the owner's only and is never read by the summary.
/// </summary>
public class StructuredResultsTests
{
    private const string Alias = "Zebra Aunt";
    private const string OtherText = "Halal";
    private const string Note = "Secret note B";
    private const string Formula = "=HYPERLINK(\"x\")";

    /// <summary>The survey of <see cref="MultiPageVoteTests"/> (q1 choice, q2 number, q3 checkboxes with Other, q4 text,
    /// q5 ranked), results for anyone with the link and ballots shown, so voters see everything a voter may.</summary>
    private static PollDraft OpenSurvey() => MultiPageVoteTests.Survey().With(d =>
    {
        d.ResultsVisibility = ResultsVisibility.Open;
        d.ShowBallots = true;
    });

    private static readonly VisitorContext Ann = new(null, "voter-key-ann-00000000000000000000", null);
    private static readonly VisitorContext Nobody = new(null, "voter-key-nobody-000000000000000000", null);
    private static readonly VisitorContext Bob = new(null, "voter-key-bob-00000000000000000000", null);

    internal static Dictionary<string, QuestionAnswer> Answers(params (string Key, QuestionAnswer Answer)[] answers) =>
        answers.ToDictionary(a => a.Key, a => a.Answer, StringComparer.Ordinal);

    internal static async Task SubmitAsync(AppFactory factory, string id, VisitorContext visitor, Dictionary<string, QuestionAnswer> answers, string? name)
    {
        var result = await WithScope(factory, sp => sp.GetRequiredService<BallotService>().SubmitResponseAsync(id, visitor, answers, name));
        Assert.True(result.Succeeded, string.Join(" ", result.Errors));
    }

    /// <summary>Three responses: Ann (everything, Other and a note that looks like a formula), one without a name, Bob.</summary>
    private static async Task<(string Id, string Owner)> SurveyAsync(AppFactory factory)
    {
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, OpenSurvey());
        await SubmitAsync(factory, id, Ann, Answers(
            ("q1", new QuestionAnswer { Choice = new ChoiceBallot(["yes"]) }),
            ("q2", new QuestionAnswer { Number = new NumberBallot(2) }),
            ("q3", new QuestionAnswer { Choice = new ChoiceBallot(["vegan"], Other: true), OtherText = OtherText }),
            ("q4", new QuestionAnswer { Text = Formula }),
            ("q5", new QuestionAnswer { Ranked = new RankedBallot(["soup", "salad"], []) })), "Ann");
        await SubmitAsync(factory, id, Nobody, Answers(
            ("q1", new QuestionAnswer { Choice = new ChoiceBallot(["no"]) }),
            ("q3", new QuestionAnswer { Choice = new ChoiceBallot(["fish"]) }),
            ("q5", new QuestionAnswer { Ranked = new RankedBallot(["salad"], []) })), null);
        await SubmitAsync(factory, id, Bob, Answers(
            ("q1", new QuestionAnswer { Choice = new ChoiceBallot(["yes"]) }),
            ("q2", new QuestionAnswer { Number = new NumberBallot(5) }),
            ("q4", new QuestionAnswer { Text = Note })), "Bob");
        var ann = (await BallotsAsync(factory, id)).Single(b => b.DisplayName == "Ann");
        Assert.Equal(AliasStatus.Ok, await WithScope(factory, async sp =>
            (await sp.GetRequiredService<PollService>().SetResponseAliasAsync(owner, id, ann.Id, Alias)).Status));
        return (id, owner);
    }

    private static Task<PollAccess> AccessAsync(AppFactory factory, string id, VisitorContext viewer) =>
        WithScope(factory, async sp => (await sp.GetRequiredService<PollAccessService>().GetAsync(id, viewer))!);

    internal static async Task<string> PageAsync(HttpClient client, string path)
    {
        var response = await GetPageAsync(client, path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    // ---- counting what reads free text -----------------------------------------------------------------------------

    /// <summary>Counts every Unprotect of the free-text protection, and every SQL command that names <c>TextCipher</c>.</summary>
    private sealed class TextReads : DbCommandInterceptor, IDataProtectionProvider
    {
        private IDataProtectionProvider? inner;
        private int unprotects;
        public readonly ConcurrentQueue<string> Commands = new();

        public int Unprotects => Volatile.Read(ref unprotects);

        public int CipherQueries => Commands.Count(c => c.Contains("TextCipher", StringComparison.Ordinal));

        public void Reset()
        {
            Interlocked.Exchange(ref unprotects, 0);
            Commands.Clear();
        }

        public ResponseTextProtection Wrap(IDataProtectionProvider provider)
        {
            inner = provider;
            return new ResponseTextProtection(this);
        }

        public IDataProtector CreateProtector(string purpose) => new Protector(this, inner!.CreateProtector(purpose));

        private sealed class Protector(TextReads owner, IDataProtector inner) : IDataProtector
        {
            public IDataProtector CreateProtector(string purpose) => new Protector(owner, inner.CreateProtector(purpose));
            public byte[] Protect(byte[] plaintext) => inner.Protect(plaintext);

            public byte[] Unprotect(byte[] protectedData)
            {
                Interlocked.Increment(ref owner.unprotects);
                return inner.Unprotect(protectedData);
            }
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Commands.Enqueue(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Enqueue(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private static AppFactory CountingFactory(TextReads reads) => new(services: s =>
    {
        s.AddSingleton(sp => reads.Wrap(sp.GetRequiredService<IDataProtectionProvider>()));
        s.ConfigureDbContext<ApplicationDbContext>(o => o.AddInterceptors(reads));
    });

    // ---- the summary ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_question_is_scored_in_one_pass_that_never_selects_or_decrypts_free_text()
    {
        var reads = new TextReads();
        await using var factory = CountingFactory(reads);
        var (id, owner) = await SurveyAsync(factory);
        var ownerAccess = await AccessAsync(factory, id, VisitorContext.ForUser(owner));
        reads.Reset();

        var r = await WithScope(factory, sp => sp.GetRequiredService<ResultsService>().ComputeAsync(ownerAccess));
        Assert.Contains(reads.Commands, c => c.Contains("PayloadJson", StringComparison.Ordinal)); // the interceptor sees the pass
        Assert.Equal(0, reads.CipherQueries);
        Assert.Equal(0, reads.Unprotects);

        Assert.Equal(3, r.VoterCount);
        Assert.Null(r.Ranked); // a structured poll: per question only
        Assert.Equal(["q1", "q2", "q3", "q4", "q5"], r.Questions.Select(q => q.Question.Key));
        var coming = r.Questions[0].Choice!;
        Assert.Equal(3, coming.RespondentCount);
        Assert.Equal([2, 1], coming.Rows.Select(x => x.Count));
        var guests = r.Questions[1].Number!;
        Assert.Equal((2, 2m, 5m, 3.5m), (guests.Count, guests.Min!.Value, guests.Max!.Value, guests.Mean!.Value));
        var diet = r.Questions[2].Choice!;
        Assert.Equal((2, 1), (diet.RespondentCount, diet.OtherCount));
        Assert.Equal(2, r.Questions[3].Text!.AnswerCount);
        Assert.Equal(2, r.Questions[4].Ranked!.VoterCount); // only the responses that answered it
        Assert.Equal("salad", r.Questions[4].Ranked!.Rows[0].Key);

        // A voter's results page and ballots view (results for anyone, ballots shown): no free text read either.
        ResultsCacheDrop(factory, id);
        reads.Reset();
        var anon = factory.CreateBrowser();
        await PageAsync(anon, $"/p/{id}/results");
        await PageAsync(anon, $"/p/{id}/results?view=ballots");
        Assert.Equal(0, reads.CipherQueries);
        Assert.Equal(0, reads.Unprotects);

        // The owner's own page reads them, a page at a time (the counter does count).
        var browser = await LoggedInBrowserAsync(factory, "o@example.com");
        reads.Reset();
        await PageAsync(browser, $"/p/{id}/results");
        Assert.Equal(1, reads.CipherQueries);
        Assert.Equal(2, reads.Unprotects); // Ann's and Bob's texts; the response without text has none
    }

    [Fact]
    public async Task A_skipped_ranked_question_is_not_answered_though_the_form_sends_an_empty_ballot()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, OpenSurvey());
        // What the vote form makes of an untouched ranked question: an empty ballot, not a missing answer.
        await SubmitAsync(factory, id, Ann, Answers(
            ("q1", new QuestionAnswer { Choice = new ChoiceBallot(["yes"]) }),
            ("q5", new QuestionAnswer { Ranked = new RankedBallot([], []) })), null);
        await SubmitAsync(factory, id, Bob, Answers(
            ("q1", new QuestionAnswer { Choice = new ChoiceBallot(["no"]) }),
            ("q5", new QuestionAnswer { Ranked = new RankedBallot(["soup"], []) })), null);
        Assert.DoesNotContain(await BallotsAsync(factory, id), b => b.PayloadJson.Contains("\"q5\":{\"ranked\":[]", StringComparison.Ordinal));

        var r = await WithScope(factory, async sp => await sp.GetRequiredService<ResultsService>().ComputeAsync(await AccessAsync(factory, id, VisitorContext.ForUser(owner))));
        Assert.Equal(2, r.VoterCount);
        Assert.Equal(1, r.Questions[4].Ranked!.VoterCount);

        // Parts stored empty by 0.4.0-rc builds read as not answered too, in the results and the change log counts;
        // a simple poll still counts an empty ballot as a voter.
        var def = (await AccessAsync(factory, id, VisitorContext.ForUser(owner))).Definition;
        var counts = PollJson.CountAnswers(["{\"q1\":{\"keys\":[\"yes\"]},\"q5\":{\"ranked\":[],\"banned\":[]}}"], def);
        Assert.Equal(0, counts.Questions["q5"]);
        Assert.Equal(1, counts.Questions["q1"]);
        var simpleDef = def with { Questions = [def.Questions[4] with { Page = null, Group = null }], Pages = [], Groups = [] };
        Assert.Equal(1, PollJson.CountAnswers(["{\"q5\":{\"ranked\":[],\"banned\":[]}}"], simpleDef).Questions["q5"]);
    }

    private static void ResultsCacheDrop(AppFactory factory, string id)
    {
        Assert.True(Telrang.Core.Ids.PollId.TryDecode(id, out var raw));
        factory.Services.GetRequiredService<PollChangeNotifier>().Changed(raw);
    }

    [Fact]
    public async Task Voters_see_counts_never_a_text_or_an_alias_whatever_the_settings()
    {
        await using var factory = new AppFactory();
        var (id, _) = await SurveyAsync(factory);
        var anon = factory.CreateBrowser();

        var summary = await PageAsync(anon, $"/p/{id}/results");
        Assert.Contains("<h2 class=\"h4 text-break mt-4 mb-3 results-page-title\">Attending</h2>", summary);
        Assert.Contains(">You</h3>", summary); // the group
        Assert.Contains("<bdi>Coming?</bdi>", summary);
        Assert.Contains("2 answers", summary); // q4, the texts
        Assert.Contains("Other", summary);
        Assert.Contains("choice-results", summary);
        Assert.Contains("histogram-wide", summary);
        foreach (var secret in new[] { OtherText, Note, "HYPERLINK", Alias, "free-text-answers" }) Assert.DoesNotContain(secret, summary);

        var ballots = await PageAsync(anon, $"/p/{id}/results?view=ballots");
        Assert.Contains("ballots-responses", ballots);
        Assert.Contains(">Ann<", ballots);
        Assert.Contains(">Anonymous<", ballots);
        Assert.Contains($"<bdi>{AnswerText.Answered}</bdi>", ballots);
        Assert.Contains("<bdi>Vegan; Other</bdi>", ballots);
        foreach (var secret in new[] { OtherText, Note, "HYPERLINK", Alias }) Assert.DoesNotContain(secret, ballots);
        Assert.DoesNotContain(" style=\"", ballots);

        // The service gives voters the same: names, "answered", "Other".
        var table = (await WithScope(factory, async sp => await sp.GetRequiredService<ResultsService>().BallotsAsync(await AccessAsync(factory, id, Ann), 1)))!;
        Assert.Equal(["Ann", "Anonymous", "Bob"], table.Responses!.Rows.Select(x => x.Name));
        Assert.Equal(["Yes", "2 people", "Vegan; Other", AnswerText.Answered, "Soup > Salad"], table.Responses.Rows[0].Cells);
        Assert.Empty(await WithScope(factory, async sp => await sp.GetRequiredService<ResultsService>().TextsAsync(await AccessAsync(factory, id, Ann), null, 1)));
    }

    [Fact]
    public async Task Owner_sees_the_texts_and_aliases_in_the_summary_and_the_ballots_view()
    {
        await using var factory = new AppFactory();
        var (id, _) = await SurveyAsync(factory);
        var browser = await LoggedInBrowserAsync(factory, "o@example.com");

        var summary = await PageAsync(browser, $"/p/{id}/results");
        Assert.Contains($"<bdi class=\"free-text\">{OtherText}</bdi>", summary);
        Assert.Contains($"<bdi class=\"free-text\">{Note}</bdi>", summary);
        Assert.Contains($"<bdi class=\"free-text\">{Formula}</bdi>", summary); // plain text, encoded (decoded here)
        Assert.Contains($"data-live-src=\"p/{id}/results/live\"", summary); // the owner's live updates stay on

        var ballots = await PageAsync(browser, $"/p/{id}/results?view=ballots");
        Assert.Contains($">{Alias}<", ballots);
        Assert.DoesNotContain(">Ann<", ballots);
        Assert.Contains($"<bdi>Vegan; Other: {OtherText}</bdi>", ballots);
        Assert.Contains($"<bdi>{Note}</bdi>", ballots);
        Assert.Contains("<bdi>5 people</bdi>", ballots);
        Assert.Contains($"data-live-src=\"p/{id}/results/live\"", ballots);
    }

    [Fact]
    public async Task Owner_summary_has_no_free_text_block_while_no_text_was_given()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, OpenSurvey());
        await SubmitAsync(factory, id, Nobody, Answers(
            ("q1", new QuestionAnswer { Choice = new ChoiceBallot(["no"]) }),
            ("q3", new QuestionAnswer { Choice = new ChoiceBallot(["fish"]) })), null);
        var browser = await LoggedInBrowserAsync(factory, "o@example.com");

        var summary = await PageAsync(browser, $"/p/{id}/results");
        Assert.Contains("choice-results", summary);
        Assert.DoesNotContain("free-text-answers", summary); // no empty "Other answers" or "Answers" heading
    }

    [Fact]
    public async Task Owner_texts_are_paged_and_decrypted_a_page_at_a_time()
    {
        var reads = new TextReads();
        await using var factory = CountingFactory(reads);
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, new PollDraft
        {
            Title = "Feedback",
            Questions = [new QuestionDraft { Type = PollType.Text }],
        });
        for (var i = 1; i <= RowWindow.PageSize + 1; i++)
            await SubmitAsync(factory, id, new VisitorContext(null, $"voter-key-{i:D4}-000000000000000000", null), Answers(("q1", new QuestionAnswer { Text = $"Answer number {i}." })), null);
        var access = await AccessAsync(factory, id, VisitorContext.ForUser(owner));
        await WithScope(factory, sp => sp.GetRequiredService<ResultsService>().ComputeAsync(access));

        reads.Reset();
        var first = Assert.Single(await WithScope(factory, sp => sp.GetRequiredService<ResultsService>().TextsAsync(access, null, 1)));
        Assert.Equal(RowWindow.PageSize, reads.Unprotects);
        Assert.Equal((1, 2, 51), (first.Window.Page, first.Window.Pages, first.Window.Total));
        Assert.Equal("Answer number 1.", first.Texts[0]);

        reads.Reset();
        var second = Assert.Single(await WithScope(factory, sp => sp.GetRequiredService<ResultsService>().TextsAsync(access, "q1", 2)));
        Assert.Equal(1, reads.Unprotects);
        Assert.Equal(["Answer number 51."], second.Texts);

        var browser = await LoggedInBrowserAsync(factory, "o@example.com");
        var page1 = await PageAsync(browser, $"/p/{id}/results");
        Assert.Contains("51 answers", page1);
        Assert.Contains("Answers 1–50 of 51", page1);
        Assert.Contains($"href=\"p/{id}/results?texts=q1&rows=2#texts-q1\"", page1);
        var page2 = await PageAsync(browser, $"/p/{id}/results?texts=q1&rows=2");
        Assert.Contains("Answer number 51.", page2);
        Assert.DoesNotContain("Answer number 1.<", page2);
        Assert.Contains($"href=\"p/{id}/results?texts=q1&rows=2\" data-enhance-nav=\"false\">Refresh", page2);

        // The ballots view of a one-question text poll is the response table too.
        var ballots = await PageAsync(browser, $"/p/{id}/results?view=ballots");
        Assert.Contains("ballots-responses", ballots);
        Assert.Contains("<bdi>Answer number 1.</bdi>", ballots);
    }

    // ---- simple polls of the new types ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_simple_choice_or_number_poll_shows_its_one_question_without_a_heading()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var choice = await CreatePollAsync(factory, owner, new PollDraft
        {
            Title = "Lunch",
            ResultsVisibility = ResultsVisibility.Open,
            Questions = [new QuestionDraft { Type = PollType.Choice, Options = [new() { Label = "Pizza" }, new() { Label = "Sushi" }] }],
        });
        var number = await CreatePollAsync(factory, owner, new PollDraft
        {
            Title = "Height",
            ResultsVisibility = ResultsVisibility.Open,
            Questions = [new QuestionDraft { Type = PollType.Number, Decimals = 2, Unit = "m" }],
        });
        await SubmitAsync(factory, choice, Ann, Answers(("q1", new QuestionAnswer { Choice = new ChoiceBallot(["sushi"]) })), null);
        await SubmitAsync(factory, number, Ann, Answers(("q1", new QuestionAnswer { Number = new NumberBallot(1.8m) })), null);
        await SubmitAsync(factory, number, Bob, Answers(("q1", new QuestionAnswer { Number = new NumberBallot(1.65m) })), null);
        var anon = factory.CreateBrowser();

        var c = await PageAsync(anon, $"/p/{choice}/results");
        Assert.Contains("choice-results", c);
        Assert.DoesNotContain("results-question", c);
        Assert.Contains("<title>1 voter (100%)</title>", c); // the value on hover (DESIGN.md §33)
        var n = await PageAsync(anon, $"/p/{number}/results");
        Assert.Contains("Mean · 1.725 m", n);
        Assert.Contains("Median · 1.725 m", n);
        Assert.Contains("histogram-wide", n);
    }

    // ---- CSV -----------------------------------------------------------------------------------------------------------

    private static async Task<string[]> CsvAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
        return Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public async Task Ballots_csv_has_one_column_per_question_with_free_text_guarded_against_formulas()
    {
        await using var factory = new AppFactory();
        var (id, _) = await SurveyAsync(factory);
        var browser = await LoggedInBrowserAsync(factory, "o@example.com");

        var lines = await CsvAsync(browser, $"/polls/{id}/export.csv");
        Assert.Equal("Name,Alias,SubmittedAt,UpdatedAt,q1 · Coming?,q2 · Guests,q3 · Diet,q4 · Notes,q5 · Dishes", lines[0]);
        Assert.Equal(4, lines.Length);
        Assert.StartsWith($"Ann,{Alias},", lines[1]);
        Assert.EndsWith($",Yes,2,Vegan; Other: {OtherText},\"'=HYPERLINK(\"\"x\"\")\",Soup > Salad", lines[1]);
        Assert.EndsWith(",No,,Fish,,Salad", lines[2]);
        Assert.EndsWith($",Yes,5,,{Note},", lines[3]);

        var results = await CsvAsync(browser, $"/polls/{id}/results.csv");
        Assert.Equal("Question,Option,Measure,Value", results[0]);
        Assert.Contains("q1 · Coming?,Yes,Count,2", results);
        Assert.Contains("q2 · Guests,,Mean,3.5", results);
        Assert.Contains("q3 · Diet,Other,Count,1", results);
        Assert.Contains("q4 · Notes,,Answers,2", results);
        Assert.Contains("q5 · Dishes,Salad,Place,1", results);
        Assert.DoesNotContain(results, l => l.Contains(OtherText) || l.Contains(Note));

        // Anyone but the owner: nothing.
        await CreateUserAsync(factory, "x@example.com");
        var other = await LoggedInBrowserAsync(factory, "x@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/polls/{id}/export.csv")).StatusCode);
    }
}
