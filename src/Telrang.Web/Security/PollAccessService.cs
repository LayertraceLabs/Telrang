using Telrang.Core.Ids;
using Telrang.Core.Polls;
using Telrang.Web.Data;
using Telrang.Web.Features.Polls;
using Microsoft.EntityFrameworkCore;

namespace Telrang.Web.Security;

public enum AccessDenial
{
    None,
    Closed,
    /// <summary>Invite-only poll and no invite token was presented.</summary>
    InviteRequired,
    /// <summary>The presented invite token does not belong to this poll.</summary>
    InviteUnknown,
    /// <summary>The presented invite token was claimed by an account the visitor is not signed in as.</summary>
    InviteClaimed,
    /// <summary>Public mode, anonymous, and the browser did not send the vk voter cookie back (cookies blocked).</summary>
    NoVoterIdentity,
    /// <summary><c>voters: accounts</c> and the visitor is not signed in.</summary>
    SignInRequired,
    /// <summary><c>voters: accounts</c> and the signed-in account has no verified email (or is gated/deleted).</summary>
    EmailNotVerified,
}

public enum VoterKind
{
    /// <summary>Signed-in account (every voter mode). Key = user id. Stored server-side, works on every browser.</summary>
    Account,
    /// <summary>Anonymous public-mode voter. Key = per-poll HMAC of the <c>vk</c> cookie (see <see cref="VoterKeys"/>).</summary>
    Cookie,
    /// <summary>Invite token not claimed by any account. Key = token hash.</summary>
    Invite,
}

/// <summary>
/// Who the visitor responds as in one poll (DESIGN.md §16), resolved only by <see cref="PollAccessService"/>.
/// Everything else derives the stored columns from it instead of branching on user id vs voter key.
/// Never shown in UI.
/// </summary>
public sealed record VoterIdentity(VoterKind Kind, string Key)
{
    public static VoterIdentity ForAccount(string userId) => new(VoterKind.Account, userId);
    public static VoterIdentity ForCookie(string perPollKey) => new(VoterKind.Cookie, perPollKey);
    public static VoterIdentity ForInvite(string tokenHash) => new(VoterKind.Invite, tokenHash);

    /// <summary><see cref="Ballot.VoterIdentity"/>: "u:" + user id, or "k:" + <see cref="VoterKey"/>.</summary>
    public string Stored => Kind == VoterKind.Account ? Ballot.IdentityForUser(Key) : Ballot.IdentityForKey(VoterKey!);

    /// <summary><see cref="Ballot.VoterKey"/>: the cookie key, "invite:" + token hash, or null for an account.</summary>
    public string? VoterKey => Kind switch
    {
        VoterKind.Cookie => Key,
        VoterKind.Invite => PollAccessService.InviteVoterPrefix + Key,
        _ => null,
    };

    /// <summary><see cref="Ballot.UserId"/>: set only for account identities.</summary>
    public string? UserId => Kind == VoterKind.Account ? Key : null;
}

/// <summary>Everything a page or service may do with one poll, decided in one place.</summary>
/// <param name="Poll">The poll row. Detached unless loaded through a tracking overload.</param>
/// <param name="Definition">Deserialised <see cref="Poll.DefinitionJson"/>.</param>
/// <param name="Voter">Who this visitor responds as, or null when they cannot respond. Never show it in UI.</param>
/// <param name="HasBallot">The visitor (as <paramref name="Voter"/>) has at least one submitted response.</param>
/// <param name="ResponseCount">Number of responses the visitor (as <paramref name="Voter"/>) has in this poll.</param>
/// <param name="Denial">Why the first missing capability (view, then vote) is missing.</param>
public sealed record PollAccess(
    Poll Poll,
    PollDefinition Definition,
    string EncodedId,
    bool IsOwner,
    bool CanView,
    bool CanVote,
    bool CanSeeResults,
    bool CanEdit,
    VoterIdentity? Voter,
    bool HasBallot,
    bool IsClosed,
    AccessDenial Denial,
    string? ReasonIfDenied)
{
    public int ResponseCount { get; init; }

    /// <summary>
    /// The voter-facing ballot rows (DESIGN.md §25: name as entered and answers, never the alias): the results' audience
    /// and timing, the poll shows ballots and asks for names. The owner's own views (grid, CSV) do not depend on it.
    /// </summary>
    public bool CanSeeBallots { get; init; }
}

