using Telrang.Core.Ballots;
using Telrang.Core.Ids;
using Telrang.Core.Polls;
using Telrang.Web.Data;
using Telrang.Web.Features.Settings;
using Telrang.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Telrang.Web.Features.Polls;

/// <summary><see cref="SetupRequired"/>: the account is gated (AccountGate) or deleted; a stale circuit cannot act.</summary>
/// <summary><see cref="LimitReached"/>: a per-account limit (§14.4) refused the creation; <see cref="PollSaveResult.Message"/> names it.</summary>
public enum PollSaveStatus { Ok, NotFound, Invalid, EmailNotVerified, SetupRequired, LimitReached }

/// <param name="Errors">Field errors of the draft (<see cref="DraftError.Field"/> paths such as <c>questions[0].budget</c>).</param>
/// <param name="Message">A problem that is not a field's (a limit), or a success note.</param>
/// <param name="Changes">Edit: the option changes recorded in the change log (empty when the poll had no ballot).</param>
public sealed record PollSaveResult(PollSaveStatus Status, string? EncodedId, IReadOnlyList<DraftError> Errors, string? Message = null,
    IReadOnlyList<OptionChange>? Changes = null)
{
    public bool Succeeded => Status == PollSaveStatus.Ok;
}

public sealed record PollSummary(string EncodedId, string Title, PollType Type, int BallotCount, bool IsClosed, DateTimeOffset CreatedAt);

/// <param name="AnswersPerKey">Per option key of the stored definition: the ballots that answered it (the editor's preview of
/// a removal, DESIGN.md §26).</param>
public sealed record PollEditModel(string EncodedId, PollDefinition Definition, int BallotCount,
    IReadOnlyDictionary<string, int>? AnswersPerKey = null)
{
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>The stored close instant (the editor preview shows it while the close date is unchanged).</summary>
    public DateTimeOffset? ClosesAt { get; init; }
}

public sealed record PollOverview(PollAccess Access, int BallotCount);

/// <summary>
/// What the pages show of a change log (DESIGN.md §31): the newest entries (at most <see cref="PollChange.ShownEntries"/>),
/// oldest first, and how many changes the log holds in all (a merged "n earlier changes" entry counts n).
/// </summary>
public sealed record ChangeLogPage(IReadOnlyList<PollChange> Entries, int Total)
{
    public static readonly ChangeLogPage Empty = new([], 0);
}

public sealed record GeneratedInvite(string? Alias, string Token);

public sealed record InviteSummary(Guid Id, string? Alias, DateTimeOffset CreatedAt, bool Used);

/// <summary>One response on the owner's list (share page). <see cref="Name"/> is the voter's entry, <see cref="Alias"/> the owner's.</summary>
public sealed record ResponseSummary(Guid Id, string? Name, string? Alias, DateTimeOffset SubmittedAt, DateTimeOffset UpdatedAt)
{
    /// <summary>What owner views show: the alias when set, else the name as entered (null when both are empty).</summary>
    public string? Shown => Alias ?? Name;
}

public enum AliasStatus { Ok, NotFound, Invalid }

