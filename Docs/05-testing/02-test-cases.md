# Test Cases

| Field | Value |
| --- | --- |
| Document ID | TST-02 |
| Version | 1.0 |
| Date | 2026-08-22 |
| Status | Approved |

---

## 1. Conventions

| Prefix | Area |
| --- | --- |
| `TC-1xx` | Print submission and queue |
| `TC-2xx` | Status, capabilities, events |
| `TC-3xx` | Payload tiers and rendering |
| `TC-4xx` | Transport and drivers |
| `TC-5xx` | App UI and operator features |
| `TC-6xx` | Security |
| `TC-7xx` | SDK |
| `TC-8xx` | Non-functional |

**Level** — `U` unit · `I` integration · `D` instrumented (device) · `M` manual field
**Priority** — `P1` release blocker · `P2` important · `P3` desirable

---

## 2. Print submission and queue — TC-1xx

| ID | Requirement | L | P | Test | Expected |
| --- | --- | :-: | :-: | --- | --- |
| TC-101 | FR-101 | I | P1 | `POST /v1/print` with a valid template payload and token | `202`; body has `jobId`, `state: QUEUED` |
| TC-102 | FR-103 | I | P1 | Kill the process immediately after `202`; restart | Job present in queue; prints on restart |
| TC-103 | FR-103 | I | P1 | Force a DB write failure during submit | `500`; **no** `202` returned for an unpersisted job |
| TC-104 | FR-102, NFR-202 | I | P1 | Submit key `K1`, then submit `K1` again within 24 h | Second returns `200`, `deduplicated: true`; **one** label |
| TC-105 | FR-102 | I | P1 | Submit `K1`, advance clock 25 h, submit `K1` | Treated as new; `202`; two labels total |
| TC-106 | FR-102 | I | P1 | Two concurrent submits with key `K1` | One job created; both responses carry the same `jobId` |
| TC-107 | FR-102 | I | P2 | Reuse `K1` with a **different** body | Original job returned; new body ignored |
| TC-108 | FR-105 | I | P1 | Submit 20 jobs rapidly | Printed in submission order |
| TC-109 | FR-106 | I | P1 | `FailNTimesThenSucceed(2)` | Succeeds on attempt 3; observed delays 2 s then 8 s |
| TC-110 | FR-106 | I | P1 | Permanently failing transient error | Stops after 5 attempts; final state `FAILED` |
| TC-111 | FR-107 | U | P1 | Job fails with `ContentTooWide` | **No** retry scheduled; terminal immediately |
| TC-112 | FR-107 | U | P1 | Classify every `PrinterError` subtype | Each maps to the disposition in [DES-07 §4.1](../03-design/07-job-lifecycle.md) |
| TC-113 | FR-108 | I | P2 | Cancel a `QUEUED` job | `200`; state `CANCELLED`; never printed |
| TC-114 | FR-108 | I | P2 | Cancel a `SENDING` job | `409 JOB_NOT_CANCELLABLE` |
| TC-115 | FR-109 | I | P1 | Submit 501 jobs with the printer offline | 501st returns `429 QUEUE_FULL`; app stable |
| TC-116 | FR-110 | I | P3 | Insert jobs older than 30 days; run pruning | Removed; recent jobs retained |
| TC-117 | NFR-201 | I | P1 | Reboot device with 5 jobs queued | All 5 print; none lost, none duplicated |
| TC-118 | DES-07 §6.1 | I | P1 | Kill the process while a job is `SENDING` | On restart: `FAILED` / `INTERRUPTED`, **not** auto-retried |
| TC-119 | DES-07 §4.3, NFR-206 | I | P1 | Job retries while the printer is disconnected | `attemptCount` does **not** increment; all 5 attempts survive; queue resumes unattended on reconnect |
| TC-120 | FR-104 | D | P3 | Open `bifrost://print?job=<base64>` | Job accepted and printed |

---

## 3. Status, capabilities, events — TC-2xx

