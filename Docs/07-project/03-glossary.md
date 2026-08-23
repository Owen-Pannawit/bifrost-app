# Glossary

| Field | Value |
| --- | --- |
| Document ID | PRJ-03 |
| Version | 1.0 |
| Date | 2026-08-22 |
| Status | Approved |

---

## Printing

**ESC/POS** — Epson Standard Code for Point of Sale. The de-facto command language for thermal
receipt printers, encoded as binary escape sequences. Sequential: content streams down a continuous
roll with no page model. Supported in v1.0.

**ZPL** — Zebra Programming Language. ASCII-based label language using absolute positioning on a
defined label canvas. Supported in v1.0.

**CPCL** — Comtec Printer Control Language, now Zebra's mobile label language. ASCII, absolute
positioning. The native language of the Zebra ZQ family and therefore the likely primary language for
this project. Supported in v1.0.

**TSPL** — TSC Printer Language. Used by TSC and many generic Asian label printers. Deferred to v1.1
(FR-606).

**EPL** — Eltron Programming Language. Zebra's older label language, superseded by ZPL. Not
supported.

**Direct thermal** — Printing by heating chemically treated paper. No ink or ribbon. Simple and
cheap, but output fades with heat, UV, and abrasion. All printers in scope are direct thermal.

**Thermal transfer** — Printing via a heated ribbon onto media. More durable than direct thermal, but
requires ribbon consumables. Relevant only if labels must survive long-term rack exposure.

**Die-cut label** — Individual labels on a backing liner, separated by gaps. Requires a **gap sensor**
so the printer knows where each label starts.

**Linerless label** — Adhesive labels with no backing liner. Eliminates liner waste and lengthens
media rolls, but needs a compatible platen and a different cutting approach.

**Gap sensor** — The optical sensor that detects the space between die-cut labels. Media loaded
incorrectly, or a dirty sensor, causes false "out of paper" reports.

**Black mark** — An alternative registration method: a printed black rectangle on the media reverse
that the printer detects instead of a gap.

**Continuous media** — Unbroken roll with no gaps or marks. Used for receipts and slips.

**Print width** — The **printable** width in dots, always narrower than the media width. 58 mm media
prints 48 mm (384 dots at 203 dpi); 80 mm prints 72 mm (576 dots); 4 in prints 104 mm (832 dots).
Confusing media width with print width is the most common cause of clipped labels.

**DPI** — Dots per inch. 203 dpi is standard for mobile printers; 300 dpi exists for small-text
applications.

**Module width** — The width in dots of the narrowest bar in a barcode. The single most important
factor in whether a barcode scans reliably. A value of 2 is typical; 3 is safer on marginal media.

**Dithering** — Converting a greyscale image to 1-bit black and white by distributing quantisation
error. Floyd–Steinberg is the usual algorithm. Needed because thermal printers have no greyscale.

**Raster** — Sending content as a bitmap rather than as text or barcode commands. Slower over
Bluetooth and larger in payload, but bypasses printer font limitations. Not used for text in this
project (FR-311) because content is English and numeric (D-09).

**CP874 / TIS-620** — Thai character encodings. Not required here, but noted because Thai on ESC/POS
printers is a well-known failure mode for this class of project in Thailand — inconsistent code page
numbering, colliding vowel marks, and missing fonts on low-cost hardware. Confirming that no Thai
content is required (D-09) removed an entire subsystem from scope.

---

## Bluetooth

**SPP** — Serial Port Profile. A Bluetooth Classic profile emulating a serial connection over
**RFCOMM**. What the majority of mobile printers expose, and — critically — **not accessible from
Web Bluetooth**, which is the core reason this project exists.

**RFCOMM** — The Bluetooth Classic transport underlying SPP. Provides stream semantics with built-in
flow control.

**BLE** — Bluetooth Low Energy. A separate protocol from Bluetooth Classic, not a faster version of
it. Lower power, but packet-oriented with small payloads.

**GATT** — Generic Attribute Profile. The BLE data model of services and characteristics. Web
Bluetooth exposes GATT only.

**Characteristic** — A GATT data endpoint. A printer typically exposes one for writing print data and
one for status notifications.

**MTU** — Maximum Transmission Unit. The largest BLE packet size, negotiated per connection. Default
23 bytes (20 usable); up to 512 where supported. Usable payload is always **MTU − 3** because of ATT
protocol overhead.

**Chunking** — Splitting a payload into MTU-sized pieces for BLE transmission. Doing this without
per-chunk acknowledgement is the standard cause of truncated labels — see
[R-02](02-risk-register.md).

**Flow control** — Waiting for confirmation that a chunk was received before sending the next.
Mandatory on BLE ([DES-06 §7.3](../03-design/06-printer-abstraction.md) rule 1). Handled
automatically by RFCOMM on Bluetooth Classic.

