using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Telrang.Web.Features.Settings;

namespace Telrang.Web.Security;

/// <summary>
/// Request body and form limits for every request (DESIGN.md §31). Kestrel refuses a body over
/// <see cref="MaxBodyBytes"/> (Program.cs) and the form options below apply to every form; endpoints may set tighter
/// ones (the vote page, <c>VoteFormLimits</c>). The largest legitimate HTTP form is the vote form (64 kB); passkey
/// credentials are a few kB. Editor saves, branding uploads and the admin's other changes travel over the Blazor circuit,
/// whose long-polling fallback posts circuit messages, so <c>/_blazor</c> gets <see cref="HubBodyBytes"/> instead.
/// </summary>
public static class RequestLimits
{
    /// <summary>Largest request body outside the Blazor hub: four times the vote form's limit.</summary>
    public const int MaxBodyBytes = 256 * 1024;

    /// <summary>Most values in one form (the account forms send about ten).</summary>
    public const int MaxFormValues = 64;

    /// <summary>Longest form value (a passkey credential is the longest legitimate one, a few kB).</summary>
    public const int MaxValueLength = 64 * 1024;

    /// <summary>Longest form key.</summary>
    public const int MaxKeyLength = 256;

    /// <summary>
    /// Largest body of a <c>/_blazor</c> request: a long-polling send may batch several circuit messages, each at most
    /// <see cref="LimitSettings.MaxCircuitMessageBytes"/> (SignalR enforces that per message).
    /// </summary>
    public static long HubBodyBytes => 4L * LimitSettings.MaxCircuitMessageBytes;

    public static bool IsHub(HttpContext http) => http.Request.Path.StartsWithSegments("/_blazor");

    /// <summary>The form options every form is read under, unless the endpoint sets its own.</summary>
    public static void Apply(FormOptions o)
    {
        o.ValueCountLimit = MaxFormValues;
        o.ValueLengthLimit = MaxValueLength;
        o.KeyLengthLimit = MaxKeyLength;
        o.MultipartBodyLengthLimit = MaxBodyBytes;
        o.BufferBodyLengthLimit = MaxBodyBytes;
    }
}

/// <summary>
/// Enforces the body limit (the endpoint's <c>[RequestSizeLimit]</c>, else <see cref="RequestLimits"/>) and turns a body
/// or form that breaks the limits into the static error page: 413 for an oversized body, 400 for too many or too long form
/// values. Without this the exception from reading the form (in antiforgery, the form guard or the page) would be a 500.
/// Runs before antiforgery so it covers every reader. The body limit is also enforced here (Content-Length before
/// anything is read, then a counting stream), so it holds whatever the server does (Kestrel applies it itself; test and
/// other servers may not). Each refusal over a limit records <see cref="AbuseSignal.FormTooLarge"/>: the app's own forms
/// never come near them. A body cut short by the client is refused without a signal.
/// </summary>
public sealed class FormLimitMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http)
    {
        // A status page re-execution keeps the first pass's body wrapper and form.
        var first = http.Features.Get<Microsoft.AspNetCore.Diagnostics.IStatusCodeReExecuteFeature>() is null;
        var hub = RequestLimits.IsHub(http);
        if (first)
        {
            long? max = http.GetEndpoint()?.Metadata.GetMetadata<IRequestSizeLimitMetadata>() is { } metadata
                ? metadata.MaxRequestBodySize
                : hub ? RequestLimits.HubBodyBytes : RequestLimits.MaxBodyBytes;
            if (hub && http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size) size.MaxRequestBodySize = max;
            if (max is { } limit)
            {
                if (http.Request.ContentLength > limit)
                {
                    await RefuseAsync(http, StatusCodes.Status413PayloadTooLarge, signal: true);
                    return;
                }
                if (!hub) http.Request.Body = new LimitedReadStream(http.Request.Body, limit);
            }
        }
        try
        {
            // Read (and cache) the form here, under the limits, so a refusal is this page and not antiforgery's
            // "Form expired". Only for a request something will read it for.
            if (first && !hub && http.GetEndpoint() is not null && HttpMethods.IsPost(http.Request.Method) && http.Request.HasFormContentType)
                await http.Request.ReadFormAsync(http.RequestAborted);
            await next(http);
        }
        catch (BadHttpRequestException e) when (!http.Response.HasStarted)
        {
            var tooLarge = e.StatusCode == StatusCodes.Status413PayloadTooLarge;
            await RefuseAsync(http, tooLarge ? StatusCodes.Status413PayloadTooLarge : StatusCodes.Status400BadRequest, signal: tooLarge);
        }
        catch (InvalidDataException) when (!http.Response.HasStarted)
        {
            // Too many values, a value or key too long, a malformed multipart body.
            await RefuseAsync(http, StatusCodes.Status400BadRequest, signal: true);
        }
    }

    private static Task RefuseAsync(HttpContext http, int status, bool signal)
    {
        if (signal)
        {
            http.RequestServices.GetService<AbuseTracker>()?.Record(ClientKeys.ForAbuse(http),
                http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, AbuseSignal.FormTooLarge);
        }
        return StaticPages.FormTooLargeAsync(http, status);
    }
}

/// <summary>A read-only body wrapper that fails with a 413 once more than <c>max</c> bytes were read.</summary>
internal sealed class LimitedReadStream(Stream inner, long max) : Stream
{
    private long read;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => read; set => throw new NotSupportedException(); }

    private int Count(int n)
    {
        read += n;
        if (read > max) throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge);
        return n;
    }

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => Count(await inner.ReadAsync(buffer, ct));
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Every <c>/p/...</c> response is <c>Cache-Control: no-store</c> (DESIGN.md §21.1): ballots, names and the voter's
/// responses are in the HTML, and no shared or browser cache may keep them.
/// </summary>
public sealed class PollNoStoreMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext http)
    {
        if (http.Request.Path.StartsWithSegments("/p"))
        {
            http.Response.OnStarting(() =>
            {
                http.Response.Headers.CacheControl = "no-store";
                return Task.CompletedTask;
            });
        }
        return next(http);
    }
}
