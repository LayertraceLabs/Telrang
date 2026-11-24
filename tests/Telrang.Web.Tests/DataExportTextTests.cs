using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Telrang.Core.Ballots;
using Telrang.Web.Data;
using Telrang.Web.Features.Accounts;
using Telrang.Web.Features.Voting;
using Telrang.Web.Security;
using static Telrang.Web.Tests.StructuredResultsTests;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>"Download my data" (DESIGN.md §14.6) with free text and drafts (§34.3, §34.4): the account's own, decrypted.</summary>
public class DataExportTextTests
{
    private const string OtherText = "Halal";
    private const string Note = "Secret note B";

    // ---- the account's data export (DESIGN.md §34.3, §34.4) ------------------------------------------------------------

    [Fact]
    public async Task Data_export_holds_the_accounts_own_texts_and_drafts_and_nothing_of_anyone_else()
    {
        await using var factory = new AppFactory();
        var owner = await CreateUserAsync(factory, "o@example.com");
        var voter = await CreateUserAsync(factory, "v@example.com");
        var id = await CreatePollAsync(factory, owner, MultiPageVoteTests.Survey().Many());
        var me = VisitorContext.ForUser(voter);
        await SubmitAsync(factory, id, me, Answers(
            ("q1", new QuestionAnswer { Choice = new ChoiceBallot(["yes"]) }),
            ("q4", new QuestionAnswer { Text = Note })), null);
        var draft = new CarryState(2, Answers(
            ("q1", new QuestionAnswer { Choice = new ChoiceBallot(["no"]) }),
            ("q3", new QuestionAnswer { Choice = new ChoiceBallot([], Other: true), OtherText = OtherText })));
        Assert.True(await WithScope(factory, sp => sp.GetRequiredService<BallotService>().SaveDraftAsync(id, me, 0, draft)));

        async Task<JsonElement> ExportAsync(string userId) => await WithScope(factory, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            return JsonDocument.Parse(await sp.GetRequiredService<AccountService>().ExportAsync((await users.FindByIdAsync(userId))!)).RootElement.Clone();
        });

        var mine = await ExportAsync(voter);
        var ballot = Assert.Single(mine.GetProperty("ballots").EnumerateArray());
        Assert.Equal(Note, ballot.GetProperty("texts").GetProperty("q4").GetString());
        var d = Assert.Single(mine.GetProperty("drafts").EnumerateArray());
        Assert.Equal("Party", d.GetProperty("poll").GetString());
        Assert.Equal(OtherText, d.GetProperty("answers").GetProperty("texts").GetProperty("q3.other").GetString());

        var theirs = await ExportAsync(owner);
        Assert.Empty(theirs.GetProperty("drafts").EnumerateArray());
        Assert.Empty(theirs.GetProperty("ballots").EnumerateArray());
        Assert.Equal(1, Assert.Single(theirs.GetProperty("polls").EnumerateArray()).GetProperty("ballotCount").GetInt32()); // the draft is no ballot
        var text = theirs.GetRawText();
        Assert.DoesNotContain(Note, text);
        Assert.DoesNotContain(OtherText, text);
    }
}
