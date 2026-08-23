using System.Net;

namespace Bifrost.Server;

/// <summary>
/// The HTTP + WebSocket server, abstracted away from whichever library provides it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this interface exists.</b> ASP.NET Core does not run on Android — there is no
/// <c>Microsoft.AspNetCore.App</c> runtime pack for <c>android-arm64</c>, and it is an acknowledged
/// product gap rather than a configuration error. The server on the critical path is therefore a
/// third-party library whose maintenance is outside our control (risk R-16).
/// </para>
/// <para>
/// No route handler, no interceptor and no serialisation code may reference an EmbedIO type. Only
/// <c>Bifrost.Server.EmbedIO</c> may, enforced by a banned-symbols analyzer rule. Swapping to
/// GenHTTP is then one new adapter and one line in the composition root.
/// </para>
/// <para>See Docs/03-design/02-adr/ADR-009-embedded-http-server.md.</para>
/// </remarks>
public interface IBridgeServer : IAsyncDisposable
{
    void MapGet(string route, RouteHandler handler);

    void MapPost(string route, RouteHandler handler);

    /// <summary>
    /// Runs before every route. The single place authentication and CORS are applied, so no
    /// endpoint can accidentally omit them (FR-502, FR-503, FR-508).
    /// </summary>
    void UseInterceptor(IRequestInterceptor interceptor);

    /// <summary>Bind and start listening. Loopback only — never 0.0.0.0 (FR-504).</summary>
    Task StartAsync(IPAddress address, int port, CancellationToken ct);

    Task StopAsync();
}

public delegate Task<BridgeResponse> RouteHandler(BridgeRequest request, CancellationToken ct);

/// <summary>
/// Returns null to let the request proceed, or a response to short-circuit it.
/// </summary>
public interface IRequestInterceptor
{
    Task<BridgeResponse?> InterceptAsync(BridgeRequest request, CancellationToken ct);
}

/// <summary>A request, in terms this project owns rather than a server library's.</summary>
public sealed record BridgeRequest(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyDictionary<string, string> Query,
    byte[] Body)
{
    public string? Header(string name) =>
        Headers.TryGetValue(name, out var value) ? value : null;

    public string BodyAsText() => System.Text.Encoding.UTF8.GetString(Body);
}

/// <summary>A response, ditto.</summary>
public sealed record BridgeResponse(
    int StatusCode,
    byte[] Body,
    string ContentType = "application/json; charset=utf-8",
    IReadOnlyDictionary<string, string>? Headers = null)
{
    public static BridgeResponse Json(int statusCode, string json) =>
        new(statusCode, System.Text.Encoding.UTF8.GetBytes(json));

    public static BridgeResponse Ok(string json) => Json(200, json);

    public static BridgeResponse Accepted(string json) => Json(202, json);

    /// <summary>An error in the shape defined by DES-03 §4 — one envelope for every failure.</summary>
    /// <remarks>
    /// camelCase is part of the wire contract, not a preference: DES-03 specifies
    /// <c>error.code</c>. Serialising with default options emits PascalCase and silently breaks
    /// every client's error handling.
    /// </remarks>
    public static BridgeResponse Error(int statusCode, string code, string message, bool transient) =>
        Json(statusCode, System.Text.Json.JsonSerializer.Serialize(
            new ErrorEnvelope(new ErrorBody(code, message, transient)), WireJson));

    /// <summary>The one serialiser configuration for everything on the wire.</summary>
    public static readonly System.Text.Json.JsonSerializerOptions WireJson = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>
/// The single error shape. Every failure response in the API uses it, so a client writes one
/// handler rather than one per endpoint (DES-03 §4).
/// </summary>
public sealed record ErrorEnvelope(ErrorBody Error);

public sealed record ErrorBody(string Code, string Message, bool Transient);
