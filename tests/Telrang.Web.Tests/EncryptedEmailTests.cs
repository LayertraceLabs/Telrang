using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Telrang.Web.Data;
using Telrang.Web.Features.Accounts;
using Telrang.Web.Features.Admin;
using Telrang.Web.Features.Mail;
using Telrang.Web.Features.Settings;
using Telrang.Web.Security;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>
/// DESIGN.md §30: email addresses are encrypted at rest and looked up by a keyed hash. A database copy holds no address;
/// sign-in, uniqueness, email change, transfer and the outbox limits work through the hash; existing rows are upgraded at
/// startup; a lost key ring leaves the app running with "unreadable address".
/// </summary>
public class EncryptedEmailTests
{
    private static AdminService AdminOf(IServiceProvider sp) => sp.GetRequiredService<AdminService>();

    private static async Task<(string Id, ClaimsPrincipal Principal)> AdminAsync(AppFactory factory, string email = "admin@example.com")
    {
        var principal = await LiveResultsNotifierTests.AdminAsync(factory, email);
        return (principal.FindFirstValue(ClaimTypes.NameIdentifier)!, principal);
    }

    /// <summary>Every text value in every table of the app's model that holds an '@', as "table.column: value".</summary>
    private static Task<List<string>> AddressesInDatabaseAsync(AppFactory factory) => WithScope(factory, async sp =>
    {
        var db = sp.GetRequiredService<ApplicationDbContext>();
        var found = new List<string>();
        foreach (var entity in CopyToPostgresCommand.CopyOrder(db.Model))
        {
            var table = entity.GetTableName()!;
            foreach (var property in entity.GetProperties().Where(p => p.ClrType == typeof(string)))
            {
                var column = property.GetColumnName();
                var sql = $"SELECT \"{column}\" AS \"Value\" FROM \"{table}\" WHERE \"{column}\" LIKE '%@%'";
                if (table == "AppSettings")
                {
                    // The server's own sender address and SMTP login are the operator's settings, not a user's address.
                    sql += $" AND \"Key\" NOT IN ('{SettingKeys.MailFromAddress}', '{SettingKeys.MailUsername}')";
                }
                foreach (var value in await db.Database.SqlQueryRaw<string>(sql).ToListAsync()) found.Add($"{table}.{column}: {value}");
            }
        }
        return found;
    });

