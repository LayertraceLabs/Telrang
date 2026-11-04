using System.Security.Claims;
using Telrang.Core.Ballots;
using Telrang.Core.Ids;
using Telrang.Web.Data;
using Telrang.Web.Features.Admin;
using Telrang.Web.Features.Polls;
using Telrang.Web.Features.Results;
using Telrang.Web.Features.Settings;
using Telrang.Web.Features.Voting;
using Telrang.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>DESIGN.md §21.2: the change notifier behind the owner's live results.</summary>
public sealed class LiveResultsNotifierTests
{
    [Fact]
    public void A_burst_coalesces_into_one_pending_change_and_end_completes_every_subscriber()
    {
        var notifier = new PollChangeNotifier();
        var poll = Guid.NewGuid();
        using var a = notifier.Subscribe(poll);
        using var b = notifier.Subscribe(poll);
        using var other = notifier.Subscribe(Guid.NewGuid());

        for (var i = 0; i < 5; i++) notifier.Changed(poll);
        Assert.True(a.Reader.TryRead(out _));
        Assert.False(a.Reader.TryRead(out _)); // capacity 1, DropWrite
        Assert.True(b.Reader.TryRead(out _));
        Assert.False(other.Reader.TryRead(out _));

        notifier.End(poll);
        Assert.True(a.Ended && b.Ended);
        Assert.True(a.Reader.Completion.IsCompleted);
        Assert.False(other.Ended);
        Assert.Equal(0, notifier.Count(poll));

        // A new stream after the end starts fresh; disposing removes it.
        var c = notifier.Subscribe(poll);
        Assert.Equal(1, notifier.Count(poll));
        c.Dispose();
        Assert.Equal(0, notifier.Count(poll));
        notifier.Changed(poll); // nobody listening: no-op
    }

    private static PollChangeNotifier Notifier(AppFactory factory) => factory.Services.GetRequiredService<PollChangeNotifier>();

    private static Guid Raw(string id)
    {
        Assert.True(PollId.TryDecode(id, out var raw));
        return raw;
    }

    private static void AssertChanged(PollChangeNotifier.Subscription sub)
    {
        Assert.True(sub.Reader.TryRead(out _), "expected a change notice");
        Assert.False(sub.Ended);
    }

