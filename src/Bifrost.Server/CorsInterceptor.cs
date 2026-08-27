namespace Bifrost.Server;

/// <summary>
/// Emits CORS headers for allowlisted origins, and for nobody else.
/// </summary>
/// <remarks>
/// <para>
/// Runs before every route through <see cref="IBridgeServer.UseInterceptor"/>, so no endpoint can
/// accidentally omit it (FR-508), and decorates the response afterwards — a browser discards a
/// reply that carries no <c>Access-Control-Allow-Origin</c>, even one the server was happy to
/// serve.
/// </para>
/// <para>
/// <b>Demo scope.</b> The bearer-token half of ADR-006 is not implemented. Origin checking is here
/// because it costs almost nothing and because a permissive default would let any page on the
/// device drive the printer. Token authentication is Phase 6.
/// </para>
/// <para>
/// <b>No wildcards for remote origins.</b> <c>*.company.local</c> would authorise any compromised
/// subdomain to put labels on physical stock (DES-08 §5).
/// </para>
/// </remarks>
public sealed class CorsInterceptor(IReadOnlyCollection<string> allowedOrigins) : IRequestInterceptor
{
    public Task<BridgeResponse?> InterceptAsync(BridgeRequest request, CancellationToken ct)
    {
        var origin = request.Header("Origin");

        // No Origin header: not a browser. Only a token would stop this, and that is Phase 6.
        if (string.IsNullOrEmpty(origin)) return Task.FromResult<BridgeResponse?>(null);

        if (!IsAllowed(origin))
        {
            return Task.FromResult<BridgeResponse?>(BridgeResponse.Error(
                403, "ORIGIN_NOT_ALLOWED",
                $"Origin {origin} is not allowed to print from this device.",
                transient: false));
        }

        // Preflight: answer here rather than letting it reach a route that does not expect it.
        if (string.Equals(request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            var headers = new Dictionary<string, string>(Headers(origin));

            // Private Network Access. A page in a more public address space reaching a more
            // private one gets a preflight carrying Access-Control-Request-Private-Network, and
            // the browser discards the response unless the server explicitly consents.
            //
            // Answering only when asked, rather than always: it is a consent to a specific
            // request, and volunteering it on every preflight advertises more than was asked for.
            if (request.Header("Access-Control-Request-Private-Network") is not null)
            {
                headers["Access-Control-Allow-Private-Network"] = "true";
            }

            return Task.FromResult<BridgeResponse?>(new BridgeResponse(204, [], "text/plain", headers));
        }

        return Task.FromResult<BridgeResponse?>(null);
    }

    /// <summary>Attach the CORS headers to a response the route produced.</summary>
    public BridgeResponse Decorate(BridgeRequest request, BridgeResponse response)
    {
        var origin = request.Header("Origin");
        if (string.IsNullOrEmpty(origin) || !IsAllowed(origin)) return response;

        var headers = new Dictionary<string, string>(response.Headers ?? new Dictionary<string, string>());
        foreach (var (name, value) in Headers(origin)) headers[name] = value;

        return response with { Headers = headers };
    }

    private bool IsAllowed(string origin) =>
        allowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase) || IsLoopback(origin);

    /// <summary>
    /// Any loopback origin is allowed, on any port.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Origins compare exactly, including the port — <c>http://localhost:3000</c> is not
    /// <c>http://localhost</c>. Enumerating ports in the allowlist would be endless, and the
    /// alternative is worse: every practical way of serving a page on the device (a dev server,
    /// the bridge's own port) would be rejected, and the operator would learn to paste origins
    /// into a config to make printing work.
    /// </para>
    /// <para>
    /// <b>What this does and does not permit.</b> A loopback origin means a page served from this
    /// device. Every <i>remote</i> page is still refused, which is the threat the allowlist exists
    /// for (T-1). It does admit another local app that serves a page — already an accepted risk
    /// while token authentication is deferred (DES-08 §3.1), and the reason ADR-006 pairs the
    /// origin check with a token in the first place.
    /// </para>
    /// </remarks>
    private static bool IsLoopback(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));

    private static Dictionary<string, string> Headers(string origin) => new()
    {
        ["Access-Control-Allow-Origin"] = origin,
        ["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS",
        ["Access-Control-Allow-Headers"] = "Authorization, Content-Type, Idempotency-Key",
        ["Access-Control-Max-Age"] = "86400",

        // Always present, so a cache cannot serve one origin's permissive response to another.
        ["Vary"] = "Origin",
    };
}
