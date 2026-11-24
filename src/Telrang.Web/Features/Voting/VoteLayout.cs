using Telrang.Core.Polls;

namespace Telrang.Web.Features.Voting;

/// <summary>A run of questions on a page: in a group (a titled box, DESIGN.md §34.2) or, with <see cref="Group"/> null, not.</summary>
public sealed record VoteBlock(PollGroup? Group, IReadOnlyList<PollQuestion> Questions);

/// <param name="Number">1-based.</param>
/// <param name="Page">The page's title and description; null for the one implicit page of a definition without pages.</param>
public sealed record VotePageLayout(int Number, PollPage? Page, IReadOnlyList<PollQuestion> Questions, IReadOnlyList<VoteBlock> Blocks);

/// <summary>How the vote page lays a definition out (DESIGN.md §34.1, §34.4).</summary>
public static class VoteLayout
{
    /// <summary>
    /// A simple poll: one question, no pages, no groups. Its vote page is exactly the one from before §34: no page chrome, no
    /// field prefixes, no carry field.
    /// </summary>
    public static bool IsSimple(PollDefinition def) => def.Questions.Count == 1 && def.Pages.Count == 0 && def.Groups.Count == 0;

    /// <summary>
    /// The pages in order with their questions (definition order) in blocks of the same group. A definition without pages is
    /// one implicit page. A question naming no known page (the validator prevents it) goes on the first.
    /// </summary>
    public static IReadOnlyList<VotePageLayout> Pages(PollDefinition def)
    {
        ArgumentNullException.ThrowIfNull(def);
        var groups = def.Groups.ToDictionary(g => g.Key, StringComparer.Ordinal);
        if (def.Pages.Count == 0) return [Layout(1, null, def.Questions, groups)];
        var known = def.Pages.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var first = def.Pages[0].Key;
        return def.Pages.Select((p, i) => Layout(i + 1, p,
            def.Questions.Where(q => (q.Page is { } k && known.Contains(k) ? k : first) == p.Key).ToList(), groups)).ToList();
    }

    private static VotePageLayout Layout(int number, PollPage? page, IReadOnlyList<PollQuestion> questions, Dictionary<string, PollGroup> groups)
    {
        var blocks = new List<VoteBlock>();
        foreach (var q in questions)
        {
            var g = q.Group is { } key ? groups.GetValueOrDefault(key) : null;
            if (blocks.Count > 0 && blocks[^1].Group?.Key == g?.Key) ((List<PollQuestion>)blocks[^1].Questions).Add(q);
            else blocks.Add(new VoteBlock(g, new List<PollQuestion> { q }));
        }
        return new VotePageLayout(number, page, questions, blocks);
    }
}