/// <summary>
/// Owner-side poll operations: create, edit, close/reopen, invites. Every method that takes an
/// encoded id authorises through <see cref="PollAccessService"/> (CanEdit); a poll the caller does
/// not own is reported as not found.
/// </summary>
public sealed class PollService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    PollAccessService access,
    SettingsService settings,
    TimeProvider time,
    AbuseTracker abuse,
    ClientInfo client,
    Results.PollChangeNotifier notifier,
    ChangeLogLimiter changeLimiter)
{
    public const int MaxInvitesPerBatch = 500;

    /// <summary><see cref="PollCreation"/> rows older than this are purged on the next creation.</summary>
    public static readonly TimeSpan PollCreationRetention = TimeSpan.FromHours(48);

    public async Task<PollSaveResult> CreatePollAsync(string ownerId, PollDraft draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!await MayActAsync(ownerId, ct)) return SetupRequired();
        if (!await HasVerifiedEmailAsync(ownerId, ct))
            return new PollSaveResult(PollSaveStatus.EmailNotVerified, null, [], "Verify your email address to create polls.");
        using var serialised = await OwnerLock.AcquireAsync(ownerId, ct); // count and insert as one step (R2-POLLS-5)
        var now = time.GetUtcNow();
        var limits = await LimitsForAsync(ownerId, ct);
        if (await CreationRefusalAsync(ownerId, limits, now, ct) is { } refusal)
        {
            abuse.Record(client.Key, ownerId, AbuseSignal.LimitRefused);
            return new PollSaveResult(PollSaveStatus.LimitReached, null, [], refusal);
        }
        var parsed = PollParsing.Validate(draft, Limits, limits.PollDurationDays, now);
        if (!parsed.Success) return new PollSaveResult(PollSaveStatus.Invalid, null, parsed.Errors);
        var definition = parsed.Definition!;
        // No time zone -> the creator's account zone now, stored in the definition, so a later
        // change of the account setting never moves the close.
        if (definition.TimeZone is null) definition = definition with { TimeZone = await DefaultTimeZoneAsync(ownerId, ct) };

        var poll = new Poll
        {
            Id = PollId.NewId(),
            OwnerId = ownerId,
            Title = "",
            DefinitionJson = "",
            CreatedAt = now,
            UpdatedAt = now,
        };
        Apply(poll, definition);
        SetClose(poll, definition, limits.PollDurationDays);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.Polls.Add(poll);
        // The per-day limit counts creations, not surviving polls; rows past 48 h are purged here (no job).
        db.PollCreations.Add(new PollCreation { Id = Guid.NewGuid(), UserId = ownerId, At = now });
        var purgeBefore = now - PollCreationRetention;
        await db.PollCreations.Where(c => c.At < purgeBefore).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        return new PollSaveResult(PollSaveStatus.Ok, PollId.Encode(poll.Id), []);
    }

    /// <summary>The account's limits in force now (override, default, halving for new self-registered accounts).</summary>
    public async Task<EffectiveLimits> LimitsForAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await UserLimits.ForUserAsync(db, settings.Current.Limits, userId, time.GetUtcNow(), ct);
    }

    /// <summary>Open polls and creations in the last 24 h against the account's limits; null when allowed.</summary>
    private async Task<string?> CreationRefusalAsync(string ownerId, EffectiveLimits limits, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (limits.ActivePolls > 0 && await CountOpenAsync(db, ownerId, now, ct) >= limits.ActivePolls)
            return UserLimits.ActivePollsMessage(limits.ActivePolls);
        var dayAgo = now - TimeSpan.FromHours(24);
        if (limits.PollsPerDay > 0 && await db.PollCreations.CountAsync(c => c.UserId == ownerId && c.At > dayAgo, ct) >= limits.PollsPerDay)
            return UserLimits.PollsPerDayMessage(limits.PollsPerDay);
        return null;
    }

    private static async Task<int> CountOpenAsync(ApplicationDbContext db, string ownerId, DateTimeOffset now, CancellationToken ct) =>
        // Nullable DateTimeOffset comparisons do not translate for SQLite: compare in memory.
        (await db.Polls.Where(p => p.OwnerId == ownerId && p.ClosedAt == null).Select(p => p.ClosesAt).ToListAsync(ct))
            .Count(c => !PollState.IsClosed(null, c, now));

    private async Task<bool> MayActAsync(string userId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await AccountGate.MayActAsync(db, userId, ct);
    }

    private static PollSaveResult SetupRequired() =>
        new(PollSaveStatus.SetupRequired, null, [], Telrang.Web.Features.Voting.BallotService.SetupRequiredMessage);

    private PollLimits Limits => PollParsing.LimitsFrom(settings.Current.Limits);

    /// <summary>The limits the editor preview should use (same as the server check).</summary>
    public PollLimits CurrentLimits => Limits;

    /// <summary>The account's time zone when it is a known IANA id, else UTC (the default time zone of a new poll).</summary>
    public async Task<string> DefaultTimeZoneAsync(string userId, CancellationToken ct = default) =>
        await AccountTimeZoneAsync(userId, ct) ?? PollTime.Utc;

    /// <summary>The account's time zone setting when it is a known IANA id, else null.</summary>
    public async Task<string?> AccountTimeZoneAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var zone = await db.Users.Where(u => u.Id == userId).Select(u => u.TimeZone).SingleOrDefaultAsync(ct);
        return PollTime.IsKnown(zone) ? zone : null;
    }

    /// <summary>Poll creation needs a verified email (DESIGN.md §14.10 decision 3).</summary>
    public async Task<bool> HasVerifiedEmailAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await AccountGate.MayActAsync(db, userId, ct, verifiedEmail: true);
    }

    /// <summary>
    /// Saves an edit (DESIGN.md §26): the validator refuses any change to a setting fixed at creation, so only the title,
    /// description, options and close date change. Ballots are never touched: option keys are stable, a removed option's
    /// key stays in stored ballots and is ignored on read. Once the poll has a ballot, every change goes into the change
    /// log (<see cref="PollChange"/>) in the same transaction.
    /// <para>Validation, the diff and the ballot counts of removals (one parse per ballot) run before the write
    /// transaction, which on PostgreSQL holds the instance-wide writer lock (DESIGN.md §31, pentest R3-VOTE-3 / R3-RES-5 /
    /// R3-DEP-8). Inside it the poll's definition and ballot count are checked against that pass; when either moved, the
    /// pass runs again (at most <see cref="EditAttempts"/> times; the last attempt keeps its counts).</para>
    /// </summary>
    public async Task<PollSaveResult> UpdatePollAsync(string userId, string encodedId, PollDraft draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        using var serialised = await OwnerLock.AcquireAsync(userId, ct); // a reopening edit counts open polls (R2-POLLS-5)
        for (var attempt = 1; ; attempt++)
        {
            var pass = await PrepareEditAsync(userId, encodedId, draft, ct);
            if (pass.Refusal is { } refusal) return refusal;
            var result = await WriteEditAsync(userId, pass, last: attempt >= EditAttempts, ct);
            if (result is not null) return result;
        }
    }

    /// <summary>Times an edit re-reads the poll when it changed between the pass outside the lock and the write.</summary>
    public const int EditAttempts = 3;

    public const string MovedMessage = "The poll changed while saving. Reload the page and save again.";

    /// <summary>The work of an edit done outside the write transaction.</summary>
    private sealed record EditPass(PollSaveResult? Refusal, string EncodedId = "", string StoredJson = "", PollDefinition? Stored = null,
        PollDefinition? Updated = null, EffectiveLimits? OwnerLimits = null, int BallotCount = 0,
        IReadOnlyList<OptionChange>? Changes = null, IReadOnlyDictionary<string, int>? Answers = null);

    private async Task<EditPass> PrepareEditAsync(string userId, string encodedId, PollDraft draft, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await AccountGate.MayActAsync(db, userId, ct)) return new EditPass(SetupRequired());
        var a = await access.GetAsync(db, encodedId, VisitorContext.ForUser(userId), tracking: false, ct);
        if (a is null || !a.CanEdit) return new EditPass(new PollSaveResult(PollSaveStatus.NotFound, null, []));

        var ownerLimits = await UserLimits.ForUserAsync(db, settings.Current.Limits, a.Poll.OwnerId, time.GetUtcNow(), ct);
        // The duration limit applies only to a changed close date (a transferred poll keeps its own).
        var parsed = PollParsing.Validate(draft, Limits, ownerLimits.PollDurationDays, a.Poll.CreatedAt, a.Definition);
        if (!parsed.Success) return new EditPass(new PollSaveResult(PollSaveStatus.Invalid, a.EncodedId, parsed.Errors));
        // Empty time zone: keep the poll's stored zone (not the account's current one).
        var updated = parsed.Definition!.TimeZone is null
            ? parsed.Definition! with { TimeZone = a.Definition.TimeZone ?? PollTime.Utc }
            : parsed.Definition!;

        var changes = Changes(a.Definition, updated);
        var removed = changes.Where(c => c.Kind == OptionChangeKind.Removed).Select(c => c.Key).ToList();
        var count = await db.Ballots.CountAsync(b => b.PollId == a.Poll.Id, ct);
        IReadOnlyDictionary<string, int>? answers = null;
        if (count > 0 && removed.Count > 0)
        {
            var payloads = await db.Ballots.AsNoTracking().Where(b => b.PollId == a.Poll.Id).Select(b => b.PayloadJson).ToListAsync(ct);
            count = payloads.Count;
            answers = PollJson.AnswerCounts(payloads, a.Definition.Questions[0], removed);
        }
        return new EditPass(null, a.EncodedId, a.Poll.DefinitionJson, a.Definition, updated, ownerLimits, count, changes, answers);
    }

    /// <summary>The write of an edit under the write transaction, or null when the poll moved since the pass (try again).</summary>
    private async Task<PollSaveResult?> WriteEditAsync(string userId, EditPass pass, bool last, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginWriteAsync(ct);
        if (!await AccountGate.MayActAsync(db, userId, ct)) return SetupRequired();
        var a = await access.GetAsync(db, pass.EncodedId, VisitorContext.ForUser(userId), tracking: true, ct);
        if (a is null || !a.CanEdit) return new PollSaveResult(PollSaveStatus.NotFound, null, []);
        var poll = a.Poll;
        var ballots = await db.Ballots.CountAsync(b => b.PollId == poll.Id, ct);
        // Another save of the definition can only come from another session of the owner; ballots come and go any time.
        if (!string.Equals(poll.DefinitionJson, pass.StoredJson, StringComparison.Ordinal))
            return last ? new PollSaveResult(PollSaveStatus.Invalid, pass.EncodedId, [], MovedMessage) : null;
        if (ballots != pass.BallotCount && !last) return null;
        var stored = pass.Stored!;
        var updated = pass.Updated!;
        var ownerLimits = pass.OwnerLimits!;

        // A later close date can reopen a poll that closed by its date: that counts against the open-polls limit like
        // reopening it does (ChangeClosedAsync), checked before anything is changed.
        var now = time.GetUtcNow();
        if (CloseDateChanged(stored, updated) && PollState.IsClosed(poll, now)
            && !PollState.IsClosed(poll.ClosedAt, CloseFor(poll.CreatedAt, updated, ownerLimits.PollDurationDays).ClosesAt, now)
            && ownerLimits.ActivePolls > 0 && await CountOpenAsync(db, poll.OwnerId, now, ct) >= ownerLimits.ActivePolls)
        {
            abuse.Record(client.Key, userId, AbuseSignal.LimitRefused);
            return new PollSaveResult(PollSaveStatus.LimitReached, a.EncodedId, [], UserLimits.ActivePollsMessage(ownerLimits.ActivePolls));
        }

        // The change log starts with the first ballot (DESIGN.md §26).
        IReadOnlyList<OptionChange> changes = [];
        if (ballots > 0)
        {
            changes = pass.Changes!;
            if (changes.Count > 0 && !changeLimiter.TryTake(poll.Id))
            {
                abuse.Record(client.Key, userId, AbuseSignal.LimitRefused);
                return new PollSaveResult(PollSaveStatus.LimitReached, a.EncodedId, [], ChangeLogLimiter.Refused);
            }
            for (var i = 0; i < changes.Count; i++)
            {
                var c = changes[i];
                db.PollChanges.Add(new PollChange
                {
                    Id = Guid.NewGuid(),
                    PollId = poll.Id,
                    At = now,
                    Seq = i,
                    Kind = c.Kind,
                    Label = Clip(c.Label),
                    OldLabel = c.OldLabel is null ? null : Clip(c.OldLabel),
                    BallotCount = c.Kind == OptionChangeKind.Removed ? pass.Answers?.GetValueOrDefault(c.Key) ?? 0 : null,
                });
            }
            await CapChangeLogAsync(db, poll.Id, changes.Count, ct);
        }

        // ClosesAt is frozen at creation: an edit never moves it (not even after the admin changed the max duration).
        // Only a changed close date recomputes it: a new date, or the date removed.
        if (CloseDateChanged(a.Definition, updated)) SetClose(poll, updated, ownerLimits.PollDurationDays);
        Apply(poll, updated);
        poll.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        notifier.Changed(poll.Id);
        return new PollSaveResult(PollSaveStatus.Ok, a.EncodedId, [], "Saved.", changes);
    }

    /// <summary>The changes an edit records (DESIGN.md §26, §31): title, description and the single question's options.</summary>
    public static IReadOnlyList<OptionChange> Changes(PollDefinition stored, PollDefinition updated) =>
        OptionChanges.Between(stored, updated);

    /// <summary>
    /// Keeps the poll's change log at <see cref="PollChange.MaxEntries"/> entries (DESIGN.md §31): when the
    /// <paramref name="adding"/> new entries (already added to <paramref name="db"/>, not saved) would pass it, the oldest
    /// entries are deleted and counted into the one <see cref="OptionChangeKind.Earlier"/> entry, which stays first.
    /// </summary>
    private static async Task CapChangeLogAsync(ApplicationDbContext db, Guid pollId, int adding, CancellationToken ct)
    {
        var stored = await db.PollChanges.CountAsync(c => c.PollId == pollId && c.Kind != OptionChangeKind.Earlier, ct);
        var overflow = stored + adding - (PollChange.MaxEntries - 1);
        if (overflow <= 0) return;
        var oldest = await db.PollChanges.Where(c => c.PollId == pollId && c.Kind != OptionChangeKind.Earlier)
            .OrderBy(c => c.At).ThenBy(c => c.Seq).Take(overflow).Select(c => new { c.Id, c.At }).ToListAsync(ct);
        var ids = oldest.Select(c => c.Id).ToList();
        await db.PollChanges.Where(c => ids.Contains(c.Id)).ExecuteDeleteAsync(ct);
        var earlier = await db.PollChanges.FirstOrDefaultAsync(c => c.PollId == pollId && c.Kind == OptionChangeKind.Earlier, ct);
        if (earlier is null)
        {
            db.PollChanges.Add(new PollChange
            {
                Id = Guid.NewGuid(),
                PollId = pollId,
                At = oldest[0].At,
                Seq = -1,
                Kind = OptionChangeKind.Earlier,
                Label = "",
                BallotCount = oldest.Count,
            });
        }
        else earlier.BallotCount = (earlier.BallotCount ?? 0) + oldest.Count;
    }

    private static string Clip(string label) => label.Length <= PollChange.MaxLabelLength ? label : label[..PollChange.MaxLabelLength];

    /// <summary>The poll for the editor, with the ballots that answered each option (one parse per ballot, no lock).</summary>
    public async Task<PollEditModel?> GetForEditAsync(string userId, string encodedId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var a = await access.GetAsync(db, encodedId, VisitorContext.ForUser(userId), tracking: false, ct);
        if (a is null || !a.CanEdit) return null;
        var payloads = await db.Ballots.AsNoTracking().Where(b => b.PollId == a.Poll.Id).Select(b => b.PayloadJson).ToListAsync(ct);
        var q = a.Definition.Questions[0];
        var perKey = PollJson.AnswerCounts(payloads, q, q.Options.Select(o => o.Key));
        return new PollEditModel(a.EncodedId, a.Definition, payloads.Count, perKey)
        {
            CreatedAt = a.Poll.CreatedAt,
            ClosesAt = a.Poll.ClosesAt,
        };
    }

    /// <summary>
    /// The newest <see cref="PollChange.ShownEntries"/> entries of the poll's change log, oldest first, and the number of
    /// changes in all (DESIGN.md §26, §31), for anyone the caller's access check let view it.
    /// </summary>
    public async Task<ChangeLogPage> ListChangesAsync(PollAccess viewable, CancellationToken ct = default)
    {
        if (!viewable.CanView) return ChangeLogPage.Empty;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var pollId = viewable.Poll.Id;
        var newest = await db.PollChanges.AsNoTracking().Where(c => c.PollId == pollId)
            .OrderByDescending(c => c.At).ThenByDescending(c => c.Seq).Take(PollChange.ShownEntries).ToListAsync(ct);
        if (newest.Count == 0) return ChangeLogPage.Empty;
        newest.Reverse();
        var total = newest.Count < PollChange.ShownEntries
            ? newest.Sum(Weight)
            : await db.PollChanges.CountAsync(c => c.PollId == pollId && c.Kind != OptionChangeKind.Earlier, ct)
              + (await db.PollChanges.Where(c => c.PollId == pollId && c.Kind == OptionChangeKind.Earlier).SumAsync(c => c.BallotCount, ct) ?? 0);
        return new ChangeLogPage(newest, total);
    }

    private static int Weight(PollChange c) => c.Kind == OptionChangeKind.Earlier ? c.BallotCount ?? 0 : 1;

    /// <summary>
    /// "Copy" (DESIGN.md §20.2): the form model of one of the caller's polls, for a new poll. Everything the definition
    /// holds is copied (title, description, settings, options); the close date only while it is still ahead and within
    /// the caller's max duration from now. Ballots, invite links, aliases and the closed state are not part of a
    /// definition, so they never come along; neither do option keys. The change log stays with the poll. Null when not found or
    /// not the caller's.
    /// </summary>
    public async Task<PollDraft?> GetCopyAsync(string userId, string encodedId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await AccountGate.MayActAsync(db, userId, ct)) return null;
        var a = await access.GetAsync(db, encodedId, VisitorContext.ForUser(userId), tracking: false, ct);
        if (a is null || !a.CanEdit) return null;
        var draft = PollDraft.FromDefinition(a.Definition);
        // A copy is a new poll: its options get their own keys (DESIGN.md §26).
        foreach (var o in draft.Questions.SelectMany(q => q.Options)) o.Key = null;
        if (draft.ClosesOn is { } day)
        {
            var now = time.GetUtcNow();
            var limits = await UserLimits.ForUserAsync(db, settings.Current.Limits, userId, now, ct);
            var ahead = PollTime.CloseInstant(day, a.Definition.TimeZone) > now;
            var withinLimit = PollParsing.LatestClose(limits.PollDurationDays, now) is not { } latest || day <= latest;
            if (!ahead || !withinLimit) draft.ClosesOn = null;
        }
        return draft;
    }

    public async Task<PollOverview?> GetOverviewAsync(string userId, string encodedId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var a = await access.GetAsync(db, encodedId, VisitorContext.ForUser(userId), tracking: false, ct);
        if (a is null || !a.CanEdit) return null;
        var count = await db.Ballots.CountAsync(b => b.PollId == a.Poll.Id, ct);
        return new PollOverview(a, count);
    }

    /// <summary>The user's own polls, newest first. (Listing by owner is not a by-id lookup.)</summary>
    public async Task<IReadOnlyList<PollSummary>> ListOwnedAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Polls.AsNoTracking()
            .Where(p => p.OwnerId == userId)
            .Select(p => new { p.Id, p.Title, p.Type, p.ClosedAt, p.ClosesAt, p.CreatedAt, Count = p.Ballots.Count })
            .ToListAsync(ct);
        var now = time.GetUtcNow();
        // DateTimeOffset ordering is done in memory: SQLite cannot ORDER BY it.
        return rows
            .Select(r => new PollSummary(PollId.Encode(r.Id), r.Title, r.Type, r.Count, PollState.IsClosed(r.ClosedAt, r.ClosesAt, now), r.CreatedAt))
            .OrderByDescending(r => r.CreatedAt)
            .ToList();
    }

    public async Task<bool> SetClosedAsync(string userId, string encodedId, bool closed, CancellationToken ct = default) =>
        (await ChangeClosedAsync(userId, encodedId, closed, ct)).Ok;

    /// <summary>Close or reopen. Reopening counts against the open-polls limit; the refusal is the error.</summary>
    public async Task<(bool Ok, string? Error)> ChangeClosedAsync(string userId, string encodedId, bool closed, CancellationToken ct = default)
    {
        using var serialised = await OwnerLock.AcquireAsync(userId, ct); // reopening counts open polls (R2-POLLS-5)
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await AccountGate.MayActAsync(db, userId, ct)) return (false, null);
        var a = await access.GetAsync(db, encodedId, VisitorContext.ForUser(userId), tracking: true, ct);
        if (a is null || !a.CanEdit) return (false, null);
        var now = time.GetUtcNow();
        if (!closed && a.Poll.ClosedAt is not null && !PollState.IsClosed(null, a.Poll.ClosesAt, now))
        {
            var limits = await UserLimits.ForUserAsync(db, settings.Current.Limits, userId, now, ct);
            if (limits.ActivePolls > 0 && await CountOpenAsync(db, userId, now, ct) >= limits.ActivePolls)
            {
                abuse.Record(client.Key, userId, AbuseSignal.LimitRefused);
                return (false, UserLimits.ActivePollsMessage(limits.ActivePolls));
            }
        }
        a.Poll.ClosedAt = closed ? a.Poll.ClosedAt ?? now : null;
        // ClosedNotifiedAt stays: the next close is mailed at most once a day (PollSweepService.NotifyDue, R2-POLLS-3).
        a.Poll.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        notifier.Changed(a.Poll.Id);
        return (true, null);
    }

    /// <summary>
    /// Creates max(<paramref name="count"/>, aliases.Count) single-person tokens. Aliases are assigned
    /// in order. The plain tokens are returned once and never stored.
    /// </summary>
    public async Task<IReadOnlyList<GeneratedInvite>?> GenerateInvitesAsync(string userId, string encodedId, int count,
        IReadOnlyList<string> aliases, CancellationToken ct = default)
    {
        var (invites, error) = await GenerateInvitesCheckedAsync(userId, encodedId, count, aliases, ct);
        return error is not null ? [] : invites;
    }

    /// <summary>
    /// Like <see cref="GenerateInvitesAsync"/>, but reports a refusal by the per-poll invite limit
    /// (existing tokens plus this batch; revoked tokens do not count). Invites null and no error = not found.
    /// </summary>
    public async Task<(IReadOnlyList<GeneratedInvite>? Invites, string? Error)> GenerateInvitesCheckedAsync(string userId, string encodedId, int count,
        IReadOnlyList<string> aliases, CancellationToken ct = default)
    {
        // An alias is copied onto the link's responses, so it obeys the voter name limit (cut, not refused: a long list).
        var maxAlias = Telrang.Web.Features.Voting.BallotService.NameLimit(settings.Current.Limits);
        var cleanAliases = aliases.Select(l => l.Trim()).Where(l => l.Length > 0)
            .Select(l => l.Length > maxAlias ? l[..maxAlias].TrimEnd() : l).ToList();
        var total = Math.Clamp(Math.Max(count, cleanAliases.Count), 0, MaxInvitesPerBatch);
        if (total == 0) return ([], null);

        using var serialised = await OwnerLock.AcquireAsync(userId, ct); // count and insert as one step (R2-POLLS-5)
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await AccountGate.MayActAsync(db, userId, ct)) return (null, null);
        var a = await access.GetAsync(db, encodedId, VisitorContext.ForUser(userId), tracking: false, ct);
        if (a is null || !a.CanEdit) return (null, null);

        var limits = await UserLimits.ForUserAsync(db, settings.Current.Limits, a.Poll.OwnerId, time.GetUtcNow(), ct);
        if (limits.InvitesPerPoll > 0)
        {
            var existing = await db.InviteTokens.CountAsync(t => t.PollId == a.Poll.Id, ct);
            if (existing + total > limits.InvitesPerPoll)
            {
                abuse.Record(client.Key, userId, AbuseSignal.LimitRefused);
                return (null, UserLimits.InvitesMessage(limits.InvitesPerPoll, Math.Max(0, limits.InvitesPerPoll - existing)));
            }
        }

        var now = time.GetUtcNow();
        var result = new List<GeneratedInvite>(total);
        for (var i = 0; i < total; i++)
        {
            var (token, hash) = InviteTokens.Generate();
            var alias = i < cleanAliases.Count ? cleanAliases[i] : null;
            db.InviteTokens.Add(new InviteToken { Id = Guid.NewGuid(), PollId = a.Poll.Id, TokenHash = hash, Alias = alias, CreatedAt = now });
            result.Add(new GeneratedInvite(alias, token));
        }
        await db.SaveChangesAsync(ct);
        return (result, null);
    }

    public async Task<IReadOnlyList<InviteSummary>?> ListInvitesAsync(string userId, string encodedId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var a = await access.GetAsync(db, encodedId, VisitorContext.ForUser(userId), tracking: false, ct);
        if (a is null || !a.CanEdit) return null;
        var rows = await db.InviteTokens.AsNoTracking()
            .Where(t => t.PollId == a.Poll.Id)
            .Select(t => new { t.Id, t.Alias, t.CreatedAt, Used = t.BallotId != null || t.ClaimedByUserId != null })
            .ToListAsync(ct);
        return rows.Select(r => new InviteSummary(r.Id, r.Alias, r.CreatedAt, r.Used))
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Alias, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Every response of the poll for its owner, oldest first. Null when not found or not yours.</summary>
    public async Task<IReadOnlyList<ResponseSummary>?> ListResponsesAsync(string userId, string encodedId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var a = await access.GetAsync(db, encodedId, VisitorContext.ForUser(userId), tracking: false, ct);
        if (a is null || !a.CanEdit) return null;
        var rows = await db.Ballots.AsNoTracking().Where(b => b.PollId == a.Poll.Id)
            .Select(b => new ResponseSummary(b.Id, b.DisplayName, b.Alias, b.SubmittedAt, b.UpdatedAt))
            .ToListAsync(ct);
        // DateTimeOffset ordering in memory: SQLite cannot ORDER BY it.
        return rows.OrderBy(r => r.SubmittedAt).ThenBy(r => r.Id).ToList();
    }

    /// <summary>
    /// Sets or clears (<paramref name="alias"/> blank) the owner-only alias of one response (DESIGN.md §20.3), in any
    /// voter mode and also after close. The owner's value wins: later submits never touch it.
    /// </summary>
    public async Task<(AliasStatus Status, string? Error)> SetResponseAliasAsync(string userId, string encodedId, Guid ballotId, string? alias,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await AccountGate.MayActAsync(db, userId, ct)) return (AliasStatus.NotFound, null);
        var a = await access.GetAsync(db, encodedId, VisitorContext.ForUser(userId), tracking: false, ct);
        if (a is null || !a.CanEdit) return (AliasStatus.NotFound, null);
        var value = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();
        var max = Telrang.Web.Features.Voting.BallotService.NameLimit(settings.Current.Limits);
        if (value is { Length: var n } && n > max) return (AliasStatus.Invalid, $"Shorten the alias to {max} characters.");
        var pollId = a.Poll.Id;
        var updated = await db.Ballots.Where(b => b.PollId == pollId && b.Id == ballotId)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Alias, value), ct);
        if (updated == 1) notifier.Changed(pollId);
        return updated == 1 ? (AliasStatus.Ok, null) : (AliasStatus.NotFound, null);
    }

    /// <summary>
    /// Revokes an invite token: the link stops working. A used (or claimed) token is only revoked with
    /// <paramref name="includeUsed"/> (the share page asks first); its ballot stays and still counts,
    /// but nobody can change it any more: a claiming account's responses go back under the token's
    /// identity (the account stays recorded on them), so a later invitation of the same account starts fresh.
    /// False if not found, not yours, or used without the flag.
    /// </summary>
    public async Task<bool> RevokeInviteAsync(string userId, string encodedId, Guid tokenId, bool includeUsed = false, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginWriteAsync(ct);
        if (!await AccountGate.MayActAsync(db, userId, ct)) return false;
        var a = await access.GetAsync(db, encodedId, VisitorContext.ForUser(userId), tracking: false, ct);
        if (a is null || !a.CanEdit) return false;
        var pollId = a.Poll.Id;
        var token = await db.InviteTokens.AsNoTracking().Where(t => t.PollId == pollId && t.Id == tokenId)
            .Select(t => new { t.TokenHash, t.ClaimedByUserId, Used = t.BallotId != null || t.ClaimedByUserId != null })
            .SingleOrDefaultAsync(ct);
        if (token is null || (token.Used && !includeUsed)) return false;
        if (token.ClaimedByUserId is { } claimant)
        {
            var from = VoterIdentity.ForAccount(claimant).Stored;
            var orphan = VoterIdentity.ForInvite(token.TokenHash);
            // The account loses the poll: no results-ready mail either (the sweep re-checks access too).
            await db.Ballots.Where(b => b.PollId == pollId && b.VoterIdentity == from)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.VoterIdentity, orphan.Stored).SetProperty(b => b.VoterKey, orphan.VoterKey)
                    .SetProperty(b => b.NotifyOnClose, false), ct);
        }
        await db.InviteTokens.Where(t => t.PollId == pollId && t.Id == tokenId).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        notifier.Changed(pollId);
        return true;
    }

    /// <summary>Deletes the poll with all its ballots and invite tokens. False if not found or not yours.</summary>
    public async Task<bool> DeletePollAsync(string userId, string encodedId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginWriteAsync(ct);
        if (!await AccountGate.MayActAsync(db, userId, ct)) return false;
        var a = await access.GetAsync(db, encodedId, VisitorContext.ForUser(userId), tracking: false, ct);
        if (a is null || !a.CanEdit) return false;
        await DeleteCascadeAsync(db, [a.Poll.Id], ct);
        await tx.CommitAsync(ct);
        notifier.End(a.Poll.Id);
        return true;
    }

    /// <summary>
    /// Deletes polls and everything hanging off them, explicitly (not relying on the provider's FK
    /// cascade). Call inside the caller's transaction.
    /// </summary>
    public static async Task<int> DeleteCascadeAsync(ApplicationDbContext db, IReadOnlyCollection<Guid> pollIds, CancellationToken ct)
    {
        if (pollIds.Count == 0) return 0;
        await db.PollChanges.Where(c => pollIds.Contains(c.PollId)).ExecuteDeleteAsync(ct);
        await db.InviteTokens.Where(t => pollIds.Contains(t.PollId)).ExecuteDeleteAsync(ct);
        await db.Ballots.Where(b => pollIds.Contains(b.PollId)).ExecuteDeleteAsync(ct);
        return await db.Polls.Where(p => pollIds.Contains(p.Id)).ExecuteDeleteAsync(ct);
    }

    /// <summary>The owner changed the close date: added, removed or moved (or, for definitions before §26, its time zone).</summary>
    public static bool CloseDateChanged(PollDefinition stored, PollDefinition updated) =>
        stored.ClosesOn != updated.ClosesOn || (updated.ClosesOn is not null && stored.TimeZone != updated.TimeZone);

    /// <summary>
    /// The close instant (DESIGN.md §13, audit fixes): the end of the close day in the poll's zone, else creation
    /// + <paramref name="maxDurationDays"/> (0 = no auto-close). Called at creation and when the close date changes.
    /// </summary>
    private static void SetClose(Poll poll, PollDefinition def, int maxDurationDays) =>
        (poll.ClosesAt, poll.AutoClose) = CloseFor(poll.CreatedAt, def, maxDurationDays);

    private static (DateTimeOffset? ClosesAt, bool AutoClose) CloseFor(DateTimeOffset createdAt, PollDefinition def, int maxDurationDays)
    {
        var closesAt = def.ClosesOn is { } day ? PollTime.CloseInstant(day, def.TimeZone) : PollState.AutoCloseAt(createdAt, maxDurationDays);
        return (closesAt, def.ClosesOn is null && closesAt is not null);
    }

    private static void Apply(Poll poll, PollDefinition def)
    {
        poll.DefinitionJson = PollJson.Serialize(def);
        poll.Title = def.Title.Length > 500 ? def.Title[..500] : def.Title;
        poll.Type = def.Questions[0].Type;
        poll.Scoring = def.Questions[0].Scoring;
        poll.ResultsVisibility = def.Results;
        poll.VoterMode = def.Voters;
    }
}
