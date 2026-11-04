using System.Net;
using Telrang.Core.Ballots;
using Telrang.Core.Polls;
using Telrang.Core.Ids;
using Telrang.Web.Data;
using Telrang.Web.Features.Polls;
using Telrang.Web.Features.Status;
using Telrang.Web.Features.Voting;
using Telrang.Web.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>
/// DESIGN.md §16 identity model: signed in = the account, state server-side (claimed
/// invitations); anonymous = the browser's cookies. Plus the IdentityModel migration and secrets kept out of
/// the Status log ring.
/// </summary>
public class IdentityModelTests
{
    private static readonly RankedBallot ParisFirst = new(["paris", "rome"], []);
    private static readonly RankedBallot RomeFirst = new(["rome", "paris"], []);

    private static PollDraft Draft(VoterMode voters = VoterMode.Public, ResultsVisibility results = ResultsVisibility.AfterVote) =>
        RankedDraft(results, voters);

    private static Task<SubmitResult> SubmitAsync(AppFactory factory, string id, VisitorContext visitor, RankedBallot ballot) =>
        WithScope(factory, sp => sp.GetRequiredService<BallotService>().SubmitRankedAsync(id, visitor, ballot, null));

    private static Task<PollAccess?> AccessAsync(AppFactory factory, string id, VisitorContext visitor) =>
        WithScope(factory, sp => sp.GetRequiredService<PollAccessService>().GetAsync(id, visitor));

    /// <summary>A fresh browser (own cookie jar) signed in through the login form.</summary>
    private static async Task<(HttpClient Client, CookieContainer Jar)> SignedInBrowserAsync(AppFactory factory, string email)
    {
        var (client, jar) = factory.CreateBrowserWithJar();
        Assert.Equal(HttpStatusCode.Redirect, (await AppFactory.LoginAsync(client, email, Password)).StatusCode);
        return (client, jar);
    }

    private static bool HasCookie(CookieContainer jar, string id, string name) =>
        jar.GetCookies(new Uri($"https://localhost/p/{id}"))[name] is not null;

    private static async Task<string> PageAsync(HttpClient client, string path) =>
        await (await GetPageAsync(client, path)).Content.ReadAsStringAsync();

    private static async Task<(string Token, string Hash)> InviteAsync(AppFactory factory, string owner, string id)
    {
        var invite = (await WithScope(factory, sp => sp.GetRequiredService<PollService>().GenerateInvitesAsync(owner, id, 1, [])))!.Single();
        Assert.True(InviteTokens.TryHash(invite.Token, out var hash));
        return (invite.Token, hash);
    }

