using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Telrang.Web.Data;
using Telrang.Web.Features.Admin;
using Telrang.Web.Security;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>
/// Server-side sessions (DESIGN.md §31, pentest R3-DEP-2): the cookie holds a random key, the ticket is in AuthSessions,
/// a key ring copy alone signs nobody in, and every revocation path ends sessions at once.
/// </summary>
public class AuthSessionTests
{
    private const string CookieName = ".AspNetCore.Identity.Application";
    private const string SessionIdClaim = "Microsoft.AspNetCore.Authentication.Cookies-SessionId";

    /// <summary>A Data Protection provider built only from a copy of {DataDir}/keys, as an attacker off the box would.</summary>
    private static IDataProtectionProvider FromKeyRingCopy(AppFactory factory)
    {
        _ = factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("warm").Protect("x"); // a key exists
        var copy = Directory.CreateTempSubdirectory("telrang-keys-copy-").FullName;
        foreach (var f in Directory.GetFiles(Path.Combine(factory.DataDir, "keys"))) File.Copy(f, Path.Combine(copy, Path.GetFileName(f)));
        var services = new ServiceCollection();
        services.AddDataProtection().SetApplicationName("Telrang").PersistKeysToFileSystem(new DirectoryInfo(copy));
        return services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
    }

    private static TicketDataFormat CookieFormat(IDataProtectionProvider dp) =>
        new(dp.CreateProtector("Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationMiddleware", IdentityConstants.ApplicationScheme, "v2"));

    private static string Forge(IDataProtectionProvider dp, params Claim[] claims)
    {
        var identity = new ClaimsIdentity(claims, IdentityConstants.ApplicationScheme);
        var now = DateTimeOffset.UtcNow;
        var props = new AuthenticationProperties { IssuedUtc = now, ExpiresUtc = now.AddDays(14), IsPersistent = true };
        return CookieFormat(dp).Protect(new AuthenticationTicket(new ClaimsPrincipal(identity), props, IdentityConstants.ApplicationScheme));
    }

    private static async Task<HttpResponseMessage> GetWithCookieAsync(AppFactory factory, string path, string cookie)
    {
        var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", CookieName + "=" + cookie);
        return await client.SendAsync(request);
    }

    private static async Task<(HttpClient Browser, string Cookie)> SignInAsync(AppFactory factory, string email)
    {
        var (browser, jar) = factory.CreateBrowserWithJar();
        Assert.Equal(HttpStatusCode.Redirect, (await AppFactory.LoginAsync(browser, email, Password)).StatusCode);
        return (browser, jar.GetCookies(new Uri("https://localhost/"))[CookieName]!.Value);
    }

