using System.Globalization;
using Telrang.Core.Polls;

namespace Telrang.Web.Features.Polls;

/// <summary>
/// The structured editor's outline operations on a <see cref="PollDraft"/> (DESIGN.md §34.1, §34.5): add, move and remove
/// questions, pages and groups with buttons. They keep what the validator needs where they can (questions in page order, a
/// group's questions together on one page, no group without questions) and leave the rest to it (an empty page shows its
/// error). Items are told apart by reference: the drafts are records, two blank ones are equal.
/// </summary>
public static class DraftOutline
{
    /// <summary>A simple poll (DESIGN.md §34.1): one question, no pages, no groups. Its editor is the simple one.</summary>
    public static bool IsSimple(PollDraft d) => d.Questions.Count == 1 && d.Pages.Count == 0 && d.Groups.Count == 0;

    /// <summary>
    /// A draft key for a new page or group: <c>new-1</c>, <c>new-2</c>, … unused in the draft. Never a stored key (those are
    /// <c>p1</c>, <c>g1</c>, …), so the validator issues the new item its own number.
    /// </summary>
    public static string NewKey(PollDraft d)
    {
        var used = d.Pages.Select(p => p.Key).Concat(d.Groups.Select(g => g.Key)).Where(k => k is not null).ToHashSet(StringComparer.Ordinal);
        for (var n = 1; ; n++)
        {
            var key = "new-" + n.ToString(CultureInfo.InvariantCulture);
            if (!used.Contains(key)) return key;
        }
    }

    /// <summary>A new question: a choice with two empty options.</summary>
    public static QuestionDraft NewQuestion(string? page, string? group) =>
        new() { Type = PollType.Choice, Page = page, Group = group, Options = [new OptionDraft(), new OptionDraft()] };

    public static int IndexOf(PollDraft d, QuestionDraft q) => d.Questions.FindIndex(x => ReferenceEquals(x, q));

    /// <summary>The page index of a question (0 without pages, or for a page the draft does not have).</summary>
    public static int PageIndex(PollDraft d, QuestionDraft q)
    {
        var i = d.Pages.FindIndex(p => p.Key is not null && p.Key == q.Page);
        return Math.Max(0, i);
    }

    public static PageDraft? PageOf(PollDraft d, QuestionDraft q) => d.Pages.FirstOrDefault(p => p.Key is not null && p.Key == q.Page);

    public static GroupDraft? GroupOf(PollDraft d, QuestionDraft q) => d.Groups.FirstOrDefault(g => g.Key is not null && g.Key == q.Group);

    public static IEnumerable<QuestionDraft> QuestionsOf(PollDraft d, GroupDraft g) => d.Questions.Where(q => q.Group is not null && q.Group == g.Key);

    public static IEnumerable<QuestionDraft> QuestionsOn(PollDraft d, PageDraft p) => d.Questions.Where(q => q.Page is not null && q.Page == p.Key);

    /// <summary>
    /// Leaving the simple editor (DESIGN.md §34.2): the one question so far had the poll title as its text; with several
    /// questions each needs its own, so it takes the title (cut to the limit) when it has none.
    /// </summary>
    private static void NameFirst(PollDraft d, PollLimits limits)
    {
        if (!IsSimple(d)) return;
        var q = d.Questions[0];
        var title = (d.Title ?? "").Trim();
        if (q.Prompt.Trim().Length == 0 && title.Length > 0) q.Prompt = title.Length <= limits.PromptMax ? title : title[..limits.PromptMax];
    }

    /// <summary>
    /// Adds a question after <paramref name="after"/> (same page and group), else at the end of <paramref name="onPage"/>,
    /// else at the end. Null at the limit.
    /// </summary>
    public static QuestionDraft? AddQuestion(PollDraft d, PollLimits limits, QuestionDraft? after = null, PageDraft? onPage = null)
    {
        if (d.Questions.Count >= limits.QuestionsMax) return null;
        NameFirst(d, limits);
        QuestionDraft q;
        int at;
        if (after is not null && IndexOf(d, after) is var i and >= 0)
        {
            q = NewQuestion(after.Page, after.Group);
            at = i + 1;
        }
        else if (onPage is not null && d.Pages.Contains(onPage))
        {
            q = NewQuestion(onPage.Key, null);
            at = EndOfPage(d, d.Pages.IndexOf(onPage));
        }
        else
        {
            q = NewQuestion(d.Pages.LastOrDefault()?.Key, null);
            at = d.Questions.Count;
        }
        d.Questions.Insert(at, q);
        return q;
    }

