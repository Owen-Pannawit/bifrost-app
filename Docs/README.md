# BifrǫstApp — Documentation

**A local print bridge that lets any web application print to a Bluetooth mobile printer with one
JavaScript call — no cloud, no vendor lock-in, no duplicate labels.**

| | |
| --- | --- |
| Version | 2.0 |
| Date | 2026-08-22 |
| Status | Design complete — ready for implementation |
| Platform | .NET for Android (C#) + TypeScript SDK |
| Owner | Bearing Team |

> **Version 2.0** — the platform moved from Kotlin to **.NET for Android**
> ([ADR-008](03-design/02-adr/ADR-008-dotnet-for-android.md)), because the organisation's development
> competence is .NET and, under a single-developer constraint, a codebase the organisation can read
> outweighs the cleanest platform bindings.
>
> **No architecture changed.** Same topology, containers, components, contracts, and guarantees —
> only the libraries filling them. ADR-002 and ADR-004 are retained as superseded, so the reasoning
> trail stays intact.

---

## The problem in one paragraph

Warehouse staff work in a web application on rugged Android handhelds and need to print part labels
on a Bluetooth printer worn on the belt. **The browser cannot reach that printer.** Web Bluetooth
speaks only BLE/GATT, while most mobile printers expose Bluetooth Classic SPP; `window.print()`
targets the A4-oriented Android Print Framework, not a 104 mm label. Today an operator walks to a
fixed print station, re-enters the part number, prints, and walks back.

**Bifrǫst** — named for the rainbow bridge of Norse mythology — is an Android app that runs a local
server on `127.0.0.1`, receives print jobs from the browser on the same device, and drives the
printer over Bluetooth. It ships with a JavaScript SDK the web app imports.

---

## Start here

| If you are… | Read |
| --- | --- |
| **New to the project** | [Problem Statement](01-discovery/01-problem-statement.md) → [PRD](02-requirements/01-prd.md) → [Architecture](03-design/01-architecture.md) |
| **Implementing the Android app** | [Architecture](03-design/01-architecture.md) → [Tech Stack](04-implementation/01-tech-stack.md) → [Project Structure](04-implementation/02-project-structure.md) → the 7 [ADRs](03-design/02-adr/) |
| **Integrating a web app** | [SDK Specification](03-design/04-js-sdk-spec.md) → [Payload Schema](03-design/05-print-payload-schema.md) → [Deployment §7](06-operations/01-deployment-guide.md) |
| **Writing a printer driver** | [Printer Abstraction](03-design/06-printer-abstraction.md) → [ADR-007](03-design/02-adr/ADR-007-printer-language-abstraction.md) |
| **Deploying to the fleet** | [Deployment Guide](06-operations/01-deployment-guide.md) → [Runbook](06-operations/02-runbook.md) |
| **Supporting operators** | [Runbook](06-operations/02-runbook.md) |
| **Buying the printer** | [Hardware Recommendation](06-operations/03-hardware-recommendation.md) |
| **Lost in an acronym** | [Glossary](07-project/03-glossary.md) |

---

## Full index

### 01 · Discovery

| Doc | Title | Contents |
| --- | --- | --- |
| DISC-01 | [Problem Statement](01-discovery/01-problem-statement.md) | Why the browser cannot reach the printer; why the obvious workarounds fail |
| DISC-02 | [Stakeholder Interview](01-discovery/02-stakeholder-interview.md) | 17 frozen decisions (`D-01` … `D-17`), deferred items, open questions |
| DISC-03 | [Competitive Research](01-discovery/03-competitive-research.md) | QZ Tray, PrintNode, CloudPRNT, ePOS surveyed; gap analysis; **10 solution ideas** |

### 02 · Requirements

| Doc | Title | Contents |
| --- | --- | --- |
| REQ-01 | [Product Requirements](02-requirements/01-prd.md) | Vision, personas, goals, success metrics, MVP scope |
| REQ-02 | [Software Requirements Specification](02-requirements/02-srs.md) | 67 functional + 39 non-functional requirements; **system-wide constants** |
| REQ-03 | [User Stories](02-requirements/03-user-stories.md) | 23 stories with Given/When/Then acceptance criteria (22 in v1.0) |

### 03 · Design

| Doc | Title | Contents |
| --- | --- | --- |
| DES-01 | [Architecture](03-design/01-architecture.md) | C4 levels 1–3, runtime views, data architecture, deployment |
| — | [ADR-001](03-design/02-adr/ADR-001-loopback-vs-cloud-relay.md) | Loopback server, not cloud relay or LAN service |
| — | [ADR-002](03-design/02-adr/ADR-002-kotlin-native-vs-flutter.md) | ~~Native Kotlin, not Flutter or KMP~~ — superseded by ADR-008 |
| — | [ADR-003](03-design/02-adr/ADR-003-three-tier-payload-api.md) | Three payload tiers over one intermediate representation |
| — | [ADR-004](03-design/02-adr/ADR-004-ktor-embedded-server.md) | ~~Ktor as the embedded server~~ — superseded by ADR-009 |
| — | [ADR-005](03-design/02-adr/ADR-005-persistent-queue-room.md) | Database-backed durable queue, single consumer |
| — | [ADR-006](03-design/02-adr/ADR-006-origin-allowlist-token-auth.md) | Origin allowlist + token, paired by QR |
| — | [ADR-007](03-design/02-adr/ADR-007-printer-language-abstraction.md) | Driver and transport abstractions before implementations |
| — | [**ADR-008**](03-design/02-adr/ADR-008-dotnet-for-android.md) | **.NET for Android** (no MAUI), not native Kotlin — supersedes ADR-002 |
| — | [**ADR-009**](03-design/02-adr/ADR-009-embedded-http-server.md) | **EmbedIO** behind an abstraction — ASP.NET Core does not run on Android — supersedes ADR-004 |
| DES-03 | [Local API Specification](03-design/03-local-api-spec.md) | 10 endpoints, error model, idempotency contract, OpenAPI |
| DES-04 | [JavaScript SDK Specification](03-design/04-js-sdk-spec.md) | Public API, result/error model, framework integration |
| DES-05 | [Print Payload Schema](03-design/05-print-payload-schema.md) | All three tiers, the IR, JSON Schemas, validation order |
| DES-06 | [Printer Abstraction](03-design/06-printer-abstraction.md) | Driver + transport interfaces, **BLE chunking rules**, mock harness |
| DES-07 | [Job Lifecycle](03-design/07-job-lifecycle.md) | State machine, retry policy, idempotency, data model |
| DES-08 | [Security Design](03-design/08-security-design.md) | Trust boundaries, STRIDE model, auth, Android hardening |
| DES-09 | [UI/UX Specification](03-design/09-ui-ux-spec.md) | Screens, state language, error catalogue, accessibility |

### 04 · Implementation

| Doc | Title | Contents |
| --- | --- | --- |
| IMP-01 | [Technology Stack](04-implementation/01-tech-stack.md) | Every choice with its rationale, and what was rejected |
| IMP-02 | [Project Structure](04-implementation/02-project-structure.md) | Module graph, packages, the dependency rule |
| IMP-03 | [Coding Standards](04-implementation/03-coding-standards.md) | Error handling, asynchrony, logging, definition of done |

### 05 · Testing

| Doc | Title | Contents |
| --- | --- | --- |
| TST-01 | [Test Strategy](05-testing/01-test-strategy.md) | Pyramid, mock harness, device matrix, **15 field scenarios** |
| TST-02 | [Test Cases](05-testing/02-test-cases.md) | 147 cases, every requirement traced |

### 06 · Operations

| Doc | Title | Contents |
| --- | --- | --- |
| OPS-01 | [Deployment Guide](06-operations/01-deployment-guide.md) | Build, sign, MDM rollout, staged deployment, integration |
| OPS-02 | [Runbook](06-operations/02-runbook.md) | Phone-ready triage for every failure mode |
| OPS-03 | [Hardware Recommendation](06-operations/03-hardware-recommendation.md) | Printer comparison and a specific recommendation |

### 07 · Project

| Doc | Title | Contents |
| --- | --- | --- |
| PRJ-01 | [Roadmap](07-project/01-roadmap.md) | 8 phases, ~18 weeks, milestones, critical path |
| PRJ-02 | [Risk Register](07-project/02-risk-register.md) | 18 risks scored, with mitigations and closure criteria |
| PRJ-03 | [Glossary](07-project/03-glossary.md) | Printing, Bluetooth, web platform, and project terminology |

---

## Key decisions at a glance

| Decision | Choice | Why |
| --- | --- | --- |
| **Topology** | Loopback `127.0.0.1:8437` | Browser and printer share a device. No relay, no discovery, works offline |
| **Platform** | **.NET for Android** (C#, no MAUI) | The organisation's language, and it binds the full Android SDK. MAUI's cross-platform UI buys nothing for six screens |
| **HTTP server** | EmbedIO behind `IBridgeServer` | ASP.NET Core has no Android runtime pack; the abstraction keeps that dependency swappable |
| **Payload API** | Three tiers → one IR | Template for the 90% case; DSL for dynamic layouts; raw as an escape hatch |
| **Queue** | SQLite + single consumer | Survives crash and reboot; serialised transmission is structural |
| **Duplicate prevention** | `Idempotency-Key`, 24 h window | A duplicate label is a data-integrity fault, not a cosmetic one |
| **Security** | Origin allowlist + bearer token | Any local process can reach a loopback port |
| **Pairing** | Scan a QR with the device's own scanner | The handheld already has a scanner. No 43-character secret to type |
| **Printers** | Driver + transport abstractions first | The printer has not been purchased yet |

---

## What makes this different

Nothing on the market is a local, vendor-neutral, programmable print bridge for Android. QZ Tray has
the right architecture but no Android build. The Android print-service plugins have the platform but
no API. The cloud relays have an API but need internet and charge per device.

Three capabilities appear in **no** existing solution and are designed in here:

1. **A template layer** — the web app sends `{ partNo, lot, qty }`; layout lives on the device and
   can be revised without a web deployment
2. **Guaranteed single printing under retry** — idempotency is designed into the API and the queue,
   not bolted onto transport retry
3. **Use of the handheld's own scanner** — to pair without typing, and to verify that a printed label
   is actually readable before it reaches a shelf

---

## Conventions

- **Requirement IDs** — every requirement, story, test, threat, and risk carries an ID. See
  [Glossary — identifiers](07-project/03-glossary.md#requirement-identifiers)
- **Traceability** — decision → requirement → story → test case. CI fails if a `Must have`
  requirement has no test
- **Canonical constants** — port, retry counts, timeouts, and limits are defined once in
  [REQ-02 §4](02-requirements/02-srs.md). Every other document matches that table
- **Diagrams** — Mermaid, rendered natively by GitHub and VS Code
- **Language** — documentation in English; requirements use RFC 2119 SHALL / SHOULD / MAY

---

## Open questions

| ID | Question | Blocks | Owner |
| --- | --- | --- | --- |
| Q-01 | Which printer model is purchased? | Roadmap Phase 5 | Bearing Team |
| Q-02 | Android versions across the existing fleet | Test matrix | Bearing Team |
| Q-03 | Which MDM is deployed? | Rollout | IT |
| Q-04 | Final label dimensions and media type | Template authoring | Warehouse operations |

Only **Q-01** is on the critical path, and even that can be absorbed by reordering phases — the whole
system is buildable and testable against `MockTransport` before a printer exists.

---

## Status

| Phase | Status |
| --- | --- |
| Discovery | ✅ Complete |
| Requirements | ✅ Complete |
| Design | ✅ Complete |
| Implementation | ⬜ Not started |
| Testing | ⬜ Not started |
| Deployment | ⬜ Not started |
