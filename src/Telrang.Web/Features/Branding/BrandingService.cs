using System.Globalization;
using Telrang.Web.Features.Status;
using Telrang.Web.Security;

namespace Telrang.Web.Features.Branding;

/// <param name="Version">Cache-busting value for <c>?v=</c>: last write time (Unix ms) and size.</param>
public sealed record BrandingFile(string Name, string Path, string ContentType, DateTimeOffset LastModified, long Length, string Version);

public sealed record BrandingSaveResult(bool Ok, string Message);

/// <summary>
/// Branding images in <c>{DataDir}/branding/</c> (DESIGN.md §17): <c>logo</c>, <c>banner</c> and
/// <c>background</c>, each as <c>.png</c>, <c>.jpg</c> or <c>.webp</c>, and <c>favicon</c> (PNG only). The format is decided by the file
/// header (magic bytes), never by extension or declared type; SVG and everything else is refused, also
/// for files an operator drops into the folder by hand (those are not served). Writes are atomic
/// (temp file + rename) and remove the same name's other extensions.
/// </summary>
public sealed class BrandingService(AppStartInfo start)
{
    public const string Logo = "logo";
    public const string Banner = "banner";
    public const string Background = "background";
    public const string Favicon = "favicon";

    public static readonly IReadOnlyList<string> Names = [Logo, Banner, Background, Favicon];

    private static readonly (string Ext, string ContentType)[] Formats = [("png", "image/png"), ("jpg", "image/jpeg"), ("webp", "image/webp")];

    public string Folder { get; } = System.IO.Path.Combine(start.DataDir, "branding");

    public static bool IsName(string? name) => name is not null && Names.Contains(name);

    /// <summary>Size cap per name: logo and banner 1 MB, background 2 MB, favicon 256 kB.</summary>
    public static long MaxBytes(string name) => name switch { Background => 2 * 1024 * 1024, Favicon => 256 * 1024, _ => 1024 * 1024 };

    /// <summary><see cref="MaxBytes"/> for people: "1 MB", "256 kB".</summary>
    public static string MaxText(string name) => MaxBytes(name) is var b && b >= 1024 * 1024 ? $"{b / (1024 * 1024)} MB" : $"{b / 1024} kB";

    public static string Label(string name) => name switch { Logo => "Logo", Banner => "Banner", Favicon => "Favicon", _ => "Background" };

    /// <summary>The formats a name accepts: the favicon is PNG only (every browser takes it as an icon).</summary>
    private static IEnumerable<(string Ext, string ContentType)> FormatsFor(string name) => name == Favicon ? Formats.Take(1) : Formats;

    /// <summary>The <c>accept</c> attribute of the upload field.</summary>
    public static string Accept(string name) => string.Join(",", FormatsFor(name).Select(f => f.ContentType));

    /// <summary>"png", "jpg" or "webp" from the first bytes, else null (SVG, HTML, GIF, anything else).</summary>
    public static string? Sniff(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 8 && head[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return "png";
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return "jpg";
        if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head.Slice(8, 4).SequenceEqual("WEBP"u8)) return "webp";
        return null;
    }

    /// <summary>The stored file for <paramref name="name"/> whose header matches its extension, newest first; null when none.</summary>
    public BrandingFile? Get(string name)
    {
        if (!IsName(name)) return null;
        BrandingFile? best = null;
        foreach (var (ext, contentType) in FormatsFor(name))
        {
            var path = System.IO.Path.Combine(Folder, $"{name}.{ext}");
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaxBytes(name)) continue;
            if (ReadFormat(path) != ext) continue;
            var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            if (best is not null && best.LastModified >= modified) continue;
            var version = modified.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "-" + info.Length.ToString(CultureInfo.InvariantCulture);
            best = new BrandingFile(name, path, contentType, modified, info.Length, version);
        }
        return best;
    }

    /// <summary>Site-relative URL with the version, or null when the file is absent.</summary>
    public string? Url(string name) => Get(name) is { } f ? $"/branding/{name}?v={f.Version}" : null;

    private static string? ReadFormat(string path)
    {
        try
        {
            Span<byte> head = stackalloc byte[12];
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var read = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            return Sniff(head[..read]);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public async Task<BrandingSaveResult> SaveAsync(string name, Stream content, CancellationToken ct = default)
    {
        if (!IsName(name)) return new BrandingSaveResult(false, "Unknown image.");
        var max = MaxBytes(name);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > max) return new BrandingSaveResult(false, $"{Label(name)}: choose a file of {MaxText(name)} or less.");
            buffer.Write(chunk, 0, read);
        }
        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        var ext = Sniff(bytes);
        if (name == Favicon && ext != "png") return new BrandingSaveResult(false, "Favicon: choose a PNG file.");
        if (ext is null) return new BrandingSaveResult(false, $"{Label(name)}: choose a PNG, JPEG or WebP file.");

        Directory.CreateDirectory(Folder);
        var target = System.IO.Path.Combine(Folder, $"{name}.{ext}");
        var temp = System.IO.Path.Combine(Folder, $".{name}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temp, buffer.ToArray(), ct);
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        foreach (var (other, _) in Formats)
        {
            if (other != ext) File.Delete(System.IO.Path.Combine(Folder, $"{name}.{other}"));
        }
        return new BrandingSaveResult(true, $"{Label(name)} saved.");
    }

    /// <summary>Removes every stored format of <paramref name="name"/>. True when something was removed.</summary>
    public bool Delete(string name)
    {
        if (!IsName(name)) return false;
        var removed = false;
        foreach (var (ext, _) in Formats)
        {
            var path = System.IO.Path.Combine(Folder, $"{name}.{ext}");
            if (!File.Exists(path)) continue;
            File.Delete(path);
            removed = true;
        }
        return removed;
    }
}

