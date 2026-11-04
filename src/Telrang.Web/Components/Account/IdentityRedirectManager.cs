using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;
using Telrang.Web.Data;

namespace Telrang.Web.Components.Account;

internal sealed class IdentityRedirectManager(NavigationManager navigationManager)
{
    public const string StatusCookieName = "Identity.StatusMessage";

    private static readonly CookieBuilder StatusCookieBuilder = new()
    {
        SameSite = SameSiteMode.Strict,
        HttpOnly = true,
        IsEssential = true,
        MaxAge = TimeSpan.FromSeconds(5),
    };

    /// <summary>The page every sign-in lands on without a (local) return URL (DESIGN.md §23.3).</summary>
    public const string SignedInLanding = "polls";

    public void RedirectTo(string? uri) => navigationManager.NavigateTo(Local(uri));

    /// <summary>After a sign-in: the return URL when it is local, else <see cref="SignedInLanding"/> (DESIGN.md §23.3).</summary>
    public void RedirectToSignedIn(string? returnUrl)
    {
        var uri = Local(returnUrl);
        navigationManager.NavigateTo(uri.Length == 0 ? SignedInLanding : uri);
    }

    /// <summary>
    /// Prevents open redirects: <paramref name="uri"/> as an address in this app, or "" (the home page) for an address
    /// outside it (pentest AV-15b: it threw, a 500).
    /// </summary>
    private string Local(string? uri)
    {
        uri ??= "";
        if (!Uri.IsWellFormedUriString(uri, UriKind.Relative)
            || uri.StartsWith("//", StringComparison.Ordinal))
        {
            try { uri = navigationManager.ToBaseRelativePath(uri); }
            catch (ArgumentException) { uri = ""; }
            if (uri.StartsWith('/') || uri.StartsWith('\\')) uri = "";
        }
        return uri;
    }

    public void RedirectTo(string uri, Dictionary<string, object?> queryParameters)
    {
        var uriWithoutQuery = navigationManager.ToAbsoluteUri(uri).GetLeftPart(UriPartial.Path);
        var newUri = navigationManager.GetUriWithQueryParameters(uriWithoutQuery, queryParameters);
        RedirectTo(newUri);
    }

    public void RedirectToWithStatus(string uri, string message, HttpContext context)
    {
        context.Response.Cookies.Append(StatusCookieName, message, StatusCookieBuilder.Build(context));
        RedirectTo(uri);
    }

    private string CurrentPath => navigationManager.ToAbsoluteUri(navigationManager.Uri).GetLeftPart(UriPartial.Path);

    public void RedirectToCurrentPage() => RedirectTo(CurrentPath);

    public void RedirectToCurrentPageWithStatus(string message, HttpContext context)
        => RedirectToWithStatus(CurrentPath, message, context);

    public void RedirectToInvalidUser(UserManager<ApplicationUser> userManager, HttpContext context)
        => RedirectToWithStatus("Account/InvalidUser", $"Error: Unable to load user with ID '{userManager.GetUserId(context.User)}'.", context);
}