    private static Task<List<AuthSession>> SessionsAsync(AppFactory factory) =>
        WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().AuthSessions.AsNoTracking().ToListAsync());

    /// <summary>The cookie signs someone in: /polls answers, or sends to the 2FA enrolment gate, but not to the login page.</summary>
    private static async Task<bool> SignedInAsync(AppFactory factory, string cookie)
    {
        var response = await GetWithCookieAsync(factory, "/polls", cookie);
        return response.StatusCode == HttpStatusCode.OK
               || (response.StatusCode == HttpStatusCode.Redirect && !response.Headers.Location!.ToString().Contains("/Account/Login"));
    }

    [Fact]
    public async Task The_cookie_holds_only_a_random_key_and_the_row_holds_neither_the_key_nor_readable_claims()
    {
        await using var factory = new AppFactory();
        var userId = await CreateUserAsync(factory, "alice@example.com");
        var (_, cookie) = await SignInAsync(factory, "alice@example.com");

        var ticket = CookieFormat(factory.Services.GetRequiredService<IDataProtectionProvider>()).Unprotect(cookie)!;
        var claim = Assert.Single(ticket.Principal.Claims);
        Assert.Equal(SessionIdClaim, claim.Type);
        Assert.True(SecretTokens.TryHash(claim.Value, out var hash));

        var row = Assert.Single(await SessionsAsync(factory));
        Assert.Equal((hash, userId), (row.Id, row.UserId));
        Assert.NotEqual(claim.Value, row.Id);
        Assert.DoesNotContain("alice", row.Ticket);
        Assert.True(await SignedInAsync(factory, cookie));
    }

    /// <summary>The pentest's P1: a full ticket forged with a copy of the key ring (the pre-§31 cookie) signs nobody in.</summary>
    [Fact]
    public async Task A_cookie_forged_with_the_key_ring_alone_is_refused()
    {
        await using var factory = new AppFactory();
        var admin = await LiveResultsNotifierTests.AdminAsync(factory);
        var adminId = admin.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var stamp = admin.FindFirstValue("AspNet.Identity.SecurityStamp")!;
        var dp = FromKeyRingCopy(factory);

        // A whole ticket, as cookies held before server-side sessions (also how every existing cookie is treated on upgrade).
        var full = Forge(dp, new Claim(ClaimTypes.NameIdentifier, adminId), new Claim(ClaimTypes.Name, "x"),
            new Claim(ClaimTypes.Role, AppRoles.Admin), new Claim("AspNet.Identity.SecurityStamp", stamp));
        // A session claim with a made-up key, and with the hash stored in the database (a database copy) as the key.
        var (madeUp, _) = SecretTokens.Generate();
        var (_, live) = await SignInAsync(factory, "admin@example.com");
        var storedHash = Assert.Single(await SessionsAsync(factory)).Id;

        foreach (var forged in new[] { full, Forge(dp, new Claim(SessionIdClaim, madeUp)), Forge(dp, new Claim(SessionIdClaim, storedHash)) })
        {
            var response = await GetWithCookieAsync(factory, "/admin", forged);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/Account/Login", response.Headers.Location!.ToString());
        }
        Assert.True(await SignedInAsync(factory, live));
    }

    [Fact]
    public async Task Sign_out_ends_the_session_and_the_old_cookie_stays_dead()
    {
        await using var factory = new AppFactory();
        await CreateUserAsync(factory, "alice@example.com");
        var (browser, cookie) = await SignInAsync(factory, "alice@example.com");
        var (_, other) = await SignInAsync(factory, "alice@example.com");
        Assert.Equal(2, (await SessionsAsync(factory)).Count);

        var logout = await AppFactory.SubmitFormAsync(browser, "/Account/Logout", "", new Dictionary<string, string> { ["returnUrl"] = "" },
            tokenSourcePath: "/polls");
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.False(await SignedInAsync(factory, cookie)); // a copy of the cookie taken before is worthless now
        Assert.True(await SignedInAsync(factory, other));
        Assert.Single(await SessionsAsync(factory));
    }

    [Fact]
    public async Task Log_out_everywhere_and_a_password_change_end_every_other_session_at_once()
    {
        await using var factory = new AppFactory();
        var userId = await CreateUserAsync(factory, "alice@example.com");
        var (browser, cookie) = await SignInAsync(factory, "alice@example.com");
        var (_, other) = await SignInAsync(factory, "alice@example.com");

        var changed = await AppFactory.SubmitFormAsync(browser, "/Account/Manage/ChangePassword", "change-password", new Dictionary<string, string>
        {
            ["Input.OldPassword"] = Password,
            ["Input.NewPassword"] = Password + "2",
            ["Input.ConfirmPassword"] = Password + "2",
        });
        Assert.Equal(HttpStatusCode.Redirect, changed.StatusCode);
        Assert.False(await SignedInAsync(factory, other)); // at once, not after the stamp validator's minute
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/polls")).StatusCode); // this browser got a new session
        Assert.False(await SignedInAsync(factory, cookie)); // under a new ticket: the old cookie value is dead

        Assert.Single(await SessionsAsync(factory), s => s.UserId == userId);
        var everywhere = await AppFactory.SubmitFormAsync(browser, "/Account/Manage/LogOutEverywhere", "", new Dictionary<string, string>(),
            tokenSourcePath: "/Account/Manage/PersonalData");
        Assert.Equal(HttpStatusCode.Redirect, everywhere.StatusCode);
        Assert.Empty(await SessionsAsync(factory));
    }

    [Fact]
    public async Task Admin_lock_delete_block_and_2fa_reset_end_the_accounts_sessions()
    {
        await using var factory = new AppFactory();
        var admin = await LiveResultsNotifierTests.AdminAsync(factory);
        var actions = new (string Email, Func<AdminService, string, Task<AdminOutcome>> Act)[]
        {
            ("locked@example.com", (a, id) => a.LockAsync(admin, id)),
            ("deleted@example.com", (a, id) => a.DeleteUserAsync(admin, id)),
            ("blocked@example.com", (a, _) => a.AddBlockAsync(admin, "blocked@example.com", 5, null)),
            ("reset@example.com", (a, id) => a.DisableTwoFactorAsync(admin, id)),
        };
        foreach (var (email, act) in actions)
        {
            var id = await CreateUserAsync(factory, email);
            var (_, cookie) = await SignInAsync(factory, email);
            Assert.True(await SignedInAsync(factory, cookie));
            var outcome = await WithScope(factory, sp => act(sp.GetRequiredService<AdminService>(), id));
            Assert.True(outcome.Ok, outcome.Message);
            Assert.DoesNotContain(await SessionsAsync(factory), s => s.UserId == id);
            Assert.False(await SignedInAsync(factory, cookie), email);
        }
    }

    [Fact]
    public async Task Expired_sessions_are_refused_and_purged()
    {
        await using var factory = new AppFactory();
        await CreateUserAsync(factory, "alice@example.com");
        var (_, cookie) = await SignInAsync(factory, "alice@example.com");
        var (_, other) = await SignInAsync(factory, "alice@example.com");
        var rows = await SessionsAsync(factory);
        await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().AuthSessions.Where(s => s.Id == rows[0].Id)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
        var store = factory.Services.GetRequiredService<AuthSessionStore>();
        Assert.Equal(1, await store.PurgeExpiredAsync());
        Assert.Single(await SessionsAsync(factory));
        Assert.True(await SignedInAsync(factory, cookie) ^ await SignedInAsync(factory, other));
    }

    [Fact]
    public void The_cookie_handler_field_the_session_handler_reflects_on_exists()
    {
        // SessionCookieHandler reads and clears CookieAuthenticationHandler's private session key. A framework update that
        // renames it should fail here by name, not as a type initializer error on the first sign-in.
        var field = typeof(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationHandler).GetField("_sessionKey",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.True(field is not null, "CookieAuthenticationHandler has no private _sessionKey field any more: update SessionCookieHandler.");
        Assert.Equal(typeof(string), field.FieldType);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(SessionCookieHandler).TypeHandle);
    }
}
