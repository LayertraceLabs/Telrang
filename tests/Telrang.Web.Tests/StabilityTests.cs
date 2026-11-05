using Telrang.Core.Ids;
using Telrang.Web.Data;
using Telrang.Web.Features.Mail;
using Telrang.Web.Features.Polls;
using Telrang.Web.Features.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>Failure paths of the background work: the mail outbox, the results cache, the close sweep, the live stream.</summary>
public class StabilityTests
{
    /// <summary>Fails the next save that marks an outbox message sent, as a full disk or a dropped connection would.</summary>
    private sealed class FailSentSave : SaveChangesInterceptor
    {
        public volatile bool Armed;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Armed && eventData.Context!.ChangeTracker.Entries<OutboxMessage>().Any(e => e.State == EntityState.Modified && e.Entity.SentAt != null))
            {
                Armed = false;
                throw new DbUpdateException("Disk full (fake).");
            }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task A_sent_mail_whose_outcome_is_not_saved_is_not_sent_again_until_its_claim_runs_out()
    {
        var clock = new MutableTime(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        var failSave = new FailSentSave();
        await using var factory = new AppFactory(services: s =>
        {
            s.AddSingleton<TimeProvider>(clock);
            s.ConfigureDbContext<ApplicationDbContext>(o => o.AddInterceptors(failSave));
        });
        await ConfigureMailAsync(factory);
        Assert.True(await factory.Services.GetRequiredService<IMailQueue>().EnqueueAsync("x@example.com", "S", "B", MailType.Test));
        factory.Mail.AfterSend = _ => failSave.Armed = true;

        await factory.SendMailAsync(); // sent, but SentAt is not saved
        Assert.Single(factory.Mail.Sent);
        factory.Mail.AfterSend = null;

        await factory.SendMailAsync();
        clock.Now += MailSender.ClaimTimeout - TimeSpan.FromSeconds(1);
        await factory.SendMailAsync();
        Assert.Single(factory.Mail.Sent); // the claim holds it back

        clock.Now += TimeSpan.FromSeconds(1);
        await factory.SendMailAsync(); // at least once: the claim ran out
        Assert.Equal(2, factory.Mail.Sent.Count);
        await factory.SendMailAsync();
        Assert.Equal(2, factory.Mail.Sent.Count);
        var row = Assert.Single(await WithScope(factory, sp =>
            sp.GetRequiredService<ApplicationDbContext>().OutboxMessages.AsNoTracking().Where(m => m.Type == MailType.Test).ToListAsync()));
        Assert.NotNull(row.SentAt);
    }

    [Fact]
    public async Task A_mail_whose_claim_cannot_be_saved_is_not_sent()
    {
        var failAll = new FailAllSaves();
        await using var factory = new AppFactory(services: s => s.ConfigureDbContext<ApplicationDbContext>(o => o.AddInterceptors(failAll)));
        await ConfigureMailAsync(factory);
        var queue = factory.Services.GetRequiredService<IMailQueue>();
        Assert.True(await queue.EnqueueAsync("x@example.com", "S", "B", MailType.Test));
        Assert.True(await queue.EnqueueAsync("y@example.com", "S", "B", MailType.Test));

        failAll.Armed = true;
        Assert.Equal(0, await factory.SendMailAsync());
        Assert.Equal(0, await factory.SendMailAsync());
        Assert.Empty(factory.Mail.Sent);

        failAll.Armed = false;
        Assert.Equal(2, await factory.SendMailAsync());
    }

    /// <summary>Fails every save of an outbox message.</summary>
    private sealed class FailAllSaves : SaveChangesInterceptor
    {
        public volatile bool Armed;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            Armed && eventData.Context!.ChangeTracker.Entries<OutboxMessage>().Any(e => e.State == EntityState.Modified)
                ? throw new DbUpdateException("Disk full (fake).")
                : base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    // ---- results cache ----------------------------------------------------------------------------------

    private static PollResultsData Data(string definition) => new(definition, null!, []);

    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    [Fact]
    public async Task A_computation_nobody_waits_for_any_more_does_not_stay_in_the_cache()
    {
        await using var factory = new AppFactory();
        var cache = factory.Services.GetRequiredService<ResultsCache>();
        var poll = Guid.NewGuid();
        var release = new TaskCompletionSource<PollResultsData>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var waiter = cache.GetAsync(poll, "d", _ => release.Task, cts.Token);
        Assert.Equal(1, cache.Computing);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);

        release.SetResult(Data("d"));
        await UntilAsync(() => cache.Computing == 0);
        Assert.Equal(1, cache.Count); // stored as usual, within the size limit
    }

    [Fact]
    public async Task A_failed_computation_nobody_waited_for_is_not_handed_to_the_next_caller()
    {
        await using var factory = new AppFactory();
        var cache = factory.Services.GetRequiredService<ResultsCache>();
        var poll = Guid.NewGuid();
        var fail = new TaskCompletionSource<PollResultsData>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var waiter = cache.GetAsync(poll, "d", _ => fail.Task, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        fail.SetException(new InvalidOperationException("database gone (fake)"));
        await UntilAsync(() => cache.Computing == 0);

        var data = await cache.GetAsync(poll, "d", _ => Task.FromResult(Data("d")), CancellationToken.None);
        Assert.Equal("d", data.DefinitionJson);
    }

    [Fact]
    public async Task A_change_drops_a_running_computation()
    {
        await using var factory = new AppFactory();
        var cache = factory.Services.GetRequiredService<ResultsCache>();
        var poll = Guid.NewGuid();
        var release = new TaskCompletionSource<PollResultsData>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiter = cache.GetAsync(poll, "d", _ => release.Task, CancellationToken.None);
        Assert.Equal(1, cache.Computing);
        factory.Services.GetRequiredService<PollChangeNotifier>().End(poll);
        Assert.Equal(0, cache.Computing);
        release.SetResult(Data("d"));
        Assert.Equal("d", (await waiter).DefinitionJson); // its caller still gets it
        Assert.Equal(0, cache.Count); // but it is not stored
    }

    // ---- close sweep ------------------------------------------------------------------------------------

    [Fact]
    public async Task The_close_sweep_loads_no_poll_that_was_mailed_within_the_interval_or_has_no_close()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var now = DateTimeOffset.UtcNow;
        async Task<Guid> PollAsync(DateTimeOffset? closedAt, DateTimeOffset? closesAt, DateTimeOffset? notifiedAt)
        {
            Assert.True(PollId.TryDecode(await CreatePollAsync(factory, owner, RankedDraft()), out var id));
            await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().Polls.Where(p => p.Id == id)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.ClosedAt, closedAt).SetProperty(p => p.ClosesAt, closesAt)
                    .SetProperty(p => p.ClosedNotifiedAt, notifiedAt)));
            return id;
        }
        var closedNeverMailed = await PollAsync(now.AddHours(-1), null, null);
        var openWithDate = await PollAsync(null, now.AddDays(1), null);
        var mailedLongAgo = await PollAsync(now.AddHours(-1), null, now.AddDays(-2));
        var mailedRecently = await PollAsync(now.AddMinutes(-10), null, now.AddHours(-1));
        var noClose = await PollAsync(null, null, null);

        var loaded = await WithScope(factory, sp => PollSweepService.NotifyCandidates(sp.GetRequiredService<ApplicationDbContext>(), now)
            .Select(p => p.Id).ToListAsync());
        Assert.Equal(new[] { closedNeverMailed, openWithDate, mailedLongAgo }.Order(), loaded.Order());
        Assert.DoesNotContain(mailedRecently, loaded);
        Assert.DoesNotContain(noClose, loaded);
    }
}
