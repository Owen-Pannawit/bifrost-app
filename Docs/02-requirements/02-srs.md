# Software Requirements Specification

| Field | Value |
| --- | --- |
| Document ID | REQ-02 |
| Version | 1.0 |
| Date | 2026-08-22 |
| Status | Approved |
| Product | BifrǫstApp v1.0 |

---

## 1. Scope and conventions

This document defines the functional and non-functional requirements for BifrǫstApp and the Bifrǫst
SDK. It is the authoritative source for what the system must do; design documents describe *how*.

**Identifier scheme**

| Range | Area |
| --- | --- |
| FR-1xx | Print job submission and queue |
| FR-2xx | Status, capabilities, events |
| FR-3xx | Payload tiers and rendering |
| FR-4xx | Android app UI and operator features |
| FR-5xx | Security and pairing |
| FR-6xx | Printer connectivity and transport |
| FR-7xx | JavaScript SDK |
| NFR-1xx … NFR-7xx | Performance, reliability, security, compatibility, usability, maintainability, operability |

**Priority (MoSCoW)** — `M` Must have (v1.0) · `S` Should have (v1.0 if time allows) ·
`C` Could have (v1.1) · `W` Won't have this release

Every requirement is verified by at least one test case in
[Test Cases](../05-testing/02-test-cases.md).

---

## 2. Functional requirements

### 2.1 Print job submission and queue — FR-1xx

| ID | Requirement | Priority |
| --- | --- | --- |
| **FR-101** | The app SHALL expose `POST /v1/print` accepting a JSON print payload and SHALL return a `jobId` and initial job state | M |
| **FR-102** | The app SHALL accept an `Idempotency-Key` header. A repeat of a key already seen within the dedup window SHALL return the original job's status **without printing again** | M |
| **FR-103** | The app SHALL persist every accepted job to local storage before responding, so that no accepted job is lost on app kill, crash, or device reboot | M |
| **FR-104** | The app SHOULD accept a print request via the deep link `bifrost://print?job=<base64url>` as a fallback when loopback HTTP is unavailable | C |
| **FR-105** | The app SHALL process queued jobs in submission order (FIFO) for a given printer | M |
| **FR-106** | The app SHALL retry a failed job automatically using bounded exponential backoff, up to a configured maximum attempt count | M |
| **FR-107** | The app SHALL NOT retry jobs that failed for non-transient reasons (malformed payload, unsupported element, payload too large) | M |
| **FR-108** | The app SHALL expose `POST /v1/jobs/{id}/cancel` to cancel a job that has not yet begun transmitting | S |
| **FR-109** | The app SHALL enforce a maximum pending-queue depth and SHALL reject further submissions with a distinct error when full | M |
| **FR-110** | The app SHALL retain completed job records for a configured retention period and SHALL prune older records automatically | S |

### 2.2 Status, capabilities, events — FR-2xx

| ID | Requirement | Priority |
| --- | --- | --- |
| **FR-201** | The app SHALL expose `GET /v1/capabilities` returning the connected printer's print width in dots, DPI, command language, supported barcode symbologies, cutter presence, and media type | M |
| **FR-202** | The app SHOULD expose `POST /v1/preview` accepting the same payload as `/v1/print` and returning a rendered PNG instead of printing | C |
| **FR-203** | The app SHALL expose `WS /v1/events` streaming printer state changes and job state transitions to connected clients | M |
| **FR-204** | The app SHALL expose `GET /v1/status` returning bridge health, printer connection state, and current queue depth — callable **without** a pairing token so a web app can detect the bridge before pairing | M |
| **FR-205** | The app SHALL expose `GET /v1/jobs/{id}` returning the current state, attempt count, and last error of a job | M |
| **FR-206** | The app SHALL report printer error conditions distinctly where the hardware exposes them: out of paper, cover open, battery low, overheated, disconnected | M |
| **FR-207** | The app SHALL expose `GET /v1/jobs` returning a paginated job history | S |

### 2.3 Payload tiers and rendering — FR-3xx

