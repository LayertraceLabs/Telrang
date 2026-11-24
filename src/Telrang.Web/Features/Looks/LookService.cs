using Telrang.Web.Data;
using Telrang.Web.Features.Branding;
using Telrang.Web.Features.Polls;
using Telrang.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Telrang.Web.Features.Looks;

public sealed record SavedLookItem(Guid Id, string Name, ThemeLook Look, DateTimeOffset UpdatedAt);

public sealed record SavedLookList(IReadOnlyList<SavedLookItem> Looks, Guid? DefaultId)
{
    public static readonly SavedLookList Empty = new([], null);
}

/// <param name="Id">The look saved or changed.</param>
public sealed record LookOutcome(bool Ok, string? Error, Guid? Id = null)
{
    public static LookOutcome Fail(string error) => new(false, error);
}

/// <summary>What an import resolves to: a look and the name it is offered under, or one line saying why not.</summary>
public sealed record LookImport(ThemeLook? Look, string? Name, string? Error);

/// <summary>
/// Saved looks (DESIGN.md §34.7): an account's named looks, its default look for new polls, and import from a poll link or a
/// look code. Every method takes the caller's user id and touches only that account's rows; a look of another account is
/// "not found". The per-account limit is checked with the insert under <see cref="OwnerLock"/>. Polls hold copies: nothing
/// here changes a poll.
/// </summary>
public sealed class LookService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    PollAccessService access,
    LookStyles styles,
    TimeProvider time)
{
    public const string NotFoundMessage = "Look not found.";
    public const string PollNotFoundMessage = "Poll not found.";
    public const string NoLookMessage = "That poll has no look.";
    public const string NameMessage = "Name: enter one.";
    public static string NameLengthMessage => $"Name: {SavedLook.MaxNameLength} characters at most.";
    public const string ImportedName = "Imported look";

    /// <summary>Longest import input read (a poll link with the site's address, or a look code).</summary>
    public const int MaxImportLength = 2048;

    public async Task<SavedLookList> ListAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.SavedLooks.AsNoTracking().Where(l => l.OwnerId == userId)
            .Select(l => new { l.Id, l.Name, l.LookJson, l.CreatedAt, l.UpdatedAt }).ToListAsync(ct);
        var defaultId = await db.Users.Where(u => u.Id == userId).Select(u => u.DefaultLookId).SingleOrDefaultAsync(ct);
        var looks = rows.OrderBy(r => r.CreatedAt)
            .Select(r => new SavedLookItem(r.Id, r.Name, ThemeLook.FromStored(r.LookJson) ?? ThemeLook.BuiltIn, r.UpdatedAt)).ToList();
        return new SavedLookList(looks, looks.Any(l => l.Id == defaultId) ? defaultId : null);
    }

    /// <summary>The look new polls start with (DESIGN.md §34.7), or null for the instance theme.</summary>
    public async Task<ThemeLook?> DefaultLookAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var defaultId = await db.Users.Where(u => u.Id == userId).Select(u => u.DefaultLookId).SingleOrDefaultAsync(ct);
        if (defaultId is not { } id) return null;
        var json = await db.SavedLooks.Where(l => l.Id == id && l.OwnerId == userId).Select(l => l.LookJson).SingleOrDefaultAsync(ct);
        return ThemeLook.FromStored(json);
    }

    /// <summary>"Name: enter one." and so on, or null; the name trimmed.</summary>
    public static (string? Name, string? Error) CheckName(string? name)
    {
        var n = name?.Trim() ?? "";
        if (n.Length == 0) return (null, NameMessage);
        if (n.Length > SavedLook.MaxNameLength) return (null, NameLengthMessage);
        if (n.Any(char.IsControl)) return (null, NameMessage);
        return (n, null);
    }

    public async Task<LookOutcome> CreateAsync(string userId, string? name, ThemeLook look, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(look);
        var (n, problem) = CheckName(name);
        if (problem is not null) return LookOutcome.Fail(problem);
        using var serialised = await OwnerLock.AcquireAsync(userId, ct); // count and insert as one step
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await AccountGate.MayActAsync(db, userId, ct)) return LookOutcome.Fail(Voting.BallotService.SetupRequiredMessage);
        if (await db.SavedLooks.CountAsync(l => l.OwnerId == userId, ct) >= UserLimits.SavedLooksPerUser)
            return LookOutcome.Fail(UserLimits.SavedLooksMessage(UserLimits.SavedLooksPerUser));
        var now = time.GetUtcNow();
        var row = new SavedLook { Id = Guid.NewGuid(), OwnerId = userId, Name = n!, LookJson = look.ToJson(), CreatedAt = now, UpdatedAt = now };
        db.SavedLooks.Add(row);
        await db.SaveChangesAsync(ct);
        return new LookOutcome(true, null, row.Id);
    }

    /// <summary>Renames the look and replaces its colours, font and radius. Polls made from it keep theirs.</summary>
    public async Task<LookOutcome> UpdateAsync(string userId, Guid id, string? name, ThemeLook look, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(look);
        var (n, problem) = CheckName(name);
        if (problem is not null) return LookOutcome.Fail(problem);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await AccountGate.MayActAsync(db, userId, ct)) return LookOutcome.Fail(Voting.BallotService.SetupRequiredMessage);
        var row = await db.SavedLooks.SingleOrDefaultAsync(l => l.Id == id && l.OwnerId == userId, ct);
        if (row is null) return LookOutcome.Fail(NotFoundMessage);
        (row.Name, row.LookJson, row.UpdatedAt) = (n!, look.ToJson(), time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return new LookOutcome(true, null, id);
    }

    /// <summary>Deletes the look; when it was the default, new polls start with the instance theme again.</summary>
    public async Task<LookOutcome> DeleteAsync(string userId, Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginWriteAsync(ct);
        var deleted = await db.SavedLooks.Where(l => l.Id == id && l.OwnerId == userId).ExecuteDeleteAsync(ct);
        if (deleted == 0) return LookOutcome.Fail(NotFoundMessage);
        await db.Users.Where(u => u.Id == userId && u.DefaultLookId == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.DefaultLookId, (Guid?)null), ct);
        await tx.CommitAsync(ct);
        return new LookOutcome(true, null, id);
    }

    /// <summary>"Use as default" (null = the instance theme). Only one of the account's own looks.</summary>
    public async Task<LookOutcome> SetDefaultAsync(string userId, Guid? id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginWriteAsync(ct);
        if (id is { } lookId && !await db.SavedLooks.AnyAsync(l => l.Id == lookId && l.OwnerId == userId, ct))
            return LookOutcome.Fail(NotFoundMessage);
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.DefaultLookId, id), ct);
        await tx.CommitAsync(ct);
        return new LookOutcome(true, null, id);
    }

    /// <summary>
    /// What a pasted text imports (DESIGN.md §34.7): a look code (<c>tl1.</c>…, decoded and checked by the rules a form gets),
    /// or a poll link or id, resolved with <see cref="PollAccessService"/> for the importing account. A poll it may not view
    /// is "not found", exactly as an unknown one; a poll without a look (or while poll looks are off) has none to import.
    /// </summary>
    public async Task<LookImport> ResolveAsync(string userId, string? input, CancellationToken ct = default)
    {
        var text = input?.Trim() ?? "";
        if (text.Length == 0 || text.Length > MaxImportLength) return new LookImport(null, null, ThemeLook.NotALookMessage);
        if (text.StartsWith(ThemeLook.CodePrefix, StringComparison.Ordinal))
        {
            var (look, error) = ThemeLook.FromCode(text);
            return new LookImport(look, look is null ? null : ImportedName, error);
        }
        var a = await access.GetAsync(PollIdOf(text), VisitorContext.ForUser(userId), ct);
        if (a is null || !a.CanView) return new LookImport(null, null, PollNotFoundMessage);
        if (styles.Shown(a.Poll) is not { } shown) return new LookImport(null, null, NoLookMessage);
        var title = a.Definition.Title.Trim();
        return new LookImport(shown, title.Length == 0 ? ImportedName : title.Length > SavedLook.MaxNameLength ? title[..SavedLook.MaxNameLength].TrimEnd() : title, null);
    }

    /// <summary>Import as a new saved look; an empty name takes the one <see cref="ResolveAsync"/> offers.</summary>
    public async Task<LookOutcome> ImportAsync(string userId, string? input, string? name, CancellationToken ct = default)
    {
        var resolved = await ResolveAsync(userId, input, ct);
        if (resolved.Look is null) return LookOutcome.Fail(resolved.Error ?? ThemeLook.NotALookMessage);
        return await CreateAsync(userId, string.IsNullOrWhiteSpace(name) ? resolved.Name : name, resolved.Look, ct);
    }

    /// <summary>The poll id in a pasted link (<c>…/p/&lt;id&gt;…</c>) or the text itself.</summary>
    public static string PollIdOf(string text)
    {
        var at = text.IndexOf("/p/", StringComparison.Ordinal);
        if (at < 0) return text;
        var rest = text[(at + 3)..];
        var end = rest.IndexOfAny(['/', '?', '#']);
        return end < 0 ? rest : rest[..end];
    }
}