    /// <summary>Where a question goes at the end of page number <paramref name="pageIndex"/>: after every question on it or before it.</summary>
    private static int EndOfPage(PollDraft d, int pageIndex)
    {
        var at = 0;
        for (var i = 0; i < d.Questions.Count; i++)
            if (PageIndex(d, d.Questions[i]) <= pageIndex) at = i + 1;
        return at;
    }

    /// <summary>
    /// Adds a group holding one new question, after <paramref name="after"/> (after its whole group when it is in one) on its
    /// page, else at the end of <paramref name="onPage"/> or of the poll. Returns the new question; null at a limit.
    /// </summary>
    public static QuestionDraft? AddGroup(PollDraft d, PollLimits limits, QuestionDraft? after = null, PageDraft? onPage = null)
    {
        if (d.Groups.Count >= limits.GroupsMax || d.Questions.Count >= limits.QuestionsMax) return null;
        NameFirst(d, limits);
        var group = new GroupDraft { Key = NewKey(d), Title = "Group " + (d.Groups.Count + 1).ToString(CultureInfo.InvariantCulture) };
        string? page;
        int at;
        if (after is not null && IndexOf(d, after) is var i and >= 0)
        {
            page = after.Page;
            at = i + 1;
            if (after.Group is { } g)
                while (at < d.Questions.Count && d.Questions[at].Group == g) at++;
        }
        else if (onPage is not null && d.Pages.Contains(onPage))
        {
            page = onPage.Key;
            at = EndOfPage(d, d.Pages.IndexOf(onPage));
        }
        else
        {
            page = d.Pages.LastOrDefault()?.Key;
            at = d.Questions.Count;
        }
        var q = NewQuestion(page, group.Key);
        d.Questions.Insert(at, q);
        d.Groups.Add(group);
        SortGroups(d);
        return q;
    }

    /// <summary>
    /// Adds a page at the end with one new question on it (a page needs a question). The first page added also gets a page
    /// before it holding every question so far. Returns the new question; null at a limit.
    /// </summary>
    public static QuestionDraft? AddPage(PollDraft d, PollLimits limits)
    {
        var needed = d.Pages.Count == 0 ? 2 : 1;
        if (d.Pages.Count + needed > limits.PagesMax || d.Questions.Count >= limits.QuestionsMax) return null;
        NameFirst(d, limits);
        if (d.Pages.Count == 0)
        {
            var first = new PageDraft { Key = NewKey(d), Title = "Page 1" };
            d.Pages.Add(first);
            foreach (var q in d.Questions) q.Page = first.Key;
        }
        var page = new PageDraft { Key = NewKey(d), Title = "Page " + (d.Pages.Count + 1).ToString(CultureInfo.InvariantCulture) };
        d.Pages.Add(page);
        var added = NewQuestion(page.Key, null);
        d.Questions.Add(added);
        return added;
    }

    private static QuestionDraft? Previous(PollDraft d, int i) => i > 0 ? d.Questions[i - 1] : null;
    private static QuestionDraft? Next(PollDraft d, int i) => i + 1 < d.Questions.Count ? d.Questions[i + 1] : null;

    private static bool SamePage(QuestionDraft a, QuestionDraft? b) => b is not null && a.Page == b.Page;

    public static bool CanMoveUp(PollDraft d, QuestionDraft q)
    {
        var i = IndexOf(d, q);
        return i >= 0 && (q.Group is not null || SamePage(q, Previous(d, i)) || PageIndex(d, q) > 0 && d.Pages.Count > 0);
    }

    public static bool CanMoveDown(PollDraft d, QuestionDraft q)
    {
        var i = IndexOf(d, q);
        return i >= 0 && (q.Group is not null || SamePage(q, Next(d, i)) || d.Pages.Count > 0 && PageIndex(d, q) < d.Pages.Count - 1);
    }

    /// <summary>
    /// One step up in the outline: out of its group when it is the group's first question, into the group above when one
    /// ends right above it, onto the previous page when it is the first of its page, else above the question before it.
    /// </summary>
    public static void MoveUp(PollDraft d, QuestionDraft q)
    {
        var i = IndexOf(d, q);
        if (i < 0) return;
        var prev = Previous(d, i);
        var samePage = SamePage(q, prev) ? prev : null;
        if (q.Group is not null && samePage?.Group != q.Group) q.Group = null;
        else if (samePage is not null)
        {
            if (samePage.Group is not null && samePage.Group != q.Group) q.Group = samePage.Group;
            else (d.Questions[i - 1], d.Questions[i]) = (q, samePage);
        }
        else if (d.Pages.Count > 0 && PageIndex(d, q) is var p and > 0) q.Page = d.Pages[p - 1].Key;
        Tidy(d);
    }