| ID | Requirement | Priority |
| --- | --- | --- |
| **FR-301** | The app SHALL support **Tier 1 (Template)** payloads: a template name plus a data object, with layout resolved on the device | M |
| **FR-302** | The app SHALL store templates on the device with a name and version, and SHALL expose `GET /v1/templates` to list them | M |
| **FR-303** | The app SHALL support **Tier 2 (Layout DSL)** payloads: an ordered array of elements | M |
| **FR-304** | The DSL SHALL support element types `text`, `barcode`, `qr`, `line`, `feed`, and `cut` | M |
| **FR-305** | The DSL SHOULD support the `image` element with monochrome dithering | S |
| **FR-306** | The app SHALL support **Tier 3 (Raw)** payloads: base64-encoded printer command bytes passed through unmodified | M |
| **FR-307** | All three tiers SHALL compile to a single internal intermediate representation before reaching a driver | M |
| **FR-308** | The app SHALL validate every payload against its schema and SHALL reject invalid payloads with a field-level error message | M |
| **FR-309** | The app SHALL support barcode symbologies CODE128, CODE39, EAN13, ITF, and QR Code | M |
| **FR-310** | The app SHALL reject a payload whose rendered width exceeds the connected printer's print width | M |
| **FR-311** | Text rendering SHALL use native printer font commands. Bitmap text rendering is NOT required | M |

### 2.4 Android app UI and operator features — FR-4xx

| ID | Requirement | Priority |
| --- | --- | --- |
| **FR-401** | The app SHALL provide a printer setup screen listing paired Bluetooth devices and allowing one to be selected as the active printer | M |
| **FR-402** | The app SHALL provide a test-print action that prints a self-check page showing printer identity and capabilities | M |
| **FR-403** | The app SHALL display the live job queue with each job's state and error, and SHALL allow a pending job to be cancelled | M |
| **FR-404** | The app SHALL display job history and SHALL allow any historical job to be reprinted from the app | M |
| **FR-405** | The app SHOULD support a print verification loop: after printing, the operator scans the printed barcode, the app compares it with what was sent, and the result is reported over `WS /v1/events` | C |
| **FR-406** | The app SHALL export a diagnostics bundle containing app and Android version, printer identity and capabilities, permission states, recent job history, and the error log | M |
| **FR-407** | The app SHALL run a foreground service of type `connectedDevice` with a persistent notification showing printer connection state | M |
| **FR-408** | The app SHALL restart its server and reconnect to the configured printer automatically after device reboot | M |
| **FR-409** | The app SHALL guide the operator through granting Bluetooth permissions and disabling battery optimisation on first run | M |
| **FR-410** | The app SHALL provide a settings screen for port, retention, retry limits, origin allowlist, and token management | M |

### 2.5 Security and pairing — FR-5xx

| ID | Requirement | Priority |
| --- | --- | --- |
| **FR-501** | The app SHALL display a pairing QR code containing the pairing token, scannable by the handheld's integrated barcode scanner | M |
| **FR-502** | The app SHALL require a valid bearer token on all endpoints except `GET /v1/status` and `POST /v1/pair` | M |
| **FR-503** | The app SHALL enforce an origin allowlist and SHALL reject requests whose `Origin` header is not allowlisted | M |
| **FR-504** | The app SHALL bind its listening socket to the loopback interface **only**, never to `0.0.0.0` | M |
| **FR-505** | The app SHALL allow the pairing token to be regenerated, which immediately invalidates the previous token | M |
| **FR-506** | A displayed pairing QR code SHALL expire after a short validity window and SHALL be single-use | M |
| **FR-507** | The app SHALL store the pairing token in Android EncryptedSharedPreferences or the Keystore, never in plaintext | M |
| **FR-508** | The app SHALL respond to CORS preflight requests with headers permitting only allowlisted origins | M |
| **FR-509** | The app SHALL log every authentication failure with timestamp and origin for diagnostics | S |

### 2.6 Printer connectivity and transport — FR-6xx

| ID | Requirement | Priority |
| --- | --- | --- |
| **FR-601** | The app SHALL connect to printers over **Bluetooth Classic (SPP)** using RFCOMM | M |
| **FR-602** | The app SHALL connect to printers over **Bluetooth LE (GATT)**, negotiating MTU and chunking writes to fit the negotiated size | M |
| **FR-603** | The app SHALL detect connection loss and SHALL attempt reconnection automatically with bounded backoff | M |
| **FR-604** | The app SHALL apply flow control on BLE writes, awaiting write confirmation before sending the next chunk | M |
| **FR-605** | The app SHALL support ESC/POS, ZPL, and CPCL command languages through a common driver interface | M |
| **FR-606** | The app SHOULD support TSPL | C |
| **FR-607** | The app SHALL allow the printer's command language to be auto-detected where the printer supports a query, and manually overridden where it does not | M |
| **FR-608** | The app SHALL read printer status where the command language supports a status query, and SHALL degrade gracefully to write-only operation where it does not | M |
| **FR-609** | The app SHALL apply a transmit timeout per job and SHALL treat expiry as a transient failure eligible for retry | M |
| **FR-610** | Adding support for a new printer language SHALL require implementing the driver interface only, with no change to the API, queue, or rendering layers | M |