    internal static async Task<ClaimsPrincipal> AdminAsync(AppFactory factory, string email = "admin@example.com")
    {
        var id = await CreateUserAsync(factory, email);
        return await WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(id))!;
            Assert.True((await users.AddToRoleAsync(user, AppRoles.Admin)).Succeeded);
            return await sp.GetRequiredService<SignInManager<ApplicationUser>>().CreateUserPrincipalAsync(user);
        });
    }

    [Fact]
    public async Task Every_committed_write_the_results_page_shows_notifies()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var voter = await CreateUserAsync(factory, "v@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft().Many());
        var def = (await WithScope(factory, sp => sp.GetRequiredService<PollService>().GetForEditAsync(owner, id)))!.Definition;
        var paris = def.Questions[0].Options[0].Key;
        using var sub = Notifier(factory).Subscribe(Raw(id));

        // Ballot save and delete.
        var saved = await SubmitRankedAsync(factory, id, VisitorContext.ForUser(voter), new RankedBallot([paris], []));
        Assert.True(saved.Succeeded);
        AssertChanged(sub);
        Assert.True((await WithScope(factory, sp => sp.GetRequiredService<BallotService>()
            .DeleteResponseAsync(id, VisitorContext.ForUser(voter), saved.ResponseNo!.Value))).Succeeded);
        AssertChanged(sub);

        // A refused write notifies nobody.
        Assert.False((await SubmitRankedAsync(factory, id, VisitorContext.ForUser(voter), new RankedBallot(["nope"], []))).Succeeded);
        Assert.False(sub.Reader.TryRead(out _));

        // Alias change.
        Assert.True((await SubmitRankedAsync(factory, id, VisitorContext.ForUser(voter), new RankedBallot([paris], []))).Succeeded);
        AssertChanged(sub);
        var ballot = Assert.Single(await BallotsAsync(factory, id));
        Assert.Equal(AliasStatus.Ok, (await WithScope(factory, sp => sp.GetRequiredService<PollService>().SetResponseAliasAsync(owner, id, ballot.Id, "V"))).Status);
        AssertChanged(sub);

        // Poll edit, close and reopen (owner and admin).
        Assert.True((await UpdatePollAsync(factory, owner, id, RankedDraft().Many().Titled("Trip 2"))).Succeeded);
        AssertChanged(sub);
        Assert.True(await WithScope(factory, sp => sp.GetRequiredService<PollService>().SetClosedAsync(owner, id, true)));
        AssertChanged(sub);
        Assert.True(await WithScope(factory, sp => sp.GetRequiredService<PollService>().SetClosedAsync(owner, id, false)));
        AssertChanged(sub);
        var admin = await AdminAsync(factory);
        Assert.True((await WithScope(factory, sp => sp.GetRequiredService<AdminService>().SetPollClosedAsync(admin, id, true))).Ok);
        AssertChanged(sub);
    }

    [Fact]
    public async Task Invite_revoke_notifies()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft(voters: VoterMode.Invite));
        var invite = (await WithScope(factory, sp => sp.GetRequiredService<PollService>().GenerateInvitesAsync(owner, id, 1, [])))!.Single();
        var token = Assert.Single((await WithScope(factory, sp => sp.GetRequiredService<PollService>().ListInvitesAsync(owner, id)))!);
        using var sub = Notifier(factory).Subscribe(Raw(id));
        Assert.True(await WithScope(factory, sp => sp.GetRequiredService<PollService>().RevokeInviteAsync(owner, id, token.Id)));
        AssertChanged(sub);
        Assert.NotNull(invite.Token);
    }

    [Fact]
    public async Task Delete_transfer_account_deletion_and_retention_end_the_streams()
    {
        await using var factory = new AppFactory();
        var admin = await AdminAsync(factory);
        var owner = await CreateUserAsync(factory, "o@example.com");
        await CreateUserAsync(factory, "t@example.com");
        var notifier = Notifier(factory);

        var own = await CreatePollAsync(factory, owner, RankedDraft());
        using (var sub = notifier.Subscribe(Raw(own)))
        {
            Assert.True(await WithScope(factory, sp => sp.GetRequiredService<PollService>().DeletePollAsync(owner, own)));
            Assert.True(sub.Ended);
        }

        var byAdmin = await CreatePollAsync(factory, owner, RankedDraft());
        using (var sub = notifier.Subscribe(Raw(byAdmin)))
        {
            Assert.True((await WithScope(factory, sp => sp.GetRequiredService<AdminService>().DeletePollAsync(admin, byAdmin))).Ok);
            Assert.True(sub.Ended);
        }

        var moved = await CreatePollAsync(factory, owner, RankedDraft());
        using (var sub = notifier.Subscribe(Raw(moved)))
        {
            Assert.True((await WithScope(factory, sp => sp.GetRequiredService<AdminService>().TransferPollAsync(admin, moved, "t@example.com"))).Ok);
            Assert.True(sub.Ended);
        }

        // Account deletion removes all its polls at once.
        var first = await CreatePollAsync(factory, owner, RankedDraft());
        var second = await CreatePollAsync(factory, owner, RankedDraft());
        using (var a = notifier.Subscribe(Raw(first)))
        using (var b = notifier.Subscribe(Raw(second)))
        {
            Assert.True((await WithScope(factory, sp => sp.GetRequiredService<AdminService>().DeleteUserAsync(admin, owner))).Ok);
            Assert.True(a.Ended && b.Ended);
        }
    }

    [Fact]
    public async Task Retention_deletion_ends_the_streams()
    {
        var clock = new MutableTime(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
        await using var factory = new AppFactory(services: s => s.AddSingleton<TimeProvider>(clock));
        await factory.Settings.SetAsync(new Dictionary<string, string?> { [SettingKeys.RetentionDeleteClosedAfterDays] = "7" }, null);
        var owner = await CreateUserAsync(factory, "o@example.com");
        var id = await CreatePollAsync(factory, owner, RankedDraft());
        Assert.True(await WithScope(factory, sp => sp.GetRequiredService<PollService>().SetClosedAsync(owner, id, true)));
        using var sub = Notifier(factory).Subscribe(Raw(id));
        clock.Now += TimeSpan.FromDays(8);
        Assert.Equal(1, await factory.Services.GetRequiredService<PollSweepService>().DeleteExpiredAsync());
        Assert.True(sub.Ended);
    }
}
