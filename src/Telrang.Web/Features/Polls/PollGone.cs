using Telrang.Web.Components.Pages;

namespace Telrang.Web.Features.Polls;

/// <summary>
/// A form posted to a poll that no longer exists (deleted while its page was open). The poll pages post their named
/// forms back to themselves, and without the form on the page the framework answers an empty 400; such a page renders
/// its named forms hidden (their handlers do nothing) and answers 404 through the Not found page, with this line.
/// A GET of an unknown poll keeps the page's usual hint.
/// </summary>
public static class PollGone
{
    public const string Message = "This poll no longer exists.";

    public static void Answer(HttpContext http)
    {
        NotFound.SetHint(http, Message);
        http.Response.StatusCode = StatusCodes.Status404NotFound;
    }
}
