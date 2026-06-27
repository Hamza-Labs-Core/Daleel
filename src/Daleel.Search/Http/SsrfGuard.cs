using System.Net;
using System.Net.Sockets;

namespace Daleel.Search.Http;

/// <summary>
/// Thrown by <see cref="SsrfGuard.ConnectAsync"/> when a connection target resolves to a private or
/// otherwise blocked address. Callers that fetch attacker-influenced URLs treat this as "skip"
/// (best-effort), never propagating it to the user.
/// </summary>
public sealed class SsrfBlockedException : Exception
{
    public SsrfBlockedException(string host)
        : base($"Refusing to connect to '{host}': it resolves to a private, loopback, or otherwise blocked address.")
    {
    }
}

/// <summary>
/// Server-side request forgery (SSRF) guard for fetching attacker-influenced URLs — scraped pages,
/// LLM-extracted <c>image_url</c> fields, and guessed homepage domains. It is the single source of
/// truth for "is this URL safe for the server to fetch", shared by the R2 image copier and every
/// scraper/planner read path.
/// </summary>
/// <remarks>
/// Defence in depth, two layers:
/// <list type="number">
/// <item><see cref="IsSafePublicUrlAsync"/> — a pre-flight scheme + DNS check a caller runs before
/// issuing a request, so an internal target is rejected before any socket opens (and the caller can
/// degrade gracefully to the original URL).</item>
/// <item><see cref="ConnectAsync"/> — wired in as <see cref="SocketsHttpHandler.ConnectCallback"/>, it
/// re-resolves and validates the address at connect time (and on every redirect hop) and pins the
/// connection to the validated IPs. This defeats the DNS-rebinding TOCTOU window that a pre-flight
/// check alone cannot close.</item>
/// </list>
/// Blocked ranges: loopback (<c>127/8</c>, <c>::1</c>), RFC1918 (<c>10/8</c>, <c>172.16/12</c>,
/// <c>192.168/16</c>), link-local (<c>169.254/16</c> — covers the cloud metadata endpoint — and
/// <c>fe80::/10</c>), ULA (<c>fc00::/7</c>), CGNAT (<c>100.64/10</c>), <c>0.0.0.0/8</c>, multicast,
/// reserved, and IPv4-mapped-IPv6 forms of all of the above.
/// </remarks>
public static class SsrfGuard
{
    /// <summary>True when <paramref name="ip"/> is loopback/private/link-local/etc. and must never be fetched.</summary>
    public static bool IsBlocked(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);

        // Unwrap IPv4-mapped IPv6 (e.g. ::ffff:10.0.0.1) so the IPv4 rules below apply to it.
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip))
        {
            return true; // 127.0.0.0/8 and ::1
        }

        switch (ip.AddressFamily)
        {
            case AddressFamily.InterNetwork:
            {
                var b = ip.GetAddressBytes();
                return b[0] == 0                                   // 0.0.0.0/8 — "this host"
                    || b[0] == 10                                  // 10.0.0.0/8 — RFC1918
                    || (b[0] == 172 && b[1] is >= 16 and <= 31)    // 172.16.0.0/12 — RFC1918
                    || (b[0] == 192 && b[1] == 168)                // 192.168.0.0/16 — RFC1918
                    || (b[0] == 169 && b[1] == 254)                // 169.254.0.0/16 — link-local (cloud metadata)
                    || (b[0] == 100 && b[1] is >= 64 and <= 127)   // 100.64.0.0/10 — CGNAT
                    || b[0] == 127                                 // belt-and-suspenders with IsLoopback
                    || b[0] >= 224;                                // 224/4 multicast + 240/4 reserved + 255.255.255.255
            }

            case AddressFamily.InterNetworkV6:
            {
                if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast)
                {
                    return true; // fe80::/10, fec0::/10 (deprecated), ff00::/8
                }

                if (ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6Loopback))
                {
                    return true;
                }

                // Unique local addresses fc00::/7 (both fc.. and fd.. high-bit forms).
                return (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;
            }

            default:
                return true; // unknown address family — refuse
        }
    }

    /// <summary>
    /// Synchronous, DNS-free pre-flight for URLs that will be fetched by a third-party scraper edge
    /// (Context.dev / Cloudflare Browser) rather than by this host directly. It rejects non-http(s)
    /// schemes, IP literals in any blocked range (incl. the cloud-metadata 169.254.169.254 and loopback),
    /// and obvious internal hostnames (localhost, *.local, *.internal). It deliberately does NOT resolve
    /// DNS: this host never opens the socket here, so a network round-trip would add nothing but flakiness.
    /// Use <see cref="IsSafePublicUrlAsync"/> for fetches issued from this host (e.g. the R2 image copier).
    /// </summary>
    public static bool IsSafePublicUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        var host = uri.DnsSafeHost;
        if (host.Length == 0)
        {
            return false;
        }

        if (IPAddress.TryParse(host, out var ip))
        {
            return !IsBlocked(ip);
        }

        return !host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Pre-flight check WITH DNS: true only when <paramref name="url"/> is an absolute http(s) URL whose
    /// host resolves exclusively to safe public addresses. A host that won't resolve, or resolves to any
    /// blocked address, returns false. Never throws. For fetches issued directly from this host.
    /// </summary>
    public static async ValueTask<bool> IsSafePublicUrlAsync(string? url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        try
        {
            var addresses = await ResolveAsync(uri.DnsSafeHost, ct).ConfigureAwait(false);
            // Conservative: reject if the host resolves to *any* blocked address — a poisoned DNS answer
            // can mix a public decoy with an internal target.
            return addresses.Length > 0 && Array.TrueForAll(addresses, a => !IsBlocked(a));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return false; // unresolvable / malformed host — treat as unsafe
        }
    }

    /// <summary>
    /// A <see cref="SocketsHttpHandler.ConnectCallback"/> that resolves the target, refuses any blocked
    /// address, and connects only to the validated IPs (pinned — never re-resolved). Fires for every new
    /// connection including redirect hops, so it is the rebind-proof backstop behind the pre-flight check.
    /// </summary>
    public static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var endPoint = context.DnsEndPoint;
        var addresses = await ResolveAsync(endPoint.Host, ct).ConfigureAwait(false);

        if (addresses.Length == 0 || Array.Exists(addresses, IsBlocked))
        {
            throw new SsrfBlockedException(endPoint.Host);
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            // Connect to the validated addresses only — no second DNS lookup, so a rebind between the
            // resolve above and the connect cannot redirect us to an internal host.
            await socket.ConnectAsync(addresses, endPoint.Port, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Builds a dedicated, SSRF-guarded <see cref="HttpClient"/> for fetching attacker-influenced URLs
    /// directly from this host (the R2 image copier). Redirects are disabled outright — combined with the
    /// connect-time guard this leaves no path to an internal address.
    /// </summary>
    public static HttpClient CreateGuardedClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 8,
            AllowAutoRedirect = false,
            ConnectCallback = ConnectAsync
        };
        return new HttpClient(handler, disposeHandler: true);
    }

    private static ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        // An IP literal needs no DNS round-trip (and Dns.GetHostAddressesAsync would just echo it back).
        return IPAddress.TryParse(host, out var literal)
            ? ValueTask.FromResult(new[] { literal })
            : new ValueTask<IPAddress[]>(Dns.GetHostAddressesAsync(host, ct));
    }
}