| ID | Requirement | L | P | Test | Expected |
| --- | --- | :-: | :-: | --- | --- |
| TC-201 | FR-204 | I | P1 | `GET /v1/status` with **no** token | `200`; `bridge.paired` present |
| TC-202 | FR-204 | I | P2 | `GET /v1/status` while unpaired | `200`; `paired: false`; no printer block |
| TC-203 | FR-201 | I | P1 | `GET /v1/capabilities` with a printer connected | Width, DPI, language, symbologies, cutter, media type |
| TC-204 | FR-201 | I | P1 | `GET /v1/capabilities` with no printer | `409 PRINTER_NOT_CONNECTED`; **no** stale cached data |
| TC-205 | FR-201 | I | P2 | Change the active printer, call again | New printer's capabilities returned |
| TC-206 | FR-203 | I | P1 | Connect WebSocket, submit a job | `job.state_changed` for each transition, in order |
| TC-207 | FR-203 | I | P1 | Connect WebSocket | Immediate `printer.state_changed` + `queue.changed` snapshot |
| TC-208 | FR-206 | I | P1 | Mock reports each printer error | Distinct code and message per condition |
| TC-209 | FR-205 | I | P2 | `GET /v1/jobs/{id}` on a failed job | State, `attemptCount`, `nextRetryAt`, `lastError` |
| TC-210 | FR-205 | I | P2 | `GET /v1/jobs/{unknown}` | `404 JOB_NOT_FOUND` |
| TC-211 | FR-207 | I | P3 | `GET /v1/jobs?state=PRINTED&limit=10` | Filtered, paginated, `nextCursor` present |
| TC-212 | FR-203 | I | P2 | Ordering across a job's lifetime | `PRINTED` never observed before its `SENDING` |
| TC-213 | FR-202 | I | P3 | `POST /v1/preview` *(v1.1)* | `200`; base64 PNG; nothing printed |

---

## 4. Payload tiers and rendering — TC-3xx

| ID | Requirement | L | P | Test | Expected |
| --- | --- | :-: | :-: | --- | --- |
| TC-301 | FR-301 | U | P1 | Tier 1 with all required fields | IR matches the template's element list, bound |
| TC-302 | FR-301 | I | P1 | Tier 1 missing a required field | `422 MISSING_TEMPLATE_FIELD`; `field` names it |
| TC-303 | FR-301 | I | P1 | Tier 1 with an unknown template name | `404 TEMPLATE_NOT_FOUND` |
| TC-304 | FR-302 | I | P2 | `GET /v1/templates` | Each template's name, version, required and optional fields |
| TC-305 | DES-05 §3.2 | U | P2 | Optional field absent with a declared default | Default substituted |
| TC-306 | DES-05 §3.2 | U | P2 | `omitIfEmpty` element with an empty value | Element dropped from the IR |
| TC-307 | FR-303/304 | U | P1 | Tier 2 with every element type | Each compiles to the correct `PrintBlock` |
| TC-308 | FR-304 | I | P1 | Unknown element type | `400 VALIDATION_ERROR`; `field` gives the index |
| TC-309 | FR-309 | U | P1 | Each symbology with valid data | Encodes correctly |
| TC-310 | FR-309 | U | P1 | `EAN13` with 12 digits | Check digit computed and appended |
| TC-311 | FR-309 | U | P1 | `CODE39` containing a lowercase letter | `VALIDATION_ERROR` — outside the character set |
| TC-312 | FR-309 | U | P1 | `ITF` with an odd digit count | `VALIDATION_ERROR` |
| TC-313 | FR-310 | I | P1 | Content wider than the printer's print width | `422 CONTENT_TOO_WIDE`; `field` names the element |
| TC-314 | FR-306 | I | P1 | Tier 3 raw payload | Bytes reach the transport **byte-identical** |
| TC-315 | FR-306 | I | P2 | Tier 3 with a mismatched `language` | Warning logged; still transmitted |
| TC-316 | FR-306 | I | P1 | Tier 3 submitted twice with one key | Deduplicated — reliability applies to raw too |
| TC-317 | FR-307 | U | P1 | Same label as tier 1 and tier 2 | **Identical** driver output |
| TC-318 | FR-308 | U | P1 | 20 malformed payloads | Each rejected with an accurate `field` path |
| TC-319 | FR-311 | U | P1 | Text element on any driver | Native font commands emitted; no raster |
| TC-320 | DES-05 §4.2 | U | P2 | QR value exceeding 2953 bytes | `VALIDATION_ERROR` |
| TC-321 | FR-305 | U | P3 | Image element with Floyd–Steinberg | 1-bit bitmap in the IR |
| TC-322 | DES-05 §9 | I | P2 | Oversized malformed payload | `413` before schema validation runs |

---

## 5. Transport and drivers — TC-4xx

