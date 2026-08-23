# User Stories & Acceptance Criteria

| Field | Value |
| --- | --- |
| Document ID | REQ-03 |
| Version | 1.0 |
| Date | 2026-08-22 |
| Status | Approved |

---

## 1. Conventions

Stories are grouped by epic. Each carries an ID (`US-nnn`), the requirements it satisfies, and
acceptance criteria in Given/When/Then form. Story points use a Fibonacci scale relative to a
1-point baseline of "add a settings toggle".

**Personas** — *Operator* (warehouse staff), *Developer* (web app integrator), *IT* (fleet support).
See [PRD §3](01-prd.md).

---

## EPIC 1 — Bridge Foundation

### US-101 — Local server accepts print jobs

> **As a** Developer, **I want** to POST a print job to a local endpoint, **so that** my web page can
> print without knowing anything about Bluetooth.

**Requirements:** FR-101, FR-103, FR-504 · **Points:** 5

- **Given** BifrǫstApp is running and paired, **when** I `POST /v1/print` with a valid payload and a
  valid token, **then** I receive `202 Accepted` with a `jobId` and state `QUEUED`
- **Given** a job has been accepted, **when** the app process is killed immediately afterwards,
  **then** the job is still present in the queue after restart and is printed
- **Given** the app is running, **when** another device on the LAN requests
  `http://<handheld-ip>:8437/v1/status`, **then** the connection is refused — the socket binds to
  loopback only
- **Given** a page served over HTTPS, **when** it calls `http://127.0.0.1:8437`, **then** the request
  is not blocked as mixed content

---

### US-102 — Job survives every interruption

> **As an** Operator, **I want** my print job to complete even if something goes wrong mid-way,
> **so that** I never have to re-enter data.

**Requirements:** FR-103, FR-106, NFR-201, NFR-206 · **Points:** 8

- **Given** a job is queued, **when** the printer is switched off, **then** the job stays queued and
  retries when the printer returns — with no operator action
- **Given** a job fails transiently, **when** it is retried, **then** the delays follow 2 s, 8 s,
  30 s, 120 s, 300 s and stop after 5 attempts
- **Given** a job failed with a malformed payload, **when** the retry scheduler runs, **then** the
  job is **not** retried and its state is terminal
- **Given** jobs are queued, **when** the device reboots, **then** the app restarts, reconnects, and
  drains the queue automatically

---

### US-103 — The same label never prints twice

> **As an** Operator, **I want** to be certain a label prints exactly once, **so that** two bins
> never carry the same identity.

**Requirements:** FR-102, NFR-202 · **Points:** 5

- **Given** a job submitted with `Idempotency-Key: abc`, **when** the same key is submitted again
  within 24 hours, **then** the original job's status is returned and **nothing prints**
- **Given** a job that printed successfully, **when** the identical key is replayed, **then** the
  response reports `PRINTED` with the original `jobId`
- **Given** a request times out client-side after the app already accepted it, **when** the SDK
  retries with the same key, **then** exactly one label is produced
- **Given** a key was first seen more than 24 hours ago, **when** it is submitted again, **then** it
  is treated as a new job

---

### US-104 — Bridge presence is detectable

> **As a** Developer, **I want** to check whether Bifrǫst is available before showing a Print button,
> **so that** the page degrades gracefully.

**Requirements:** FR-204, FR-708 · **Points:** 2

- **Given** the app is not installed, **when** I call `isAvailable()`, **then** it resolves `false`
  without throwing
- **Given** the app is running but not yet paired, **when** I call `GET /v1/status` with no token,
  **then** I receive `200` with `paired: false`
- **Given** the app is running and paired, **when** I call `isAvailable()`, **then** it resolves
  `true` within 500 ms

---

## EPIC 2 — Printer Connectivity

### US-201 — Connect to a Bluetooth Classic printer

> **As an** Operator, **I want** to select my printer from a list, **so that** printing works without
> IT involvement.

**Requirements:** FR-401, FR-601, FR-603 · **Points:** 8

- **Given** a printer is paired in Android Bluetooth settings, **when** I open the printer setup
  screen, **then** it appears in the list
- **Given** I select a printer, **when** the connection succeeds, **then** the app shows *Connected*
  and the persistent notification updates
- **Given** the printer moves out of range, **when** the connection drops, **then** the app
  reconnects automatically within 10 s of the printer returning
