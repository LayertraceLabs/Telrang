using Telrang.Core.Ballots;
using Telrang.Core.Ids;
using Telrang.Core.Polls;
using Telrang.Web.Data;
using Telrang.Web.Features.Polls;
using Telrang.Web.Features.Settings;
using Telrang.Web.Features.Status;
using Telrang.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Telrang.Web.Features.Voting;

public enum SubmitStatus { Ok, NotFound, Denied, Invalid }

/// <param name="Access">Access re-evaluated after the submit (e.g. AfterVote results become visible).</param>
public sealed record SubmitResult(SubmitStatus Status, IReadOnlyList<string> Errors, PollAccess? Access)
{
    public bool Succeeded => Status == SubmitStatus.Ok;

    /// <summary>The form id was used already by this voter: nothing was written, <see cref="ResponseNo"/> is that response.</summary>
    public bool Duplicate { get; init; }

    /// <summary>The response that was saved (or deleted).</summary>
    public int? ResponseNo { get; init; }
}

public sealed record MyBallot(int ResponseNo, RankedBallot? Ranked, PointsBallot? Points, string? DisplayName,
    DateTimeOffset SubmittedAt, DateTimeOffset UpdatedAt)
{
    /// <summary>Dates polls: the answers (null for the other types).</summary>
    public DatesBallot? Dates { get; init; }

    /// <summary>"Email me when this poll closes" is ticked on this response.</summary>
    public bool NotifyOnClose { get; init; }

    /// <summary>
    /// Every question's answer (DESIGN.md §34.3), with the voter's own free text decrypted; a question without an entry was
    /// not answered. <see cref="Ranked"/>, <see cref="Points"/> and <see cref="Dates"/> are the first question's.
    /// </summary>
    public IReadOnlyDictionary<string, QuestionAnswer> Answers { get; init; } = new Dictionary<string, QuestionAnswer>();
}

/// <summary>A signed-in voter's unfinished answers (DESIGN.md §34.4): the carry content and when it was saved.</summary>
public sealed record ResponseDraftState(CarryState State, DateTimeOffset SavedAt);

/// <param name="Responses">The visitor's own responses, by response number (at most one under <c>responses: one</c>).</param>
public sealed record VoteState(PollAccess Access, IReadOnlyList<MyBallot> Responses)
{
    /// <summary>The visitor's first response (the only one under <c>responses: one</c>).</summary>
    public MyBallot? Ballot => Responses.Count > 0 ? Responses[0] : null;

    public MyBallot? Find(int responseNo) => Responses.FirstOrDefault(r => r.ResponseNo == responseNo);

    /// <summary>The vote form shows "Email me when this poll closes" (see <see cref="BallotService.MayNotifyAsync"/>).</summary>
    public bool CanNotify { get; init; }
}

/// <summary>One poll on "My votes".</summary>
/// <param name="ResultsLink">The account can see the results now (<see cref="PollAccess.CanSeeResults"/>).</param>
/// <param name="Revoked">Invite poll whose invitation the owner withdrew: the ballot still counts and is the account's
/// data (export), but the account can no longer open it, so the page lists it without links.</param>
public sealed record MyVoteSummary(string EncodedId, string Title, PollType Type, bool IsClosed, int ResponseCount,
    DateTimeOffset LastUpdated, bool ResultsLink, bool Revoked = false, int QuestionCount = 1, int PageCount = 1, string? TimeZone = null)
{
    /// <summary>The type of a simple poll, the size of a structured one (DESIGN.md §34.1).</summary>
    public string Kind => PollText.KindLine(Type, QuestionCount, PageCount);
}

/// <summary>
/// What the vote form sends besides the ballot (DESIGN.md §14.7, §21.1): the honeypot field's value, the signed form
/// stamp (<see cref="FormGuard"/>, at least the minimum fill time and at most <see cref="BallotService.StampMaxAge"/> old),
/// the captcha token (only checked for anonymous voters in <c>voters: public</c> polls when a captcha is enabled) and the
/// form's single-use id (<see cref="VoteFormIds"/>). The vote page always passes one; null (in-process callers such as
/// tests) skips the form checks but not the captcha.
/// </summary>
public sealed record BallotGuard(string? Honeypot = null, string? CaptchaToken = null, string? Stamp = null, string? FormId = null);

