# ADR-004 — Ktor as the embedded HTTP/WebSocket server

| Field | Value |
| --- | --- |
| Status | ⚠️ **SUPERSEDED by [ADR-009](ADR-009-embedded-http-server.md)** |
| Date | 2026-08-22 |
| Superseded | 2026-08-22 |
| Deciders | Bearing Team |

> **This decision no longer holds.** Ktor is a Kotlin library, and
> [ADR-008](ADR-008-dotnet-for-android.md) moved the app to .NET. The server is now **EmbedIO behind
> an `IBridgeServer` abstraction** — see [ADR-009](ADR-009-embedded-http-server.md).
>
> The **requirements** below are unchanged and were carried into ADR-009 verbatim — in particular
> the demand for a *single interception point* for authentication and CORS ahead of every route,
> which remains the property that most shaped the choice.

---

## Context

The app must run an HTTP server on `127.0.0.1:8437` ([ADR-001](ADR-001-loopback-vs-cloud-relay.md))
that additionally serves a WebSocket endpoint for live job and printer events (FR-203).

Requirements it must satisfy:

- HTTP routing with JSON request/response bodies
- WebSocket with server-initiated push and fan-out to multiple subscribers
- Request interception for authentication and CORS before handlers run (FR-502, FR-503, FR-508)
- Small APK footprint and low idle battery cost (NFR-106)
- Kotlin-native, coroutine-friendly (ADR-002)

## Options considered

### A. NanoHTTPD

- **+** Tiny (~50 KB), long-established, trivially embeddable
- **−** Thread-per-connection, not coroutine-based
- **−** WebSocket support is a separate, minimally maintained module
- **−** No routing, no content negotiation, no interceptor pipeline — all hand-rolled
- **−** Hand-rolled CORS and auth interception is exactly where security bugs live

### B. Ktor embedded (CIO engine)

- **+** Kotlin-first, coroutine-native — matches the rest of the app
- **+** WebSocket is a first-class plugin with clean session handling
- **+** Plugin pipeline gives a single, testable place for auth and CORS
- **+** `ContentNegotiation` with `kotlinx.serialization` — the same serialisation used for payload
  parsing
- **+** Actively maintained by JetBrains
- **−** Larger dependency (~2 MB with the CIO engine and plugins)
- **−** More configuration surface than NanoHTTPD

### C. OkHttp MockWebServer / a raw `ServerSocket`

- **−** MockWebServer is a test tool, not a production server
- **−** A raw socket means writing HTTP parsing, which is not a reasonable use of the budget

### D. gRPC / a binary protocol

- **−** Browsers cannot speak gRPC without a proxy
- **−** Rejects the requirement that the caller is ordinary JavaScript (P-2)

## Decision

**Use Ktor with the CIO engine, embedded in the app process.**

Plugins enabled: `Routing`, `ContentNegotiation` (kotlinx.serialization), `WebSockets`, `CORS`,
`StatusPages`, `CallLogging`.

Authentication and origin checking are implemented as a single interception plugin that runs before
any route handler, so no endpoint can accidentally omit them.

The server is owned by the foreground service (FR-407), started when the service starts and stopped
with it — it never runs without the notification that tells the operator the bridge is live.

## Consequences

**Positive**

- One interception point for auth and CORS, which is the correct shape for FR-502/FR-503/FR-508 and
  makes them testable in isolation
- WebSocket fan-out uses coroutine channels, matching `EventHub`'s design with no adapter
- Same serialisation library for the wire format and for payload schemas, so a payload type is
  declared once

**Negative**

- ~2 MB of APK size. Acceptable for an MDM-distributed internal app
- Ktor's CIO engine spawns its own dispatcher; idle cost must be measured against NFR-106 during
  performance testing

**Neutral**

- If footprint ever became critical, the routing surface is small enough that swapping the engine
  would be contained — the handlers depend on the contract, not on Ktor internals

## Verification

- NFR-102: `POST /v1/print` acknowledges within 150 ms at p95
- NFR-103: `GET /v1/status` responds within 50 ms at p95
- NFR-106: measured idle battery drain over an 8-hour period stays within 3%
- FR-508: preflight from a non-allowlisted origin returns no permissive CORS headers

## Related

- [Local API Specification](../03-local-api-spec.md)
- [Security Design](../08-security-design.md)
- [Tech Stack](../../04-implementation/01-tech-stack.md)
