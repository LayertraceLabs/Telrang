namespace Telrang.Web.Hosting;

/// <summary>
/// <c>healthcheck [--url &lt;url&gt;]</c> (DESIGN.md §29): GETs <c>/healthz</c> of the app on this machine and exits 0 when
/// it answers 200, else 1. The Docker image's <c>HEALTHCHECK</c> (the runtime image has no curl); also usable from a
/// shell on a Linux or Windows install. Without <c>--url</c> the address is the first <c>http</c> one in
/// <c>ASPNETCORE_URLS</c>/<c>URLS</c> with a wildcard host replaced by loopback, else <c>http://127.0.0.1:8080</c>.
/// It starts no server and reads no database or operator file.
/// </summary>
public static class HealthCheckCommand
{
    public const string Name = "healthcheck";

    public static async Task<int> RunAsync(string[] args, TextWriter output)
    {
        string? url = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--url" && i + 1 < args.Length) url = args[++i];
            else
            {
                await output.WriteLineAsync("usage: healthcheck [--url http://127.0.0.1:8080]");
                return 2;
            }
        }
        url ??= DefaultUrl(Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? Environment.GetEnvironmentVariable("URLS"));
        // The Linux service's Unix socket (http://unix:/run/telrang/telrang.sock): local by nature.
        if (Security.ReverseProxy.UnixSocketPath(url) is { } socket) return await CheckSocketAsync(socket, output);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("http" or "https"))
        {
            await output.WriteLineAsync("healthcheck: --url must be an http(s) address");
            return 2;
        }
        // Loopback only: this checks the local app and is never a way to make the host fetch other addresses.
        if (!baseUri.IsLoopback)
        {
            await output.WriteLineAsync("healthcheck: the address must be loopback (127.0.0.1, ::1 or localhost)");
            return 2;
        }
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            // A loopback HTTPS endpoint usually serves the public name's certificate: the name cannot match here.
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var response = await client.GetAsync(new Uri(baseUri, "/healthz"));
            await output.WriteLineAsync($"healthcheck: {(int)response.StatusCode}");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            await output.WriteLineAsync("healthcheck: no answer");
            return 1;
        }
    }

    private static async Task<int> CheckSocketAsync(string path, TextWriter output)
    {
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectCallback = async (_, ct) =>
            {
                var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream,
                    System.Net.Sockets.ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(path), ct);
                    return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var response = await client.GetAsync("http://localhost/healthz");
            await output.WriteLineAsync($"healthcheck: {(int)response.StatusCode}");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Net.Sockets.SocketException)
        {
            await output.WriteLineAsync("healthcheck: no answer");
            return 1;
        }
    }

    /// <summary>The first http address of a <c>URLS</c> value with <c>+</c>, <c>*</c>, <c>0.0.0.0</c> or <c>[::]</c> as loopback.</summary>
    public static string DefaultUrl(string? urls)
    {
        var all = (urls ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var pick = all.FirstOrDefault(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) ?? all.FirstOrDefault();
        if (pick is null) return "http://127.0.0.1:8080";
        return pick.Replace("://+", "://127.0.0.1").Replace("://*", "://127.0.0.1")
            .Replace("://0.0.0.0", "://127.0.0.1").Replace("://[::]", "://127.0.0.1");
    }
}
