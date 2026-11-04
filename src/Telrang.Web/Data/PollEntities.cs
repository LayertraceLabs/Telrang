using Telrang.Core.Polls;

namespace Telrang.Web.Data;

/// <summary>
/// A poll. <see cref="DefinitionJson"/> (the validated <see cref="PollDefinition"/>) is the only stored form; the editor
/// works on <c>PollDraft.FromDefinition</c> of it. Title/Type/Scoring/ResultsVisibility/VoterMode are denormalised from it
/// (Type and Scoring: the first question's) for lists and mails; ClosesAt is derived from it at creation. Never query this
/// by id outside <c>PollAccessService</c>.
/// </summary>
public class Poll
{
    public Guid Id { get; set; }
    public required string OwnerId { get; set; }
    public ApplicationUser? Owner { get; set; }

    public required string Title { get; set; }
    public PollType Type { get; set; }
    public ScoringScheme Scoring { get; set; }
    public required string DefinitionJson { get; set; }

    public ResultsVisibility ResultsVisibility { get; set; }
    public VoterMode VoterMode { get; set; }
    /// <summary>
    /// The close instant, frozen at creation (see <c>PollState</c>): the end of the "Closes on" day in the poll's zone,
    /// or creation + the owner's max duration (<see cref="AutoClose"/>), or null (no auto-close). An edit recomputes it
    /// only when the owner changes the close date (or the time zone together with it).
    /// </summary>
    public DateTimeOffset? ClosesAt { get; set; }

    /// <summary><see cref="ClosesAt"/> is the automatic close (no "Closes on" date): pages say "Closes automatically on …".</summary>
    public bool AutoClose { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Set when the owner closes the poll manually.</summary>
    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>
    /// When the results-ready sweep (<c>PollSweepService</c>) last handled this poll's close; null = never. Reopening
    /// keeps it: a later close is mailed only when it is a day old (<c>PollSweepService.NotifyDue</c>, R2-POLLS-3).
    /// </summary>
    public DateTimeOffset? ClosedNotifiedAt { get; set; }

    public List<Ballot> Ballots { get; set; } = [];
    public List<InviteToken> InviteTokens { get; set; } = [];
    public List<PollChange> Changes { get; set; } = [];
}

/// <summary>
/// One response. With <c>responses: one</c> there is one row per voter identity per poll (revising
/// replaces <see cref="PayloadJson"/> and bumps <see cref="UpdatedAt"/>); with <c>responses: many</c>
/// each response is its own row, numbered by <see cref="ResponseNo"/> per identity.
/// </summary>
public class Ballot
{
    public const string UserIdentityPrefix = "u:";
    public const string KeyIdentityPrefix = "k:";

    public Guid Id { get; set; }
    public Guid PollId { get; set; }
    public Poll? Poll { get; set; }

    /// <summary>
    /// Anonymous public-mode voters: base64url(HMAC-SHA256(secret, vk cookie || poll id)) (see VoterKeys),
    /// so one browser cannot be correlated across polls. Unclaimed invite tokens: "invite:" + token hash.
    /// Null for voters identified by account. Never shown in UI.
    /// </summary>
    public string? VoterKey { get; set; }

    /// <summary>
    /// The account this response belongs to (identity "u:" + id): signed-in voters in every mode, including
    /// invite ballots whose token the account claimed. Null for cookie and unclaimed-token ballots. Owners
    /// see names, never this id.
    /// </summary>
    public string? UserId { get; set; }
    public ApplicationUser? User { get; set; }

    /// <summary>
    /// Who this response belongs to: "u:" + <see cref="UserId"/> or "k:" + <see cref="VoterKey"/>, filled
    /// by BallotService (a plain column, not a computed one, to stay provider-neutral). Unique with
    /// <see cref="PollId"/> and <see cref="ResponseNo"/>.
    /// </summary>
    public required string VoterIdentity { get; set; }

