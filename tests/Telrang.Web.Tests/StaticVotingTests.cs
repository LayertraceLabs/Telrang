using Telrang.Core.Ballots;
using Telrang.Core.Polls;
using Telrang.Web.Features.Voting;
using Telrang.Web.Security;
using Microsoft.Extensions.DependencyInjection;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>DESIGN.md §21.1: the vote form's guard (stamp, single-use form id) in BallotService.</summary>
public class StaticVotingTests
{
    private static readonly RankedBallot ParisFirst = new(["paris", "rome"], []);

    private static Task<SubmitResult> SubmitAsync(AppFactory factory, string id, VisitorContext visitor, RankedBallot ballot, BallotGuard? guard,
        int? responseNo = null) =>
        WithScope(factory, sp => sp.GetRequiredService<BallotService>().SubmitRankedAsync(id, visitor, ballot, null, responseNo, guard));

    private static BallotGuard Guard(AppFactory factory, string? formId = null) =>
        new(Stamp: factory.Services.GetRequiredService<FormGuard>().CreateStamp(), FormId: formId);

    [Fact]
    public async Task The_same_form_id_twice_creates_one_response()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft().Many());
        var visitor = new VisitorContext(null, "voter-key-1", null);
        var formId = VoteFormIds.Create();

        var first = await SubmitAsync(factory, id, visitor, ParisFirst, Guard(factory, formId));
        var again = await SubmitAsync(factory, id, visitor, ParisFirst, Guard(factory, formId));
        Assert.True(first.Succeeded);
        Assert.False(first.Duplicate);
        Assert.True(again.Succeeded);
        Assert.True(again.Duplicate);
        Assert.Equal(first.ResponseNo, again.ResponseNo);
        Assert.Single(await BallotsAsync(factory, id));

        // Another form is another response; another voter with the same id is not this voter's form.
        Assert.Equal(2, (await SubmitAsync(factory, id, visitor, ParisFirst, Guard(factory, VoteFormIds.Create()))).ResponseNo);
        var other = await SubmitAsync(factory, id, new VisitorContext(null, "voter-key-2", null), ParisFirst, Guard(factory, formId));
        Assert.False(other.Duplicate);
        Assert.Equal(3, (await BallotsAsync(factory, id)).Count);
    }

    [Fact]
    public async Task A_refused_form_can_be_sent_again_with_the_same_id()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft().Many());
        var visitor = new VisitorContext(null, "voter-key-1", null);
        var formId = VoteFormIds.Create();

        var refused = await SubmitAsync(factory, id, visitor, new RankedBallot(["paris"], ["paris"]), Guard(factory, formId));
        Assert.Equal(SubmitStatus.Invalid, refused.Status);
        var saved = await SubmitAsync(factory, id, visitor, ParisFirst, Guard(factory, formId));
        Assert.True(saved.Succeeded);
        Assert.False(saved.Duplicate);
        Assert.Single(await BallotsAsync(factory, id));
    }

    [Fact]
    public async Task A_stamp_older_than_12_hours_is_refused()
    {
        var clock = new MutableTime(DateTimeOffset.UtcNow);
        await using var factory = new AppFactory(services: s => s.AddSingleton<TimeProvider>(clock));
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft());
        var visitor = new VisitorContext(null, "voter-key-1", null);

        // 11 hours: past the 2 h of the account forms, inside the vote form's 12 h.
        var guard = Guard(factory);
        clock.Now += TimeSpan.FromHours(11);
        Assert.True((await SubmitAsync(factory, id, visitor, ParisFirst, guard)).Succeeded);

        guard = Guard(factory);
        clock.Now += TimeSpan.FromHours(12) + TimeSpan.FromMinutes(1);
        var old = await SubmitAsync(factory, id, visitor, ParisFirst, guard);
        Assert.Equal(SubmitStatus.Denied, old.Status);
        Assert.Equal([BallotService.StampTooOldMessage], old.Errors);

        // A missing or forged stamp is refused the same way.
        Assert.Equal([BallotService.StampTooOldMessage], (await SubmitAsync(factory, id, visitor, ParisFirst, new BallotGuard())).Errors);
        Assert.Equal([BallotService.StampTooOldMessage], (await SubmitAsync(factory, id, visitor, ParisFirst, new BallotGuard(Stamp: "forged"))).Errors);
    }
}