/// <summary>
/// The generated theme stylesheet and its version, cached per settings snapshot and background file. Two variants:
/// public (with the admin's custom CSS) and admin (<c>?admin=1</c>, without it), so broken custom CSS cannot hide
/// the admin page's own controls.
/// </summary>
public sealed class ThemeService(Settings.SettingsService settings, BrandingService branding)
{
    private sealed record Cached(Settings.AppSettingsSnapshot Snapshot, string? BackgroundUrl, string Css, string Version);

    private volatile Cached? cache, adminCache;

    public ThemeSettings Settings => settings.Current.Theme;

    public (string Css, string Version) Current => Get(admin: false);

    public (string Css, string Version) Get(bool admin)
    {
        var snapshot = settings.Current;
        var bg = snapshot.Theme.BackgroundEnabled ? branding.Url(BrandingService.Background) : null;
        var c = admin ? adminCache : cache;
        if (c is not null && ReferenceEquals(c.Snapshot, snapshot) && c.BackgroundUrl == bg) return (c.Css, c.Version);
        var css = ThemeCss.Generate(snapshot.Theme, bg, includeCustomCss: !admin);
        var fresh = new Cached(snapshot, bg, css, ThemeCss.Version(css));
        if (admin) adminCache = fresh;
        else cache = fresh;
        return (fresh.Css, fresh.Version);
    }

    public string Version => Current.Version;

    /// <summary>Site-relative URL of the stylesheet with its current version.</summary>
    public string Url => "/branding/theme.css?v=" + Version;

    /// <summary>The admin pages' stylesheet: same theme, no custom CSS.</summary>
    public string AdminUrl => "/branding/theme.css?admin=1&v=" + Get(admin: true).Version;

    /// <summary>The default favicon: the brand mark on the palette (<see cref="ThemeCss.Favicon"/>), versioned with the theme.</summary>
    public string FaviconUrl => "/branding/favicon.svg?v=" + Version;

    public string FaviconSvg => ThemeCss.Favicon(Settings);

    private static readonly string[] AdminVariantPrefixes = ["/admin", "/Account", "/setup", "/notify", "/Error", "/not-found"];

    /// <summary>
    /// Pages that must work whatever the custom CSS does get <see cref="AdminUrl"/>: the admin pages, sign-in and account
    /// pages, setup and recovery, the unsubscribe page and the error pages (the static blocked/429/expired pages use it
    /// too). A broken rule can then never lock an admin out of the controls that fix it.
    /// </summary>
    public static bool UsesAdminVariant(PathString path) =>
        AdminVariantPrefixes.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));

    public string UrlFor(PathString path) => UsesAdminVariant(path) ? AdminUrl : Url;

    /// <summary>
    /// The visitor's light/dark choice (DESIGN.md §24.4), set by <c>js/theme-switch.js</c>: <c>light</c> or <c>dark</c>; no
    /// cookie (System) follows the admin's default mode. A preference per browser, never per account.
    /// </summary>
    public const string ModeCookie = "av-theme";

    /// <summary>"light", "dark" or null (System, or anything else in the cookie).</summary>
    public static string? VisitorMode(HttpContext? http) =>
        http?.Request.Cookies[ModeCookie] is { } v && (v == "light" || v == "dark") ? v : null;

    /// <summary>
    /// <c>data-bs-theme</c> for this request: the visitor's choice, else the admin's default mode; null follows the OS
    /// (prefers-color-scheme). Rendered by the server so a reload never flashes the other mode.
    /// </summary>
    public string? BootstrapThemeFor(HttpContext? http) => VisitorMode(http) ?? Settings.BootstrapTheme;
}

/// <summary>
/// <c>GET /branding/theme.css</c>, <c>GET /branding/favicon.svg</c> and <c>GET /branding/{name}</c>: anonymous (outside the account gate), long
/// cache when <c>?v=</c> matches the current version, else <c>no-cache</c>; 404 when absent.
/// </summary>
public static class BrandingEndpoints
{
    public const string Immutable = "public, max-age=31536000, immutable";

    public static IEndpointRouteBuilder MapBranding(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/branding/theme.css", (HttpContext http, ThemeService theme) =>
            {
                var (css, version) = theme.Get(admin: http.Request.Query["admin"] == "1");
                SetCache(http, version);
                return Microsoft.AspNetCore.Http.Results.Text(css, "text/css; charset=utf-8");
            })
            .AllowAnonymous()
            .ExemptFromAbuseBlock()
            .ExcludeFromDescription();

        endpoints.MapGet("/branding/favicon.svg", (HttpContext http, ThemeService theme) =>
            {
                SetCache(http, theme.Version);
                return Microsoft.AspNetCore.Http.Results.Text(theme.FaviconSvg, "image/svg+xml");
            })
            .AllowAnonymous()
            .ExemptFromAbuseBlock()
            .ExcludeFromDescription();

        endpoints.MapGet("/branding/{name}", (string name, HttpContext http, BrandingService branding) =>
            {
                if (branding.Get(name) is not { } file) return Microsoft.AspNetCore.Http.Results.NotFound();
                SetCache(http, file.Version);
                return Microsoft.AspNetCore.Http.Results.File(file.Path, file.ContentType, lastModified: file.LastModified);
            })
            .AllowAnonymous()
            .ExemptFromAbuseBlock()
            .ExcludeFromDescription();

        return endpoints;
    }

    private static void SetCache(HttpContext http, string version)
    {
        var headers = http.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.CacheControl = string.Equals(http.Request.Query["v"], version, StringComparison.Ordinal) ? Immutable : "no-cache";
    }
}