    /// <summary>
    /// An instance in use: self-registered, admin-created, invited and set-up accounts, display names, a changed email,
    /// queued mail (verification, reset, invite, change notices), admin actions (transfer, lock, block, invite), sessions.
    /// A copy of its database contains no '@' anywhere (beyond the server's own sender address in the settings).
    /// </summary>
    [Fact]
    public async Task A_database_copy_holds_no_email_address_anywhere()
    {
        await using var factory = new AppFactory();
        await ConfigureMailAsync(factory, dailyMax: 20, cooldownMinutes: 0);
        var (_, admin) = await AdminAsync(factory, "boss@example.com");

        await AppFactory.RegisterAsync(factory.CreateBrowser(), "self@example.com", Password);
        var ownerId = await CreateUserAsync(factory, "owner@example.com");
        var targetId = await CreateUserAsync(factory, "target@example.com");
        var poll = await CreatePollAsync(factory, ownerId, RankedDraft());
        var signedIn = await LoggedInBrowserAsync(factory, "owner@example.com");
        Assert.Equal(HttpStatusCode.OK, (await signedIn.GetAsync("/polls")).StatusCode);

        await WithScope(factory, async sp =>
        {
            var accounts = sp.GetRequiredService<AccountService>();
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            await accounts.RequestPasswordResetAsync("owner@example.com");
            var target = (await users.FindByIdAsync(targetId))!;
            target.DisplayName = "Target Person";
            await users.UpdateAsync(target);
            Assert.NotNull(await accounts.RequestEmailChangeAsync(target, "target.new@example.com"));
            return 0;
        });
        Assert.True((await WithScope(factory, sp => AdminOf(sp).InviteAsync(admin, "invited@example.com"))).Ok);
        Assert.True((await WithScope(factory, sp => AdminOf(sp).TransferPollAsync(admin, poll, "TARGET@example.com"))).Ok);
        Assert.True((await WithScope(factory, sp => AdminOf(sp).AddBlockAsync(admin, "self@example.com", 5, "note"))).Ok);
        Assert.True((await WithScope(factory, sp => AdminOf(sp).LockAsync(admin, ownerId))).Ok);
        Assert.True((await WithScope(factory, sp => AdminOf(sp).SendTestMailAsync(admin))).Ok);

        var counts = await WithScope(factory, async sp =>
        {
            var db = sp.GetRequiredService<ApplicationDbContext>();
            return (Users: await db.Users.CountAsync(), Outbox: await db.OutboxMessages.CountAsync(), Log: await db.AdminActionLogs.CountAsync());
        });
        Assert.True(counts.Users >= 5 && counts.Outbox >= 4 && counts.Log >= 4, counts.ToString());
        Assert.Empty(await AddressesInDatabaseAsync(factory));

        // What the database holds instead: ciphertext and hashes, which the app reads back.
        var user = await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().Users.AsNoTracking().SingleAsync(u => u.Id == targetId));
        Assert.StartsWith(EmailProtection.Prefix, user.Email);
        Assert.StartsWith(EmailProtection.Prefix, user.UserName);
        Assert.NotEqual(user.Email, user.UserName); // randomised encryption
        Assert.Equal(EmailHash(factory, "target@example.com"), user.NormalizedEmail);
        Assert.Equal(EmailHash(factory, "TARGET@EXAMPLE.COM"), user.NormalizedUserName);
        Assert.Equal("target@example.com", Reveal(factory, user.Email));
        var mails = Revealed(factory, await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().OutboxMessages.AsNoTracking().ToListAsync()));
        Assert.Contains(mails, m => m.To == "owner@example.com" && m.Type == MailType.PasswordReset && m.TextBody!.Contains("ResetPassword?token="));
        Assert.Contains(mails, m => m.To == "target.new@example.com" && m.Type == MailType.EmailChange);
    }

    [Fact]
    public async Task Sign_in_registration_and_uniqueness_find_accounts_through_the_hash_in_any_case()
    {
        await using var factory = new AppFactory();
        await CreateUserAsync(factory, "Alice@Example.com");

        Assert.Equal(HttpStatusCode.Redirect, (await AppFactory.LoginAsync(factory.CreateBrowser(), "alice@EXAMPLE.COM", Password)).StatusCode);
        var wrong = await AppFactory.LoginAsync(factory.CreateBrowser(), "alice@example.org", Password);
        Assert.DoesNotContain("/polls", wrong.Headers.Location?.ToString() ?? "");

        await AppFactory.RegisterAsync(factory.CreateBrowser(), "ALICE@example.COM", Password);
        Assert.Equal(1, await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().Users.CountAsync()));
        var duplicate = await WithScope(factory, sp => sp.GetRequiredService<UserManager<ApplicationUser>>()
            .CreateAsync(new ApplicationUser { UserName = "x@example.com", Email = "aLiCe@example.com" }, Password));
        Assert.Contains(duplicate.Errors, e => e.Code == nameof(IdentityErrorDescriber.DuplicateEmail));

        var found = await WithScope(factory, sp => sp.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync("ALICE@EXAMPLE.COM"));
        Assert.NotNull(found);
        Assert.Equal("Alice@Example.com", await WithScope(factory, sp => sp.GetRequiredService<UserManager<ApplicationUser>>().GetEmailAsync(found!)));
    }

    [Fact]
    public async Task An_email_change_moves_the_lookup_to_the_new_address()
    {
        await using var factory = new AppFactory();
        await ConfigureMailAsync(factory);
        var id = await CreateUserAsync(factory, "old@example.com");
        await WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(id))!;
            var token = await users.GenerateChangeEmailTokenAsync(user, "New@example.com");
            var code = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
            Assert.True((await sp.GetRequiredService<AccountService>().ConfirmEmailChangeAsync(user, "New@example.com", code)).Succeeded);
            return 0;
        });
        Assert.Equal(HttpStatusCode.Redirect, (await AppFactory.LoginAsync(factory.CreateBrowser(), "new@example.com", Password)).StatusCode);
        Assert.Null(await WithScope(factory, sp => sp.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync("old@example.com")));
        var row = Revealed(factory, await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().Users.AsNoTracking().SingleAsync(u => u.Id == id)));
        Assert.Equal(("New@example.com", "New@example.com"), (row.Email, row.UserName));
        // The notice to the old address went out (decrypted at sending).
        await factory.SendMailAsync();
        Assert.Contains(factory.Mail.Sent, m => m.To == "old@example.com" && m.Subject.Contains("email was changed"));
    }

    [Fact]
    public async Task Outbox_limits_count_one_recipient_in_any_case()
    {
        await using var factory = new AppFactory();
        await ConfigureMailAsync(factory, cooldownMinutes: 15);
        var queue = factory.Services.GetRequiredService<IMailQueue>();
        Assert.True(await queue.EnqueueAsync("Bob@Example.com", "Reset", "https://localhost/x?token=t", MailType.PasswordReset));
        var again = await queue.TryEnqueueAsync("bob@example.COM", "Reset", "body", MailType.PasswordReset);
        Assert.Equal(MailDropReason.Cooldown, again.Reason);
        Assert.True(await queue.EnqueueAsync("carol@example.com", "Reset", "body", MailType.PasswordReset));

        var row = await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().OutboxMessages.AsNoTracking().FirstAsync(m => m.ToHash == EmailHash(factory, "BOB@example.com")));
        Assert.StartsWith(EmailProtection.Prefix, row.To);
        Assert.StartsWith(EmailProtection.BodyPrefix, row.TextBody); // R3-DEP-6: no live link in a queued row
        Assert.DoesNotContain("token=t", row.TextBody);
        Assert.Equal(2, await factory.SendMailAsync());
        Assert.Contains(factory.Mail.Sent, m => m.To == "Bob@Example.com" && m.TextBody.Contains("token=t"));
        Assert.Contains((await factory.Services.GetRequiredService<MailSender>().RecentAsync()), m => m.To == "Bob@Example.com");
    }

    [Fact]
    public async Task Admin_pages_show_addresses_and_transfer_finds_the_target_by_its_hash()
    {
        await using var factory = new AppFactory();
        var (_, admin) = await AdminAsync(factory);
        var ownerId = await CreateUserAsync(factory, "owner@example.com");
        await CreateUserAsync(factory, "target@example.com");
        var poll = await CreatePollAsync(factory, ownerId, RankedDraft());

        Assert.False((await WithScope(factory, sp => AdminOf(sp).TransferPollAsync(admin, poll, "target@example"))).Ok); // no partial match
        var moved = await WithScope(factory, sp => AdminOf(sp).TransferPollAsync(admin, poll, " Target@Example.com "));
        Assert.True(moved.Ok, moved.Message);
        Assert.Contains("target@example.com", moved.Message);

        var rows = (await WithScope(factory, sp => AdminOf(sp).ListUsersAsync(admin)))!;
        Assert.Equal(["admin@example.com", "owner@example.com", "target@example.com"], rows.Select(r => r.Email));
        var polls = (await WithScope(factory, sp => AdminOf(sp).ListPollsAsync(admin)))!;
        Assert.Equal("target@example.com", Assert.Single(polls).OwnerEmail);
        var (log, _) = (await WithScope(factory, sp => AdminOf(sp).ListLogAsync(admin, 0)))!.Value;
        Assert.Contains(log, l => l.Note!.Contains("owner@example.com -> target@example.com") && l.AdminEmail == "admin@example.com");

        // The account page and "Download my data" show the address in clear.
        var browser = await LoggedInBrowserAsync(factory, "owner@example.com");
        var page = await (await browser.GetAsync("/Account/Manage")).Content.ReadAsStringAsync();
        Assert.Contains("owner@example.com", page);
        Assert.DoesNotContain(EmailProtection.Prefix + "CfDJ8", page);
        var export = Encoding.UTF8.GetString(await WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            return await sp.GetRequiredService<AccountService>().ExportAsync((await users.FindByIdAsync(ownerId))!);
        }));
        Assert.Contains("\"email\": \"owner@example.com\"", export);
    }

    /// <summary>
    /// A database written before §30 (plain addresses, upper-case normalised values, plain outbox and admin log notes) is
    /// encrypted at the next start: idempotent, row by row, and sign-in by email keeps working. Both providers.
    /// </summary>
    [Fact]
    public async Task Startup_encrypts_a_populated_database_once_and_sign_in_keeps_working()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "telrang-tests-" + Guid.NewGuid().ToString("N"));
        var config = new Dictionary<string, string?> { ["App:DataDir"] = dataDir };
        try
        {
            await using (var before = new AppFactory(config))
            {
                await CreateUserAsync(before, "keep@example.com"); // written by this version: encrypted already
                await WithScope(before, async sp =>
                {
                    var db = sp.GetRequiredService<ApplicationDbContext>();
                    var hasher = sp.GetRequiredService<IPasswordHasher<ApplicationUser>>();
                    foreach (var email in new[] { "Legacy@Example.com", "second@example.com" })
                    {
                        var user = new ApplicationUser
                        {
                            UserName = email, NormalizedUserName = email.ToUpperInvariant(), Email = email, NormalizedEmail = email.ToUpperInvariant(),
                            EmailConfirmed = true, SecurityStamp = Guid.NewGuid().ToString("N"),
                        };
                        user.PasswordHash = hasher.HashPassword(user, Password);
                        db.Users.Add(user); // straight into the table, as the earlier version stored it
                    }
                    var pending = new ApplicationUser { PendingSetup = true, Email = "pending@example.com", NormalizedEmail = "PENDING@EXAMPLE.COM" };
                    pending.UserName = pending.Id;
                    pending.NormalizedUserName = pending.Id.ToUpperInvariant();
                    db.Users.Add(pending);
                    db.OutboxMessages.Add(new OutboxMessage
                    {
                        Id = Guid.NewGuid(), To = "legacy@example.com", Subject = "Reset", TextBody = "https://x/Account/ResetPassword?token=abc",
                        Type = MailType.PasswordReset, CreatedAt = DateTimeOffset.UtcNow, SendAfter = DateTimeOffset.UtcNow,
                        LastError = "SmtpCommandException: 550 <legacy@example.com> mailbox unavailable",
                    });
                    db.AdminActionLogs.Add(new AdminActionLog { At = DateTimeOffset.UtcNow, AdminUserId = "a", Action = "user.lock", Note = "legacy@example.com" });
                    await db.SaveChangesAsync();
                    return 0;
                });
                Assert.NotEmpty(await AddressesInDatabaseAsync(before));
            }

            await using var after = new AppFactory(config); // the startup step runs
            Assert.Empty(await AddressesInDatabaseAsync(after));
            Assert.Equal(HttpStatusCode.Redirect, (await AppFactory.LoginAsync(after.CreateBrowser(), "legacy@example.com", Password)).StatusCode);
            Assert.Equal(HttpStatusCode.Redirect, (await AppFactory.LoginAsync(after.CreateBrowser(), "KEEP@example.com", Password)).StatusCode);
            var outbox = Revealed(after, await WithScope(after, sp => sp.GetRequiredService<ApplicationDbContext>().OutboxMessages.AsNoTracking().ToListAsync()));
            var mail = Assert.Single(outbox);
            Assert.Equal(("legacy@example.com", "https://x/Account/ResetPassword?token=abc"), (mail.To, mail.TextBody));
            Assert.Equal("SmtpCommandException: 550 <address> mailbox unavailable", mail.LastError);
            Assert.Equal(EmailHash(after, "LEGACY@example.com"), (await WithScope(after, sp => sp.GetRequiredService<ApplicationDbContext>().OutboxMessages.AsNoTracking().SingleAsync())).ToHash);
            var pendingRow = await WithScope(after, sp => sp.GetRequiredService<ApplicationDbContext>().Users.AsNoTracking().SingleAsync(u => u.PendingSetup));
            Assert.Equal(pendingRow.Id, Reveal(after, pendingRow.UserName));
            Assert.Equal("pending@example.com", Reveal(after, pendingRow.Email));

            // Once is enough: a second run (another start) changes nothing.
            var again = await WithScope(after, sp => EmailUpgrade.RunAsync(sp.GetRequiredService<ApplicationDbContext>(),
                sp.GetRequiredService<EmailProtection>(), NullLogger.Instance));
            Assert.Equal(new EmailUpgrade.Result(0, 0, 0, 0), again);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dataDir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>An upgrade interrupted half-way continues with the rows it did not reach and leaves the done ones alone.</summary>
    [Fact]
    public async Task The_upgrade_resumes_where_it_stopped()
    {
        await using var factory = new AppFactory();
        var emails = factory.Services.GetRequiredService<EmailProtection>();
        var done = await CreateUserAsync(factory, "done@example.com");
        var doneRow = await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().Users.AsNoTracking().SingleAsync(u => u.Id == done));
        await WithScope(factory, async sp =>
        {
            var db = sp.GetRequiredService<ApplicationDbContext>();
            for (var i = 0; i < 450; i++) // more than two batches
            {
                var email = $"user{i}@example.com";
                db.Users.Add(new ApplicationUser { UserName = email, NormalizedUserName = email.ToUpperInvariant(), Email = email, NormalizedEmail = email.ToUpperInvariant() });
            }
            await db.SaveChangesAsync();
            return 0;
        });
        var result = await WithScope(factory, sp => EmailUpgrade.RunAsync(sp.GetRequiredService<ApplicationDbContext>(), emails, NullLogger.Instance));
        Assert.Equal(450, result.Accounts);
        var after = await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().Users.AsNoTracking().SingleAsync(u => u.Id == done));
        Assert.Equal((doneRow.Email, doneRow.UserName, doneRow.NormalizedEmail), (after.Email, after.UserName, after.NormalizedEmail));
        Assert.NotNull(await WithScope(factory, sp => sp.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync("USER449@example.com")));
        Assert.Empty(await AddressesInDatabaseAsync(factory));
    }

    /// <summary>
    /// The key ring is lost (its key files replaced) but the database stays: the app starts, the admin list shows
    /// "unreadable address", sign-in by email fails for those accounts, new accounts work, and the old email key is kept.
    /// </summary>
    [Fact]
    public async Task A_lost_key_ring_leaves_unreadable_addresses_and_the_app_running()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "telrang-tests-" + Guid.NewGuid().ToString("N"));
        var config = new Dictionary<string, string?> { ["App:DataDir"] = dataDir };
        try
        {
            await using (var before = new AppFactory(config))
            {
                await CreateUserAsync(before, "lost@example.com");
                Assert.Equal(HttpStatusCode.Redirect, (await AppFactory.LoginAsync(before.CreateBrowser(), "lost@example.com", Password)).StatusCode);
            }
            var keys = Path.Combine(dataDir, "keys");
            foreach (var file in Directory.GetFiles(keys, "key-*.xml")) File.Delete(file);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            await using var after = new AppFactory(config);
            var (_, admin) = await AdminAsync(after, "fresh@example.com");
            var rows = (await WithScope(after, sp => AdminOf(sp).ListUsersAsync(admin)))!;
            Assert.Contains(rows, r => r.Email == EmailProtection.UnreadableLabel);
            Assert.Contains(rows, r => r.Email == "fresh@example.com");
            var login = await AppFactory.LoginAsync(after.CreateBrowser(), "lost@example.com", Password);
            Assert.DoesNotContain("/polls", login.Headers.Location?.ToString() ?? "");
            Assert.Equal(HttpStatusCode.Redirect, (await AppFactory.LoginAsync(after.CreateBrowser(), "fresh@example.com", Password)).StatusCode);
            Assert.Single(Directory.GetFiles(keys, "emailkey.unreadable-*"));
            Assert.True(File.Exists(Path.Combine(keys, "emailkey")));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dataDir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Mail_server_errors_lose_the_addresses_they_quote()
    {
        Assert.Equal("550 5.1.1 <address>: Recipient address rejected (<address>)",
            EmailProtection.WithoutAddresses("550 5.1.1 <bob@example.com>: Recipient address rejected (x.y+z@sub.example.org)"));
        Assert.Equal("Connection refused", EmailProtection.WithoutAddresses("Connection refused"));
    }
}
