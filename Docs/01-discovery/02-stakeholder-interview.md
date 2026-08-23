# Stakeholder Interview & Decision Log

| Field | Value |
| --- | --- |
| Document ID | DISC-02 |
| Version | 1.0 |
| Date | 2026-08-22 |
| Status | Approved |
| Participants | Bearing Team (product owner / developer), engineering |

---

## 1. Purpose

This document records the requirements-elicitation session held on 2026-08-22 and freezes the
decisions taken. Every decision below carries an ID (`D-nn`) and is referenced from the design
documents, so that any later change can be traced to the artefacts it invalidates.

---

## 2. Decision log

| ID | Topic | Decision | Consequence |
| --- | --- | --- | --- |
| **D-01** | Topology | The web app is served from a company intranet server; the browser runs on the **same handheld** that is paired to the printer | Bridge listens on loopback `127.0.0.1`. No relay, no LAN discovery, no mDNS |
| **D-02** | Network | Fully intranet. No internet egress | Cloud-relay architectures excluded. No telemetry to external services |
| **D-03** | Printer transport | **Bluetooth Classic (SPP)** and **Bluetooth LE (GATT)** | Two transport implementations required. USB and TCP deferred |
| **D-04** | Web app ownership | The company owns and can freely modify the web app | Ship a **JavaScript SDK**; no need to intercept `window.print()` or reverse-engineer a third-party page |
| **D-05** | Deliverable | **Android app + JavaScript library**. The web application itself is *not* in scope | Two build pipelines, two versioned artefacts, one shared API contract |
| **D-06** | Print content | Labels/stickers with **barcode and QR**, plus paper receipts/slips | Both die-cut label media and continuous receipt media must be supported |
| **D-07** | Printer model | **Not yet purchased.** Vendor recommendation requested | [Hardware Recommendation](../06-operations/03-hardware-recommendation.md) is a required deliverable. Driver layer must not assume a vendor |
| **D-08** | Printers per handheld | **One printer per handheld**, which must handle both labels and receipts | Single active connection. Media-type switching handled in-app, not by routing to a second device |
| **D-09** | Language | **English and numerals only.** No Thai | Native printer font commands are sufficient. No bitmap text rendering, no CP874/TIS-620 handling. Smaller payloads, faster prints |
| **D-10** | Web protocol | Currently HTTP; can be migrated to HTTPS | The SDK must work from both origins. Loopback exemption from mixed-content makes this viable |
| **D-11** | Scale | **20–100 handhelds** | Central configuration and MDM-based rollout needed; full fleet-telemetry infrastructure is not |
| **D-12** | Reliability | **Persistent queue + automatic retry**, and **duplicate prevention (idempotency)** | Durable on-device queue surviving app restart; `Idempotency-Key` contract on the API |
| **D-13** | Hardware | **Rugged handhelds** (Zebra / Honeywell / Urovo class) | Devices ship with an integrated barcode scanner — exploitable for pairing and print verification. Older Android versions likely; min SDK 29 |
| **D-14** | Payload API | **All three tiers**: Template, Layout DSL, and Raw bytes | Layered API with a shared intermediate representation. Largest single scope item |
| **D-15** | Security | **Origin allowlist + pairing token** | Any local process can reach a loopback port, so requests must be authenticated. Full user-level auth deferred |
| **D-16** | Team | **One developer, AI-assisted** | Favour a single-language stack with minimal moving parts. Documentation must be executable-grade, not tutorial-grade |
| **D-17** | Documentation | **Practical** — complete across the lifecycle but concise | Full SDLC coverage, requirement IDs and traceability, without formal IEEE templates or sign-off ceremony |

---

## 3. Discussion notes

### 3.1 Topology (D-01)

The decisive question of the session. Three candidate topologies were presented:

1. Browser and printer-bridge on the same device (loopback)
2. Browser on a separate PC/tablet, bridge on an Android print station (LAN)
3. Browser and backend in the cloud, bridge polling remotely (relay)

The answer — *"the web/API is on the company server, it runs on handheld Android, everything is
inside the company network"* — resolves to topology 1. The web **server** is remote, but the
**browser** is on the handheld, and it is the browser's location that determines how the SDK can
reach the bridge.

