# ADR-001 — Loopback local server rather than cloud relay or LAN service

| Field | Value |
| --- | --- |
| Status | Accepted |
| Date | 2026-08-22 |
| Deciders | Bearing Team |
| Supersedes | — |

---

## Context

The web application must reach a Bluetooth printer. Three transport topologies are possible, and the
choice determines most of the rest of the architecture.

From [DISC-02](../../01-discovery/02-stakeholder-interview.md):

- **D-01** — the web app is served from an intranet server, but the **browser runs on the same
  handheld** that holds the printer connection
- **D-02** — the network is intranet-only; there is no internet egress
- **D-10** — pages are HTTP today and may become HTTPS
- **D-16** — one developer

## Options considered

### A. Loopback local server — `http://127.0.0.1:8437`

The app listens on the loopback interface. The SDK, running in a page on the same device, calls it
directly.

- **+** No network hop; latency is process-to-process
- **+** Works with Wi-Fi down — printing continues during a network outage
- **+** Chrome exempts `http://localhost` and `127.0.0.1` from mixed-content blocking, so an HTTPS
  page can call it without the app holding a TLS certificate
- **+** Loopback is unreachable from other hosts, so the attack surface is device-local
- **+** No discovery, no relay, no remote queue, no push infrastructure to build
- **−** Only works when browser and printer share a device
- **−** A loopback port is reachable by *any* process on the device, so requests must be authenticated

### B. LAN service — app listens on `0.0.0.0`, browser on another host

- **+** Browser and printer can be on different devices
- **−** Requires device discovery (mDNS) or static IP management across 20–100 devices
- **−** Chrome's Local Network Access permission prompt on every origin/device pair
- **−** Mixed-content blocking applies to non-loopback addresses; an HTTPS page cannot reach
  `http://192.168.x.x` — a certificate on each handheld would be required
- **−** Exposes the print bridge to the whole network
- **−** Solves a problem D-01 says we do not have

### C. Cloud relay — app polls or subscribes to a remote job queue

- **+** Works regardless of where the browser is
- **+** Central visibility across sites
- **−** Requires internet egress — **directly violates D-02**
- **−** Recurring cost per device, or a relay service to build and operate
- **−** Seconds of added latency on an action the operator is standing still for
- **−** Printing stops entirely when the uplink drops
- **−** Print content leaves the device — violates NFR-306

## Decision

**Adopt option A: a loopback-only local HTTP/WebSocket server on `127.0.0.1:8437`.**

The socket binds to the loopback interface exclusively and must never bind `0.0.0.0` (FR-504).
Because any local process can reach a loopback port, every state-changing endpoint requires
authentication — see [ADR-006](ADR-006-origin-allowlist-token-auth.md).

## Consequences

**Positive**

- Removes discovery, relay infrastructure, remote queueing, and push delivery from the scope. This is
  the single decision that makes the project tractable for one developer (D-16)
- Meets the offline requirement (NFR-204) structurally rather than through caching
- Same code path for HTTP and HTTPS pages (FR-710), thanks to the loopback mixed-content exemption

**Negative**

- Printing from a desktop browser to a handheld's printer is impossible. Accepted — this is non-goal
  NG-2
- Authentication becomes mandatory rather than optional, adding the pairing flow to v1.0 scope

**Neutral**

- If a future requirement genuinely needs cross-device printing, the queue and driver layers are
  unaffected; only a new transport into the queue would be added. The decision is reversible at the
  edge, not at the core

## Verification

- FR-504 test: a request to `http://<handheld-lan-ip>:8437/v1/status` from another host is refused
- FR-710 test: the SDK works identically from an HTTP page and an HTTPS page
- NFR-204 test: with Wi-Fi disabled, submitting and printing a job still succeeds

## Related

- [Architecture §2](../01-architecture.md)
- [Security Design](../08-security-design.md)
- [ADR-006](ADR-006-origin-allowlist-token-auth.md)