    private static async Task<List<InviteToken>> TokensAsync(AppFactory factory, string id)
    {
        Assert.True(PollId.TryDecode(id, out var pollId));
        return await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().InviteTokens.AsNoTracking().Where(t => t.PollId == pollId).ToListAsync());
    }

    // ---- invite claims -------------------------------------------------------------------------------

    [Fact]
    public async Task Anonymous_invite_ballot_moves_to_the_account_that_uses_the_link_and_the_first_browser_is_refused()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var me = await CreateUserAsync(factory, "me@example.com");
        var id = await CreatePollAsync(factory, owner, Draft(voters: VoterMode.Invite));
        var (token, hash) = await InviteAsync(factory, owner, id);

        // Browser A, anonymous: opens the link (pi_ cookie) and votes.
        var (a, jarA) = factory.CreateBrowserWithJar();
        Assert.Equal(HttpStatusCode.Redirect, (await a.GetAsync($"/p/{id}?t={token}")).StatusCode);
        Assert.True(HasCookie(jarA, id, PollCookies.InviteCookieName(id)));
        Assert.True((await SubmitAsync(factory, id, VisitorFrom(factory, jarA, id), ParisFirst)).Succeeded);
        var anonymousBallot = Assert.Single(await BallotsAsync(factory, id));
        Assert.Equal("k:invite:" + hash, anonymousBallot.VoterIdentity);
        Assert.Null(anonymousBallot.UserId);

        // Browser B, signed in: the unclaimed link shows the token's ballot; the first write claims it.
        var (b, jarB) = await SignedInBrowserAsync(factory, "me@example.com");
        Assert.Equal(HttpStatusCode.Redirect, (await b.GetAsync($"/p/{id}?t={token}")).StatusCode);
        var visitorB = VisitorFrom(factory, jarB, id, me);
        Assert.Equal(VoterKind.Invite, (await AccessAsync(factory, id, visitorB))!.Voter!.Kind);
        Assert.Contains("You have already voted", await PageAsync(b, $"/p/{id}"));
        Assert.Null(Assert.Single(await TokensAsync(factory, id)).ClaimedByUserId);
        Assert.True((await SubmitAsync(factory, id, visitorB, RomeFirst)).Succeeded);

        var ballot = Assert.Single(await BallotsAsync(factory, id));
        Assert.Equal(anonymousBallot.Id, ballot.Id); // same row, re-keyed
        Assert.Equal("u:" + me, ballot.VoterIdentity);
        Assert.Equal(me, ballot.UserId);
        Assert.Null(ballot.VoterKey);
        var claimed = Assert.Single(await TokensAsync(factory, id));
        Assert.Equal(me, claimed.ClaimedByUserId);
        Assert.Equal(ballot.Id, claimed.BallotId);

        // Browser A's cookie no longer works.
        var pageA = await PageAsync(a, $"/p/{id}");
        Assert.Contains(PollAccessService.InviteClaimedMessage, pageA);
        Assert.Contains("Account/Login?ReturnUrl=", pageA);
        Assert.DoesNotContain("ballot-editor", pageA);
        var refused = await SubmitAsync(factory, id, VisitorFrom(factory, jarA, id), ParisFirst);
        Assert.Equal(AccessDenial.InviteClaimed, refused.Access!.Denial);
        Assert.Equal(["rome", "paris"], PollJson.DeserializeRanked(Assert.Single(await BallotsAsync(factory, id)).PayloadJson, "q1").Ranked);

        // Browser B no longer needs the invite cookie: the next view drops it and still finds the ballot.
        Assert.Contains("You have already voted", await PageAsync(b, $"/p/{id}"));
        Assert.False(HasCookie(jarB, id, PollCookies.InviteCookieName(id)));
        Assert.Contains("You have already voted", await PageAsync(b, $"/p/{id}"));
    }

    [Fact]
    public async Task Token_claimed_while_signed_in_is_refused_anonymously_and_to_another_account()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var me = await CreateUserAsync(factory, "me@example.com");
        var other = await CreateUserAsync(factory, "other@example.com");
        var id = await CreatePollAsync(factory, owner, Draft(voters: VoterMode.Invite));
        var (token, hash) = await InviteAsync(factory, owner, id);

        Assert.True((await SubmitAsync(factory, id, new VisitorContext(me, null, hash), ParisFirst)).Succeeded);
        Assert.Equal(me, Assert.Single(await TokensAsync(factory, id)).ClaimedByUserId);

        // Anonymous with the link: service and page refuse with the message.
        var anonymous = await SubmitAsync(factory, id, new VisitorContext(null, null, hash), RomeFirst);
        Assert.Equal(AccessDenial.InviteClaimed, anonymous.Access!.Denial);
        Assert.Equal([PollAccessService.InviteClaimedMessage], anonymous.Errors);
        var (anon, _) = factory.CreateBrowserWithJar();
        Assert.Equal(HttpStatusCode.Redirect, (await anon.GetAsync($"/p/{id}?t={token}")).StatusCode); // not a 404
        var anonPage = await PageAsync(anon, $"/p/{id}");
        Assert.Contains(PollAccessService.InviteClaimedMessage, anonPage);
        Assert.DoesNotContain("ballot-editor", anonPage);

        // Another account with the link: refused too, and it cannot claim it.
        var otherResult = await SubmitAsync(factory, id, new VisitorContext(other, null, hash), RomeFirst);
        Assert.Equal(AccessDenial.InviteClaimed, otherResult.Access!.Denial);
        var (otherBrowser, _) = await SignedInBrowserAsync(factory, "other@example.com");
        Assert.Equal(HttpStatusCode.Redirect, (await otherBrowser.GetAsync($"/p/{id}?t={token}")).StatusCode);
        var otherPage = await PageAsync(otherBrowser, $"/p/{id}");
        Assert.Contains(PollAccessService.InviteClaimedSignedInMessage, otherPage);
        Assert.DoesNotContain(PollAccessService.InviteClaimedMessage, otherPage);
        Assert.Equal([PollAccessService.InviteClaimedSignedInMessage], otherResult.Errors);
        Assert.DoesNotContain("ballot-editor", otherPage);
        Assert.Equal(me, Assert.Single(await TokensAsync(factory, id)).ClaimedByUserId);
        var ballot = Assert.Single(await BallotsAsync(factory, id));
        Assert.Equal(["paris", "rome"], PollJson.DeserializeRanked(ballot.PayloadJson, "q1").Ranked);

        // The claimant, signed in on a browser that never saw the link, opens the ballot (no ?t=, no cookie).
        var (mine, _) = await SignedInBrowserAsync(factory, "me@example.com");
        Assert.Contains("You have already voted", await PageAsync(mine, $"/p/{id}"));
        Assert.Equal(VoterKind.Account, (await AccessAsync(factory, id, VisitorContext.ForUser(me)))!.Voter!.Kind);
    }

    [Fact]
    public async Task My_votes_opens_an_invite_ballot_on_a_browser_without_cookies()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var me = await CreateUserAsync(factory, "me@example.com");
        var id = await CreatePollAsync(factory, owner, Draft(voters: VoterMode.Invite, results: ResultsVisibility.Creator));
        var (_, hash) = await InviteAsync(factory, owner, id);
        Assert.True((await SubmitAsync(factory, id, new VisitorContext(me, null, hash), ParisFirst)).Succeeded);

        var (browser, jar) = await SignedInBrowserAsync(factory, "me@example.com");
        var list = await (await browser.GetAsync("/my/votes")).Content.ReadAsStringAsync();
        Assert.Contains($"href=\"p/{id}\"", list);
        Assert.Contains("Open ballot", list);

        var page = await PageAsync(browser, $"/p/{id}");
        Assert.Contains("You have already voted", page);
        Assert.DoesNotContain("invite-only", page);
        Assert.False(HasCookie(jar, id, PollCookies.InviteCookieName(id)));

        // Closed: the read-only ballot, still by account.
        Assert.True(await WithScope(factory, sp => sp.GetRequiredService<PollService>().SetClosedAsync(owner, id, true)));
        var closed = await PageAsync(browser, $"/p/{id}");
        Assert.Contains("Poll closed.", closed);
        Assert.Contains("Your ballot", closed);
    }

    [Fact]
    public async Task After_vote_results_open_for_the_claiming_account_on_any_browser()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var me = await CreateUserAsync(factory, "me@example.com");
        var id = await CreatePollAsync(factory, owner, Draft(voters: VoterMode.Invite, results: ResultsVisibility.AfterVote));
        var (_, hash) = await InviteAsync(factory, owner, id);
        var (browser, _) = await SignedInBrowserAsync(factory, "me@example.com");

        Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync($"/p/{id}/results")).StatusCode);
        Assert.False((await AccessAsync(factory, id, VisitorContext.ForUser(me)))!.CanSeeResults);

        var submitted = await SubmitAsync(factory, id, new VisitorContext(me, null, hash), ParisFirst);
        Assert.True(submitted.Succeeded);
        Assert.True(submitted.Access!.CanSeeResults);
        Assert.True((await AccessAsync(factory, id, VisitorContext.ForUser(me)))!.CanSeeResults);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync($"/p/{id}/results")).StatusCode);
        var list = await WithScope(factory, sp => sp.GetRequiredService<BallotService>().ListMyVotesAsync(me));
        Assert.True(Assert.Single(list).ResultsLink);

        // The anonymous holder of the (now claimed) link has not voted as far as results go.
        Assert.False((await AccessAsync(factory, id, new VisitorContext(null, null, hash)))!.CanSeeResults);
    }

    [Fact]
    public async Task Revoking_a_claimed_invite_keeps_the_ballot_but_detaches_it_from_the_account()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var me = await CreateUserAsync(factory, "me@example.com");
        var id = await CreatePollAsync(factory, owner, Draft(voters: VoterMode.Invite));
        var (_, hash) = await InviteAsync(factory, owner, id);
        Assert.True((await SubmitAsync(factory, id, new VisitorContext(me, null, hash), ParisFirst)).Succeeded);
        var polls = (IServiceProvider sp) => sp.GetRequiredService<PollService>();
        var token = Assert.Single((await WithScope(factory, sp => polls(sp).ListInvitesAsync(owner, id)))!);
        Assert.True(token.Used);
        Assert.False(await WithScope(factory, sp => polls(sp).RevokeInviteAsync(owner, id, token.Id)));
        Assert.True(await WithScope(factory, sp => polls(sp).RevokeInviteAsync(owner, id, token.Id, includeUsed: true)));

        var ballot = Assert.Single(await BallotsAsync(factory, id));
        Assert.Equal("k:invite:" + hash, ballot.VoterIdentity);
        Assert.Equal(me, ballot.UserId); // still the user's own data (my votes, export)
        Assert.Equal(AccessDenial.InviteRequired, (await AccessAsync(factory, id, VisitorContext.ForUser(me)))!.Denial);

        // A new invitation for the same account starts fresh; the old ballot stays counted on its own.
        var (_, hash2) = await InviteAsync(factory, owner, id);
        Assert.True((await SubmitAsync(factory, id, new VisitorContext(me, null, hash2), RomeFirst)).Succeeded);
        Assert.Equal(2, (await BallotsAsync(factory, id)).Count);
    }

    // ---- anonymous stays anonymous -----------------------------------------------------------------

    /// <summary>
    /// Audit 2026-10-26 finding 1: the anonymous invite cookie is bound to "anonymous", so the account signing in on the
    /// same browser neither sees nor can claim the token's ballot (a submit is refused); the cookie is dropped.
    /// </summary>
    [Fact]
    public async Task Signing_in_on_a_browser_does_not_merge_or_claim_its_anonymous_state()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var me = await CreateUserAsync(factory, "me@example.com");
        var id = await CreatePollAsync(factory, owner, Draft(voters: VoterMode.Invite));
        var (token, _) = await InviteAsync(factory, owner, id);

        var (browser, jar) = factory.CreateBrowserWithJar();
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync($"/p/{id}?t={token}")).StatusCode);
        Assert.True((await SubmitAsync(factory, id, VisitorFrom(factory, jar, id), ParisFirst)).Succeeded);
        Assert.Equal(HttpStatusCode.Redirect, (await AppFactory.LoginAsync(browser, "me@example.com", Password)).StatusCode);

        // Signed in, the anonymous cookie does not count: the account is asked for its own link; nothing is claimed.
        var visitor = VisitorFrom(factory, jar, id, me);
        Assert.Null(visitor.InviteTokenHash);
        var page = await PageAsync(browser, $"/p/{id}");
        Assert.DoesNotContain("You have already voted", page);
        Assert.Contains("invite-only", page);
        Assert.False(HasCookie(jar, id, PollCookies.InviteCookieName(id))); // dropped on that view
        var refused = await SubmitAsync(factory, id, visitor, RomeFirst);
        Assert.Equal(AccessDenial.InviteRequired, refused.Access!.Denial);
        Assert.Null(Assert.Single(await TokensAsync(factory, id)).ClaimedByUserId);
        Assert.Null(Assert.Single(await BallotsAsync(factory, id)).UserId);
        Assert.Empty(await WithScope(factory, sp => sp.GetRequiredService<BallotService>().ListMyVotesAsync(me)));
    }

    // ---- migration -----------------------------------------------------------------------------------

    /// <summary>
    /// Seeds a database in the pre-IdentityModel shape (migrated to Phase5bLimits): an invite poll whose token
    /// A was used while signed in (old model: <c>UserId</c> recorded on a <c>k:invite:</c> ballot) plus a later
    /// anonymous revision, token B used anonymously, an account with two used tokens (C, D; left unclaimed,
    /// they would collide), and a public poll (then password-protected) with an anonymous ballot. Then lets the
    /// app migrate it at startup.
    /// </summary>
    [SqliteOnlyFact]
    public async Task IdentityModel_migration_claims_signed_in_invites_and_keeps_ballots()
    {
        var dir = Path.Combine(Path.GetTempPath(), "telrang-tests-" + Guid.NewGuid().ToString("N"));
        var settings = new Dictionary<string, string?> { ["App:DataDir"] = dir };
        var invitePoll = PollId.NewId();
        var publicPoll = PollId.NewId();
        const string rawVk = "pre-migration-browser";
        var (_, hashA) = InviteTokens.Generate();
        var (_, hashB) = InviteTokens.Generate();
        var (_, hashC) = InviteTokens.Generate();
        var (_, hashD) = InviteTokens.Generate();
        var alice = Guid.NewGuid().ToString();
        var bob = Guid.NewGuid().ToString();
        var owner = Guid.NewGuid().ToString();
        try
        {
            string voterKey;
            await using (var first = new AppFactory(settings))
            {
                voterKey = first.Services.GetRequiredService<VoterKeys>().ForPoll(rawVk, publicPoll);
            }
            SqliteConnection.ClearAllPools();
            foreach (var file in Directory.GetFiles(dir, "app.db*")) File.Delete(file);

            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite($"Data Source={Path.Combine(dir, "app.db")}").Options;
            await using (var db = new ApplicationDbContext(options))
            {
                await db.GetService<IMigrator>().MigrateAsync("20261026071411_Phase5bLimits");
                foreach (var (uid, email) in new[] { (owner, "owner@example.com"), (alice, "alice@example.com"), (bob, "bob@example.com") })
                {
                    await db.Database.ExecuteSqlRawAsync(
                        """
                        INSERT INTO "AspNetUsers" ("Id", "UserName", "NormalizedUserName", "Email", "NormalizedEmail", "EmailConfirmed",
                            "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount", "CreatedAt", "PendingSetup", "MustEnrolTwoFactor")
                        VALUES ({0}, {1}, {2}, {1}, {2}, 1, 0, 0, 1, 0, '2026-10-01 10:00:00+00:00', 0, 0);
                        """, uid, email, email.ToUpperInvariant());
                }
                // Today's definition and payload shape (DefinitionJson is never migrated; no release had another).
                string Definition(string mode) => PollJson.Serialize(TestUtil.Definition(Ranked("Old trip", "Paris", "Rome")
                    .Results(ResultsVisibility.AfterVote).Closes("2099-01-31").Zone("UTC").Voters(Enum.Parse<VoterMode>(mode))));
                async Task InsertPoll(Guid pollId, string mode, string? passwordHash, int version) =>
                    await db.Database.ExecuteSqlRawAsync(
                        """
                        INSERT INTO "Polls" ("Id", "OwnerId", "Title", "Type", "Scoring", "Source", "DefinitionJson", "PasswordHash", "PasswordVersion",
                            "ResultsVisibility", "VoterMode", "ClosesAt", "CreatedAt", "UpdatedAt")
                        VALUES ({0}, {1}, 'Old trip', 'Ranked', 'Borda', {2}, {3}, {4}, {5}, 'AfterVote', {6}, '2099-01-31 23:59:59.9999999+00:00',
                            '2026-10-01 10:00:00+00:00', '2026-10-01 10:00:00+00:00');
                        """, pollId.ToString().ToUpperInvariant(), owner, $"title: Old trip\ntype: ranked\nresults: after-vote\nvoters: {mode.ToLowerInvariant()}\n---\n- Paris\n- Rome\n",
                        Definition(mode), passwordHash!, version, mode);
                await InsertPoll(invitePoll, "Invite", null, 0);
                await InsertPoll(publicPoll, "Public", "AQAAAAIAAAAA-not-a-real-hash", 1);

                async Task<Guid> InsertBallot(Guid pollId, string voterKey, string? userId, int responseNo, string ranked)
                {
                    var ballotId = Guid.NewGuid();
                    await db.Database.ExecuteSqlRawAsync(
                        """
                        INSERT INTO "Ballots" ("Id", "PollId", "VoterKey", "UserId", "VoterIdentity", "ResponseNo", "DisplayName", "SubmittedAt", "UpdatedAt", "PayloadJson")
                        VALUES ({0}, {1}, {2}, {3}, {4}, {5}, NULL, '2026-10-02 10:00:00+00:00', '2026-10-02 10:00:00+00:00', {6});
                        """, ballotId.ToString().ToUpperInvariant(), pollId.ToString().ToUpperInvariant(), voterKey, userId!, "k:" + voterKey, responseNo,
                        $$$"""{"q1":{"ranked":[{{{ranked}}}],"banned":[]}}""");
                    return ballotId;
                }
                async Task InsertToken(string hash, Guid? ballotId) =>
                    await db.Database.ExecuteSqlRawAsync(
                        """
                        INSERT INTO "InviteTokens" ("Id", "PollId", "TokenHash", "Label", "CreatedAt", "BallotId")
                        VALUES ({0}, {1}, {2}, NULL, '2026-10-01 11:00:00+00:00', {3});
                        """, Guid.NewGuid().ToString().ToUpperInvariant(), invitePoll.ToString().ToUpperInvariant(), hash,
                        ballotId?.ToString().ToUpperInvariant()!);

                // A: cast signed in by Alice (UserId recorded), revised later; B: anonymous; C and D: both Bob's.
                var ballotA = await InsertBallot(invitePoll, "invite:" + hashA, alice, 1, "\"rome\",\"paris\"");
                await InsertToken(hashA, ballotA);
                await InsertToken(hashB, await InsertBallot(invitePoll, "invite:" + hashB, null, 1, "\"paris\""));
                await InsertToken(hashC, await InsertBallot(invitePoll, "invite:" + hashC, bob, 1, "\"paris\""));
                await InsertToken(hashD, await InsertBallot(invitePoll, "invite:" + hashD, bob, 1, "\"rome\""));
                await InsertBallot(publicPoll, voterKey, null, 1, "\"rome\"");
            }
            SqliteConnection.ClearAllPools();

            await using var factory = new AppFactory(settings);
            var inviteId = PollId.Encode(invitePoll);
            var publicId = PollId.Encode(publicPoll);
            var ballots = await BallotsAsync(factory, inviteId);
            Assert.Equal(4, ballots.Count);
            var tokens = await TokensAsync(factory, inviteId);
            Assert.Equal(alice, tokens.Single(t => t.TokenHash == hashA).ClaimedByUserId);
            Assert.All(tokens.Where(t => t.TokenHash != hashA), t => Assert.Null(t.ClaimedByUserId));
            var aliceBallot = Assert.Single(ballots, b => b.UserId == alice);
            Assert.Equal("u:" + alice, aliceBallot.VoterIdentity);
            Assert.Null(aliceBallot.VoterKey);
            Assert.Equal(2, ballots.Count(b => b.UserId == bob && b.VoterKey!.StartsWith("invite:")));

            // Alice finds her ballot by account; her old link refuses anonymous use.
            var aliceState = (await WithScope(factory, sp => sp.GetRequiredService<BallotService>().GetVoteStateAsync(inviteId, VisitorContext.ForUser(alice))))!;
            Assert.True(aliceState.Access.HasBallot);
            Assert.Equal(["rome", "paris"], aliceState.Ballot!.Ranked!.Ranked);
            Assert.Equal(AccessDenial.InviteClaimed, (await AccessAsync(factory, inviteId, new VisitorContext(null, null, hashA)))!.Denial);
            Assert.True((await SubmitAsync(factory, inviteId, VisitorContext.ForUser(alice), ParisFirst)).Succeeded);

            // B (anonymous) and Bob's unclaimed C keep working as bearer links.
            Assert.True((await AccessAsync(factory, inviteId, new VisitorContext(null, null, hashB)))!.HasBallot);
            Assert.True((await AccessAsync(factory, inviteId, new VisitorContext(null, null, hashC)))!.HasBallot);

            // The vk ballot keeps working; the poll password is gone (RemovePollPasswordsAddAliases).
            var publicAccess = (await AccessAsync(factory, publicId, new VisitorContext(null, rawVk, null)))!;
            Assert.True(publicAccess.CanView);
            Assert.True(publicAccess.HasBallot);
            Assert.True(publicAccess.CanSeeResults);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ---- secrets and the log ring --------------------------------------------------------------------

    [Fact]
    public async Task Setup_key_goes_to_the_console_logger_but_never_into_the_status_log_ring()
    {
        var capture = new CapturingLoggerProvider();
        await using var factory = new AppFactory(services: s => s.AddSingleton<ILoggerProvider>(capture));
        _ = factory.Services; // starts the host: no admin yet, so the setup key is issued and logged

        var keyLine = Assert.Single(capture.Entries, e => e.EventId.Id == SecretLogEvents.SetupKey.Id);
        Assert.Contains("Admin setup key: ", keyLine.Message);
        var ring = factory.Services.GetRequiredService<LogRing>();
        Assert.DoesNotContain(ring.Snapshot(), e => e.Message.Contains("setup key", StringComparison.OrdinalIgnoreCase));

        // Same filter for the recovery key; ordinary warnings still reach the ring.
        var logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Test");
        logger.LogWarning(SecretLogEvents.RecoveryKey, "Admin recovery key: {RecoveryKey}", "secret-value");
        logger.LogWarning("Ordinary warning");
        Assert.Contains(capture.Entries, e => e.Message.Contains("secret-value"));
        Assert.DoesNotContain(ring.Snapshot(), e => e.Message.Contains("secret-value"));
        Assert.Contains(ring.Snapshot(), e => e.Message == "Ordinary warning");
    }

    private sealed record CapturedLog(EventId EventId, LogLevel Level, string Message);

    /// <summary>Stands in for the console logger: records every entry.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<CapturedLog> entries = [];

        public IReadOnlyList<CapturedLog> Entries
        {
            get { lock (entries) return entries.ToList(); }
        }

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose() { }

        private sealed class Logger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                lock (owner.entries) owner.entries.Add(new CapturedLog(eventId, logLevel, formatter(state, exception)));
            }
        }
    }
}
