namespace Telrang.Web.Security;

/// <summary>
/// Turns a request body or form that breaks the endpoint's limits (<c>[RequestSizeLimit]</c>, <c>[RequestFormLimits]</c>,
/// e.g. on the vote page, DESIGN.md §21.1) into the static error page: 413 for an oversized body, 400 for too many or
/// too long form values. Without this the exception from reading the form (in antiforgery, the form guard or the page)
/// would be a 500. Runs before antiforgery so it covers every reader. The endpoint's body size limit is also enforced
/// here (Content-Length, then a counting stream), so it holds whatever the server does with the metadata (Kestrel applies
/// it itself; test and other servers may not).
/// </summary>
public sealed class FormLimitMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http)
    {
        if (http.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata>()?.MaxRequestBodySize is long max)
        {
            if (http.Request.ContentLength > max)
            {
                await StaticPages.FormTooLargeAsync(http, StatusCodes.Status413PayloadTooLarge);
                return;
            }
            http.Request.Body = new LimitedReadStream(http.Request.Body, max);
        }
        try
        {
            // Read (and cache) the form here, under the endpoint's form limits, so a refusal is this page and not
            // antiforgery's "Form expired".
            if (http.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.IFormOptionsMetadata>() is not null
                && HttpMethods.IsPost(http.Request.Method) && http.Request.HasFormContentType)
                await http.Request.ReadFormAsync(http.RequestAborted);
            await next(http);
        }
        catch (BadHttpRequestException e) when (!http.Response.HasStarted)
        {
            await StaticPages.FormTooLargeAsync(http, e.StatusCode == StatusCodes.Status413PayloadTooLarge
                ? StatusCodes.Status413PayloadTooLarge : StatusCodes.Status400BadRequest);
        }
        catch (InvalidDataException) when (!http.Response.HasStarted)
        {
            await StaticPages.FormTooLargeAsync(http, StatusCodes.Status400BadRequest);
        }
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