    /// <summary>1-based per voter identity; always 1 under <c>responses: one</c>.</summary>
    public int ResponseNo { get; set; } = 1;

    /// <summary>The name the voter entered (<c>names:</c>), as entered.</summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Owner-only name for this response (DESIGN.md §20.3): copied from the invite link's alias when the response is
    /// created, then set or cleared only by the owner. Owner views and the CSV prefer it over <see cref="DisplayName"/>;
    /// voters never see it.
    /// </summary>
    public string? Alias { get; set; }

    public DateTimeOffset SubmittedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Serialized RankedBallot or PointsBallot (see BallotJson).</summary>
    public required string PayloadJson { get; set; }

    /// <summary>
    /// "Email me when this poll closes": only ever true on account ballots of a verified address, set by
    /// BallotService; cleared by the unsubscribe link (<c>/notify/off/{token}</c>).
    /// </summary>
    public bool NotifyOnClose { get; set; }

    public static string IdentityForUser(string userId) => UserIdentityPrefix + userId;
    public static string IdentityForKey(string voterKey) => KeyIdentityPrefix + voterKey;
}

/// <summary>Single-person invite link. Only the SHA-256 of the token is stored.</summary>
public class InviteToken
{
    public Guid Id { get; set; }
    public Guid PollId { get; set; }
    public Poll? Poll { get; set; }

    /// <summary>base64url(SHA-256(token bytes)).</summary>
    public required string TokenHash { get; set; }

    /// <summary>Owner-only name of the invitee; copied onto the token's new responses as <see cref="Ballot.Alias"/>.</summary>
    public string? Alias { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Set when a ballot was submitted with this token.</summary>
    public Guid? BallotId { get; set; }
    public Ballot? Ballot { get; set; }

    /// <summary>
    /// The account that first used this token while signed in (DESIGN.md §16). From then on the token's
    /// ballots belong to that account and the token works for nobody else.
    /// </summary>
    public string? ClaimedByUserId { get; set; }
}

/// <summary>
/// One entry of a poll's change log (DESIGN.md §26, §31): an option added, removed, renamed or given new details, or the
/// title or description changed, after the poll had its first ballot. Written by <c>PollService</c> in the edit's
/// transaction, shown to everyone who can view the poll, never edited or hidden. At most <see cref="MaxEntries"/> per poll:
/// older ones are merged into one <see cref="OptionChangeKind.Earlier"/> entry. Deleted with the poll, kept on transfer,
/// never copied.
/// </summary>
public class PollChange
{
    /// <summary>Labels are stored up to the highest option label limit an admin can set.</summary>
    public const int MaxLabelLength = 200;

    /// <summary>Most entries kept per poll, the merged "n earlier changes" entry included (DESIGN.md §31).</summary>
    public const int MaxEntries = 500;

    /// <summary>Most entries the vote and results pages render (the newest).</summary>
    public const int ShownEntries = 50;

    public Guid Id { get; set; }
    public Guid PollId { get; set; }
    public Poll? Poll { get; set; }

    /// <summary>When the edit was saved; all entries of one edit share it.</summary>
    public DateTimeOffset At { get; set; }

    /// <summary>Order within one edit.</summary>
    public int Seq { get; set; }
    public OptionChangeKind Kind { get; set; }

    /// <summary>The option's label after the change (removed: its last label).</summary>
    public required string Label { get; set; }

    /// <summary>Renamed: the label before.</summary>
    public string? OldLabel { get; set; }

    /// <summary>Removed: how many stored ballots answered the option (ranked or banned, points above 0, yes or maybe).
    /// Earlier: how many entries were merged into this one.</summary>
    public int? BallotCount { get; set; }
}

/// <summary>
/// One poll creation, for the "new polls per day" limit (deleting a poll does not give the creation back).
/// Rows older than 48 h are purged by the next creation; no background job.
/// </summary>
public class PollCreation
{
    public Guid Id { get; set; }
    public required string UserId { get; set; }
    public DateTimeOffset At { get; set; }
}