### 2.7 JavaScript SDK — FR-7xx

| ID | Requirement | Priority |
| --- | --- | --- |
| **FR-701** | The SDK SHALL provide a promise-based `print()` accepting all three payload tiers | M |
| **FR-702** | The SDK SHALL ship complete TypeScript type definitions | M |
| **FR-703** | The SDK SHALL have **zero runtime dependencies** | M |
| **FR-704** | The SDK SHALL be distributed as both ESM and UMD bundles, usable via a bundler or a plain `<script>` tag | M |
| **FR-705** | The SDK SHALL generate an idempotency key automatically when the caller does not supply one | M |
| **FR-706** | The SDK SHALL expose a typed error union distinguishing bridge-unavailable, unauthorised, printer-error, and validation-error cases | M |
| **FR-707** | The SDK SHALL expose an event subscription API wrapping the WebSocket stream, with automatic reconnection | M |
| **FR-708** | The SDK SHALL provide `isAvailable()` to detect the bridge without throwing, so a page can degrade gracefully | M |
| **FR-709** | The SDK SHALL persist the pairing token in `localStorage` under a namespaced key | M |
| **FR-710** | The SDK SHALL work identically when the page is served over HTTP and over HTTPS | M |

---

## 3. Non-functional requirements

### 3.1 Performance — NFR-1xx

| ID | Requirement | Target | Priority |
| --- | --- | --- | --- |
| **NFR-101** | Submit-to-paper latency for a standard label, printer already connected | p95 ≤ 3 s | M |
| **NFR-102** | `POST /v1/print` acknowledgement latency (enqueue and respond) | p95 ≤ 150 ms | M |
| **NFR-103** | `GET /v1/status` response latency | p95 ≤ 50 ms | M |
| **NFR-104** | Printer reconnection time after the printer is powered back on | ≤ 10 s | M |
| **NFR-105** | App cold start to server listening | ≤ 3 s | S |
| **NFR-106** | Idle battery consumption attributable to the app | ≤ 3% per 8-hour shift | S |
| **NFR-107** | Maximum accepted request body size | 2 MB | M |

### 3.2 Reliability — NFR-2xx

| ID | Requirement | Priority |
| --- | --- | --- |
| **NFR-201** | No accepted job SHALL be lost due to app kill, crash, or device reboot | M |
| **NFR-202** | No job SHALL be printed more than once, under any combination of client retry, queue retry, and app restart | M |
| **NFR-203** | Print success rate SHALL be ≥ 99% of submitted jobs under normal warehouse conditions | M |
| **NFR-204** | The app SHALL remain operational with no internet connection at any time | M |
| **NFR-205** | A malformed or hostile payload SHALL NOT crash the app or leave the queue in an unrecoverable state | M |
| **NFR-206** | The queue SHALL resume automatically when a printer returns after being unavailable, with no operator action | M |

### 3.3 Security — NFR-3xx

| ID | Requirement | Priority |
| --- | --- | --- |
| **NFR-301** | The listening socket SHALL be reachable only from the device itself | M |
| **NFR-302** | Every state-changing endpoint SHALL require authentication | M |
| **NFR-303** | The pairing token SHALL be at least 256 bits of cryptographically secure randomness | M |
| **NFR-304** | Tokens SHALL never be written to application logs or the diagnostics bundle | M |
| **NFR-305** | The app SHALL request only the Android permissions it needs, with runtime rationale shown to the operator | M |
| **NFR-306** | Print payload content SHALL NOT leave the device except to the printer | M |

### 3.4 Compatibility — NFR-4xx

| ID | Requirement | Priority |
| --- | --- | --- |
| **NFR-401** | The app SHALL support Android 10 (API 29) through Android 15 (API 35) | M |
| **NFR-402** | The app SHALL handle both the legacy (≤ API 30) and runtime (≥ API 31) Bluetooth permission models | M |
| **NFR-403** | The SDK SHALL support Chrome/Chromium 90+ on Android | M |
| **NFR-404** | The system SHALL be vendor-neutral: no requirement shall depend on a specific printer manufacturer's SDK | M |
| **NFR-405** | The SDK SHALL function from both HTTP and HTTPS page origins | M |

### 3.5 Usability — NFR-5xx

