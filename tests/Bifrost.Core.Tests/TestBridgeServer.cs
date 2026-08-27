using System.Net;
using System.Text;
using Bifrost.Server;

namespace Bifrost.Core.Tests;

/// <summary>
/// An <see cref="IBridgeServer"/> that dispatches in-process instead of opening a socket.
/// </summary>
/// <remarks>
/// This is the payoff of ADR-009 that was not its motivation. The abstraction exists because
/// ASP.NET Core is unavailable on Android; a side effect is that the complete request pipeline —
/// interceptors, routing, serialisation, error mapping — is exercisable with no socket, no
/// Android and no printer, at unit-test speed (TST-01 §3.2).
/// </remarks>
public sealed class TestBridgeServer : IBridgeServer
{
    private readonly Dictionary<(string Method, string Route), RouteHandler> _routes = [];
    private readonly List<IRequestInterceptor> _interceptors = [];

    public bool IsListening { get; private set; }

    public IPAddress? BoundAddress { get; private set; }

    public int BoundPort { get; private set; }

    public void MapGet(string route, RouteHandler handler) => _routes[("GET", route)] = handler;

    public void MapPost(string route, RouteHandler handler) => _routes[("POST", route)] = handler;

    public void UseInterceptor(IRequestInterceptor interceptor) => _interceptors.Add(interceptor);

    public Task StartAsync(IPAddress address, int port, CancellationToken ct)
    {
        BoundAddress = address;
        BoundPort = port;
        IsListening = true;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        IsListening = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Every registered route, so a test can assert none escaped an interceptor.</summary>
    public IReadOnlyCollection<(string Method, string Route)> Routes => _routes.Keys;

    public Task<BridgeResponse> SendAsync(
        string method,
        string path,
        string? body = null,
        IReadOnlyDictionary<string, string>? headers = null) =>
        DispatchAsync(new BridgeRequest(
            method,
            path,
            headers ?? new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            body is null ? [] : Encoding.UTF8.GetBytes(body)));

    public async Task<BridgeResponse> DispatchAsync(BridgeRequest request)
    {
        foreach (var interceptor in _interceptors)
        {
            var shortCircuit = await interceptor.InterceptAsync(request, CancellationToken.None);
            if (shortCircuit is not null) return shortCircuit;
        }

        var response = _routes.TryGetValue((request.Method, request.Path), out var handler)
            ? await handler(request, CancellationToken.None)
            : BridgeResponse.Error(404, "NOT_FOUND", $"No route for {request.Method} {request.Path}.", false);

        // Must mirror the real adapter, or tests pass against a pipeline that does not exist.
        // Missing this is exactly how the absent CORS headers on success went unnoticed.
        for (var i = _interceptors.Count - 1; i >= 0; i--)
        {
            response = _interceptors[i].Decorate(request, response);
        }

        return response;
    }
}
