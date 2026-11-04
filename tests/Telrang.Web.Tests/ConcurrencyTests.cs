using System.Text.RegularExpressions;
using Telrang.Core.Ballots;
using Telrang.Web.Data;
using Telrang.Web.Features.Mail;
using Telrang.Web.Features.Polls;
using Telrang.Web.Features.Settings;
using Telrang.Web.Features.Voting;
using Telrang.Web.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>
/// DESIGN.md §28, locking: the writes SQLite serialises by its single writer stay correct under PostgreSQL's concurrent
/// writers (DbWrites). Each test hammers one write path in parallel; they run on whichever provider the suite runs on
/// (SQLite by default, PostgreSQL with TELRANG_TEST_POSTGRES), so both are covered.
/// </summary>
public class ConcurrencyTests
{
    private const int Parallel = 24;

    private static Task<T[]> Hammer<T>(int count, Func<int, Task<T>> action) =>
        Task.WhenAll(Enumerable.Range(0, count).Select(i => Task.Run(() => action(i))));

    private static Task<SubmitResult> Vote(AppFactory factory, string id, VisitorContext visitor, RankedBallot ballot, int? responseNo = null) =>
        WithScope(factory, sp => sp.GetRequiredService<BallotService>().SubmitRankedAsync(id, visitor, ballot, null, responseNo));

    [Fact]
    public async Task Parallel_votes_stop_exactly_at_the_ballots_per_poll_limit()
    {
        await using var factory = new AppFactory();
        await factory.Settings.SetAsync(new Dictionary<string, string?> { [SettingKeys.LimitBallotsPerPoll] = "5" }, null);
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft());

        var results = await Hammer(Parallel, i => Vote(factory, id, new VisitorContext(null, "browser-" + i, null), new RankedBallot(["paris"], [])));

