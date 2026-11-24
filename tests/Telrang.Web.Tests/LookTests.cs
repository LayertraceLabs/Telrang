using System.Buffers.Text;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Telrang.Core.Ids;
using Telrang.Core.Polls;
using Telrang.Web.Data;
using Telrang.Web.Features.Accounts;
using Telrang.Web.Features.Admin;
using Telrang.Web.Features.Branding;
using Telrang.Web.Features.Looks;
using Telrang.Web.Features.Polls;
using Telrang.Web.Features.Settings;
using Telrang.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>
/// Poll looks and saved looks (DESIGN.md §34.6, §34.7): the one set of look rules (forms, stored JSON, look codes), the
/// token-only <c>look.css</c> behind the poll's access check, the admin switch, the editor and the looks page, import and
/// export, the account's data export and deletion.
/// </summary>
public partial class LookTests
{
    private static readonly ThemeLook Plum = ThemeLook.Create("#5a2a5f", "#d9a62e", "#2a6f2b", "#e0457b", "serif", 12).Look!;

    private static string Code(string json) => ThemeLook.CodePrefix + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));

    // ---- the rules ----

    [Fact]
    public void A_look_is_made_only_through_the_rules()
    {
        Assert.Equal(new ThemeLook("#5a2a5f", "#d9a62e", "#2a6f2b", "#e0457b", ThemeFont.Serif, 12), Plum);
        Assert.Equal("#abcdef", ThemeLook.Create(" #ABCDEF ", "#000000", "#000000", "#000000", "mono", 0).Look!.LightBase);

        Assert.Equal("Light base: use #rrggbb.", ThemeLook.Create("red", "#000000", "#000000", "#000000", "serif", 6).Error);
        Assert.Equal("Dark highlight: use #rrggbb.", ThemeLook.Create("#000000", "#000000", "#000000", "#00000", "serif", 6).Error);
        Assert.Equal("Light highlight: use #rrggbb.", ThemeLook.Create("#000000", "#000000; } body { display: none", "#000000", "#000000", "serif", 6).Error);
        Assert.Equal("Font: choose one from the list.", ThemeLook.Create("#000000", "#000000", "#000000", "#000000", "Comic Sans", 6).Error);
        Assert.Equal("Font: choose one from the list.", ThemeLook.Create("#000000", "#000000", "#000000", "#000000", "serif; } *{", 6).Error);
        Assert.Equal("Corner radius: 0 to 16 px.", ThemeLook.Create("#000000", "#000000", "#000000", "#000000", "serif", 17).Error);
        Assert.Equal("Corner radius: 0 to 16 px.", ThemeLook.Create("#000000", "#000000", "#000000", "#000000", "serif", -1).Error);
        Assert.Equal("Corner radius: 0 to 16 px.", ThemeLook.Create("#000000", "#000000", "#000000", "#000000", "serif", null).Error);

        // The instance theme's fields go by the same rules (one validator).
        Assert.Equal("Light base: use #rrggbb.", ThemeSettings.Validate(new Dictionary<string, string?> { [SettingKeys.ThemeLightBase] = "red" }));
        Assert.Equal(ThemeLook.FontMessage, ThemeSettings.Validate(new Dictionary<string, string?> { [SettingKeys.ThemeFontStack] = "Comic Sans" }));
        Assert.Equal(new ThemeLook(ThemeLook.DefaultBase, ThemeLook.DefaultHighlight, ThemeLook.DefaultBase, ThemeLook.DefaultHighlight, ThemeFont.SystemUi, 6),
            ThemeSettings.Default.Look);
    }

    [Fact]
    public void Look_codes_round_trip_and_anything_else_is_refused_with_one_line()
    {
        var code = Plum.ToCode();
        Assert.StartsWith("tl1.", code);
        Assert.True(Encoding.UTF8.GetByteCount(code) <= ThemeLook.MaxCodeBytes);
        Assert.Equal(Plum, ThemeLook.FromCode(code).Look);
        Assert.Equal(Plum, ThemeLook.FromCode("  " + code + "\n").Look);
        Assert.Equal(Plum, ThemeLook.FromStored(Plum.ToJson()));
        Assert.Equal(ThemeLook.BuiltIn, ThemeLook.FromCode(ThemeLook.BuiltIn.ToCode()).Look);

        var notALook = new[]
        {
            null, "", "tl1.", "tl2." + code[4..], code[4..], "tl1.!!!!", "tl1." + Base64Url.EncodeToString([0xff, 0xfe, 0x00]),
            Code("[]"), Code("null"), Code("\"x\""), Code("{\"lightBase\": {\"a\": 1}}"),
            Code(Plum.ToJson().Replace("\"radius\":12", "\"radius\":\"12\"", StringComparison.Ordinal)), // a number as text
            Code(Plum.ToJson().Replace("}", ",\"css\":\"body{display:none}\"}", StringComparison.Ordinal)), // unknown member
            Code(Plum.ToJson().Replace("}", ",\"radius\":3}", StringComparison.Ordinal)), // a member twice
            Code(Plum.ToJson() + new string(' ', 400)), // over 512 bytes
        };
        foreach (var c in notALook)
        {
            var (look, error) = ThemeLook.FromCode(c);
            Assert.Null(look);
            Assert.Equal(ThemeLook.NotALookMessage, error);
        }
        // Well-formed but against the rules: the form's own line.
        Assert.Equal("Dark base: use #rrggbb.", ThemeLook.FromCode(Code(Plum.ToJson().Replace("#2a6f2b", "url(x)", StringComparison.Ordinal))).Error);
        Assert.Equal("Corner radius: 0 to 16 px.", ThemeLook.FromCode(Code(Plum.ToJson().Replace("\"radius\":12", "\"radius\":99", StringComparison.Ordinal))).Error);
        Assert.Equal("Light base: use #rrggbb.", ThemeLook.FromCode(Code("{}")).Error);
        Assert.Null(ThemeLook.FromStored("{\"lightBase\":\"red\"}")); // a stored value that fails reads as no look
    }

    [GeneratedRegex(@"^ *(--av-[a-z-]+): ([^;{}]+);$")]
    private static partial Regex TokenLine();

    [Fact]
    public void Poll_look_css_holds_token_overrides_and_nothing_else()
    {
        var css = ThemeCss.PollLook(Plum);
        var lines = css.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("/* Poll look. Light: #5a2a5f #d9a62e, dark: #2a6f2b #e0457b. */", lines[0]);
        string[] structure = [":root {", "}", ":root[data-bs-theme=dark] {", "@media (prefers-color-scheme: dark) {", "  :root:not([data-bs-theme]) {", "  }"];
        foreach (var line in lines.Skip(1))
            Assert.True(structure.Contains(line) || TokenLine().IsMatch(line), line);
        Assert.Contains("  --av-radius: 12px;\n", css);
        Assert.Contains("  --av-font: " + ThemeLook.Fonts.First(f => f.Font == ThemeFont.Serif).Stack + ";\n", css);
        var light = PaletteDerivation.Derive("#5a2a5f", "#d9a62e", dark: false);
        var dark = PaletteDerivation.Derive("#2a6f2b", "#e0457b", dark: true);
        Assert.Contains($"  --av-primary: {light.Primary.Hex};\n", css);
        Assert.Contains($"  --av-primary: {dark.Primary.Hex};\n", css);

        // The instance theme's tokens are the same writer's (DESIGN.md §34.6): the default look gives theme.css's blocks.
        var theme = ThemeCss.Generate(ThemeSettings.Default, null);
        Assert.Contains(ThemeCss.PollLook(ThemeLook.BuiltIn)["/* Poll look. ".Length..], theme);

        // The preview: the same tokens, scoped to its two panels.
        var preview = ThemeCss.Preview(Plum);
        foreach (var line in preview.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
            Assert.True(line is ".look-preview {" or ".look-preview-light {" or ".look-preview-dark {" or "}" || TokenLine().IsMatch(line), line);
    }

    // ---- look.css and the vote pages ----

    private static async Task<string> CreateLookPollAsync(AppFactory factory, string owner, ThemeLook? look, PollDraft? draft = null)
    {
        var result = await WithScope(factory, sp => sp.GetRequiredService<PollService>().CreatePollAsync(owner, draft ?? RankedDraft(results: ResultsVisibility.Open), look));
        Assert.True(result.Succeeded, result.Message);
        return result.EncodedId!;
    }

    [GeneratedRegex("""<link rel="stylesheet" href="(/p/[^"]+/look\.css\?v=([0-9a-f]+))" data-poll-look""")]
    private static partial Regex LookLink();

    private static string Token(string css, string block, string name) =>
        Regex.Match(css, "^" + Regex.Escape(block) + @" \{\n(.*?)^\}", RegexOptions.Multiline | RegexOptions.Singleline).Groups[1].Value
            is var b && Regex.Match(b, "^ +" + Regex.Escape(name) + ": (.+);$", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value : "";

    [Fact]
    public async Task Poll_pages_link_look_css_after_the_theme_and_it_answers_like_the_page()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "owner@example.com");
        var id = await CreateLookPollAsync(factory, owner, Plum);
        var plain = await CreateLookPollAsync(factory, owner, null);
        var anonymous = factory.CreateBrowser();

        // Vote page and results page (and its ballots view): linked after the instance stylesheet.
        var (css, version) = factory.Services.GetRequiredService<LookStyles>().PollCss(Plum);
        foreach (var path in new[] { $"/p/{id}", $"/p/{id}/results", $"/p/{id}?page=1" })
        {
            var html = await (await GetPageAsync(anonymous, path)).Content.ReadAsStringAsync();
            var link = LookLink().Match(html);
            Assert.True(link.Success, path);
            Assert.Equal(version, link.Groups[2].Value);
            Assert.True(html.IndexOf("data-theme-css", StringComparison.Ordinal) < link.Index, "after theme.css");
            Assert.DoesNotContain(" style=\"", html);
        }
        Assert.DoesNotContain("look.css", await (await GetPageAsync(anonymous, $"/p/{plain}")).Content.ReadAsStringAsync());

        // The sheet: tokens only, text/css, nosniff, cached by its hash.
        var response = await anonymous.GetAsync($"/p/{id}/look.css?v={version}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/css", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        var cache = response.Headers.CacheControl!;
        Assert.True(cache.Private && !cache.Public && cache.MaxAge == TimeSpan.FromDays(365) && cache.ToString().Contains("immutable", StringComparison.Ordinal), cache.ToString());
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(css, body);
        Assert.Equal("no-cache", (await anonymous.GetAsync($"/p/{id}/look.css?v=stale")).Headers.CacheControl!.ToString());
        Assert.Equal("no-cache", (await anonymous.GetAsync($"/p/{id}/look.css")).Headers.CacheControl!.ToString());

        // A token differs from the instance theme in light and in dark.
        var theme = await anonymous.GetStringAsync("/branding/theme.css");
        foreach (var block in new[] { ":root", ":root[data-bs-theme=dark]" })
        {
            Assert.NotEqual("", Token(body, block, "--av-primary"));
            Assert.NotEqual(Token(theme, block, "--av-primary"), Token(body, block, "--av-primary"));
        }

        // Unknown, undecodable and look-less polls: 404, as the page.
        foreach (var path in new[] { $"/p/{PollId.Encode(Guid.NewGuid())}/look.css", "/p/nope/look.css", $"/p/{plain}/look.css" })
            Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(path)).StatusCode);

        // Pages on the ?admin=1 stylesheet, and every other page, never link a look.
        var browser = await LoggedInBrowserAsync(factory, "owner@example.com");
        foreach (var path in new[] { "/polls", $"/polls/{id}/share", "/Account/Manage", "/looks" })
            Assert.DoesNotContain("data-poll-look", await (await GetPageAsync(browser, path)).Content.ReadAsStringAsync());
        Assert.DoesNotContain("data-poll-look", await (await GetPageAsync(anonymous, "/Account/Login")).Content.ReadAsStringAsync());
        // The signed-in owner's shell on the poll page gets it too (the whole page).
        Assert.Matches(LookLink(), await (await GetPageAsync(browser, $"/p/{id}")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Admin_switch_off_hides_every_look_and_the_editor_section()
    {
        await using var factory = new AppFactory();
        var (_, admin) = await AdminAsync(factory);
        var owner = await CreateUserAsync(factory, "owner@example.com");
        var id = await CreateLookPollAsync(factory, owner, Plum);
        var anonymous = factory.CreateBrowser();
        var browser = await LoggedInBrowserAsync(factory, "owner@example.com");
        Assert.Contains("aria-label=\"About Look\"", await (await GetPageAsync(browser, "/polls/new")).Content.ReadAsStringAsync());

        Assert.Equal("Poll looks: true or false.", (await Save(factory, admin, new() { [SettingKeys.PollLooksEnabled] = "maybe" })).Message);
        Assert.True((await Save(factory, admin, new() { [SettingKeys.PollLooksEnabled] = "false" })).Ok);
        Assert.False(factory.Settings.Current.PollLooks);
        Assert.Contains(SettingKeys.PollLooksEnabled, (await LogNotesAsync(factory)));

        Assert.DoesNotContain("look.css", await (await GetPageAsync(anonymous, $"/p/{id}")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/p/{id}/look.css")).StatusCode);
        var editor = await (await GetPageAsync(browser, "/polls/new")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("aria-label=\"About Look\"", editor);
        Assert.DoesNotContain("aria-label=\"About Look\"", await (await GetPageAsync(browser, $"/polls/{id}/edit")).Content.ReadAsStringAsync());

        // The stored look is kept and shows again when the setting is back on (no row = on).
        Assert.True((await Save(factory, admin, new() { [SettingKeys.PollLooksEnabled] = null })).Ok);
        Assert.Matches(LookLink(), await (await GetPageAsync(anonymous, $"/p/{id}")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_look_is_saved_with_an_edit_not_logged_and_copied_with_the_poll()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "owner@example.com");
        var id = await CreateLookPollAsync(factory, owner, null, RankedDraft());
        Assert.True((await SubmitRankedAsync(factory, id, new VisitorContext(null, "browser-1", null), new(["paris"], []), "Ann")).Succeeded);
        Assert.True((await WithScope(factory, sp => sp.GetRequiredService<PollService>().GetForEditAsync(owner, id)))!.Look is null);

        var draft = PollDraft.FromDefinition((await WithScope(factory, sp => sp.GetRequiredService<PollService>().GetForEditAsync(owner, id)))!.Definition);
        Assert.True((await WithScope(factory, sp => sp.GetRequiredService<PollService>().UpdatePollAsync(owner, id, draft, Plum))).Succeeded);
        var model = await WithScope(factory, sp => sp.GetRequiredService<PollService>().GetForEditAsync(owner, id));
        Assert.Equal(Plum, model!.Look);
        Assert.True(PollId.TryDecode(id, out var pollId));
        Assert.Equal(0, await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().PollChanges.CountAsync(c => c.PollId == pollId)));

        // An edit without a look keeps it; a copy copies it; another account copies nothing.
        Assert.True((await UpdatePollAsync(factory, owner, id, draft)).Succeeded);
        Assert.Equal(Plum, (await WithScope(factory, sp => sp.GetRequiredService<PollService>().GetForEditAsync(owner, id)))!.Look);
        Assert.Equal(Plum, (await WithScope(factory, sp => sp.GetRequiredService<PollService>().GetCopyWithLookAsync(owner, id)))!.Look);
        var other = await CreateUserAsync(factory, "other@example.com");
        Assert.Null(await WithScope(factory, sp => sp.GetRequiredService<PollService>().GetCopyWithLookAsync(other, id)));

        // Back to the instance theme.
        Assert.True((await WithScope(factory, sp => sp.GetRequiredService<PollService>().UpdatePollAsync(owner, id, draft, null))).Succeeded);
        Assert.Null((await WithScope(factory, sp => sp.GetRequiredService<PollService>().GetForEditAsync(owner, id)))!.Look);
    }

    // ---- saved looks ----

    private static Task<T> Service<T>(AppFactory factory, Func<LookService, Task<T>> action) =>
        WithScope(factory, sp => action(sp.GetRequiredService<LookService>()));

    [Fact]
    public async Task Saved_looks_limit_default_and_ownership()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "owner@example.com");
        var other = await CreateUserAsync(factory, "other@example.com");

        Assert.Equal(LookService.NameMessage, (await Service(factory, s => s.CreateAsync(owner, "  ", Plum))).Error);
        Assert.Equal("Name: 60 characters at most.", (await Service(factory, s => s.CreateAsync(owner, new string('x', 61), Plum))).Error);

        var first = (await Service(factory, s => s.CreateAsync(owner, " Plum ", Plum))).Id!.Value;
        // Parallel creations cannot pass the limit together (the count and the insert run under the owner's lock).
        var results = await Task.WhenAll(Enumerable.Range(0, 25).Select(i => Service(factory, s => s.CreateAsync(owner, $"Look {i}", ThemeLook.BuiltIn))));
        Assert.Equal(UserLimits.SavedLooksPerUser - 1, results.Count(r => r.Ok));
        Assert.All(results.Where(r => !r.Ok), r => Assert.Equal("Limit reached: 20 looks. Delete one first.", r.Error));
        var list = await Service(factory, s => s.ListAsync(owner));
        Assert.Equal(UserLimits.SavedLooksPerUser, list.Looks.Count);
        Assert.Equal("Plum", list.Looks[0].Name);
        Assert.Equal(Plum, list.Looks[0].Look);

        // Another account's look is not found, for every action; its own list is empty.
        Assert.Empty((await Service(factory, s => s.ListAsync(other))).Looks);
        Assert.Equal(LookService.NotFoundMessage, (await Service(factory, s => s.UpdateAsync(other, first, "Mine", Plum))).Error);
        Assert.Equal(LookService.NotFoundMessage, (await Service(factory, s => s.DeleteAsync(other, first))).Error);
        Assert.Equal(LookService.NotFoundMessage, (await Service(factory, s => s.SetDefaultAsync(other, first))).Error);

        // Default: new polls start with it; editing it never changes a poll made from it; deleting it clears the default.
        Assert.True((await Service(factory, s => s.SetDefaultAsync(owner, first))).Ok);
        Assert.Equal(Plum, await Service(factory, s => s.DefaultLookAsync(owner)));
        Assert.Equal(first, (await Service(factory, s => s.ListAsync(owner))).DefaultId);
        var poll = await CreateLookPollAsync(factory, owner, await Service(factory, s => s.DefaultLookAsync(owner)));
        Assert.True((await Service(factory, s => s.UpdateAsync(owner, first, "Plum 2", ThemeLook.BuiltIn))).Ok);
        Assert.Equal(Plum, (await WithScope(factory, sp => sp.GetRequiredService<PollService>().GetForEditAsync(owner, poll)))!.Look);
        Assert.True((await Service(factory, s => s.DeleteAsync(owner, first))).Ok);
        Assert.Null(await Service(factory, s => s.DefaultLookAsync(owner)));
        Assert.Null((await Service(factory, s => s.ListAsync(owner))).DefaultId);
        Assert.Equal(Plum, (await WithScope(factory, sp => sp.GetRequiredService<PollService>().GetForEditAsync(owner, poll)))!.Look);
        Assert.True((await Service(factory, s => s.CreateAsync(owner, "Back under the limit", Plum))).Ok);
    }

    [Fact]
    public async Task Import_takes_a_look_code_or_a_poll_the_account_can_view()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "owner@example.com");
        var importer = await CreateUserAsync(factory, "importer@example.com");
        var poll = await CreateLookPollAsync(factory, owner, Plum, RankedDraft(title: "Club evening"));
        var plain = await CreateLookPollAsync(factory, owner, null);

        var fromCode = await Service(factory, s => s.ImportAsync(importer, Plum.ToCode(), null));
        Assert.True(fromCode.Ok, fromCode.Error);
        var fromLink = await Service(factory, s => s.ImportAsync(importer, $"https://vote.example.com/p/{poll}/results?x=1", null));
        Assert.True(fromLink.Ok, fromLink.Error);
        Assert.True((await Service(factory, s => s.ImportAsync(importer, poll, "By id"))).Ok);
        var list = (await Service(factory, s => s.ListAsync(importer))).Looks;
        Assert.Equal(["Imported look", "Club evening", "By id"], list.Select(l => l.Name));
        Assert.All(list, l => Assert.Equal(Plum, l.Look));

        Assert.Equal(ThemeLook.NotALookMessage, (await Service(factory, s => s.ImportAsync(importer, "tl1.garbage", null))).Error);
        Assert.Equal(ThemeLook.NotALookMessage, (await Service(factory, s => s.ImportAsync(importer, "", null))).Error);
        Assert.Equal(LookService.PollNotFoundMessage, (await Service(factory, s => s.ImportAsync(importer, PollId.Encode(Guid.NewGuid()), null))).Error);
        Assert.Equal(LookService.PollNotFoundMessage, (await Service(factory, s => s.ImportAsync(importer, "https://x/p/nope", null))).Error);
        Assert.Equal(LookService.NoLookMessage, (await Service(factory, s => s.ImportAsync(importer, plain, null))).Error);

        // While poll looks are off a poll shows no look, so it has none to give.
        await factory.Settings.SetAsync(new Dictionary<string, string?> { [SettingKeys.PollLooksEnabled] = "false" }, null);
        Assert.Equal(LookService.NoLookMessage, (await Service(factory, s => s.ImportAsync(importer, poll, null))).Error);
    }

    [Fact]
    public async Task Data_export_and_account_deletion_cover_saved_looks()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "owner@example.com");
        var look = (await Service(factory, s => s.CreateAsync(owner, "Plum", Plum))).Id!.Value;
        Assert.True((await Service(factory, s => s.SetDefaultAsync(owner, look))).Ok);
        await CreateLookPollAsync(factory, owner, Plum);

        var export = await WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            return JsonDocument.Parse(await sp.GetRequiredService<AccountService>().ExportAsync((await users.FindByIdAsync(owner))!)).RootElement.Clone();
        });
        var saved = export.GetProperty("looks")[0];
        Assert.Equal("Plum", saved.GetProperty("name").GetString());
        Assert.True(saved.GetProperty("isDefault").GetBoolean());
        Assert.Equal(Plum.ToCode(), saved.GetProperty("code").GetString());
        Assert.Equal("#5a2a5f", saved.GetProperty("look").GetProperty("lightBase").GetString());
        Assert.Equal("serif", export.GetProperty("polls")[0].GetProperty("look").GetProperty("font").GetString());

        await WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            await sp.GetRequiredService<AccountService>().SoftDeleteAsync((await users.FindByIdAsync(owner))!);
            return 0;
        });
        var db = factory.Services.CreateScope().ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.SavedLooks.AnyAsync(l => l.OwnerId == owner));
        Assert.Null((await db.Users.AsNoTracking().SingleAsync(u => u.Id == owner)).DefaultLookId);
    }

    // ---- pages ----

    [Fact]
    public async Task Looks_page_editor_and_preview_sheet()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "owner@example.com");
        var anonymous = factory.CreateBrowser();
        Assert.NotEqual(HttpStatusCode.OK, (await anonymous.GetAsync("/looks")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await anonymous.GetAsync("/looks/preview.css?c=" + Uri.EscapeDataString(Plum.ToCode()))).StatusCode);

        var look = (await Service(factory, s => s.CreateAsync(owner, "Plum", Plum))).Id!.Value;
        Assert.True((await Service(factory, s => s.SetDefaultAsync(owner, look))).Ok);
        var browser = await LoggedInBrowserAsync(factory, "owner@example.com");
        var page = await (await GetPageAsync(browser, "/looks")).Content.ReadAsStringAsync();
        Assert.Contains("<h1", page);
        Assert.Contains("data-look=\"Plum\"", page);
        Assert.Contains($"data-copy=\"{Plum.ToCode()}\"", page);
        Assert.Contains("Default for new polls · Plum", page);
        Assert.DoesNotContain(" style=\"", page);
        Assert.Contains("href=\"looks\"", page); // the account menu links the page

        // New polls start with the default look: the editor shows it with its preview.
        var editor = await (await GetPageAsync(browser, "/polls/new")).Content.ReadAsStringAsync();
        Assert.Contains("href=\"looks/preview.css?c=" + Uri.EscapeDataString(Plum.ToCode()) + "\"", editor);
        foreach (var label in new[] { "Look", "Font", "Corner radius", "Colours", "Look name" })
            Assert.Contains($"aria-label=\"About {label}\"", editor);
        Assert.DoesNotContain(" style=\"", editor);

        var sheet = await browser.GetAsync("/looks/preview.css?c=" + Uri.EscapeDataString(Plum.ToCode()));
        Assert.Equal(HttpStatusCode.OK, sheet.StatusCode);
        Assert.Equal("text/css", sheet.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", sheet.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains(".look-preview-dark {", await sheet.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/looks/preview.css?c=tl1.nope")).StatusCode);
    }

    // ---- helpers ----

    private static async Task<(string Id, ClaimsPrincipal Principal)> AdminAsync(AppFactory factory)
    {
        var id = await CreateUserAsync(factory, "admin@example.com");
        var principal = await WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(id))!;
            Assert.True((await users.AddToRoleAsync(user, AppRoles.Admin)).Succeeded);
            return await sp.GetRequiredService<SignInManager<ApplicationUser>>().CreateUserPrincipalAsync(user);
        });
        return (id, principal);
    }

    private static Task<AdminOutcome> Save(AppFactory factory, ClaimsPrincipal admin, Dictionary<string, string?> values) =>
        WithScope(factory, sp => sp.GetRequiredService<AdminService>().SaveSettingsAsync(admin, values, "settings.save"));

    private static Task<string> LogNotesAsync(AppFactory factory) =>
        WithScope(factory, async sp =>
        {
            var notes = await sp.GetRequiredService<ApplicationDbContext>().AdminActionLogs.AsNoTracking().Select(l => l.Note).ToListAsync();
            return string.Join("\n", notes.Select(n => Reveal(factory, n)));
        });
}
