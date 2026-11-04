using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Telrang.Web.Security;

public static class ReverseProxy
{
    /// <summary>
    /// The TCP networks whose X-Forwarded-* headers are trusted when nothing is configured: none outside a container. Not
    /// loopback either (pentest R3-DEP-7): every local account and process can reach a loopback port, so trusting it lets
    /// them pick any client address; a proxy on loopback is configured (<c>ReverseProxy:KnownProxies=127.0.0.1</c>), the
    /// Linux service listens on a Unix socket instead, whose peers are trusted (<see cref="IsUnixSocket"/>). In a container
    /// (<c>DOTNET_RUNNING_IN_CONTAINER=true</c>, set by the official .NET images) the networks of the container's own
    /// interfaces, i.e. the Docker bridge network that the published port and a proxy container connect from; see
    /// <see cref="ContainerNetworks"/>.
    /// </summary>
    public static IReadOnlyList<string> DefaultNetworks(IConfiguration config) =>
        bool.TryParse(config["DOTNET_RUNNING_IN_CONTAINER"], out var inContainer) && inContainer
            ? ContainerNetworks(InterfaceAddresses())
            : [];

    /// <summary>
    /// The request came over a Unix domain socket (the Linux service's default, <c>http://unix:/run/telrang/telrang.sock</c>).
    /// Only accounts in the socket's group can connect (the proxy), so its X-Forwarded-* headers are trusted.
    /// </summary>
    public static bool IsUnixSocket(HttpContext http) =>
        http.Features.Get<Microsoft.AspNetCore.Connections.Features.IConnectionEndPointFeature>()?.LocalEndPoint
            is System.Net.Sockets.UnixDomainSocketEndPoint;

    /// <summary>
    /// UseForwardedHeaders for both kinds of connection: from a Unix socket peer every X-Forwarded-* header is applied (the
    /// right-most entry, as from a trusted proxy); over TCP only from the configured or default trusted addresses.
    /// </summary>
    public static IApplicationBuilder UseAppForwardedHeaders(this IApplicationBuilder app)
    {
        var socket = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, ForwardLimit = 1 };
        socket.KnownProxies.Clear();
        socket.KnownIPNetworks.Clear();
        app.UseWhen(IsUnixSocket, b => b.UseForwardedHeaders(socket));
        app.UseWhen(http => !IsUnixSocket(http), b => b.UseForwardedHeaders());
        return app;
    }

    /// <summary>
    /// Kestrel creates a Unix socket with the process's umask; the proxy's group needs read and write (0660, pentest
    /// R3-DEP-3). Called once the server listens. The group comes from the directory (the unit's <c>/run/telrang</c> is
    /// setgid <c>telrang-proxy</c>).
    /// </summary>
    public static void OpenUnixSockets(IEnumerable<string> urls, ILogger logger)
    {
        if (OperatingSystem.IsWindows()) return;
        foreach (var url in urls)
        {
            if (UnixSocketPath(url) is not { } path || !File.Exists(path)) continue;
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
            logger.LogInformation("Listening on the Unix socket {Path} (mode 0660, for the proxy's group).", path);
        }
    }

    /// <summary>The socket path of an <c>http://unix:/path</c> address, else null.</summary>
    public static string? UnixSocketPath(string url) =>
        url.StartsWith("http://unix:", StringComparison.OrdinalIgnoreCase) ? url["http://unix:".Length..] :
        url.StartsWith("https://unix:", StringComparison.OrdinalIgnoreCase) ? url["https://unix:".Length..] : null;

    /// <summary>
    /// The networks of a container's interfaces (address masked to its prefix), without loopback and IPv6 link-local.
    /// On a Docker bridge network that is e.g. 172.18.0.0/16: the gateway (host side of a published port) and other
    /// containers on it. With <c>network_mode: host</c> it would be the host's LAN: configure KnownProxies there.
    /// </summary>
    public static IReadOnlyList<string> ContainerNetworks(IEnumerable<(IPAddress Address, int PrefixLength)> addresses) =>
        addresses
            .Where(a => !IPAddress.IsLoopback(a.Address) && !a.Address.IsIPv6LinkLocal)
            .Where(a => a.PrefixLength > 0 && a.PrefixLength <= (a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128))
            .Select(a => new System.Net.IPNetwork(Mask(a.Address, a.PrefixLength), a.PrefixLength).ToString())
            .Distinct()
            .ToList();

    private static IEnumerable<(IPAddress, int)> InterfaceAddresses()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(u => (u.Address, u.PrefixLength))
                .ToList();
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            return [];
        }
    }

    private static IPAddress Mask(IPAddress address, int prefix)
    {
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++) bytes[i] &= (byte)(0xFF << (8 - Math.Clamp(prefix - i * 8, 0, 8)));
        return new IPAddress(bytes);
    }

    /// <summary>The "unspecified" address (0.0.0.0): never a connection's peer, so listing it alone trusts nobody.</summary>
    private static readonly IPAddress NoProxy = IPAddress.Any;

    public static IServiceCollection AddAppForwardedHeaders(this IServiceCollection services, IConfiguration config)
    {
        var proxies = Split(config["ReverseProxy:KnownProxies"]);
        var networks = Split(config["ReverseProxy:KnownNetworks"]);
        var trustAll = bool.TryParse(config["ReverseProxy:TrustAll"], out var t) && t; // blank = false
        if (proxies.Length == 0 && networks.Length == 0 && !trustAll)
        {
            networks = [.. DefaultNetworks(config)];
        }
        // Nothing to trust over TCP: an impossible proxy keeps the middleware checking (both lists empty would trust all).
        if (proxies.Length == 0 && networks.Length == 0 && !trustAll) proxies = [NoProxy.ToString()];

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            // Both lists empty = the middleware trusts every source; that only happens with TrustAll.
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            if (trustAll)
            {
                return;
            }

            foreach (var p in proxies)
            {
                options.KnownProxies.Add(IPAddress.Parse(p));
            }
            foreach (var n in networks)
            {
                options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(n));
            }
        });

        return services;
    }

    /// <summary>
    /// True when Kestrel will listen on HTTPS itself (cert-file mode). Behind a TLS-terminating
    /// proxy this is false and redirection is left to the proxy.
    /// </summary>
    public static bool HttpsEndpointConfigured(IConfiguration config)
    {
        static bool HasHttps(string? urls) =>
            urls?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) == true;

        return HasHttps(config["urls"])
               || !string.IsNullOrWhiteSpace(config["https_ports"])
               || config.GetSection("Kestrel:Endpoints").GetChildren().Any(e => HasHttps(e["Url"]));
    }

    private static string[] Split(string? value) =>
        value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
}

/// <summary>
/// Host filtering from the DB setting <c>App.PublicUrl</c> (pentest AV-01): once it is set, a request whose Host header
/// is neither its host nor a loopback name gets a bare 400, like ASP.NET Core's <c>AllowedHosts</c> filter (which is
/// configuration, read before the DB setting exists). <c>/healthz</c> passes for probes that use a container name.
/// </summary>
public sealed class PublicHostFilterMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext http, Features.Settings.SettingsService settings)
    {
        if (!settings.IsAllowedHost(http.Request.Host.Host) && !http.Request.Path.StartsWithSegments("/healthz"))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Task.CompletedTask;
        }
        return next(http);
    }
}