| ID | Requirement | Priority |
| --- | --- | --- |
| **NFR-501** | Every operator-visible error SHALL state what happened and what to do next, in plain English, without error codes as the primary message | M |
| **NFR-502** | Printer connection state SHALL be visible without opening the app | M |
| **NFR-503** | First-time setup SHALL be completable by an operator in under 3 minutes with a one-page instruction sheet | M |
| **NFR-504** | Touch targets SHALL be usable with gloves — minimum 48 dp | M |
| **NFR-505** | The UI SHALL be legible in warehouse lighting: high contrast, minimum 14 sp body text | M |

### 3.6 Maintainability — NFR-6xx

| ID | Requirement | Priority |
| --- | --- | --- |
| **NFR-601** | Core logic — queue, rendering, drivers — SHALL be unit-testable without an Android device | M |
| **NFR-602** | A mock printer harness SHALL allow end-to-end testing with no physical printer | M |
| **NFR-603** | The API contract SHALL be versioned in the URL path; a breaking change requires a new version | M |
| **NFR-604** | Line coverage of the core modules SHALL be ≥ 70% | S |
| **NFR-605** | Every architectural decision SHALL be recorded as an ADR | M |

### 3.7 Operability — NFR-7xx

| ID | Requirement | Priority |
| --- | --- | --- |
| **NFR-701** | The app SHALL be installable and updatable via MDM without operator interaction | M |
| **NFR-702** | Configuration SHALL be settable via MDM managed configuration as well as in-app | S |
| **NFR-703** | Logs SHALL be retained on-device with bounded size and rotation | M |
| **NFR-704** | The diagnostics bundle SHALL be exportable by an operator in one action and shareable by any available means | M |
| **NFR-705** | The app version SHALL be visible in the UI and included in every diagnostics bundle | M |

---

## 4. System-wide constants

Canonical values. Any document, schema, or implementation stating one of these values must match
this table.

| Constant | Value | Requirement |
| --- | --- | --- |
| Listening address | `127.0.0.1` (loopback only) | FR-504 |
| Default port | `8437` | FR-101 |
| API version prefix | `/v1` | NFR-603 |
| Android package ID | `com.bearing.bifrost` | — |
| SDK package name | `@bearing/bifrost-sdk` | FR-704 |
| Min SDK / Target SDK | 29 / 36 | NFR-401 |
| Pairing token length | 32 bytes (256 bits), base64url-encoded | NFR-303 |
| Pairing QR validity | 5 minutes, single use | FR-506 |
| Idempotency dedup window | 24 hours | FR-102 |
| Max retry attempts | 5 | FR-106 |
| Retry backoff sequence | 2 s, 8 s, 30 s, 120 s, 300 s | FR-106 |
| Max pending queue depth | 500 jobs | FR-109 |
| Job history retention | 30 days or 1000 jobs, whichever comes first | FR-110 |
| Max request body size | 2 MB | NFR-107 |
| Per-job transmit timeout | 30 s | FR-609 |
| BLE MTU | negotiate up to 512 bytes; fall back to 23 | FR-602 |
| Log retention | 7 days, max 10 MB, rotated | NFR-703 |

---

## 5. Traceability

| Source | Requirements |
| --- | --- |
| D-01 same-device topology | FR-504, NFR-301, NG-2 |
| D-03 BT Classic + BLE | FR-601, FR-602, FR-604 |
| D-05 app + SDK deliverable | FR-701 … FR-710 |
| D-07 printer not selected | FR-605, FR-607, FR-610, NFR-404 |
| D-09 no Thai | FR-311 |
| D-10 HTTP and HTTPS | FR-710, NFR-405 |
| D-12 queue, retry, idempotency | FR-102, FR-103, FR-106, NFR-201, NFR-202 |
| D-13 rugged handheld with scanner | FR-405, FR-501, NFR-401, NFR-402 |
| D-14 three payload tiers | FR-301 … FR-307 |
| D-15 origin allowlist + token | FR-502, FR-503, FR-505, NFR-302 |
| Idea 6.1 scan-to-pair | FR-501, FR-506 |
| Idea 6.2 print verification | FR-405 |
| Idea 6.3 capability negotiation | FR-201 |
| Idea 6.4 preview | FR-202 |
| Idea 6.5 device-side templates | FR-301, FR-302 |
| Idea 6.6 idempotency keys | FR-102 |
| Idea 6.7 health push | FR-203, FR-206 |
| Idea 6.8 local reprint | FR-404 |
| Idea 6.9 URL scheme fallback | FR-104 |
| Idea 6.10 diagnostics export | FR-406, NFR-704 |

---

## 6. Related documents

- [Product Requirements](01-prd.md)
- [User Stories](03-user-stories.md)
- [Local API Specification](../03-design/03-local-api-spec.md)
- [Test Cases](../05-testing/02-test-cases.md)