/// <summary>Who sees results and ballot rows (DESIGN.md §25), from the definition alone.</summary>
public static class ResultsRules
{
    /// <summary>
    /// The owner always, live. Otherwise the audience (voters: the visitor's identity has a ballot, the same identity as
    /// voting, so a claimed invite is the account; anyone: everyone who can view, public polls only) and the timing
    /// (after closing: only once <c>PollState.IsClosed</c>).
    /// </summary>
    public static bool CanSeeResults(PollDefinition def, bool isOwner, bool hasBallot, bool isClosed)
    {
        if (isOwner) return true;
        var audience = def.Results switch
        {
            ResultsVisibility.Open => def.Voters == VoterMode.Public,
            ResultsVisibility.AfterVote => hasBallot,
            _ => false,
        };
        return audience && (def.ResultsTiming == ResultsTiming.WhileOpen || isClosed);
    }

    public static bool CanSeeBallots(PollDefinition def, bool isOwner, bool hasBallot, bool isClosed) =>
        def.ShowBallots && def.Names != NamesMode.None && def.Results != ResultsVisibility.Creator
        && CanSeeResults(def, isOwner, hasBallot, isClosed);
}

/// <summary>
/// The single entry point for loading a poll. Pages and services never query <see cref="Poll"/>
/// by id themselves. A null result means "not found" (also for undecodable ids): callers answer
/// 404, never 403, so existence does not leak.
/// </summary>
public sealed class PollAccessService(IDbContextFactory<ApplicationDbContext> dbFactory, TimeProvider time, VoterKeys voterKeys)
{
    public const string InviteVoterPrefix = "invite:";
    public const string InviteClaimedMessage = "This invitation is linked to another account. Sign in.";
    /// <summary>The same refusal for a visitor already signed in, as an account that is not the one the link belongs to.</summary>
    public const string InviteClaimedSignedInMessage = "This invitation is linked to another account.";

    /// <summary>Read-only access check on its own short-lived DbContext.</summary>
    public async Task<PollAccess?> GetAsync(string? encodedId, VisitorContext visitor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await GetAsync(db, encodedId, visitor, tracking: false, ct);
    }

    /// <summary>
    /// Access check inside a caller's unit of work. With <paramref name="tracking"/> the returned
    /// <see cref="PollAccess.Poll"/> is tracked by <paramref name="db"/> so the caller can modify it
    /// after checking the capability it needs.
    /// </summary>
    public async Task<PollAccess?> GetAsync(ApplicationDbContext db, string? encodedId, VisitorContext visitor, bool tracking, CancellationToken ct = default)
    {
        if (!PollId.TryDecode(encodedId, out var id)) return null;
        var query = tracking ? db.Polls : db.Polls.AsNoTracking();
        var poll = await query.SingleOrDefaultAsync(p => p.Id == id, ct);
        if (poll is null) return null;

        var definition = PollJson.DeserializeDefinition(poll.DefinitionJson);
        var now = time.GetUtcNow();

        var isOwner = visitor.UserId is not null && visitor.UserId == poll.OwnerId;
        var isClosed = PollState.IsClosed(poll, now);

        var (voter, denial) = await ResolveVoterAsync(db, poll, visitor, ct);

        var stored = voter?.Stored;
        var responseCount = voter is null ? 0 : await db.Ballots.CountAsync(b => b.PollId == poll.Id && b.VoterIdentity == stored, ct);
        var hasBallot = responseCount > 0;
        var canVote = !isClosed && voter is not null;
        // Never overwrite an earlier denial (callers rely on e.g. InviteUnknown to answer 404), except
        // a missing voter cookie: on a closed poll "closed" is the reason that matters.
        if (isClosed && denial is AccessDenial.None or AccessDenial.NoVoterIdentity or AccessDenial.SignInRequired
                or AccessDenial.EmailNotVerified)
            denial = AccessDenial.Closed;

        var canSeeResults = ResultsRules.CanSeeResults(definition, isOwner, hasBallot, isClosed);

        return new PollAccess(poll, definition, PollId.Encode(poll.Id), isOwner,
            CanView: true, // a poll link is a capability (DESIGN.md §15): whoever has the id may see the poll
            canVote,
            canSeeResults, CanEdit: isOwner, voter, hasBallot, isClosed, denial, Describe(denial, visitor.UserId is not null))
        {
            ResponseCount = responseCount,
            CanSeeBallots = ResultsRules.CanSeeBallots(definition, isOwner, hasBallot, isClosed),
        };
    }