**Bonded device** — A Bluetooth device already paired at the Android OS level. BifrǫstApp lists only
bonded devices (FR-401); pairing itself is delegated to Android settings.

---

## Web platform

**Web Bluetooth** — Browser API for BLE/GATT access. Chromium only; no Safari, no Firefox. **Cannot
reach Bluetooth Classic SPP**, which is why it does not solve this problem.

**Loopback** — The `127.0.0.1` / `localhost` address, reachable only from the device itself. The
transport BifrǫstApp uses ([ADR-001](../03-design/02-adr/ADR-001-loopback-vs-cloud-relay.md)).

**Mixed content** — A browser blocking HTTP resources on an HTTPS page. **Loopback addresses are
exempt** in Chrome, which is what allows an HTTPS page to call `http://127.0.0.1:8437` without a
certificate on the device.

**Local Network Access (LNA)** — Chrome's permission prompt for requests from a public page to a
private network address. Applies to LAN addresses; a further reason the loopback topology was chosen
over a LAN service.

**CORS** — Cross-Origin Resource Sharing. The browser mechanism controlling which origins may read a
response. Bifrǫst emits permissive CORS headers only for allowlisted origins (FR-508).

**Origin** — Scheme + host + port. `http://a.local`, `https://a.local`, and `http://a.local:8080` are
three **different** origins. Exact matching is what makes the allowlist meaningful — and what makes
a changed web app URL break the fleet ([R-10](02-risk-register.md)).

**Keyboard wedge** — A barcode scanner that delivers scanned data as simulated keystrokes into the
focused input. How the handheld's integrated scanner feeds the pairing flow (FR-501).

---

## Architecture

**Bridge** — The pattern this project implements: a local agent connecting two systems that cannot
address each other directly. Named for **Bifrǫst**, the rainbow bridge of Norse mythology connecting
worlds that otherwise cannot touch.

**Loopback server** — An HTTP server bound to `127.0.0.1`, reachable only from the device. Not
reachable from the LAN (FR-504).

**Idempotency** — The property that repeating an operation has the same effect as performing it once.
Here: replaying an `Idempotency-Key` returns the original job and prints nothing further
(FR-102, NFR-202).

**Idempotency key** — A caller-supplied identifier, retained 24 hours, that makes retry safe. Modelled
on the Stripe API.

**At-least-once delivery** — A guarantee that a message arrives one or more times. What retry
provides. Combined with idempotency, it yields exactly-once *printing* — which is the guarantee that
actually matters.

**Intermediate representation (IR)** — `PrintDocument`, the internal form that all three payload tiers
compile to and that every driver consumes. Models **intent** — "CODE128 barcode, this value, 80 dots
high" — rather than coordinates, so sequential and absolute languages can each realise it in their own
idiom.

**Driver** — A `PrinterDriver` implementation. Converts a `PrintDocument` into one command language.
Never touches Bluetooth.

**Transport** — A `PrinterTransport` implementation. Moves bytes to the printer. Never interprets
command bytes.

**Single-consumer model** — Exactly one `PrintWorker` consumer `Task` per printer, making serialised
transmission structural rather than a locking discipline
([ADR-005](../03-design/02-adr/ADR-005-persistent-queue-room.md)).

**Foreground service** — An Android service with a persistent notification, kept alive by the system.
From Android 14 it must declare a type; Bifrǫst uses `connectedDevice` (FR-407).

**Managed configuration** — Android's mechanism for an MDM to set application settings centrally
(NFR-702).

**ADR** — Architecture Decision Record. A short document capturing a decision, the alternatives
considered, and the consequences. Seven exist in
[03-design/02-adr/](../03-design/02-adr/).

**ULID** — Universally Unique Lexicographically Sortable Identifier. Used for job IDs: sortable by
creation time, URL-safe, and readable aloud during phone support.

---

## .NET platform

**.NET for Android** — Microsoft's binding of the complete Android SDK to C#, producing a native
Android application. Distinct from .NET MAUI, which adds a cross-platform UI layer on top. Bifrǫst
uses .NET for Android **without** MAUI ([ADR-008](../03-design/02-adr/ADR-008-dotnet-for-android.md)).

**TFM (target framework moniker)** — The identifier naming what a project builds against.
`net10.0` is plain .NET with no platform APIs; `net10.0-android` adds the Android bindings. Bifrǫst
uses the difference as a **compile-time enforcement mechanism**: `Bifrost.Core` targets `net10.0`, so
`Android.*` types are not resolvable there at all ([IMP-02 §2.1](../04-implementation/02-project-structure.md)).