- **Given** the printer is off, **when** I select it, **then** I see *"Printer not responding — check
  it is switched on and in range"*, not a stack trace

---

### US-202 — Connect to a BLE printer without corrupt output

> **As an** Operator, **I want** BLE printers to produce the same output as Classic ones, **so that**
> hardware choice does not change behaviour.

**Requirements:** FR-602, FR-604 · **Points:** 8

- **Given** a BLE printer, **when** the app connects, **then** it negotiates MTU up to 512 bytes and
  falls back to 23 if negotiation fails
- **Given** a payload larger than the MTU, **when** it is transmitted, **then** it is split into
  chunks and each chunk awaits write confirmation before the next is sent
- **Given** a 40 KB payload over BLE, **when** it prints, **then** the output is byte-identical to
  the same payload printed over SPP
- **Given** a write confirmation never arrives, **when** the transmit timeout of 30 s expires,
  **then** the job fails transiently and is eligible for retry

---

### US-203 — Printer language is not hard-coded

> **As** IT, **I want** to add a new printer model without a code change to the API or queue,
> **so that** hardware choice stays open.

**Requirements:** FR-605, FR-607, FR-610, NFR-404 · **Points:** 8

- **Given** a printer that supports a language query, **when** it connects, **then** the app detects
  ESC/POS, ZPL, or CPCL automatically
- **Given** a printer that does not, **when** I set the language manually in settings, **then** it is
  used and persisted
- **Given** a new driver implementing the driver interface, **when** it is registered, **then** it
  works end-to-end with no change to the API, queue, or rendering layers
- **Given** any supported language, **when** the same Tier 1 payload is printed, **then** the visual
  result is equivalent within that language's capabilities

---

### US-204 — Printer problems are named, not guessed

> **As an** Operator, **I want** to be told exactly what is wrong with the printer, **so that** I can
> fix it myself.

**Requirements:** FR-206, FR-608, NFR-501 · **Points:** 5

- **Given** the printer is out of paper, **when** I print, **then** both the app and the web page say
  *"Printer out of paper"*
- **Given** the cover is open, **when** I print, **then** the message names the cover
- **Given** the battery is low, **when** the app polls status, **then** a warning appears before
  printing fails
- **Given** a printer with no status query, **when** an error occurs, **then** the app reports a
  generic transmit failure and still retries rather than reporting a false success

---

## EPIC 3 — Payload API

### US-301 — Print from a template

> **As a** Developer, **I want** to send data and a template name, **so that** I never write a
> printer command.

**Requirements:** FR-301, FR-302, FR-307 · **Points:** 8

- **Given** a template `part-label` on the device, **when** I send `{ template: 'part-label', data:
  { partNo, lot, qty } }`, **then** a correctly laid-out label prints
- **Given** a template referencing a field absent from `data`, **when** I print, **then** I receive a
  validation error naming the missing field
- **Given** `GET /v1/templates`, **when** I call it, **then** I receive every template's name and
  version
- **Given** a template is updated on the device, **when** the same web-app call is made, **then** the
  new layout is used with **no web app change**

---

### US-302 — Compose a layout at runtime

> **As a** Developer, **I want** to build a layout from elements, **so that** I can handle variable
> content no fixed template covers.

**Requirements:** FR-303, FR-304, FR-309, FR-310 · **Points:** 8

- **Given** a DSL payload of `text`, `barcode`, `qr`, `line`, `feed`, and `cut`, **when** I print,
  **then** the elements appear in order with the specified styling
- **Given** a `barcode` element, **when** I specify CODE128, CODE39, EAN13, or ITF, **then** it
  renders and is scannable
- **Given** content wider than the printer's print width, **when** I print, **then** the job is
  rejected with a width error naming the offending element
- **Given** an unknown element type, **when** I print, **then** validation fails with a field path
  and the job is not retried

---

### US-303 — Escape hatch for anything unsupported

> **As a** Developer, **I want** to send raw printer bytes, **so that** no requirement is ever
> blocked by a missing SDK feature.

**Requirements:** FR-306 · **Points:** 3

- **Given** base64-encoded command bytes, **when** I send them as a Tier 3 payload, **then** they
  reach the printer unmodified
- **Given** a raw payload, **when** it is submitted, **then** it still receives queueing, retry, and
  idempotency handling
