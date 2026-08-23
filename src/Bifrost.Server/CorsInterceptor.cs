namespace Bifrost.Server;

/// <summary>
/// Emits CORS headers for allowlisted origins, and for nobody else.
/// </summary>
/// <remarks>
/// <para>
/// Runs before every route through <see cref="IBridgeServer.UseInterceptor"/>, so no endpoint can
/// accidentally omit it (FR-508).
/// </para>
/// <para>
/// <b>Demo scope.</b> The bearer-token half of ADR-006 is not implemented — see the plan's "What is
/// deliberately not built". Origin checking is here because it costs almost nothing and because
/// leaving <c>Access-Control-Allow-Origin: *</c> in place would let any page on the device drive
/// the printer. Token authentication is Phase 6.
/// </para>
/// <para>
/// <b>No wildcards, ever.</b> <c>*.company.local</c> would authorise any compromised subdomain to
/// put labels on physical stock (DES-08 §5).
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
            return Task.FromResult<BridgeResponse?>(new BridgeResponse(204, [], "text/plain", Headers(origin)));
        }

        return Task.FromResult<BridgeResponse?>(null);
    }

    /// <summary>Headers to attach to a successful response for an allowlisted origin.</summary>
    public IReadOnlyDictionary<string, string>? HeadersFor(string? origin) =>
        !string.IsNullOrEmpty(origin) && IsAllowed(origin) ? Headers(origin) : null;

    private bool IsAllowed(string origin) =>
        allowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase);

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