This single fact removes device discovery, relay infrastructure, remote job queues, push
notification delivery, and cross-device pairing from the scope. See
[ADR-001](../03-design/02-adr/ADR-001-loopback-vs-cloud-relay.md).

### 3.2 No Thai text (D-09)

Raised proactively because Thai on ESC/POS printers is the most common failure mode for this class
of project in Thailand: inconsistent code page numbering across vendors, above/below vowel marks
colliding, and low-cost printers shipping without a Thai font at all. The reliable fix is rendering
text to a bitmap on the Android side and sending it as a raster image.

The confirmation that content is English and numeric only removes that entire subsystem. Native font
commands can be used throughout, which also keeps payloads small — a material benefit on a Bluetooth
link. Bitmap rendering remains available for the `image` element but is not on the text path.

### 3.3 Three-tier payload API (D-14)

Rather than choosing one abstraction level, all three are layered so that each tier compiles down to
the next:

| Tier | Caller writes | Best for |
| --- | --- | --- |
| 1 — Template | `{ template: "part-label", data: {...} }` | The 90% case. Layout lives on the device and can be revised without redeploying the web app |
| 2 — Layout DSL | An array of `text` / `barcode` / `qr` / `image` elements | Dynamic layouts the web app composes at runtime |
| 3 — Raw | Base64 printer command bytes | Escape hatch. Guarantees no requirement is ever blocked by a missing SDK feature |

The cost is a larger API surface; the benefit is that tier 3 makes the project un-blockable, and
tier 1 keeps everyday calls to a few lines. See
[ADR-003](../03-design/02-adr/ADR-003-three-tier-payload-api.md).

### 3.4 Idempotency (D-12)

Duplicate labels are worse than missing ones. A missing label is visible and gets reprinted; a
duplicate sticker on a bin means two physical items claim the same identity. Because the queue
retries automatically and the web app may also retry after a timeout, at-least-once delivery is
guaranteed but exactly-once *printing* must be enforced by deduplication on a caller-supplied key.

### 3.5 Integrated scanner as an asset (D-13)

Rugged handhelds in this class ship with a hardware barcode scanner. Two design opportunities follow,
both carried into the requirements:

- **Scan-to-pair** — the app displays a QR code containing the pairing token; the operator scans it
  with the device's own scanner instead of typing a token
- **Print verification** — after printing a label, the operator scans the printed barcode; the app
  compares it to what was sent and reports the result back to the web app, catching faded or
  misprinted labels at the moment of printing rather than weeks later

---

## 4. Deferred items

Explicitly out of scope for v1.0, recorded so they are not silently lost.

| Item | Reason | Revisit when |
| --- | --- | --- |
| USB OTG transport | No current need | A dock-mounted or fixed printer is introduced |
| Wi-Fi / TCP 9100 transport | Printers in scope have no Wi-Fi radio | A network printer enters the fleet |
| Multiple printers per handheld | One device covers both media types (D-08) | Label and receipt volumes justify separate hardware |
| Per-user authentication | Intranet-only, physically controlled devices (D-15) | An audit requirement names individual operators |
| iOS support | Fleet is Android-only | Never, under current hardware policy |
| Thai / complex script rendering | Confirmed unnecessary (D-09) | Content language changes |
| Public distribution / licensing | Internal use only | The organisation decides to productise |

---

## 5. Open questions

| ID | Question | Owner | Needed by |
| --- | --- | --- | --- |
| Q-01 | Which printer model is purchased? | Bearing Team | Before driver implementation begins (Phase 2 of the roadmap) |
| Q-02 | Exact Android version(s) on the existing handheld fleet | Bearing Team | Before the device test matrix is finalised |
| Q-03 | Is an MDM already deployed, and which one? | IT | Before the deployment guide is executed |
| Q-04 | Final label dimensions and media type (die-cut vs linerless) | Warehouse operations | Before templates are authored |

---

## 6. Related documents

- [Problem Statement](01-problem-statement.md)
- [Competitive Research](03-competitive-research.md)
- [Product Requirements](../02-requirements/01-prd.md)
