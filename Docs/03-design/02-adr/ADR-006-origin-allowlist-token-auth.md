# ADR-006 — Origin allowlist plus bearer token, established by QR pairing

| Field | Value |
| --- | --- |
| Status | Accepted |
| Date | 2026-08-22 |
| Deciders | Bearing Team |

---

## Context

[ADR-001](ADR-001-loopback-vs-cloud-relay.md) places an HTTP server on `127.0.0.1:8437`. Loopback
protects against *remote* hosts, but not against the device itself: **any app or any web page open
on the handheld can reach that port.** Without authentication, a malicious page could waste an
operator's media, or print misleading labels onto physical stock — a data-integrity attack, not a
nuisance.

**D-15** sets the required level: origin allowlist plus token. Full per-user authentication is
deferred (NG-7) because devices are physically controlled and the network is intranet-only.

## Options considered

### A. No authentication

- **+** Nothing to build or explain
- **−** Any local page can drive the printer. Rejected by D-15

### B. Origin allowlist only

- **+** No pairing step
- **−** A native app on the device can set any `Origin` header it likes; only browsers enforce it
- **−** Insufficient alone

### C. Bearer token only

- **+** Strong against unauthenticated callers
- **−** Any page that obtains the token keeps it; no way to scope which sites may use it

### D. Origin allowlist + bearer token

- **+** Two independent controls: browsers cannot forge `Origin`, and non-browser callers do not have
  the token
- **+** Compromise of either alone is insufficient
- **−** Requires a pairing flow

### E. Request signing (QZ Tray model)

- **+** Strongest: each request signed by a private key held server-side
- **−** Requires key management infrastructure and a signing endpoint on the company web server
- **−** Disproportionate for an intranet-only deployment with physically controlled devices, and it
  would spend the single developer's budget on the wrong risk

## Decision

**Adopt option D: origin allowlist plus bearer token, with the token established by scanning a QR
code on the device's own barcode scanner.**

### Rules

1. The socket binds to loopback only (FR-504)
2. All endpoints require `Authorization: Bearer <token>` **except** `GET /v1/status` and
   `POST /v1/pair` (FR-502) — `/v1/status` must be reachable unauthenticated so a page can detect
   the bridge before pairing (FR-204)
3. Requests whose `Origin` is not on the allowlist are rejected with `403`, **regardless of token
   validity** (FR-503)
4. CORS preflight returns permissive headers only for allowlisted origins (FR-508)
5. The token is 32 bytes from a CSPRNG, base64url-encoded (NFR-303)
6. Only a **hash** of the token is stored in the database; the plaintext lives in
   EncryptedSharedPreferences for QR display (FR-507)
7. Tokens do not expire automatically but can be regenerated, immediately invalidating the previous
   one (FR-505)
8. The **pairing QR code** is valid for 5 minutes and is single-use (FR-506)
9. Tokens never appear in logs or the diagnostics bundle (NFR-304)

### Why QR pairing

The rugged handhelds carry an integrated barcode scanner (D-13). Displaying the token as a QR code
turns a 43-character secret into a one-second scan. Idea 6.1 from
[DISC-03](../../01-discovery/03-competitive-research.md) — using hardware already in the operator's
hand rather than asking them to type.

```mermaid
sequenceDiagram
    participant OP as Operator
    participant APP as BifrǫstApp
    participant WEB as Web app + SDK

    OP->>APP: open Pairing screen
    APP->>APP: generate token (32 bytes CSPRNG)
    APP->>APP: store hash; plaintext to EncryptedSharedPreferences
    APP-->>OP: display QR (valid 5 min, single use)
    OP->>WEB: focus pairing field, scan QR with device scanner
    WEB->>APP: POST /v1/pair { token, origin }
    APP->>APP: verify token, validity window, unused
    APP->>APP: add origin to allowlist; mark QR consumed
    APP-->>WEB: 200 { paired: true }
    WEB->>WEB: persist token in localStorage
    Note over WEB,APP: all later requests carry Bearer token
```

## Consequences

**Positive**

- Two independent controls; neither alone is sufficient to drive the printer
- Pairing takes about a second and requires no typing (NFR-503)
- Revocation is immediate and local — no server round trip (FR-505)

**Negative**

- The web app must handle an unpaired state and prompt for pairing. Handled by the SDK's typed
  `UNAUTHORIZED` error (FR-706)
- A token persisted in `localStorage` is readable by script on the same origin. Accepted: that origin
  is already allowlisted, so it is authorised by definition. The token grants printing on one device
  and nothing else

**Neutral**

- If a stronger model is ever needed, request signing can be added as an additional scheme without
  changing the transport or the API shape

## Verification

- FR-502: unauthenticated calls to protected endpoints return `401`; `/v1/status` returns `200`
- FR-503: a valid token from a non-allowlisted origin returns `403`
- FR-506: a QR older than 5 minutes, or already consumed, is refused
- FR-507: inspecting app storage shows no plaintext token
- NFR-304: `grep` for the token across the diagnostics bundle and logs returns nothing

## Related

- [Security Design](../08-security-design.md)
- [Local API Specification](../03-local-api-spec.md)
- [ADR-001](ADR-001-loopback-vs-cloud-relay.md)
