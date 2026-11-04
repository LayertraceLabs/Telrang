using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Telrang.Web.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Poll> Polls => Set<Poll>();
    public DbSet<Ballot> Ballots => Set<Ballot>();
    public DbSet<InviteToken> InviteTokens => Set<InviteToken>();
    public DbSet<PollChange> PollChanges => Set<PollChange>();
    public DbSet<PollCreation> PollCreations => Set<PollCreation>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<SetupLink> SetupLinks => Set<SetupLink>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<AdminActionLog> AdminActionLogs => Set<AdminActionLog>();
    public DbSet<BlockedRange> BlockedRanges => Set<BlockedRange>();
    public DbSet<WriterLock> WriterLocks => Set<WriterLock>();
    public DbSet<AuthSession> AuthSessions => Set<AuthSession>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Provider-neutral: enums as strings, bounded strings, no provider-specific column types.
        // Timestamps that are compared or ordered in queries are stored as a long (UTC ticks +
        // offset, DateTimeOffsetToBinaryConverter): SQLite cannot compare DateTimeOffset columns.
        var binaryTime = new DateTimeOffsetToBinaryConverter();

        builder.Entity<ApplicationUser>(e =>
        {
            e.Property(u => u.DisplayName).HasMaxLength(ApplicationUser.MaxDisplayNameLength);
            e.Property(u => u.TimeZone).HasMaxLength(100);
        });

        builder.Entity<AppSetting>(e =>
        {
            e.HasKey(s => s.Key);
            e.Property(s => s.Key).HasMaxLength(100);
            e.Property(s => s.Value).HasMaxLength(64_000);
            e.Property(s => s.UpdatedBy).HasMaxLength(450);
        });

        builder.Entity<SetupLink>(e =>
        {
            e.HasKey(l => l.Id);
            e.Property(l => l.Id).ValueGeneratedNever();
            e.HasOne(l => l.User).WithMany().HasForeignKey(l => l.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(l => l.TokenHash).IsUnique();
            e.HasIndex(l => l.UserId);
            e.Property(l => l.TokenHash).HasMaxLength(64);
            e.Property(l => l.Purpose).HasConversion<string>().HasMaxLength(20);
            e.Property(l => l.CreatedBy).HasMaxLength(450);
            e.Property(l => l.SecurityStamp).HasMaxLength(256);
            e.Property(l => l.CreatedAt).HasConversion(binaryTime);
            e.Property(l => l.ExpiresAt).HasConversion(binaryTime);
        });

        builder.Entity<OutboxMessage>(e =>
        {
            e.HasKey(m => m.Id);
            e.Property(m => m.Id).ValueGeneratedNever();
            e.Property(m => m.To).HasMaxLength(320);
            e.Property(m => m.Subject).HasMaxLength(200);
            e.Property(m => m.Type).HasConversion<string>().HasMaxLength(30);
            e.Property(m => m.LastError).HasMaxLength(1000);
            e.Property(m => m.CreatedAt).HasConversion(binaryTime);
            e.Property(m => m.SendAfter).HasConversion(binaryTime);
            e.Property(m => m.SentAt).HasConversion(binaryTime);
            e.Property(m => m.DeadAt).HasConversion(binaryTime);
            e.HasIndex(m => new { m.SentAt, m.DeadAt, m.SendAfter });
            e.Property(m => m.ToHash).HasMaxLength(64);
            e.HasIndex(m => new { m.ToHash, m.CreatedAt });
        });

        builder.Entity<AdminActionLog>(e =>
        {
            e.HasKey(l => l.Id);
            e.Property(l => l.AdminUserId).HasMaxLength(450);
            e.Property(l => l.Action).HasMaxLength(60);
            e.Property(l => l.TargetType).HasMaxLength(20);
            e.Property(l => l.TargetId).HasMaxLength(450);
            e.Property(l => l.Note).HasMaxLength(500);
        });

        builder.Entity<AuthSession>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasMaxLength(64);
            e.Property(s => s.UserId).HasMaxLength(450);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => s.UserId);
            e.Property(s => s.CreatedAt).HasConversion(binaryTime);
            e.Property(s => s.ExpiresAt).HasConversion(binaryTime);
            e.HasIndex(s => s.ExpiresAt);
        });

        builder.Entity<WriterLock>(e =>
        {
            e.HasKey(l => l.Id);
            e.Property(l => l.Id).ValueGeneratedNever();
        });

        builder.Entity<BlockedRange>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.Cidr).HasMaxLength(50);
            e.HasIndex(r => r.Cidr).IsUnique();
            e.Property(r => r.Note).HasMaxLength(BlockedRange.MaxNoteLength);
            e.Property(r => r.CreatedByUserId).HasMaxLength(450); // no FK: accounts are only soft-deleted
            e.Property(r => r.CreatedAt).HasConversion(binaryTime);
            e.Property(r => r.ExpiresAt).HasConversion(binaryTime);
        });
        builder.Entity<Poll>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).ValueGeneratedNever();
            e.HasOne(p => p.Owner).WithMany().HasForeignKey(p => p.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(p => p.OwnerId);
            e.Property(p => p.Title).HasMaxLength(500);
            e.Property(p => p.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.Scoring).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.ResultsVisibility).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.VoterMode).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.ClosedNotifiedAt).HasConversion(binaryTime);
        });

        builder.Entity<PollCreation>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).ValueGeneratedNever();
            e.Property(c => c.UserId).HasMaxLength(450); // no FK: accounts are only soft-deleted
            e.Property(c => c.At).HasConversion(binaryTime);
            e.HasIndex(c => new { c.UserId, c.At });
            e.HasIndex(c => c.At);
        });

        builder.Entity<Ballot>(e =>
        {
            e.HasKey(b => b.Id);
            e.Property(b => b.Id).ValueGeneratedNever();
            e.HasOne(b => b.Poll).WithMany(p => p.Ballots).HasForeignKey(b => b.PollId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(b => b.User).WithMany().HasForeignKey(b => b.UserId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(b => new { b.PollId, b.VoterIdentity, b.ResponseNo }).IsUnique();
            e.HasIndex(b => new { b.UserId, b.PollId });
            e.Property(b => b.VoterKey).HasMaxLength(100);
            e.Property(b => b.VoterIdentity).HasMaxLength(110);
            e.Property(b => b.DisplayName).HasMaxLength(60);
            e.Property(b => b.Alias).HasMaxLength(Features.Voting.BallotService.MaxNameLimit);
        });

        builder.Entity<PollChange>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).ValueGeneratedNever();
            e.HasOne(c => c.Poll).WithMany(p => p.Changes).HasForeignKey(c => c.PollId).OnDelete(DeleteBehavior.Cascade);
            e.Property(c => c.At).HasConversion(binaryTime);
            e.Property(c => c.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(c => c.Label).HasMaxLength(PollChange.MaxLabelLength);
            e.Property(c => c.OldLabel).HasMaxLength(PollChange.MaxLabelLength);
            e.HasIndex(c => new { c.PollId, c.At, c.Seq });
        });

        builder.Entity<InviteToken>(e =>
        {
            e.HasKey(t => t.Id);
            e.Property(t => t.Id).ValueGeneratedNever();
            e.HasOne(t => t.Poll).WithMany(p => p.InviteTokens).HasForeignKey(t => t.PollId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(t => t.Ballot).WithMany().HasForeignKey(t => t.BallotId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.HasIndex(t => t.PollId);
            e.Property(t => t.TokenHash).HasMaxLength(64);
            e.Property(t => t.Alias).HasMaxLength(Features.Voting.BallotService.MaxNameLimit);
            // No foreign key: accounts are only soft-deleted, and adding one would make SQLite rebuild the table
            // outside the migration's transaction.
            e.Property(t => t.ClaimedByUserId).HasMaxLength(450);
            // One claimed token per account and poll (NULLs are distinct; EF filters them on SQL Server).
            e.HasIndex(t => new { t.PollId, t.ClaimedByUserId }).IsUnique();
        });
    }
}
