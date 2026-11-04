using Telrang.Core.Ids;
using Telrang.Core.Polls;
using Telrang.Web.Data;
using Telrang.Web.Features.Polls;
using Telrang.Web.Features.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>Work bounded by the poll limits (AV-03), the open-polls limit on edit (AV-13), far dates and retention (AV-14).</summary>
public class BoundsAndRetentionTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Telrang.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine([dir!.FullName, .. parts]);
    }

    // ---- AV-03 ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Saving_a_draft_with_fifty_thousand_options_is_one_count_error()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var draft = Ranked("Many");
        draft.Questions[0].Options = Enumerable.Range(0, 50_000).Select(i => new OptionDraft { Label = "x" }).ToList();
        var result = await WithScope(factory, sp => sp.GetRequiredService<PollService>().CreatePollAsync(owner, draft));
        Assert.Equal(PollSaveStatus.Invalid, result.Status);
        var e = Assert.Single(result.Errors);
        Assert.Equal(("questions[0].options", "Remove options: at most 100 are allowed (now 50000)."), (e.Field, e.Message));
    }

    [Fact]
    public void Options_text_is_parsed_on_change_and_capped_in_length()
    {
        var razor = File.ReadAllText(RepoFile("src", "Telrang.Web", "Features", "Polls", "PollEditor.razor"));
        var start = razor.IndexOf("private RenderFragment OptionsText()", StringComparison.Ordinal);
        var textarea = razor[start..razor.IndexOf("</textarea>", start, StringComparison.Ordinal)];
        Assert.Contains("@bind=\"optionsText\"", textarea);
        Assert.DoesNotContain("oninput", textarea);
        Assert.Contains("maxlength=\"@OptionListParser.MaxTextLength(", textarea);
    }

    [Fact]
    public async Task The_text_length_follows_the_admin_limits()
    {
        await using var factory = new AppFactory();
        var limits = WithScopeSync(factory, sp => sp.GetRequiredService<PollService>().CurrentLimits);
        Assert.Equal(limits.OptionsMax * (limits.OptionLabelMax + 2 * limits.OptionDescriptionMax + OptionListParser.OptionOverhead),
            OptionListParser.MaxTextLength(limits));
    }

    // ---- AV-13 ---------------------------------------------------------------------------------------

    [Fact]
    public async Task An_edit_that_reopens_a_poll_counts_against_the_open_polls_limit()
    {
        var clock = new MutableTime(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
        await using var factory = new AppFactory(services: s => s.AddSingleton<TimeProvider>(clock));
        await factory.Settings.SetAsync(new Dictionary<string, string?> { [SettingKeys.LimitActivePolls] = "1" }, null);
        var owner = await CreateUserAsync(factory, "o@example.com");
        // Closed on arrival (a past close date): not counted, so all three are created.
        var ids = new List<string>();
        foreach (var title in new[] { "One", "Two", "Three" })
            ids.Add(await CreatePollAsync(factory, owner, Ranked(title).Closes("2026-10-01")));

        Assert.True((await UpdatePollAsync(factory, owner, ids[0], Ranked("One").Closes("2026-11-15"))).Succeeded);
        var refused = await UpdatePollAsync(factory, owner, ids[1], Ranked("Two").Closes("2026-11-15"));
        Assert.Equal(PollSaveStatus.LimitReached, refused.Status);
        Assert.Equal("Limit reached: 1 open poll. Close one first.", refused.Message);
        var model = await WithScope(factory, sp => sp.GetRequiredService<PollService>().GetForEditAsync(owner, ids[1]));
        Assert.Equal(new DateOnly(2026, 10, 1), model!.Definition.ClosesOn); // nothing was saved
        Assert.Equal("Two", model.Definition.Title);

        // Edits that keep the poll closed, or of the poll that is already open, are not limited.
        Assert.True((await UpdatePollAsync(factory, owner, ids[1], Ranked("Two renamed").Closes("2026-10-02"))).Succeeded);
        Assert.True((await UpdatePollAsync(factory, owner, ids[0], Ranked("One").Closes("2026-11-20"))).Succeeded);

        // Once the open one is closed, the edit may reopen another.
        Assert.True(await WithScope(factory, sp => sp.GetRequiredService<PollService>().SetClosedAsync(owner, ids[0], true)));
        Assert.True((await UpdatePollAsync(factory, owner, ids[2], Ranked("Three").Closes("2026-11-15"))).Succeeded);
    }

    // ---- AV-14 ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_close_date_near_9999_is_refused_even_without_a_duration_limit_an_option_date_is_not()
    {
        await using var factory = new AppFactory();
        await factory.Settings.SetAsync(new Dictionary<string, string?> { [SettingKeys.LimitPollDurationDays] = "0" }, null);
        var owner = await CreateUserAsync(factory, "o@example.com");
        var result = await WithScope(factory, sp => sp.GetRequiredService<PollService>().CreatePollAsync(owner, Ranked("Far").Closes("9999-12-30")));
        Assert.Equal(PollSaveStatus.Invalid, result.Status);
        Assert.Equal("closesOn", Assert.Single(result.Errors).Field);
        var dates = await WithScope(factory, sp => sp.GetRequiredService<PollService>().CreatePollAsync(owner,
            Dates("Far", "2026-11-03").With(d => d.Questions[0].Options.Add(new OptionDraft { Date = DateOnly.MaxValue }))));
        Assert.True(dates.Succeeded); // DESIGN.md §27: option dates span 0001–9999, never converted to instants
    }

    [Fact]
    public void Retention_and_formatting_survive_the_ends_of_the_calendar()
    {
        var far = new DateTimeOffset(9999, 12, 30, 0, 0, 0, TimeSpan.Zero);
        Assert.Null(Retention.DeleteOn(far, null, 7));
        Assert.Null(Retention.DeleteOn(null, DateTimeOffset.MaxValue, 1));
        Assert.Null(Retention.DeleteOn(DateTimeOffset.UnixEpoch, null, int.MaxValue));
        Assert.Equal(far.AddHours(12), Retention.DeleteOn(far.AddHours(-12), null, 1));
        // The runtime clamps at the ends of the calendar: formatting never throws.
        Assert.StartsWith("9999-12-31 ", PollTime.Format(DateTimeOffset.MaxValue, "Asia/Tokyo"));
        Assert.StartsWith("0001-01-01 ", PollTime.Format(DateTimeOffset.MinValue, "America/New_York"));
        Assert.StartsWith("9999-12-31 ", PollTime.Format(far.AddDays(1).AddHours(23), "Pacific/Kiritimati"));
    }

    [Fact]
    public async Task A_poll_closing_near_9999_does_not_block_retention_of_the_others()
    {
        var clock = new MutableTime(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
        await using var factory = new AppFactory(services: s => s.AddSingleton<TimeProvider>(clock));
        await factory.Settings.SetAsync(new Dictionary<string, string?> { [SettingKeys.RetentionDeleteClosedAfterDays] = "7" }, null);
        var owner = await CreateUserAsync(factory, "o@example.com");
        var far = await CreatePollAsync(factory, owner, Ranked("Far"));
        var old = await CreatePollAsync(factory, owner, Ranked("Old"));
        Assert.True(await WithScope(factory, sp => sp.GetRequiredService<PollService>().SetClosedAsync(owner, old, true)));
        // A row written before the date limit existed: closes at the very end of the calendar.
        Assert.True(PollId.TryDecode(far, out var farId));
        await WithScope(factory, sp => sp.GetRequiredService<ApplicationDbContext>().Polls.Where(p => p.Id == farId)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.ClosesAt, (DateTimeOffset?)DateTimeOffset.MaxValue.AddTicks(-1))));

        clock.Now += TimeSpan.FromDays(8);
        Assert.Equal(1, await factory.Services.GetRequiredService<PollSweepService>().DeleteExpiredAsync());
        var left = await WithScope(factory, sp => sp.GetRequiredService<PollService>().ListOwnedAsync(owner));
        Assert.Equal(far, Assert.Single(left).EncodedId);
        // The share page of the far poll still renders (no deletion date to show).
        var browser = await LoggedInBrowserAsync(factory, "o@example.com");
        Assert.DoesNotContain("retention-line", await browser.GetStringAsync($"/polls/{far}/share"));
    }

    private static T WithScopeSync<T>(AppFactory factory, Func<IServiceProvider, T> action)
    {
        using var scope = factory.Services.CreateScope();
        return action(scope.ServiceProvider);
    }
}