| ID | Requirement | L | P | Test | Expected |
| --- | --- | :-: | :-: | --- | --- |
| TC-401 | FR-601 | D | P1 | Connect over Bluetooth Classic SPP | Connected; capabilities read |
| TC-402 | FR-601 | D | P2 | Connect while discovery is running | `CancelDiscovery()` called; connection succeeds |
| TC-403 | FR-602 | D | P1 | Connect over BLE | MTU negotiated; value read from `OnMtuChanged` |
| TC-404 | FR-602 | D | P1 | BLE printer that ignores `RequestMtu` | Falls back to 23; still prints correctly |
| TC-405 | FR-604 | I | P1 | Payload larger than MTU over mock BLE | Chunked at `MTU − 3`; each chunk awaits acknowledgement |
| TC-406 | FR-604 | I | P1 | `TruncateAt(1024)` scenario | Detected as `TRANSMIT_TIMEOUT`; **not** reported as success |
| TC-407 | DES-06 §7.3 | I | P1 | Attempt two concurrent GATT writes | Serialised through `GattQueue`; neither dropped |
| TC-408 | FR-602 | M | P1 | 40 KB label over BLE and over SPP | Output **byte-identical** between transports |
| TC-409 | FR-603 | D | P1 | Power-cycle the printer | Reconnects within 10 s (NFR-104) |
| TC-410 | FR-603 | D | P2 | Walk out of range and back | Reconnects; queued job resumes |
| TC-411 | FR-605 | U | P1 | Golden output for each driver | Byte-exact match against the recorded fixture |
| TC-412 | FR-607 | D | P2 | Connect a printer that supports a language query | Language detected automatically |
| TC-413 | FR-607 | D | P2 | Connect a printer that answers nothing | Operator prompted; manual choice persisted |
| TC-414 | FR-608 | I | P1 | Driver with `StatusQuery()` returning null | Write-only operation; no false success reported |
| TC-415 | FR-609 | I | P1 | `SlowWrite` exceeding 30 s | `TRANSMIT_TIMEOUT`; classified transient |
| TC-416 | FR-610, NFR-404 | U | P1 | Add a stub driver | Works end-to-end with **no** change outside `Bifrost.Drivers`; no vendor SDK required |
| TC-417 | NFR-402 | D | P1 | Permission flow on API 29 and API 31 | Both models handled; connection succeeds |
| TC-418 | DES-06 §8.1 | U | P2 | Width validation per media size | 384 / 576 / 832 dots enforced correctly |

---

## 6. Security — TC-6xx

Derived from [DES-08 §8](../03-design/08-security-design.md).

| ID | Requirement | L | P | Test | Expected |
| --- | --- | :-: | :-: | --- | --- |
| TC-601 | FR-504, NFR-301 / T-3 | D | P1 | Request `http://<handheld-lan-ip>:8437/v1/status` from another host | **Connection refused** |
| TC-602 | FR-502, NFR-302 / T-2 | I | P1 | Call each protected endpoint with no token | `401 UNAUTHORIZED` |
| TC-603 | FR-502 | I | P1 | Call with a malformed or wrong token | `401` |
| TC-604 | FR-503 / T-1 | I | P1 | Valid token, non-allowlisted origin | `403 ORIGIN_NOT_ALLOWED` |
| TC-605 | FR-503 | I | P1 | Origin check precedes token check | Bad origin + bad token → `403`, not `401` |
| TC-606 | FR-508 | I | P1 | CORS preflight from a foreign origin | **No** `Access-Control-Allow-*` headers |
| TC-607 | FR-508 | I | P2 | Preflight from an allowlisted origin | Correct headers; `Vary: Origin` present |
| TC-608 | FR-501 | D | P1 | Pair by scanning the QR | Token stored; subsequent calls authenticate |
| TC-609 | FR-506 / T-10 | I | P1 | Present a QR older than 5 minutes | `410 PAIRING_EXPIRED` |
| TC-610 | FR-506 | I | P1 | Present an already-used QR | `409 PAIRING_ALREADY_USED` |
| TC-611 | FR-505 / T-9 | I | P1 | Regenerate the token, then use the old one | `401` |
| TC-612 | FR-507 / T-5 | D | P1 | Inspect app storage | No plaintext token outside EncryptedSharedPreferences |
| TC-613 | FR-507 | I | P1 | Inspect the `AUTH_TOKEN` table | Hash only; plaintext not recoverable |
| TC-614 | NFR-304 / T-6 | I | P1 | Grep logs and diagnostics bundle for the active token | **No match** |
| TC-615 | NFR-306 | U | P1 | Inspect the merged **Release** manifest, parsing `<uses-permission>` elements | **No** `INTERNET` permission. Debug builds legitimately add it for the debugger, so this must run against Release. Parse elements, not raw text — an explanatory comment in the source manifest merges through and produces a false failure |
| TC-616 | NFR-205 / T-8 | I | P1 | Fuzz `POST /v1/print` with ~10k malformed bodies | No crash; queue consistent afterwards |
| TC-617 | NFR-303 | U | P1 | Generate 1000 tokens | All 32 bytes, all distinct, from `SecureRandom` |
| TC-618 | FR-509 | I | P2 | Trigger an auth failure | Logged with timestamp and origin; token absent |
| TC-619 | NFR-305 | U | P2 | Review requested permissions | Only those in [DES-08 §6.1](../03-design/08-security-design.md) |
| TC-620 | DES-08 §6.2 | U | P2 | Inspect the manifest | `allowBackup="false"`; service `exported="false"` |