/// <summary>Voter-side operations. Access is re-checked on every call; the server is authoritative.</summary>
public sealed class BallotService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    PollAccessService access,
    TimeProvider time,
    SettingsService settings,
    FormGuard formGuard,
    AbuseTracker abuse,
    ClientInfo client,
    ICaptchaVerifier captcha,
    AppMetrics metrics,
    VoteFormIds formIds,
    Results.PollChangeNotifier notifier,
    ResponseTextProtection textProtection,
    ILogger<BallotService> logger)
{
    /// <summary>Oldest vote form stamp accepted (DESIGN.md §21.1): a form left open over a working day still works.</summary>
    public static readonly TimeSpan StampMaxAge = VoteFormIds.Lifetime;
    public const string StampTooOldMessage = "The form was open too long. Submit it again.";

    public const string CaptchaMissingMessage = "Complete the check above the button.";
    public const string CaptchaFailedMessage = "The check failed. Try it again.";
    public const string CaptchaUnavailableMessage = "The check is unavailable. Try again in a few minutes.";

    /// <summary><c>Limits.VoterNameLength</c> is clamped to this range (default 60).</summary>
    public const int MinNameLimit = 10, MaxNameLimit = 120;

    /// <summary>The voter name limit in force (the admin setting, clamped to 10–120).</summary>
    public int MaxNameLength => NameLimit(settings.Current.Limits);

    public static int NameLimit(LimitSettings limits) => Math.Clamp(limits.VoterNameLength, MinNameLimit, MaxNameLimit);
    public const string SetupRequiredMessage = "Finish setting up your account first.";
    public const string ResponseGoneMessage = "That response no longer exists. Reload the page.";

    /// <summary>Access plus the visitor's own responses. Null = poll not found.</summary>
    public async Task<VoteState?> GetVoteStateAsync(string encodedId, VisitorContext visitor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var a = await access.GetAsync(db, encodedId, visitor, tracking: false, ct);
        if (a is null) return null;
        var canNotify = await MayNotifyAsync(db, a, visitor.UserId, ct);
        if (a.Voter is null || !a.HasBallot) return new VoteState(a, []) { CanNotify = canNotify };
        var identity = a.Voter.Stored;
        var rows = await db.Ballots.AsNoTracking()
            .Where(b => b.PollId == a.Poll.Id && b.VoterIdentity == identity)
            .OrderBy(b => b.ResponseNo)
            .ToListAsync(ct);
        return new VoteState(a, rows.Select(r => ToMyBallot(a.Definition, r, textProtection.RevealTexts(a.Poll.Id, r.TextCipher))).ToList())
        {
            CanNotify = canNotify,
        };
    }

    /// <summary>
    /// "Email me when this poll closes" is offered and stored only for a signed-in voter with a verified email,
    /// on a poll whose results voters can see (<c>after-vote</c> or <c>open</c>), while mail and the public URL
    /// (needed for the links) are configured. Anonymous ballots never carry it.
    /// </summary>
    public async Task<bool> MayNotifyAsync(ApplicationDbContext db, PollAccess a, string? userId, CancellationToken ct)
    {
        var s = settings.Current;
        if (userId is null || a.Voter is null || !s.Mail.IsConfigured || s.PublicUrl is null) return false;
        if (a.Definition.Results is not (ResultsVisibility.AfterVote or ResultsVisibility.Open)) return false;
        return await AccountGate.MayActAsync(db, userId, ct, verifiedEmail: true);
    }

    /// <param name="responseNo"><c>responses: many</c>: the response to revise, or null for a new one. Ignored under <c>one</c>.</param>
    /// <param name="notifyOnClose">"Email me when this poll closes"; stored only when <see cref="MayNotifyAsync"/> allows it.</param>
    public Task<SubmitResult> SubmitRankedAsync(string encodedId, VisitorContext visitor, RankedBallot ballot, string? displayName,
        int? responseNo = null, BallotGuard? guard = null, bool notifyOnClose = false, CancellationToken ct = default) =>
        SubmitFirstAsync(encodedId, visitor, new QuestionAnswer { Ranked = ballot }, displayName, responseNo, guard, notifyOnClose, ct);

    /// <param name="responseNo"><c>responses: many</c>: the response to revise, or null for a new one. Ignored under <c>one</c>.</param>
    /// <param name="notifyOnClose">"Email me when this poll closes"; stored only when <see cref="MayNotifyAsync"/> allows it.</param>
    public Task<SubmitResult> SubmitPointsAsync(string encodedId, VisitorContext visitor, PointsBallot ballot, string? displayName,
        int? responseNo = null, BallotGuard? guard = null, bool notifyOnClose = false, CancellationToken ct = default) =>
        SubmitFirstAsync(encodedId, visitor, new QuestionAnswer { Points = ballot }, displayName, responseNo, guard, notifyOnClose, ct);

    /// <param name="responseNo"><c>responses: many</c>: the response to revise, or null for a new one. Ignored under <c>one</c>.</param>
    /// <param name="notifyOnClose">"Email me when this poll closes"; stored only when <see cref="MayNotifyAsync"/> allows it.</param>
    public Task<SubmitResult> SubmitDatesAsync(string encodedId, VisitorContext visitor, DatesBallot ballot, string? displayName,
        int? responseNo = null, BallotGuard? guard = null, bool notifyOnClose = false, CancellationToken ct = default) =>
        SubmitFirstAsync(encodedId, visitor, new QuestionAnswer { Dates = ballot }, displayName, responseNo, guard, notifyOnClose, ct);

    /// <summary>An answer to the first question only (in-process callers of the one-question API, tests).</summary>
    private async Task<SubmitResult> SubmitFirstAsync(string encodedId, VisitorContext visitor, QuestionAnswer answer, string? displayName,
        int? responseNo, BallotGuard? guard, bool notifyOnClose, CancellationToken ct)
    {
        var a = await access.GetAsync(encodedId, visitor, ct);
        var answers = new Dictionary<string, QuestionAnswer>(StringComparer.Ordinal);
        if (a is not null) answers[a.Definition.Questions[0].Key] = answer;
        return await SubmitResponseAsync(encodedId, visitor, answers, displayName, responseNo, guard, notifyOnClose, ct);
    }

    /// <summary>
    /// Writes a whole response (DESIGN.md §34.3, §34.4): every question's answer is checked against the current definition
    /// ("Required" included) and the free text against the response limit; the scored parts go into
    /// <see cref="Ballot.PayloadJson"/>, the free text encrypted into <see cref="Ballot.TextCipher"/>. One write transaction,
    /// as for every ballot; a signed-in voter's draft for the response goes in the same transaction.
    /// </summary>
    /// <param name="answers">Per question key; questions the definition does not have and options it no longer has are ignored.</param>
    public Task<SubmitResult> SubmitResponseAsync(string encodedId, VisitorContext visitor, IReadOnlyDictionary<string, QuestionAnswer> answers,
        string? displayName, int? responseNo = null, BallotGuard? guard = null, bool notifyOnClose = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(answers);
        var limits = PollParsing.LimitsFrom(settings.Current.Limits);
        return SubmitAsync(encodedId, visitor, displayName, responseNo, guard, notifyOnClose,
            def => ValidateResponse(def, Only(def, answers), limits),
            (def, pollId) => (ResponseAnswers.Payload(def, Only(def, answers)), textProtection.ProtectTexts(pollId, ResponseAnswers.Texts(def, Only(def, answers)))),
            ct);
    }

    /// <summary>The answers to the definition's questions, normalised (unknown questions dropped; unknown options stay, the validator names them).</summary>
    private static Dictionary<string, QuestionAnswer> Only(PollDefinition def, IReadOnlyDictionary<string, QuestionAnswer> answers)
    {
        var only = new Dictionary<string, QuestionAnswer>(StringComparer.Ordinal);
        foreach (var q in def.Questions)
            if (ResponseAnswers.Normalize(q, answers.GetValueOrDefault(q.Key)) is { } a) only[q.Key] = a;
        return only;
    }

    /// <summary>
    /// Every question's errors and the free-text total; in a poll with several questions each message names its question
    /// (<see cref="QuestionLabel"/>). The vote page runs the same check per page (<see cref="ValidatePage"/>).
    /// </summary>
    public static List<string> ValidateResponse(PollDefinition def, IReadOnlyDictionary<string, QuestionAnswer> answers, PollLimits limits)
    {
        var errors = ValidatePage(def, def.Questions, answers, limits, requireAnswers: true);
        foreach (var e in ResponseText.Validate(def.Questions, ResponseAnswers.Texts(def, answers), limits)) errors.Add(e);
        return errors;
    }

    /// <summary>The errors of <paramref name="questions"/> (a page), each naming its question unless the poll is simple.</summary>
    public static List<string> ValidatePage(PollDefinition def, IEnumerable<PollQuestion> questions, IReadOnlyDictionary<string, QuestionAnswer> answers,
        PollLimits limits, bool requireAnswers)
    {
        var simple = VoteLayout.IsSimple(def);
        var errors = new List<string>();
        foreach (var q in questions)
            foreach (var e in ResponseAnswers.Validate(q, answers.GetValueOrDefault(q.Key), limits, requireAnswers))
                errors.Add(simple ? e : $"{QuestionLabel(def, q)}: {e}");
        return errors;
    }

    /// <summary>A question's name in messages: its prompt, else "Question N".</summary>
    public static string QuestionLabel(PollDefinition def, PollQuestion q)
    {
        if (!string.IsNullOrWhiteSpace(q.Prompt)) return q.Prompt.Trim();
        var i = def.Questions.ToList().FindIndex(x => x.Key == q.Key);
        return $"Question {i + 1}";
    }

    /// <summary>Honeypot, form stamp and captcha. Null when the vote may proceed.</summary>
    private async Task<SubmitResult?> CheckBotAsync(string encodedId, VisitorContext visitor, BallotGuard? guard, CancellationToken ct)
    {
        var check = guard is null ? FormCheck.Ok
            : !string.IsNullOrEmpty(guard.Honeypot) ? FormCheck.Honeypot
            : formGuard.Check(null, guard.Stamp, StampMaxAge); // missing or forged = Expired
        if (check != FormCheck.Ok)
        {
            // A form left open too long is a person, not a bot: no abuse signal for it.
            if (check != FormCheck.Expired) abuse.Record(client.Key, visitor.UserId, FormGuard.Signal(check));
            return new SubmitResult(SubmitStatus.Denied, [check == FormCheck.Expired ? StampTooOldMessage : FormGuard.Message(check)], null);
        }

        var captchaSettings = settings.Current.Captcha;
        if (!captchaSettings.Enabled || visitor.UserId is not null) return null;
        // Checked before the write transaction: the provider call must not hold the database lock.
        var a = await access.GetAsync(encodedId, visitor, ct);
        if (a is null || !CaptchaProviders.Required(captchaSettings, a.Poll, visitor.UserId)) return null;
        if (string.IsNullOrWhiteSpace(guard?.CaptchaToken)) return new SubmitResult(SubmitStatus.Denied, [CaptchaMissingMessage], a);
        var secret = settings.GetCaptchaSecret();
        if (secret is null)
        {
            // A configuration fault (e.g. a secret the Data Protection keys can no longer read), not the voter's.
            logger.LogWarning("Captcha is enabled but its secret is missing or unreadable. Enter it again on the admin Settings tab.");
            return new SubmitResult(SubmitStatus.Denied, [CaptchaUnavailableMessage], a);
        }
        switch (await captcha.VerifyAsync(captchaSettings.Provider, secret, guard.CaptchaToken, ct))
        {
            case CaptchaResult.Accepted:
                return null;
            case CaptchaResult.Rejected:
                abuse.Record(client.Key, null, AbuseSignal.Captcha);
                return new SubmitResult(SubmitStatus.Denied, [CaptchaFailedMessage], a);
            case CaptchaResult.Unavailable:
                // Provider outage or misconfiguration: no abuse signal, or one classroom behind a NAT gets blocked
                // (pentest R2-VOTE-2).
                return new SubmitResult(SubmitStatus.Denied, [CaptchaUnavailableMessage], a);
            default:
                throw new ArgumentOutOfRangeException(nameof(captcha));
        }
    }

    private async Task<SubmitResult> SubmitAsync(string encodedId, VisitorContext visitor, string? displayName, int? responseNo, BallotGuard? guard,
        bool notifyOnClose, Func<PollDefinition, IReadOnlyList<string>> validate, Func<PollDefinition, Guid, (string Payload, string? TextCipher)> payload,
        CancellationToken ct)
    {
        if (await CheckBotAsync(encodedId, visitor, guard, ct) is { } botRefusal) return botRefusal;
        var formId = VoteFormIds.IsWellFormed(guard?.FormId) ? guard!.FormId : null;
        (Guid PollId, string Identity)? reserved = null;
        try
        {
            return await WriteAsync();
        }
        finally
        {
            // Still reserved = nothing was committed (refused or failed): the same form may be sent again.
            if (reserved is { } r) formIds.Release(r.PollId, r.Identity, formId!);
        }

        async Task<SubmitResult> WriteAsync()
        {
            for (var attempt = 0; ; attempt++)
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                await using var tx = await db.Database.BeginPollWriteAsync(LockId(encodedId), ct);
                var (a, refused) = await AuthorizeAsync(db, encodedId, visitor, ct);
                if (refused is not null) return refused;

                var errors = new List<string>(validate(a!.Definition));
                var name = NormalizeName(a.Definition.Names, displayName, errors, MaxNameLength);
                if (errors.Count > 0) return new SubmitResult(SubmitStatus.Invalid, errors, a);

                // The same form sent twice by the same voter (double click, resent POST): the first one's response.
                // Reserved only for a valid ballot, so refused posts leave nothing behind (pentest R2-VOTE-1).
                if (formId is not null && reserved is null)
                {
                    if (!formIds.TryReserve(a.Poll.Id, a.Voter!.Stored, formId, out var usedFor))
                        return new SubmitResult(SubmitStatus.Ok, [], a) { ResponseNo = usedFor, Duplicate = true };
                    reserved = (a.Poll.Id, a.Voter.Stored);
                }

                var voter = a.Voter!;
                var identity = voter.Stored;
                var mine = await db.Ballots
                    .Where(b => b.PollId == a.Poll.Id && b.VoterIdentity == identity)
                    .OrderBy(b => b.ResponseNo)
                    .ToListAsync(ct);
                Ballot? row;
                if (a.Definition.Responses == ResponsePolicy.One) row = mine.FirstOrDefault();
                else if (responseNo is int n)
                {
                    row = mine.FirstOrDefault(b => b.ResponseNo == n);
                    if (row is null) return new SubmitResult(SubmitStatus.Invalid, [ResponseGoneMessage], a);
                }
                else row = null;

                var notify = notifyOnClose && await MayNotifyAsync(db, a, visitor.UserId, ct);
                var now = time.GetUtcNow();
                var (payloadJson, textCipher) = payload(a.Definition, a.Poll.Id);
                if (row is null)
                {
                    var limits = await UserLimits.ForUserAsync(db, settings.Current.Limits, a.Poll.OwnerId, now, ct);
                    if (limits.ResponsesPerVoter > 0 && a.Definition.Responses == ResponsePolicy.Many && mine.Count >= limits.ResponsesPerVoter)
                    {
                        abuse.Record(client.Key, visitor.UserId, AbuseSignal.LimitRefused);
                        return new SubmitResult(SubmitStatus.Denied, [UserLimits.ResponsesMessage(limits.ResponsesPerVoter)], a);
                    }
                    // A full poll is the poll's state, not this voter's doing: no abuse signal, or every honest voter behind
                    // one address who arrives late counts against it (pentest R2-VOTE-2).
                    if (limits.BallotsPerPoll > 0 && await db.Ballots.CountAsync(b => b.PollId == a.Poll.Id, ct) >= limits.BallotsPerPoll)
                        return new SubmitResult(SubmitStatus.Denied, [UserLimits.BallotsMessage(limits.BallotsPerPoll)], a);
                    // Invite polls: a new response starts with its link's alias (DESIGN.md §20.3). Only here: afterwards the
                    // alias is the owner's (set or cleared on the share page), and a revision never touches it.
                    var alias = a.Poll.VoterMode == VoterMode.Invite
                        ? await TokenOf(db, a.Poll.Id, voter).Select(t => t.Alias).FirstOrDefaultAsync(ct)
                        : null;
                    row = new Ballot
                    {
                        Id = Guid.NewGuid(),
                        PollId = a.Poll.Id,
                        VoterKey = voter.VoterKey,
                        UserId = voter.UserId,
                        VoterIdentity = identity,
                        ResponseNo = mine.Count == 0 ? 1 : mine.Max(b => b.ResponseNo) + 1,
                        PayloadJson = payloadJson,
                        TextCipher = textCipher,
                        DisplayName = name,
                        Alias = alias,
                        SubmittedAt = now,
                        UpdatedAt = now,
                        NotifyOnClose = notify,
                    };
                    db.Ballots.Add(row);
                }
                else
                {
                    row.PayloadJson = payloadJson;
                    row.TextCipher = textCipher;
                    row.DisplayName = name;
                    row.UpdatedAt = now;
                    row.NotifyOnClose = notify;
                }

                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException) when (attempt == 0)
                {
                    continue; // concurrent submit from the same voter took this number: retry once
                }

                if (a.Poll.VoterMode == VoterMode.Invite)
                {
                    var ballotId = row.Id;
                    await TokenOf(db, a.Poll.Id, voter).ExecuteUpdateAsync(s => s.SetProperty(t => t.BallotId, ballotId), ct);
                }
                // The response is written: its draft goes (DESIGN.md §34.4). Under responses: one every draft of the account
                // in the poll is for this response; under many, the one for the response edited (0 = new).
                if (voter.Kind == VoterKind.Account)
                {
                    var uid = voter.Key;
                    var pid = a.Poll.Id;
                    var draftNo = responseNo ?? 0;
                    var one = a.Definition.Responses == ResponsePolicy.One;
                    await db.ResponseDrafts.Where(d => d.PollId == pid && d.UserId == uid && (one || d.ResponseNo == draftNo)).ExecuteDeleteAsync(ct);
                }
                await tx.CommitAsync(ct);
                // Marked used at once: an exception after the commit must not release an id whose response exists
                // (pentest R2-VOTE-3f), or a resent form would save it twice.
                if (reserved is { } done)
                {
                    formIds.Complete(done.PollId, done.Identity, formId!, row.ResponseNo);
                    reserved = null;
                }
                notifier.Changed(a.Poll.Id);
                metrics.BallotsSubmitted.Add(1);

                var after = await access.GetAsync(db, encodedId, visitor, tracking: false, ct);
                return new SubmitResult(SubmitStatus.Ok, [], after) { ResponseNo = row.ResponseNo };
            }
        }
    }

    /// <summary>
    /// Deletes one of the visitor's own responses while the poll is open (<c>responses: many</c> only).
    /// An invite token that pointed at it moves to the voter's latest remaining response.
    /// </summary>
    public async Task<SubmitResult> DeleteResponseAsync(string encodedId, VisitorContext visitor, int responseNo, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginPollWriteAsync(LockId(encodedId), ct);
        var (a, refused) = await AuthorizeAsync(db, encodedId, visitor, ct);
        if (refused is not null) return refused;
        if (a!.Definition.Responses != ResponsePolicy.Many)
            return new SubmitResult(SubmitStatus.Denied, ["This poll keeps one response per voter."], a);

        var identity = a.Voter!.Stored;
        var row = await db.Ballots.SingleOrDefaultAsync(b => b.PollId == a.Poll.Id && b.VoterIdentity == identity && b.ResponseNo == responseNo, ct);
        if (row is null) return new SubmitResult(SubmitStatus.Invalid, [ResponseGoneMessage], a);

        if (a.Poll.VoterMode == VoterMode.Invite)
        {
            var deletedId = row.Id;
            var next = await db.Ballots
                .Where(b => b.PollId == a.Poll.Id && b.VoterIdentity == identity && b.Id != deletedId)
                .OrderByDescending(b => b.ResponseNo).Select(b => (Guid?)b.Id).FirstOrDefaultAsync(ct);
            await TokenOf(db, a.Poll.Id, a.Voter).ExecuteUpdateAsync(s => s.SetProperty(t => t.BallotId, next), ct);
        }
        db.Ballots.Remove(row);
        await db.SaveChangesAsync(ct);
        // The response's draft goes with it: a later response gets this number again and must not find it (DESIGN.md §34.4).
        if (a.Voter.Kind == VoterKind.Account)
        {
            var (pid, uid) = (a.Poll.Id, a.Voter.Key);
            await db.ResponseDrafts.Where(d => d.PollId == pid && d.UserId == uid && d.ResponseNo == responseNo).ExecuteDeleteAsync(ct);
        }
        await tx.CommitAsync(ct);
        notifier.Changed(a.Poll.Id);

        var after = await access.GetAsync(db, encodedId, visitor, tracking: false, ct);
        return new SubmitResult(SubmitStatus.Ok, [], after) { ResponseNo = responseNo };
    }

    /// <summary>
    /// "My votes": every poll the account has a response in (by <see cref="Ballot.UserId"/>, so ballots of
    /// invite tokens the account claimed count too), newest activity first. Each poll is loaded through
    /// <see cref="PollAccessService"/> as the account; the results link is its <see cref="PollAccess.CanSeeResults"/>.
    /// A ballot whose invitation was revoked is listed as such, without links.
    /// </summary>
    public async Task<IReadOnlyList<MyVoteSummary>> ListMyVotesAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Ballots.AsNoTracking()
            .Where(b => b.UserId == userId)
            .Select(b => new { b.PollId, b.UpdatedAt })
            .ToListAsync(ct);
        var result = new List<MyVoteSummary>();
        foreach (var group in rows.GroupBy(r => r.PollId))
        {
            var a = await access.GetAsync(db, PollId.Encode(group.Key), VisitorContext.ForUser(userId), tracking: false, ct);
            if (a is null || a.Unavailable) continue; // disabled by an admin (DESIGN.md §34.8): not listed while it lasts
            // Invite poll and the account holds no ballot as itself: its ballots were detached by a revoke.
            var revoked = a.Poll.VoterMode == VoterMode.Invite && !a.HasBallot && !a.IsOwner;
            result.Add(new MyVoteSummary(a.EncodedId, a.Definition.Title, a.Definition.Questions[0].Type, a.IsClosed, group.Count(),
                group.Max(r => r.UpdatedAt), a.CanSeeResults && !revoked, revoked, a.Definition.Questions.Count, Math.Max(1, a.Definition.Pages.Count), a.Definition.TimeZone));
        }
        // DateTimeOffset ordering in memory: SQLite cannot ORDER BY it.
        return result.OrderByDescending(r => r.LastUpdated).ToList();
    }

    /// <summary>The poll row a ballot write locks (DbWrites); an id that does not decode locks nothing and is refused as not found.</summary>
    private static Guid LockId(string encodedId) => PollId.TryDecode(encodedId, out var id) ? id : Guid.Empty;

    /// <summary>Common checks for submit and delete: account may act, poll exists, visitor may vote now.</summary>
    private async Task<(PollAccess? Access, SubmitResult? Refused)> AuthorizeAsync(ApplicationDbContext db, string encodedId,
        VisitorContext visitor, CancellationToken ct)
    {
        // A circuit can outlive the account state: a signed-in voter whose account is gated
        // (AccountGate) or deleted cannot act.
        if (visitor.UserId is { } uid && !await AccountGate.MayActAsync(db, uid, ct))
            return (null, new SubmitResult(SubmitStatus.Denied, [SetupRequiredMessage], null));
        var a = await access.GetAsync(db, encodedId, visitor, tracking: false, ct);
        if (a is null) return (null, new SubmitResult(SubmitStatus.NotFound, ["This poll no longer exists."], null));
        if (!a.CanVote || a.Voter is null)
            return (a, new SubmitResult(SubmitStatus.Denied, [a.ReasonIfDenied ?? "You cannot vote in this poll."], a));
        if (a.Voter.Kind == VoterKind.Invite && visitor.UserId is { } claimant)
        {
            a = await ClaimAsync(db, a, a.Voter.Key, claimant, encodedId, visitor, ct);
            if (a is null) return (null, new SubmitResult(SubmitStatus.NotFound, ["This poll no longer exists."], null));
            if (!a.CanVote || a.Voter is null)
                return (a, new SubmitResult(SubmitStatus.Denied, [a.ReasonIfDenied ?? "You cannot vote in this poll."], a));
        }
        return (a, null);
    }

    /// <summary>
    /// DESIGN.md §16: the first write of a signed-in account with an unclaimed invite token claims it. The
    /// token's existing responses move to the account (identity "u:" + id, <see cref="Ballot.UserId"/> set,
    /// <see cref="Ballot.VoterKey"/> null) in the caller's transaction; from then on the token works only for
    /// that account. Returns the access re-evaluated as the account (or, when another account won a race for
    /// the token, the refusal).
    /// </summary>
    private async Task<PollAccess?> ClaimAsync(ApplicationDbContext db, PollAccess a, string tokenHash, string userId, string encodedId,
        VisitorContext visitor, CancellationToken ct)
    {
        var pollId = a.Poll.Id;
        var claimed = await db.InviteTokens
            .Where(t => t.PollId == pollId && t.TokenHash == tokenHash && t.ClaimedByUserId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ClaimedByUserId, userId), ct);
        if (claimed == 1)
        {
            var fromIdentity = a.Voter!.Stored;
            var account = VoterIdentity.ForAccount(userId);
            var toIdentity = account.Stored;
            // An account has no other responses in an invite poll before its one claim; renumber anyway so
            // the unique (poll, identity, response) index can never be hit.
            var offset = await db.Ballots.Where(b => b.PollId == pollId && b.VoterIdentity == toIdentity)
                .MaxAsync(b => (int?)b.ResponseNo, ct) ?? 0;
            await db.Ballots.Where(b => b.PollId == pollId && b.VoterIdentity == fromIdentity)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.VoterIdentity, toIdentity)
                    .SetProperty(b => b.UserId, userId)
                    .SetProperty(b => b.VoterKey, (string?)null)
                    .SetProperty(b => b.ResponseNo, b => b.ResponseNo + offset), ct);
        }
        return await access.GetAsync(db, encodedId, visitor, tracking: false, ct);
    }

    /// <summary>The invite token behind an identity in an invite poll: the presented one, or the account's claimed one.</summary>
    private static IQueryable<InviteToken> TokenOf(ApplicationDbContext db, Guid pollId, VoterIdentity voter)
    {
        var key = voter.Key;
        return voter.Kind switch
        {
            VoterKind.Invite => db.InviteTokens.Where(t => t.PollId == pollId && t.TokenHash == key),
            VoterKind.Account => db.InviteTokens.Where(t => t.PollId == pollId && t.ClaimedByUserId == key),
            _ => db.InviteTokens.Where(t => false),
        };
    }

    /// <summary>Trims; enforces NamesMode and the name limit. Returns the name to store (null for None/empty).</summary>
    public static string? NormalizeName(NamesMode mode, string? input, List<string> errors, int maxLength)
    {
        if (mode == NamesMode.None) return null;
        var name = input?.Trim() ?? "";
        if (name.Length > maxLength) errors.Add($"Shorten your name to {maxLength} characters.");
        if (mode == NamesMode.Required && name.Length == 0) errors.Add("Enter your name.");
        return name.Length == 0 ? null : name;
    }

    /// <summary>
    /// The voter's own response: every question's answer with its free text (<paramref name="texts"/>, decrypted; null when
    /// unreadable), and the first question's ballot in the one-question fields. Options removed since the ballot was stored are
    /// left out (DESIGN.md §26), so the summary, the read-only view and the edit form show only current ones.
    /// </summary>
    private static MyBallot ToMyBallot(PollDefinition def, Ballot row, IReadOnlyDictionary<string, string>? texts)
    {
        var q = def.Questions[0];
        var answers = ResponseAnswers.Read(def, row.PayloadJson, texts);
        var ballot = q.Type switch
        {
            PollType.Ranked => new MyBallot(row.ResponseNo, PollJson.ReadRanked(row.PayloadJson, q), null, row.DisplayName, row.SubmittedAt, row.UpdatedAt),
            PollType.Points => new MyBallot(row.ResponseNo, null, PollJson.ReadPoints(row.PayloadJson, q), row.DisplayName, row.SubmittedAt, row.UpdatedAt),
            PollType.Dates => new MyBallot(row.ResponseNo, null, null, row.DisplayName, row.SubmittedAt, row.UpdatedAt) { Dates = PollJson.ReadDates(row.PayloadJson, q) },
            PollType.Choice or PollType.Text or PollType.Number => new MyBallot(row.ResponseNo, null, null, row.DisplayName, row.SubmittedAt, row.UpdatedAt),
            _ => throw new ArgumentOutOfRangeException(nameof(def), q.Type, null),
        };
        return ballot with { NotifyOnClose = row.NotifyOnClose, Answers = answers };
    }

    // ---- drafts (DESIGN.md §34.4) ----------------------------------------------------------------

    /// <summary>
    /// The account's draft for response <paramref name="responseNo"/> (0 = a new one), or null. Only for a voter identified by
    /// account who may vote now, in a poll with several pages; nobody else has drafts, and nobody else reads them (the key is
    /// the identity <see cref="PollAccessService"/> resolved for this request).
    /// </summary>
    public async Task<ResponseDraftState?> GetDraftAsync(PollAccess a, int responseNo, CancellationToken ct = default)
    {
        if (a is not { CanVote: true, Voter.Kind: VoterKind.Account } || VoteLayout.Pages(a.Definition).Count < 2) return null;
        var uid = a.Voter.Key;
        var pid = a.Poll.Id;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.ResponseDrafts.AsNoTracking().FirstOrDefaultAsync(d => d.PollId == pid && d.UserId == uid && d.ResponseNo == responseNo, ct);
        // A draft of a response that no longer exists is not shown (one left by a deletion before the draft went with it).
        var identity = a.Voter.Stored;
        if (row is not null && responseNo != 0
            && !await db.Ballots.AnyAsync(b => b.PollId == pid && b.VoterIdentity == identity && b.ResponseNo == responseNo, ct)) return null;
        if (row is null || textProtection.RevealDraft(pid, uid, responseNo, row.Cipher) is not { } plain) return null;
        return VoteCarry.Deserialize(a.Definition, plain, time.GetUtcNow(), null) is { } state ? new ResponseDraftState(state, row.UpdatedAt) : null;
    }

    /// <summary>
    /// Keeps the carry content as the account's draft (every Next, DESIGN.md §34.4). Access is checked again here, as for a
    /// ballot: an account that may act, voting by account in an open poll with several pages, and for an existing response
    /// one of its own. False when nothing was stored.
    /// </summary>
    public async Task<bool> SaveDraftAsync(string encodedId, VisitorContext visitor, int responseNo, CarryState state, CancellationToken ct = default)
    {
        if (visitor.UserId is not { } uid) return false;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginPollWriteAsync(LockId(encodedId), ct);
        if (!await AccountGate.MayActAsync(db, uid, ct)) return false;
        var a = await access.GetAsync(db, encodedId, visitor, tracking: false, ct);
        if (a is not { CanVote: true, Voter.Kind: VoterKind.Account } || a.Voter.Key != uid || VoteLayout.Pages(a.Definition).Count < 2) return false;
        var pid = a.Poll.Id;
        var identity = a.Voter.Stored;
        if (responseNo != 0 && !await db.Ballots.AnyAsync(b => b.PollId == pid && b.VoterIdentity == identity && b.ResponseNo == responseNo, ct)) return false;
        var plain = VoteCarry.Serialize(a.Definition, state, time.GetUtcNow());
        if (ResponseTextProtection.Pack(plain).Length > VoteFormLimits.CarryMaxBytes) return false;
        var cipher = textProtection.ProtectDraft(pid, uid, responseNo, plain);
        var now = time.GetUtcNow();
        var row = await db.ResponseDrafts.FirstOrDefaultAsync(d => d.PollId == pid && d.UserId == uid && d.ResponseNo == responseNo, ct);
        if (row is null) db.ResponseDrafts.Add(new ResponseDraft { PollId = pid, UserId = uid, ResponseNo = responseNo, Cipher = cipher, UpdatedAt = now });
        else
        {
            row.Cipher = cipher;
            row.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>Discard: deletes the account's draft for the response (the identity is the request's, never a form field).</summary>
    public async Task DiscardDraftAsync(string encodedId, VisitorContext visitor, int responseNo, CancellationToken ct = default)
    {
        if (visitor.UserId is not { } uid || !PollId.TryDecode(encodedId, out var pid)) return;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.ResponseDrafts.Where(d => d.PollId == pid && d.UserId == uid && d.ResponseNo == responseNo).ExecuteDeleteAsync(ct);
    }
}