- **Given** a raw payload targeting a language the connected printer does not use, **when** I print,
  **then** the app warns but still transmits, because the caller has taken responsibility

---

### US-304 — Adapt the layout to the actual printer

> **As a** Developer, **I want** to ask what the printer can do, **so that** I do not hard-code a
> print width I cannot see.

**Requirements:** FR-201 · **Points:** 3

- **Given** a connected printer, **when** I call `GET /v1/capabilities`, **then** I receive print
  width in dots, DPI, language, supported symbologies, cutter presence, and media type
- **Given** no printer is connected, **when** I call it, **then** I receive a clear
  `PRINTER_NOT_CONNECTED` error rather than stale cached data
- **Given** the printer is changed in settings, **when** I call it again, **then** the new printer's
  capabilities are returned

---

## EPIC 4 — Security

### US-401 — Pair by scanning, not typing

> **As an** Operator, **I want** to pair by scanning a code, **so that** I never type a long secret.

**Requirements:** FR-501, FR-506, FR-507 · **Points:** 5

- **Given** the pairing screen is open, **when** it displays, **then** a QR code containing the token
  is shown
- **Given** I scan it with the handheld's scanner, **when** the SDK receives the value, **then** the
  token is stored and subsequent calls authenticate
- **Given** a QR code was displayed more than 5 minutes ago, **when** it is scanned, **then** pairing
  is refused and a new code must be generated
- **Given** a token has been used to pair, **when** the same QR is scanned again, **then** it is
  rejected as single-use
- **Given** a token is stored on the device, **when** I inspect app storage, **then** it is encrypted

---

### US-402 — Only our web app can print

> **As** IT, **I want** other pages and apps on the device to be unable to drive the printer,
> **so that** the printer cannot be abused.

**Requirements:** FR-502, FR-503, FR-508, FR-509 · **Points:** 5

- **Given** a request with no token, **when** it targets any endpoint except `/v1/status` and
  `/v1/pair`, **then** it is rejected with `401`
- **Given** a request from an origin not on the allowlist, **when** it arrives, **then** it is
  rejected with `403` regardless of token validity
- **Given** a CORS preflight from a non-allowlisted origin, **when** it arrives, **then** the
  response does not include permissive CORS headers
- **Given** an authentication failure, **when** it occurs, **then** it is logged with timestamp and
  origin — and the token is **not** logged

---

### US-403 — Revoke access instantly

> **As** IT, **I want** to invalidate a token, **so that** a lost device can be cut off.

**Requirements:** FR-505 · **Points:** 2

- **Given** a paired web app, **when** I regenerate the token in the app, **then** the previous token
  is rejected on the next request
- **Given** the token was regenerated, **when** the SDK receives `401`, **then** it surfaces a typed
  `UNAUTHORIZED` error prompting re-pairing

---

## EPIC 5 — Operator Experience

### US-501 — See and control the queue

> **As an** Operator, **I want** to see what is waiting to print, **so that** I understand what the
> device is doing.

**Requirements:** FR-403, FR-108 · **Points:** 5

- **Given** jobs are queued, **when** I open the queue screen, **then** each shows its state, attempt
  count, and last error
- **Given** a job has not begun transmitting, **when** I cancel it, **then** it moves to `CANCELLED`
  and does not print
- **Given** a job is mid-transmission, **when** I attempt to cancel, **then** cancellation is refused
  with an explanation
- **Given** the queue reaches 500 pending jobs, **when** another is submitted, **then** it is
  rejected with a distinct queue-full error

---

### US-502 — Reprint without going back to the web app

> **As an** Operator, **I want** to reprint the last label from the app, **so that** a torn sticker
> costs me seconds, not a workflow round trip.

**Requirements:** FR-404, FR-110 · **Points:** 3

- **Given** job history, **when** I select a job and tap Reprint, **then** it prints again as a **new
  job with a new ID**, bypassing idempotency deduplication
- **Given** a job older than the retention window, **when** I look for it, **then** it is absent and
  the retention rule is stated on screen
- **Given** history exists, **when** I search by part number or job ID, **then** matching jobs are
  listed

---

### US-503 — Verify the label is actually readable

> **As an** Operator, **I want** to confirm the label I printed can be scanned, **so that** faded
> output is caught now rather than at picking.

**Requirements:** FR-405 · **Points:** 8 · **Release:** v1.1

- **Given** verification is enabled, **when** a label finishes printing, **then** the app prompts me
  to scan it