---

## 7. App UI and operator features — TC-5xx

| ID | Requirement | L | P | Test | Expected |
| --- | --- | :-: | :-: | --- | --- |
| TC-501 | FR-401 | D | P1 | Open printer setup with a bonded printer | Listed; selectable |
| TC-502 | FR-401 | D | P2 | Open setup with no bonded devices | Empty state with a link to Bluetooth settings |
| TC-503 | FR-402 | M | P1 | Tap Test print | Self-check label with identity, width, barcode, QR |
| TC-504 | FR-403 | D | P1 | Queue with jobs in mixed states | Each shows state, attempt count, error |
| TC-505 | FR-403 | D | P2 | Cancel button visibility | Shown only for cancellable jobs |
| TC-506 | FR-404 | D | P1 | Reprint from history | New `jobId`, new key; prints again |
| TC-507 | FR-404 | D | P2 | Search history by part number | Matching jobs listed |
| TC-508 | FR-406, NFR-704, NFR-705 | D | P1 | Export diagnostics in one action | Single file with every §7.2 section; app version present; no token |
| TC-509 | FR-407, NFR-502 | D | P1 | Service running | Notification shows live printer state without opening the app |
| TC-510 | FR-407 | D | P1 | Notification content per state | Matches [DES-09 §4](../03-design/09-ui-ux-spec.md) |
| TC-511 | FR-408 | D | P1 | Reboot with jobs pending | Service restarts; queue drains unattended |
| TC-512 | FR-409 | D | P1 | First run | Guided permission and battery-optimisation steps |
| TC-513 | FR-409 | D | P1 | Battery optimisation enabled | Explained; deep-links to the correct system screen |
| TC-514 | FR-410 | D | P2 | Settings screen | All groups from [DES-09 §5.6](../03-design/09-ui-ux-spec.md) |
| TC-515 | NFR-702 | D | P2 | Value set by MDM | Shown locked with an administrator note |
| TC-516 | NFR-501 | D | P1 | Each error state | Plain-English message; code collapsed below |
| TC-517 | NFR-504 | D | P1 | Measure touch targets | All ≥ 48 dp |
| TC-518 | NFR-505 | D | P2 | Font scaling at 130% | No truncation or overlap |
| TC-519 | FR-405 | M | P3 | Verification loop *(v1.1)* | Scan match marks verified; mismatch offers reprint |
| TC-520 | NFR-503 | M | P1 | Untrained operator, one-page sheet | Setup complete in under 3 minutes |

---

## 8. SDK — TC-7xx

| ID | Requirement | L | P | Test | Expected |
| --- | --- | :-: | :-: | --- | --- |
| TC-701 | FR-701 | U | P1 | `print()` with each tier | Correct request body per tier |
| TC-702 | FR-702 | U | P1 | Type-check invalid payload shapes | Compile error |
| TC-703 | FR-703 | U | P1 | Inspect `package.json` | `dependencies` is empty |
| TC-704 | FR-704 | U | P1 | Build output | Both ESM and UMD present; UMD exposes `Bifrost` |
| TC-705 | FR-705 | U | P1 | `print()` with no key supplied | UUIDv4 generated and sent |
| TC-706 | FR-705 | U | P1 | Network failure then retry | **Same** key reused across retries |
| TC-707 | FR-706 | U | P1 | Each error condition | Correct `code`; `ok: false`; nothing thrown |
| TC-708 | FR-707 | U | P1 | `on()` with the socket dropped | Reconnects with backoff; handler still fires |
| TC-709 | FR-707 | U | P2 | Unsubscribe function | Handler no longer called |
| TC-710 | FR-708 | U | P1 | `isAvailable()` with no bridge | Resolves `false`; does **not** throw |
| TC-711 | FR-709 | U | P2 | Successful pair | Token in `localStorage` under the namespaced key |
| TC-712 | FR-709 | U | P2 | `401` received | Stored token cleared; `UNAUTHORIZED` surfaced |
| TC-713 | FR-710, NFR-405 | M | P1 | Same page over HTTP and HTTPS | Identical behaviour |
| TC-714 | DES-04 §5.5 | U | P2 | `doc()` builder | Produces a valid tier 2 payload |
| TC-715 | NFR-602 | U | P2 | `MockBifrostClient` | Records jobs; simulates events; no network |