    /// <summary>One step down: the mirror of <see cref="MoveUp"/>.</summary>
    public static void MoveDown(PollDraft d, QuestionDraft q)
    {
        var i = IndexOf(d, q);
        if (i < 0) return;
        var next = Next(d, i);
        var samePage = SamePage(q, next) ? next : null;
        if (q.Group is not null && samePage?.Group != q.Group) q.Group = null;
        else if (samePage is not null)
        {
            if (samePage.Group is not null && samePage.Group != q.Group) q.Group = samePage.Group;
            else (d.Questions[i + 1], d.Questions[i]) = (q, samePage);
        }
        else if (d.Pages.Count > 0 && PageIndex(d, q) is var p && p < d.Pages.Count - 1) q.Page = d.Pages[p + 1].Key;
        Tidy(d);
    }

    /// <summary>Removes a question (never the last one); a group left without questions goes with it.</summary>
    public static bool RemoveQuestion(PollDraft d, QuestionDraft q)
    {
        if (d.Questions.Count <= 1) return false;
        var i = IndexOf(d, q);
        if (i < 0) return false;
        d.Questions.RemoveAt(i);
        Tidy(d);
        return true;
    }

    /// <summary>Removes a group; its questions stay where they are, outside any group.</summary>
    public static void RemoveGroup(PollDraft d, GroupDraft g)
    {
        foreach (var q in QuestionsOf(d, g).ToList()) q.Group = null;
        d.Groups.RemoveAll(x => ReferenceEquals(x, g));
    }

    /// <summary>
    /// Removes a page; its questions (with their groups) join the page before it, or the next one when it is the first. The
    /// last page goes with every page reference: the poll is one implicit page again.
    /// </summary>
    public static void RemovePage(PollDraft d, PageDraft p)
    {
        var i = d.Pages.FindIndex(x => ReferenceEquals(x, p));
        if (i < 0) return;
        if (d.Pages.Count == 1)
        {
            foreach (var q in d.Questions) q.Page = null;
            d.Pages.Clear();
            return;
        }
        var target = d.Pages[i > 0 ? i - 1 : 1];
        foreach (var q in d.Questions.Where(q => q.Page is not null && q.Page == p.Key)) q.Page = target.Key;
        d.Pages.RemoveAt(i);
    }

    /// <summary>Moves a page one place (<paramref name="delta"/> -1 or 1), with its questions.</summary>
    public static void MovePage(PollDraft d, PageDraft p, int delta)
    {
        var i = d.Pages.FindIndex(x => ReferenceEquals(x, p));
        var j = i + delta;
        if (i < 0 || j < 0 || j >= d.Pages.Count) return;
        (d.Pages[i], d.Pages[j]) = (d.Pages[j], d.Pages[i]);
        var order = d.Questions.Select((q, n) => (q, n)).OrderBy(x => PageIndex(d, x.q)).ThenBy(x => x.n).Select(x => x.q).ToList();
        d.Questions.Clear();
        d.Questions.AddRange(order);
        SortGroups(d);
    }

    /// <summary>After a move or removal: groups without questions go, the rest in the order of their first question.</summary>
    private static void Tidy(PollDraft d)
    {
        d.Groups.RemoveAll(g => !QuestionsOf(d, g).Any());
        SortGroups(d);
    }

    private static void SortGroups(PollDraft d)
    {
        int First(GroupDraft g)
        {
            var i = d.Questions.FindIndex(q => q.Group is not null && q.Group == g.Key);
            return i < 0 ? int.MaxValue : i;
        }
        var sorted = d.Groups.OrderBy(First).ToList();
        d.Groups.Clear();
        d.Groups.AddRange(sorted);
    }

    /// <summary>The type as the editor names it: a choice with more than one selection is "Checkboxes" (DESIGN.md §34.3).</summary>
    public static string TypeLabel(PollType type, int maxSelect) =>
        type == PollType.Choice && maxSelect > 1 ? "Checkboxes" : PollText.TypeName(type);
}
