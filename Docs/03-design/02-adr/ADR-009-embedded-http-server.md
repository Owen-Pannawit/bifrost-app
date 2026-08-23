# ADR-009 — EmbedIO as the embedded HTTP/WebSocket server, behind an abstraction

| Field | Value |
| --- | --- |
| Status | **Accepted** |
| Date | 2026-08-22 |
| Deciders | Bearing Team |
| Supersedes | [ADR-004](ADR-004-ktor-embedded-server.md) |

---

## Context

[ADR-001](ADR-001-loopback-vs-cloud-relay.md) requires an HTTP + WebSocket server bound to
`127.0.0.1:8437` inside the app process. [ADR-004](ADR-004-ktor-embedded-server.md) selected Ktor,
which is a Kotlin library and became inapplicable when
[ADR-008](ADR-008-dotnet-for-android.md) moved the app to .NET.

**The obvious .NET replacement does not exist on this platform.** ASP.NET Core with Kestrel cannot
run on Android: there is no `Microsoft.AspNetCore.App` runtime pack for `android-arm64`, and the
`Microsoft.AspNetCore.App` framework reference is unsupported on `net*-android` target frameworks.
This is an acknowledged product gap with a long-standing open feature request — not a configuration
problem, and not something a workaround resolves cleanly.

A third-party embedded server is therefore mandatory, which introduces a dependency this project
would not otherwise have taken.

### Requirements, unchanged from ADR-004

- HTTP routing with JSON bodies
- WebSocket with server-initiated push and fan-out to multiple subscribers (FR-203)
- A **single interception point** for authentication and CORS ahead of every route
  (FR-502, FR-503, FR-508) — the requirement that most strongly shaped ADR-004
- Small footprint, low idle battery cost (NFR-106)
- Must load and run on `net10.0-android`

---

## Options considered

### A. ASP.NET Core / Kestrel

- **+** The obvious choice; middleware pipeline is exactly the right shape for auth and CORS
- **−** **Does not run on Android.** No runtime pack for `android-arm64`
- **−** Community workarounds exist but involve hand-copying framework assemblies into the app — an
  unsupported configuration to place a warehouse's printing on

Rejected on availability, not merit.

### B. EmbedIO

- **+** `netstandard2.0`, so it loads on `net10.0-android` without qualification
- **+** **Explicit Xamarin.Android track record** — the exact platform, with real deployments behind it
- **+** WebSocket support built in, not a side module
- **+** Module-based pipeline gives the single pre-route interception point the auth design needs
- **+** Small
- **−** Development pace has slowed; it is mature rather than actively evolving
- **−** Smaller API surface than ASP.NET Core — routing and content negotiation are more manual

### C. GenHTTP

- **+** Actively developed; explicitly documents embedding in MAUI, UWP, and Uno applications
- **+** WebSocket supported; optimised for low CPU and memory
- **+** Modern API
- **−** Smaller community than EmbedIO, and less specifically proven on Android
- **−** Newer, so fewer worked examples for the awkward cases

### D. Hand-rolled `HttpListener` or raw `TcpListener`

- **−** `HttpListener` support on Android is unreliable
- **−** A raw socket means implementing HTTP and the WebSocket handshake by hand. Not a defensible
  use of a single developer's budget

---

## Decision

**Use EmbedIO, accessed only through an `IBridgeServer` abstraction.**

EmbedIO is chosen over GenHTTP on one criterion: it has a demonstrated history on
**Xamarin.Android** specifically, which is the same runtime `net10.0-android` produces. For a
component that must not fail in a warehouse, a proven track record on the exact platform outweighs a
more modern API.

### The abstraction is the important half of this decision

Because ASP.NET Core is unavailable for reasons outside our control, and because EmbedIO's future
maintenance is not guaranteed, the HTTP layer is isolated behind an interface. No route handler, no
authentication logic, and no serialisation code references an EmbedIO type.

```csharp
public interface IBridgeServer
{
    void MapGet(string route, RouteHandler handler);
    void MapPost(string route, RouteHandler handler);
    void MapWebSocket(string route, IWebSocketHandler handler);
    void UseInterceptor(IRequestInterceptor interceptor);   // auth + CORS, before every route
    Task StartAsync(IPAddress address, int port, CancellationToken ct);
    Task StopAsync();
}

public delegate Task<BridgeResponse> RouteHandler(BridgeRequest request, CancellationToken ct);
```

`BridgeRequest` and `BridgeResponse` are plain records owned by `Bifrost.Server` — headers, path,
query, body bytes, status. Swapping to GenHTTP, or to Kestrel if ASP.NET Core ever ships Android
support, means writing one new `IBridgeServer` implementation and changing one line of composition
root. Routes, authentication, CORS, and WebSocket fan-out are untouched.

The auth and CORS interceptor runs through `UseInterceptor` ahead of all routing, preserving the
property that made ADR-004's design correct: **no endpoint can accidentally omit the security
check** (FR-502, FR-503, FR-508).

---

## Consequences

**Positive**

- The system's most externally-imposed constraint is contained behind one interface
- EmbedIO's `netstandard2.0` target removes any doubt about loading on Android
- Single interception point preserved, so the security properties of ADR-004 carry over unchanged
- Smaller and lighter than ASP.NET Core would have been

**Negative**

- A dependency on a third-party library whose maintenance is outside our control, for a component on
  the critical path. Tracked as [R-16](../../07-project/02-risk-register.md); the abstraction is the
  mitigation
- More manual routing and content handling than ASP.NET Core. The API is small — ten endpoints — so
  the cost is bounded
- The abstraction itself is code that would not exist under ASP.NET Core. Justified here, where the
  replacement risk is real rather than theoretical

**Neutral**

- The wire contract in [DES-03](../03-local-api-spec.md) is unaffected. It was written against HTTP,
  not against a server library — which is why this substitution costs nothing above the transport

---

## Verification

- NFR-102: `POST /v1/print` acknowledges within 150 ms at p95 (TC-802)
- NFR-103: `GET /v1/status` responds within 50 ms at p95 (TC-803)
- NFR-106: idle battery drain over 8 hours within 3% (TC-806)
- FR-508: preflight from a non-allowlisted origin returns no permissive CORS headers (TC-606)
- FR-502: every protected route rejects an unauthenticated request — verified by iterating **all**
  registered routes, so a new endpoint cannot silently escape the interceptor (TC-602)
- The abstraction holds: `Bifrost.Server` route and auth code contains no `EmbedIO` reference,
  checked by a build-time analyzer rule

---

## Related

- [ADR-004 — superseded](ADR-004-ktor-embedded-server.md)
- [ADR-008 — .NET for Android](ADR-008-dotnet-for-android.md)
- [Local API Specification](../03-local-api-spec.md)
- [Security Design](../08-security-design.md)
- [R-16 — third-party server dependency](../../07-project/02-risk-register.md)

---

## Sources

- [Support for MAUI runtimes in ASP.NET Core? — dotnet/aspnetcore#35077](https://github.com/dotnet/aspnetcore/issues/35077)
- [No runtime pack for Microsoft.AspNetCore.App for the specified RuntimeIdentifier — Microsoft Q&A](https://learn.microsoft.com/en-us/answers/questions/974223/no-runtime-pack-for-microsoft-aspnetcore-app-avail)
- [unosquare/embedio — a tiny, cross-platform, module-based web server for .NET](https://github.com/unosquare/embedio)
- [Adding an EmbedIO embedded web server to a Xamarin Forms app](https://spin.atomicobject.com/2018/01/08/xamarin-forms-embedded-web-server/)
- [GenHTTP webserver](https://genhttp.org/)