---

## 9. Non-functional — TC-8xx

| ID | Requirement | L | P | Test | Target |
| --- | --- | :-: | :-: | --- | --- |
| TC-801 | NFR-101 | M | P1 | 100 labels, submit → printed | p95 ≤ 3 s |
| TC-802 | NFR-102 | I | P1 | 1000 submissions, ack latency | p95 ≤ 150 ms |
| TC-803 | NFR-103 | I | P2 | 1000 status calls | p95 ≤ 50 ms |
| TC-804 | NFR-104 | D | P1 | 20 printer power cycles | Reconnect ≤ 10 s |
| TC-805 | NFR-105 | D | P2 | Cold start to listening | ≤ 3 s |
| TC-806 | NFR-106 | M | P1 | 8 h idle, connected | ≤ 3% battery |
| TC-807 | NFR-107 | I | P1 | Body of 2 MB + 1 byte | `413` |
| TC-808 | NFR-203 | M | P1 | 500-job soak in warehouse conditions | ≥ 99% success |
| TC-809 | NFR-204 | M | P1 | Wi-Fi fully disabled | Printing works normally |
| TC-810 | NFR-401 | D | P1 | Full suite on API 29, 31, 34 | All pass |
| TC-811 | NFR-403 | M | P2 | SDK on Chrome 90 and current | Both work |
| TC-812 | NFR-604 | U | P2 | Coverage report | `Bifrost.Core` + `Bifrost.Drivers` ≥ 70% lines |
| TC-813 | IMP-01 §6 | U | P3 | SDK bundle size | ≤ 12 KB minified + gzip |
| TC-814 | IMP-01 §6 | U | P3 | APK size — release, trimmed, AOT, `android-arm64` only | ≤ 30 MB |
| TC-815 | NFR-601 | U | P1 | Run `Bifrost.Core` and `Bifrost.Drivers` suites with no emulator or device attached | All pass as plain .NET tests |
| TC-816 | NFR-603 | I | P2 | Call an endpoint without the `/v1` prefix, and with an unknown version prefix | `404`; the version is required and part of the contract |
| TC-817 | NFR-703 | D | P2 | Generate logs beyond 10 MB and older than 7 days | Rotated; total size bounded; oldest entries dropped |
| TC-818 | NFR-701 | M | P1 | Push the APK by MDM to a clean device | Installs and starts with **no** operator interaction |
| TC-819 | NFR-702 | M | P2 | Set `allowed_origins` and `listen_port` by MDM managed configuration | Adopted on next start; shown locked in Settings |

---

## 10. Traceability summary

| Requirement group | Test cases | Gaps |
| --- | --- | --- |
| FR-101 … FR-110 | TC-101 … TC-120 | none |
| FR-201 … FR-207 | TC-201 … TC-213 | none |
| FR-301 … FR-311 | TC-301 … TC-322 | none |
| FR-401 … FR-410 | TC-501 … TC-520 | none |
| FR-501 … FR-509 | TC-601 … TC-620 | none |
| FR-601 … FR-610 | TC-401 … TC-418 | FR-606 (TSPL) deferred to v1.1 |
| FR-701 … FR-710 | TC-701 … TC-715 | none |
| NFR-1xx … NFR-7xx | TC-801 … TC-819 + inline | NFR-605 — see below |

### 10.1 Requirements not verified by an automated test

Two requirements are process obligations rather than runtime behaviour and are verified by review at
each phase boundary, not by a test:

| Requirement | Verification |
| --- | --- |
| **NFR-605** — every architectural decision recorded as an ADR | Reviewed at each phase boundary ([PRJ-01 §8](../07-project/01-roadmap.md)). A design change merged without a corresponding ADR fails the [definition of done](../04-implementation/03-coding-standards.md#11-definition-of-done) |
| **FR-606** — TSPL driver | Deferred to v1.1 (`Could have`). No test until the language is implemented |

Every other `Must have` and `Should have` requirement in
[REQ-02](../02-requirements/02-srs.md) is cited by at least one test case above.

A CI script cross-references requirement IDs in [REQ-02](../02-requirements/02-srs.md) against IDs
appearing in this document and in test names. **The build fails if any `Must have` requirement has no
test case**, with NFR-605 explicitly exempted as a process requirement.

---

## 11. Related documents

- [Test Strategy](01-test-strategy.md)
- [Software Requirements Specification](../02-requirements/02-srs.md)
- [User Stories](../02-requirements/03-user-stories.md)
- [Security Design §8](../03-design/08-security-design.md)
