using System.Globalization;
using Telrang.Core.Polls;

namespace Telrang.Web.Features.Polls;

/// <summary>Plain-language labels shared by the poll pages.</summary>
public static class PollText
{
    public static string TypeName(PollType type) => type switch
    {
        PollType.Ranked => "Ranked",
        PollType.Points => "Points",
        PollType.Dates => "Dates",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    public static string ScoringName(ScoringScheme s) => s switch
    {
        ScoringScheme.Borda => "Borda count",
        ScoringScheme.Dowdall => "Dowdall (1, 1/2, 1/3, ...)",
        _ => s.ToString(),
    };

    public static string ResultsName(ResultsVisibility r) => r switch
    {
        ResultsVisibility.Creator => "owner only",
        ResultsVisibility.AfterVote => "voters",
        ResultsVisibility.Open => "anyone with the link",
        _ => r.ToString(),
    };

    /// <summary>The results settings in words (DESIGN.md §25), e.g. "Results: voters, after closing, with ballots."</summary>
    public static string ResultsLine(PollDefinition d)
    {
        if (d.Results == ResultsVisibility.Creator) return "Results: owner only.";
        var line = "Results: " + ResultsName(d.Results) + (d.ResultsTiming == ResultsTiming.AfterClose ? ", after closing" : ", while voting");
        if (d.ShowBallots && d.Names != NamesMode.None) line += ", with ballots";
        return line + ".";
    }

    /// <summary>Why a visitor sees no results yet (results page), from the settings and their state.</summary>
    public static string ResultsHidden(PollDefinition d, bool hasBallot, bool isClosed)
    {
        var afterClose = d.ResultsTiming == ResultsTiming.AfterClose;
        return d.Results switch
        {
            ResultsVisibility.Open => afterClose ? "Results open when the poll closes." : "Results are visible to the poll's owner only.",
            ResultsVisibility.AfterVote when hasBallot => "Results open when the poll closes.",
            ResultsVisibility.AfterVote when isClosed => "Results are visible to voters only.",
            ResultsVisibility.AfterVote => afterClose ? "Results open to voters when the poll closes." : "Results open after you vote.",
            _ => "Results are visible to the poll's owner only.",
        };
    }

    /// <summary>
    /// One change log entry in words (DESIGN.md §26, §31), e.g. "“Pizza” removed (on 7 ballots)"; a removal without a count
    /// (<paramref name="ballotCount"/> null) is "“Pizza” removed". Pages render it through <c>ChangeLine</c>, which isolates
    /// the labels.
    /// </summary>
    public static string ChangeText(OptionChangeKind kind, string label, string? oldLabel, int? ballotCount) => kind switch
    {
        OptionChangeKind.Added => $"“{label}” added",
        OptionChangeKind.Removed => ballotCount is int n ? $"“{label}” removed (on {BallotsText(n)})" : $"“{label}” removed",
        OptionChangeKind.Renamed => $"“{oldLabel}” renamed to “{label}”",
        OptionChangeKind.DetailsChanged => $"Details of “{label}” changed",
        OptionChangeKind.TitleChanged => $"Title changed: “{oldLabel}” → “{label}”",
        OptionChangeKind.DescriptionChanged => "Description changed",
        OptionChangeKind.Earlier => EarlierChanges(ballotCount ?? 0),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>The entry older entries beyond the cap are merged into (DESIGN.md §31).</summary>
    public static string EarlierChanges(int n) => n == 1 ? "1 earlier change" : n.ToString(CultureInfo.InvariantCulture) + " earlier changes";

    public static string BallotsText(int n) => n switch
    {
        0 => "no ballots",
        1 => "1 ballot",
        _ => n.ToString(CultureInfo.InvariantCulture) + " ballots",
    };

    /// <summary>Share page: who can respond, in one line.</summary>
    public static string ModeLine(VoterMode voters, ResponsePolicy responses)
    {
        var many = responses == ResponsePolicy.Many;
        return voters switch
        {
            VoterMode.Accounts => many ? "Signed-in users · several responses each" : "Signed-in users · one response each",
            VoterMode.Invite => many ? "Invite links · several responses per link" : "Invite links · one response per link",
            _ => many ? "Anyone with the link · several responses each" : "Anyone with the link · one response each",
        };
    }

    /// <summary>The rules as one line under the poll title (details go behind "How scoring works").</summary>
    public static string RulesLine(PollQuestion d)
    {
        if (d.Type == PollType.Dates) return d.Maybe ? "Answer yes, maybe or no per date." : "Answer yes or no per date.";
        if (d.Type == PollType.Points)
        {
            var budget = d.Budget ?? 0;
            var text = $"Spread {budget} {(budget == 1 ? "point" : "points")}.";
            if (d.MaxPerOption is int max && max < budget) text += $" At most {max} per option.";
            if (d.SpendAll) text += " Spend all.";
            return text;
        }
        var line = d.RankAll ? "Rank every option." : "Unranked options score 0.";
        if (d.MaxBans > 0) line += $" Up to {d.MaxBans} {(d.MaxBans == 1 ? "ban" : "bans")}.";
        return line;
    }

    /// <summary>The long form of the rules, for the "How scoring works" disclosure.</summary>
    public static IReadOnlyList<string> ScoringDetails(PollQuestion d)
    {
        var lines = new List<string>();
        if (d.Type == PollType.Dates)
        {
            lines.Add(d.Maybe ? "Each date takes yes, maybe (if need be) or no. Unanswered dates count as no."
                : "Each date takes yes or no. Unanswered dates count as no.");
            lines.Add(d.Maybe ? "The best date has the most yes answers, then the most maybe answers." : "The best date has the most yes answers.");
            return lines;
        }
        if (d.Type == PollType.Points)
        {
            var budget = d.Budget ?? 0;
            lines.Add($"Each voter has {budget} {(budget == 1 ? "point" : "points")} to spread over the options in whole numbers.");
            if (d.MaxPerOption is int max && max < budget) lines.Add($"One option can get at most {max}.");
            lines.Add(d.SpendAll ? "A ballot must use every point." : "Unspent points are fine.");
            lines.Add("Results add up the points per option.");
            return lines;
        }
        var n = d.Options.Count;
        lines.Add(d.Scoring switch
        {
            ScoringScheme.Dowdall => "Dowdall scoring: first choice 1 point, second 1/2, third 1/3, and so on.",
            _ => n >= 3
                ? $"Borda count: first choice {n} points, second {n - 1}, third {n - 2}, and so on down to 1."
                : $"Borda count: first choice {n} points, second {n - 1}.",
        });
        lines.Add(d.RankAll ? $"Every option{(d.MaxBans > 0 ? " that is not banned" : "")} must be ranked." : "Unranked options get 0 points.");
        if (d.MaxBans > 0)
            lines.Add($"Up to {d.MaxBans} {(d.MaxBans == 1 ? "option" : "options")} can be banned. Bans do not subtract points; results count them separately. A banned option cannot be ranked.");
        lines.Add("Results add up the points per option.");
        return lines;
    }

    /// <summary>1st, 2nd, 3rd, 4th, …, 11th, 12th, 13th, 21st.</summary>
    public static string Ordinal(int n) => n.ToString(CultureInfo.InvariantCulture) + ((n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch
    {
        1 => "st",
        2 => "nd",
        3 => "rd",
        _ => "th",
    });

    /// <summary>"3 voters (43%)" for a hover text; the share is left out when nobody voted.</summary>
    public static string VotersShare(int count, int voters) => count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " voter" : " voters")
        + (voters > 0 ? " (" + (count * 100.0 / voters).ToString("0", CultureInfo.InvariantCulture) + "%)" : "");

    public static string Date(DateTimeOffset d) => d.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string DateTime(DateTimeOffset d) => d.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";

    public static string Score(double score, ScoringScheme scheme) =>
        scheme == ScoringScheme.Borda
            ? Math.Round(score).ToString(CultureInfo.InvariantCulture)
            : score.ToString("0.00", CultureInfo.InvariantCulture);
}