- **Given** I scan the printed barcode, **when** it matches what was sent, **then** the job is marked
  verified and the result is pushed over `WS /v1/events`
- **Given** the scan does not match, **when** the mismatch is detected, **then** the app offers an
  immediate reprint
- **Given** I skip verification, **when** I dismiss the prompt, **then** the job is marked
  `unverified` and printing is not blocked

---

### US-504 — Know the printer state before pressing Print

> **As an** Operator, **I want** the web page to show printer status live, **so that** I do not
> discover a problem by trying.

**Requirements:** FR-203, FR-206, FR-707, NFR-502 · **Points:** 5

- **Given** an open WebSocket, **when** the printer disconnects, **then** the web app receives the
  event within 2 s
- **Given** the printer is disconnected, **when** the page renders, **then** the Print button is
  disabled with the reason shown
- **Given** the socket drops, **when** the SDK notices, **then** it reconnects automatically with
  backoff
- **Given** the printer is connected and ready, **when** the page loads, **then** the current state
  arrives without an explicit poll

---

### US-505 — Get set up in three minutes

> **As an** Operator, **I want** first-run setup to guide me, **so that** I do not need IT.

**Requirements:** FR-409, FR-402, NFR-503 · **Points:** 5

- **Given** first launch, **when** the app opens, **then** it explains and requests Bluetooth
  permissions with a plain-language rationale
- **Given** battery optimisation is enabled for the app, **when** setup runs, **then** the app
  explains why it must be disabled and opens the correct system screen
- **Given** setup completes, **when** I tap Test Print, **then** a self-check page prints showing
  printer identity and capabilities
- **Given** a printed one-page instruction sheet, **when** an untrained operator follows it,
  **then** setup completes in under 3 minutes

---

## EPIC 6 — Deployment & Support

### US-601 — Deploy to the fleet without touching devices

> **As** IT, **I want** to install and update via MDM, **so that** 100 devices do not mean 100 visits.

**Requirements:** FR-701 *(SDK)*, NFR-701, NFR-702 · **Points:** 5

- **Given** the signed APK, **when** it is pushed via MDM, **then** it installs with no operator
  interaction
- **Given** an update, **when** it is pushed, **then** queued jobs and the paired printer survive it
- **Given** MDM managed configuration, **when** port, allowlist, and retention are set centrally,
  **then** the app adopts them on next start

---

### US-602 — Diagnose remotely from one file

> **As** IT, **I want** a support bundle the operator can send me, **so that** *"it doesn't print"*
> becomes actionable.

**Requirements:** FR-406, NFR-704, NFR-705 · **Points:** 3

- **Given** a problem, **when** the operator taps Export Diagnostics, **then** a single file is
  produced containing app and Android version, printer identity and capabilities, permission states,
  recent job history, and the error log
- **Given** the bundle, **when** I inspect it, **then** it contains **no** pairing token
- **Given** the bundle, **when** the operator shares it by any available means, **then** no internet
  connection was required to produce it

---

### US-603 — Test without hardware

> **As a** Developer, **I want** to build and test without a printer on my desk, **so that**
> development is not gated on hardware.

**Requirements:** NFR-601, NFR-602 · **Points:** 5

- **Given** the mock printer harness, **when** I run the test suite, **then** end-to-end print flows
  execute with no physical printer
- **Given** the mock, **when** I make it report paper-out or disconnect, **then** the app's handling
  is exercised deterministically
- **Given** the core projects, **when** I run unit tests, **then** they execute as plain .NET tests
  without an Android device or emulator

---

## 2. Story map by release

| Release | Stories | Theme |
| --- | --- | --- |
| **v1.0 MVP** | US-101 … US-104, US-201 … US-204, US-301 … US-304, US-401 … US-403, US-501, US-502, US-504, US-505, US-601 … US-603 | Complete printing path, reliable and secured |
| **v1.1** | US-503 | Print verification loop |
| **Backlog** | Preview API · TSPL driver · in-app template editor · URL-scheme fallback · multi-printer routing | — |

**v1.0 total: 22 stories, 116 points.**

---

## 3. Related documents

- [Product Requirements](01-prd.md)
- [Software Requirements Specification](02-srs.md)
- [Test Cases](../05-testing/02-test-cases.md)
- [Roadmap](../07-project/01-roadmap.md)
