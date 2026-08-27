using System.Net;
using EmbedIO;
using EmbedIO.Actions;

namespace Bifrost.Server.EmbedIO;

/// <summary>
/// <see cref="IBridgeServer"/> implemented over EmbedIO.
/// </summary>
/// <remarks>
/// <b>This is the only file in the solution permitted to reference EmbedIO.</b> Everything above it
/// works with <see cref="BridgeRequest"/> and <see cref="BridgeResponse"/>. See ADR-009 and
/// Docs/04-implementation/02-project-structure.md §2.2.
///
/// EmbedIO was chosen over GenHTTP on one criterion: a demonstrated history on Xamarin.Android,
/// which is the same runtime net10.0-android produces. For a component that must not fail in a
/// warehouse, a proven track record on the exact platform outweighs a more modern API.
/// </remarks>
public sealed class EmbedIoBridgeServer : IBridgeServer
{
    private readonly Dictionary<(string Method, string Route), RouteHandler> _routes = [];
    private readonly List<IRequestInterceptor> _interceptors = [];

    private WebServer? _server;
    private CancellationTokenSource? _cts;

    public void MapGet(string route, RouteHandler handler) => _routes[("GET", route)] = handler;

    public void MapPost(string route, RouteHandler handler) => _routes[("POST", route)] = handler;

    public void UseInterceptor(IRequestInterceptor interceptor) => _interceptors.Add(interceptor);

    public Task StartAsync(IPAddress address, int port, CancellationToken ct)
    {
        // Loopback only. Binding 0.0.0.0 would expose the printer to the whole network and is the
        // one thing FR-504 forbids outright.
        var url = $"http://{address}:{port}/";

        var server = new WebServer(o => o
            .WithUrlPrefix(url)
            .WithMode(HttpListenerMode.EmbedIO));

        // One module for everything, and routing done here rather than by EmbedIO.
        //
        // Registering a module per route looks tidier and is wrong: a request that matches no
        // route — every CORS preflight, since OPTIONS is not a registered verb — is answered 404
        // by EmbedIO before any module runs, so the interceptors never see it. Chrome then treats
        // the preflight as failed and the real request never leaves the browser.
        //
        // Private Network Access makes this fatal rather than cosmetic: Chrome preflights even a
        // simple GET when the target is a loopback address, so 404-ing preflights breaks every
        // endpoint, not just the ones taking a JSON body.
        //
        // Routing here also makes this adapter behave like TestBridgeServer, which already did its
        // own routing. That divergence is what hid the bug.
        server = server.WithModule(new ActionModule("/", HttpVerbs.Any, HandleAsync));

        _server = server;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // RunAsync returns as soon as the listener is up; the task runs for the server's lifetime.
        _ = server.RunAsync(_cts.Token);
        return Task.CompletedTask;
    }

    private async Task HandleAsync(IHttpContext context)
    {
        var request = await ToBridgeRequestAsync(context).ConfigureAwait(false);

        // Interceptors run before routing, always, in registration order — so a preflight is
        // answered even though OPTIONS matches no route.
        foreach (var interceptor in _interceptors)
        {
            var shortCircuit = await interceptor
                .InterceptAsync(request, context.CancellationToken)
                .ConfigureAwait(false);

            if (shortCircuit is not null)
            {
                await WriteAsync(context, shortCircuit).ConfigureAwait(false);
                return;
            }
        }

        var response = _routes.TryGetValue((request.Method, request.Path), out var handler)
            ? await handler(request, context.CancellationToken).ConfigureAwait(false)
            : BridgeResponse.Error(404, "NOT_FOUND",
                $"No route for {request.Method} {request.Path}.", transient: false);

        // Decorate in reverse registration order, so the first interceptor registered has the
        // last word on the response — the mirror of it having the first word on the request.
        for (var i = _interceptors.Count - 1; i >= 0; i--)
        {
            response = _interceptors[i].Decorate(request, response);
        }

        await WriteAsync(context, response).ConfigureAwait(false);
    }

    private static async Task<BridgeRequest> ToBridgeRequestAsync(IHttpContext context)
    {
        var headers = context.Request.Headers.AllKeys
            .Where(k => k is not null)
            .ToDictionary(k => k!, k => context.Request.Headers[k] ?? string.Empty,
                          StringComparer.OrdinalIgnoreCase);

        var query = context.Request.QueryString.AllKeys
            .Where(k => k is not null)
            .ToDictionary(k => k!, k => context.Request.QueryString[k] ?? string.Empty,
                          StringComparer.OrdinalIgnoreCase);

        byte[] body = [];
        if (context.Request.HasEntityBody)
        {
            using var ms = new MemoryStream();
            await context.Request.InputStream.CopyToAsync(ms).ConfigureAwait(false);
            body = ms.ToArray();
        }

        return new BridgeRequest(
            context.Request.HttpMethod,
            context.Request.Url.AbsolutePath,
            headers,
            query,
            body);
    }

    private static async Task WriteAsync(IHttpContext context, BridgeResponse response)
    {
        context.Response.StatusCode = response.StatusCode;
        context.Response.ContentType = response.ContentType;

        if (response.Headers is not null)
        {
            foreach (var (name, value) in response.Headers)
            {
                context.Response.Headers[name] = value;
            }
        }

        context.Response.ContentLength64 = response.Body.Length;
        await context.Response.OutputStream
            .WriteAsync(response.Body, context.CancellationToken)
            .ConfigureAwait(false);
    }

    public Task StopAsync()
    {
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts?.Dispose();
        _server?.Dispose();
    }
}
