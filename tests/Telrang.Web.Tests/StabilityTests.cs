using Telrang.Web.Data;
using Telrang.Web.Features.Mail;
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
}
