using System.Net;
using System.Security.Claims;
using Telrang.Web.Data;
using Telrang.Web.Features.Admin;
using Telrang.Web.Security;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>Admin Users tab: search, confirmed role changes, who may be made admin (DESIGN.md §10).</summary>
public class AdminRolesTests
{
    private static async Task<(string Id, ClaimsPrincipal Principal)> AdminAsync(AppFactory factory, string email = "admin@example.com")
    {
        var id = await CreateUserAsync(factory, email);
        var principal = await WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(id))!;
            Assert.True((await users.AddToRoleAsync(user, AppRoles.Admin)).Succeeded);
            return await sp.GetRequiredService<SignInManager<ApplicationUser>>().CreateUserPrincipalAsync(user);
        });
        return (id, principal);
    }

    private static Task<T> Admin<T>(AppFactory factory, Func<AdminService, Task<T>> action) =>
        WithScope(factory, sp => action(sp.GetRequiredService<AdminService>()));

    private static Task<bool> IsAdminAsync(AppFactory factory, string id) =>
        WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            return await users.IsInRoleAsync((await users.FindByIdAsync(id))!, AppRoles.Admin);
        });

    private static Task<List<string>> LogActionsAsync(AppFactory factory) =>
        WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().AdminActionLogs.OrderBy(l => l.Id).Select(l => l.Action).ToListAsync());

    private static Task<string> UnverifiedUserAsync(AppFactory factory, string email) =>
        WithScope(factory, async sp =>
        {
            var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = false };
            Assert.True((await sp.GetRequiredService<UserManager<ApplicationUser>>().CreateAsync(user, Password)).Succeeded);
            return user.Id;
        });

    [Fact]
    public async Task Search_by_complete_email_or_partial_display_name_returns_only_matches()
    {
        await using var factory = new AppFactory();
        var (_, admin) = await AdminAsync(factory, "boss@corp.test");
        var alice = await CreateUserAsync(factory, "alice@example.com");
        var bob = await CreateUserAsync(factory, "bob@example.com");
        await CreateUserAsync(factory, "carol@other.org");
        await WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var carol = (await users.FindByEmailAsync("carol@other.org"))!;
            carol.DisplayName = "Carol Smith";
            return (await users.UpdateAsync(carol)).Succeeded;
        });

        // Addresses are stored encrypted (DESIGN.md §30): a complete one matches in any case, a part of one matches nothing.
        var byEmail = (await Admin(factory, a => a.SearchUsersAsync(admin, "Alice@EXAMPLE.com")))!;
        Assert.Equal([alice], byEmail.Rows.Select(r => r.Id));
        Assert.Equal((1, "alice@example.com"), (byEmail.Total, byEmail.Rows[0].Email));
        Assert.False(byEmail.Truncated);
        Assert.Empty((await Admin(factory, a => a.SearchUsersAsync(admin, "example.com")))!.Rows);
        Assert.Equal([bob], (await Admin(factory, a => a.SearchUsersAsync(admin, " bob@example.com ")))!.Rows.Select(r => r.Id));

        var byName = Assert.Single((await Admin(factory, a => a.SearchUsersAsync(admin, "smi")))!.Rows);
        Assert.Equal(("carol@other.org", "Carol Smith"), (byName.Email, byName.DisplayName));

        Assert.Empty((await Admin(factory, a => a.SearchUsersAsync(admin, "nobody")))!.Rows);
        Assert.Equal(4, (await Admin(factory, a => a.SearchUsersAsync(admin, "  ")))!.Total);

        // Capped: the count covers every match, the rows only the first ones.
        var capped = (await Admin(factory, a => a.SearchUsersAsync(admin, null, max: 2)))!;
        Assert.Equal((2, 4, true), (capped.Rows.Count, capped.Total, capped.Truncated));

        var (_, user) = await WithScope(factory, async sp =>
        {
            var u = (await sp.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(alice))!;
            return (u.Id, await sp.GetRequiredService<SignInManager<ApplicationUser>>().CreateUserPrincipalAsync(u));
        });
        Assert.Null(await Admin(factory, a => a.SearchUsersAsync(user, "example")));
    }

    [Fact]
    public async Task Promotion_works_and_is_logged_and_the_page_asks_for_confirmation()
    {
        await using var factory = new AppFactory();
        var (_, admin) = await AdminAsync(factory);
        var userId = await CreateUserAsync(factory, "user@example.com");

        // Page: the row offers "Make admin…", which opens the confirm panel; no button promotes directly.
        var browser = await LoggedInBrowserAsync(factory, "admin@example.com");
        var page = await browser.GetAsync("/admin");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("id=\"user-search\"", html);
        Assert.Contains("2 users", html);
        Assert.Contains("Make admin…</button>", html);
        Assert.DoesNotContain("Make admin</button>", html);

        var panel = await WithScope(factory, async sp =>
        {
            await using var renderer = new HtmlRenderer(sp, sp.GetRequiredService<ILoggerFactory>());
            return await renderer.Dispatcher.InvokeAsync(async () =>
                (await renderer.RenderComponentAsync<AdminConfirm>(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(AdminConfirm.Id)] = "confirm-grant-" + userId,
                    [nameof(AdminConfirm.Text)] = "Grant admin rights to user@example.com",
                    [nameof(AdminConfirm.Action)] = "Make admin",
                }))).ToHtmlString());
        });
        Assert.Contains("class=\"confirm-panel", panel);
        Assert.Contains($"type=\"checkbox\" id=\"confirm-grant-{userId}\"", panel);
        Assert.Contains("Grant admin rights to user@example.com", panel);
        Assert.Matches("<button[^>]*disabled[^>]*>Make admin</button>", panel); // until ticked

        // Service: promotes and logs.
        var done = await Admin(factory, a => a.SetAdminAsync(admin, userId, true));
        Assert.True(done.Ok, done.Message);
        Assert.True(await IsAdminAsync(factory, userId));
        Assert.Equal(["user.promote"], await LogActionsAsync(factory));
        Assert.True((await Admin(factory, a => a.ListUsersAsync(admin)))!.Single(u => u.Id == userId).IsAdmin);

        var after = await (await browser.GetAsync("/admin")).Content.ReadAsStringAsync();
        Assert.Contains("Remove admin…</button>", after);
        Assert.DoesNotContain("Remove admin</button>", after);

        Assert.True((await Admin(factory, a => a.SetAdminAsync(admin, userId, false))).Ok);
        Assert.False(await IsAdminAsync(factory, userId));
        Assert.Equal(["user.promote", "user.demote"], await LogActionsAsync(factory));
    }

    [Fact]
    public async Task Unverified_and_pending_accounts_cannot_be_promoted()
    {
        await using var factory = new AppFactory();
        var (adminId, admin) = await AdminAsync(factory);
        var unverified = await UnverifiedUserAsync(factory, "new@example.com");
        var created = await Admin(factory, a => a.CreateAccountAsync(admin, "http://localhost/"));
        Assert.True(created.Ok);
        var pending = (await Admin(factory, a => a.ListUsersAsync(admin)))!.Single(u => u.PendingSetup).Id;

        var refused = await Admin(factory, a => a.SetAdminAsync(admin, unverified, true));
        Assert.False(refused.Ok);
        Assert.Contains("verified email", refused.Message);
        Assert.False((await Admin(factory, a => a.SetAdminAsync(admin, pending, true))).Ok);
        Assert.False(await IsAdminAsync(factory, unverified));
        Assert.False(await IsAdminAsync(factory, pending));
        Assert.DoesNotContain("user.promote", await LogActionsAsync(factory));

        var rows = (await Admin(factory, a => a.ListUsersAsync(admin)))!;
        Assert.Equal("Unverified", rows.Single(u => u.Id == unverified).AdminBlocker);
        Assert.Equal("Pending setup", rows.Single(u => u.Id == pending).AdminBlocker);
        Assert.Null(rows.Single(u => u.Id == adminId).AdminBlocker);

        // The row shows the reason instead of the button.
        var html = await (await (await LoggedInBrowserAsync(factory, "admin@example.com")).GetAsync("/admin")).Content.ReadAsStringAsync();
        Assert.Contains("admin-blocker\">Unverified</span>", html);
        Assert.DoesNotContain("Make admin…</button>", html);
    }

    [Fact]
    public async Task Removing_yourself_or_the_last_admin_is_refused()
    {
        await using var factory = new AppFactory();
        var (adminId, admin) = await AdminAsync(factory);
        var (otherId, other) = await AdminAsync(factory, "other@example.com");

        var self = await Admin(factory, a => a.SetAdminAsync(admin, adminId, false));
        Assert.False(self.Ok);
        Assert.Contains("another admin", self.Message);
        Assert.True(await IsAdminAsync(factory, adminId));

        // A principal issued while "other" was an admin, demoted in the database since: refused (the role is read there).
        await WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            return (await users.RemoveFromRoleAsync((await users.FindByIdAsync(otherId))!, AppRoles.Admin)).Succeeded;
        });
        var last = await Admin(factory, a => a.SetAdminAsync(other, adminId, false));
        Assert.False(last.Ok);
        Assert.Equal(AdminOutcome.Denied, last); // the role is read from the database (pentest R2-ACC-2)
        Assert.True(await IsAdminAsync(factory, adminId));
        Assert.Empty(await LogActionsAsync(factory));
    }
}