        Assert.Equal(5, results.Count(r => r.Succeeded));
        Assert.All(results.Where(r => !r.Succeeded), r => Assert.Equal(SubmitStatus.Denied, r.Status));
        Assert.Equal(5, (await BallotsAsync(factory, id)).Count);
    }

    [Fact]
    public async Task Parallel_new_responses_of_one_voter_get_distinct_numbers_and_respect_the_limit()
    {
        await using var factory = new AppFactory();
        await factory.Settings.SetAsync(new Dictionary<string, string?> { [SettingKeys.LimitResponsesPerVoter] = "7" }, null);
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft().Many());
        var me = new VisitorContext(null, "one-browser", null);

        var results = await Hammer(Parallel, _ => Vote(factory, id, me, new RankedBallot(["rome"], [])));

        Assert.Equal(7, results.Count(r => r.Succeeded));
        var numbers = (await BallotsAsync(factory, id)).Select(b => b.ResponseNo).Order().ToList();
        Assert.Equal(Enumerable.Range(1, 7), numbers);
    }

    [Fact]
    public async Task Parallel_revisions_of_one_response_leave_one_ballot()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft());
        var voter = await CreateUserAsync(factory, "v@example.com");
        var me = VisitorContext.ForUser(voter);
        string[] keys = ["paris", "rome", "berlin"];

        var results = await Hammer(Parallel, i => Vote(factory, id, me, new RankedBallot([keys[i % 3]], [])));

        Assert.All(results, r => Assert.True(r.Succeeded));
        var ballot = Assert.Single(await BallotsAsync(factory, id));
        Assert.Equal(1, ballot.ResponseNo);
    }

    [Fact]
    public async Task Parallel_votes_on_different_polls_all_land()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var ids = new List<string>();
        for (var i = 0; i < 4; i++) ids.Add(await CreatePollAsync(factory, owner, RankedDraft(title: "Poll " + i)));

        var results = await Hammer(Parallel * 2, i => Vote(factory, ids[i % 4], new VisitorContext(null, "b-" + i, null), new RankedBallot(["berlin"], [])));

        Assert.All(results, r => Assert.True(r.Succeeded));
        foreach (var id in ids) Assert.Equal(Parallel / 2, (await BallotsAsync(factory, id)).Count);
    }

    /// <summary>
    /// An edit that removes an option races the first votes. The change log starts with the first ballot (DESIGN.md §26):
    /// whenever a stored ballot names the removed option, the edit saw that ballot and logged the removal.
    /// </summary>
    [Fact]
    public async Task An_edit_racing_the_first_votes_never_loses_the_change_log()
    {
        for (var round = 0; round < 3; round++)
        {
            await using var factory = new AppFactory();
            var owner = await CreateUserAsync(factory, "o@example.com");
            var id = await CreatePollAsync(factory, owner, Ranked("Lunch", "Pizza", "Pasta", "Salad"));
            var draft = PollDraft.FromDefinition((await WithScope(factory, sp => sp.GetRequiredService<PollService>().GetForEditAsync(owner, id)))!.Definition);
            draft.Questions[0].Options.RemoveAt(0); // Pizza

            var votes = Hammer(Parallel, i => Vote(factory, id, new VisitorContext(null, "lunch-" + i, null), new RankedBallot(["pizza", "pasta"], [])));
            var edit = Task.Run(() => UpdatePollAsync(factory, owner, id, draft));
            await Task.WhenAll(votes, edit);
            Assert.True((await edit).Succeeded);

            var ballots = await BallotsAsync(factory, id);
            var changes = await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().PollChanges.AsNoTracking().ToListAsync());
            var namingPizza = ballots.Count(b => b.PayloadJson.Contains("pizza", StringComparison.Ordinal));
            if (namingPizza > 0)
            {
                var removed = Assert.Single(changes);
                Assert.Equal(OptionChangeKind.Removed, removed.Kind);
                Assert.Equal(namingPizza, removed.BallotCount);
            }
            else Assert.Empty(changes);
        }
    }

    [Fact]
    public async Task Two_accounts_racing_for_one_invite_link_leave_one_claimant()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft(voters: VoterMode.Invite));
        var invite = Assert.Single((await WithScope(factory, sp => sp.GetRequiredService<PollService>().GenerateInvitesAsync(owner, id, 1, [])))!);
        Assert.True(InviteTokens.TryHash(invite.Token, out var hash));
        var accounts = new List<string>();
        for (var i = 0; i < 6; i++) accounts.Add(await CreateUserAsync(factory, $"a{i}@example.com"));

        var results = await Hammer(accounts.Count * 3, i =>
            Vote(factory, id, new VisitorContext(accounts[i % accounts.Count], null, hash), new RankedBallot(["paris"], [])));

        var winners = await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().InviteTokens.AsNoTracking()
            .Select(t => t.ClaimedByUserId).ToListAsync());
        var claimant = Assert.Single(winners);
        Assert.NotNull(claimant);
        var ballot = Assert.Single(await BallotsAsync(factory, id));
        Assert.Equal(claimant, ballot.UserId);
        Assert.Contains(results, r => r.Succeeded);
    }

    [Fact]
    public async Task Parallel_sweeps_mail_each_subscriber_once()
    {
        await using var factory = new AppFactory();
        await ConfigureMailAsync(factory, hourlyCap: 1000);
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, Ranked("Lunch", "A", "B").Results(ResultsVisibility.AfterVote));
        for (var i = 0; i < 5; i++)
        {
            var voter = await CreateUserAsync(factory, $"v{i}@example.com");
            Assert.True((await WithScope(factory, sp => sp.GetRequiredService<BallotService>()
                .SubmitRankedAsync(id, VisitorContext.ForUser(voter), new RankedBallot(["a", "b"], []), null, null, null, true))).Succeeded);
        }
        Assert.True(await WithScope(factory, sp => sp.GetRequiredService<PollService>().SetClosedAsync(owner, id, true)));

        var sweep = factory.Services.GetRequiredService<PollSweepService>();
        var queued = await Hammer(8, _ => sweep.NotifyClosedAsync());

        Assert.Equal(5, queued.Sum());
        Assert.Equal(5, await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().OutboxMessages.AsNoTracking()
            .CountAsync(m => m.Type == MailType.ResultsReady)));
    }

    [Fact]
    public async Task Parallel_invite_batches_respect_the_invites_per_poll_limit()
    {
        await using var factory = new AppFactory();
        await factory.Settings.SetAsync(new Dictionary<string, string?> { [SettingKeys.LimitInvitesPerPoll] = "10" }, null);
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft(voters: VoterMode.Invite));

        var results = await Hammer(12, _ => WithScope(factory, sp => sp.GetRequiredService<PollService>().GenerateInvitesCheckedAsync(owner, id, 3, [])));

        Assert.Equal(3, results.Count(r => r.Error is null && r.Invites is { Count: 3 }));
        Assert.Equal(9, await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().InviteTokens.CountAsync()));
    }

    /// <summary>The writer lock runs one write transaction at a time, as SQLite does; ballot writes on two polls overlap
    /// only on PostgreSQL (SQLite has one writer).</summary>
    [Fact]
    public async Task Write_transactions_wait_for_each_other()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var a = await CreatePollAsync(factory, owner, RankedDraft(title: "A"));
        var b = await CreatePollAsync(factory, owner, RankedDraft(title: "B"));
        Assert.True(Telrang.Core.Ids.PollId.TryDecode(a, out var pollA));
        Assert.True(Telrang.Core.Ids.PollId.TryDecode(b, out var pollB));
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

        await using (var first = await contexts.CreateDbContextAsync())
        await using (var tx = await first.Database.BeginWriteAsync())
        {
            await first.Polls.Where(p => p.Id == pollA).ExecuteUpdateAsync(s => s.SetProperty(p => p.Title, "held"));
            var second = Task.Run(async () =>
            {
                await using var db = await contexts.CreateDbContextAsync();
                await using var t2 = await db.Database.BeginWriteAsync();
                await db.Polls.Where(p => p.Id == pollB).ExecuteUpdateAsync(s => s.SetProperty(p => p.Title, "second"));
                await t2.CommitAsync();
            });
            await Task.Delay(400);
            Assert.False(second.IsCompleted);
            await tx.CommitAsync();
            await second.WaitAsync(TimeSpan.FromSeconds(20));
        }

        if (!AppFactory.OnPostgres) return;
        // PostgreSQL: ballot writes on two polls hold their locks at the same time; on one poll the second waits.
        await using (var one = await contexts.CreateDbContextAsync())
        await using (var held = await one.Database.BeginPollWriteAsync(pollA))
        {
            await using (var other = await contexts.CreateDbContextAsync())
            await using (var parallel = await other.Database.BeginPollWriteAsync(pollB).WaitAsync(TimeSpan.FromSeconds(5)))
            {
                await parallel.CommitAsync();
            }
            var samePoll = Task.Run(async () =>
            {
                await using var db = await contexts.CreateDbContextAsync();
                await using var t = await db.Database.BeginPollWriteAsync(pollA);
                await t.CommitAsync();
            });
            var exclusive = Task.Run(async () =>
            {
                await using var db = await contexts.CreateDbContextAsync();
                await using var t = await db.Database.BeginWriteAsync();
                await t.CommitAsync();
            });
            await Task.Delay(400);
            Assert.False(samePoll.IsCompleted);
            Assert.False(exclusive.IsCompleted);
            await held.CommitAsync();
            await Task.WhenAll(samePoll, exclusive).WaitAsync(TimeSpan.FromSeconds(20));
        }
    }

    /// <summary>Every explicit transaction goes through DbWrites, so none misses the PostgreSQL writer lock.</summary>
    [Fact]
    public void No_code_opens_a_transaction_without_the_writer_lock()
    {
        var root = Path.Combine(SourceRoot(), "src", "Telrang.Web");
        var allowed = new[] { Path.Combine("Data", "DbWrites.cs"), Path.Combine("Data", "CopyToPostgresCommand.cs") };
        var offenders = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !allowed.Any(a => f.EndsWith(a, StringComparison.Ordinal)))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"\.BeginTransaction(Async)?\("))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();
        Assert.Empty(offenders);
    }

    internal static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Telrang.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