**netstandard2.0** — An older compatibility target that loads on essentially every .NET runtime,
including Android. Used as the compatibility check when adopting a dependency
([IMP-01 §7](../04-implementation/01-tech-stack.md)).

**ASP.NET Core / Kestrel** — Microsoft's web framework and HTTP server. **Does not run on Android** —
there is no `Microsoft.AspNetCore.App` runtime pack for `android-arm64`. This is why Bifrǫst uses a
third-party embedded server ([ADR-009](../03-design/02-adr/ADR-009-embedded-http-server.md)).

**EmbedIO** — A small `netstandard2.0` embedded HTTP and WebSocket server with a Xamarin.Android
track record. Bifrǫst's server, accessed only through `IBridgeServer`.

**`IBridgeServer`** — The in-house abstraction isolating every route, interceptor, and WebSocket
handler from the server library. Its purpose is that replacing EmbedIO costs one adapter, not an
audit ([ADR-009](../03-design/02-adr/ADR-009-embedded-http-server.md)).

**Dapper** — A minimal object mapper over ADO.NET. Used with Microsoft.Data.Sqlite instead of an
ORM, because the queue's queries are index-sensitive and hand-tuned.

**`System.Threading.Channels`** — The .NET producer/consumer primitive replacing Kotlin's `Channel`.
Drives the print worker and the WebSocket event hub.

**`IStateStream<T>`** — A small in-house type replacing Kotlin's `StateFlow`: a current value plus an
`IAsyncEnumerable<T>` of changes, built on Channels. Used for connection state and queue depth.

**Trimming / AOT** — Build steps that remove unused code and pre-compile to native. Both are required
in release builds to keep the APK within budget and start-up within NFR-105
([IMP-02 §6](../04-implementation/02-project-structure.md)).

**XHarness** — Microsoft's tool for running .NET test suites on physical devices and emulators. How
the instrumented tests execute.

**Banned-symbols analyzer** — A Roslyn rule that fails the build when a forbidden type is referenced.
Used to keep `EmbedIO` inside its adapter project.

---

## Project

**MDM** — Mobile Device Management. The platform used to deploy and configure the app across the
fleet.

**Rugged handheld** — A hardened Android device built for warehouse use, typically with an integrated
barcode scanner. Zebra, Honeywell, and Urovo are the vendors in scope (D-13). The integrated scanner
is exploited for both pairing (FR-501) and print verification (FR-405).

**Golden-output test** — A test asserting exact expected bytes for a given input. The primary defence
against command-language regressions, which are invisible in code review and produce subtly wrong
labels.

**Mock transport** — `MockTransport`, the substitute for a physical printer in automated tests. What
allows the system to be built before the printer is purchased (NFR-602, Q-01).

**Print verification loop** — Scanning a printed label with the handheld's own scanner to confirm it
is readable, then reporting the result back to the web app. The project's principal differentiator
(FR-405, v1.1).

**MoSCoW** — Prioritisation scheme: Must have, Should have, Could have, Won't have. Used throughout
[REQ-02](../02-requirements/02-srs.md).

**Transient error** — A failure that a retry could resolve: out of paper, disconnected, timeout.
Contrasted with **permanent** errors — malformed payload, content too wide — which are never retried
(FR-107).

---

## Requirement identifiers

| Prefix | Meaning | Document |
| --- | --- | --- |
| `D-nn` | Stakeholder decision | [DISC-02](../01-discovery/02-stakeholder-interview.md) |
| `Q-nn` | Open question | [DISC-02 §5](../01-discovery/02-stakeholder-interview.md) |
| `G-n` / `NG-n` | Goal / non-goal | [REQ-01](../02-requirements/01-prd.md) |
| `M-n` | Success metric | [REQ-01 §5](../02-requirements/01-prd.md) |
| `FR-nnn` | Functional requirement | [REQ-02](../02-requirements/02-srs.md) |
| `NFR-nnn` | Non-functional requirement | [REQ-02](../02-requirements/02-srs.md) |
| `US-nnn` | User story | [REQ-03](../02-requirements/03-user-stories.md) |
| `ADR-nnn` | Architecture decision | [03-design/02-adr/](../03-design/02-adr/) |
| `TC-nnn` | Test case | [TST-02](../05-testing/02-test-cases.md) |
| `F-nn` | Field test scenario | [TST-01 §6](../05-testing/01-test-strategy.md) |
| `T-nn` | Threat | [DES-08 §3](../03-design/08-security-design.md) |
| `R-nn` | Risk | [PRJ-02](02-risk-register.md) |

---

## Related documents

- [Documentation index](../README.md)
- [Problem Statement](../01-discovery/01-problem-statement.md)
- [Printer Abstraction](../03-design/06-printer-abstraction.md)