    /// <summary>
    /// The one place that decides who the visitor responds as (DESIGN.md §16). Signed in: the account, in every
    /// mode; in invite mode through a token the account claimed, or an unclaimed token it presents (claimed on
    /// its first write, see BallotService). Anonymous: the <c>vk</c> cookie (public) or an unclaimed token (invite).
    /// </summary>
    private async Task<(VoterIdentity? Voter, AccessDenial Denial)> ResolveVoterAsync(ApplicationDbContext db, Poll poll,
        VisitorContext visitor, CancellationToken ct)
    {
        var uid = visitor.UserId;
        switch (poll.VoterMode)
        {
            case VoterMode.Accounts:
                if (uid is null) return (null, AccessDenial.SignInRequired);
                return await AccountGate.MayActAsync(db, uid, ct, verifiedEmail: true) ? (VoterIdentity.ForAccount(uid), AccessDenial.None) : (null, AccessDenial.EmailNotVerified);

            case VoterMode.Invite:
                if (uid is not null && await db.InviteTokens.AnyAsync(t => t.PollId == poll.Id && t.ClaimedByUserId == uid, ct))
                    return (VoterIdentity.ForAccount(uid), AccessDenial.None);
                if (visitor.InviteTokenHash is not { } hash) return (null, AccessDenial.InviteRequired);
                var token = await db.InviteTokens.Where(t => t.PollId == poll.Id && t.TokenHash == hash)
                    .Select(t => new { t.ClaimedByUserId }).SingleOrDefaultAsync(ct);
                if (token is null) return (null, AccessDenial.InviteUnknown);
                // Claimed by an account, and the visitor is not it (signed out, or another account).
                if (token.ClaimedByUserId is not null) return (null, AccessDenial.InviteClaimed);
                return (VoterIdentity.ForInvite(hash), AccessDenial.None);

            default:
                if (uid is not null) return (VoterIdentity.ForAccount(uid), AccessDenial.None);
                // Per-poll derived key, never the raw cookie value (see VoterKeys).
                return visitor.LinkVoterKey is { } vk
                    ? (VoterIdentity.ForCookie(voterKeys.ForPoll(vk, poll.Id)), AccessDenial.None)
                    : (null, AccessDenial.NoVoterIdentity);
        }
    }

    /// <summary>
    /// Admin-only lookup by encoded id, regardless of owner. The caller must already have checked the
    /// Admin policy (AdminService does). Null = not found.
    /// </summary>
    public async Task<Poll?> GetForAdminAsync(ApplicationDbContext db, string? encodedId, bool tracking, CancellationToken ct = default)
    {
        if (!PollId.TryDecode(encodedId, out var id)) return null;
        var query = tracking ? db.Polls : db.Polls.AsNoTracking();
        return await query.SingleOrDefaultAsync(p => p.Id == id, ct);
    }

    private static string? Describe(AccessDenial denial, bool signedIn) => denial switch
    {
        AccessDenial.None => null,
        AccessDenial.Closed => "Poll closed.",
        AccessDenial.InviteRequired => "This poll is invite-only. Open your personal invite link to vote.",
        AccessDenial.InviteUnknown => "This invite link is not valid for this poll.",
        AccessDenial.InviteClaimed => signedIn ? InviteClaimedSignedInMessage : InviteClaimedMessage,
        AccessDenial.NoVoterIdentity => "Cookies are required to vote.",
        AccessDenial.SignInRequired => "Sign in to respond.",
        AccessDenial.EmailNotVerified => "Verify your email to respond.",
        _ => null,
    };
}
