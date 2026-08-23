# Local API Specification

| Field | Value |
| --- | --- |
| Document ID | DES-03 |
| Version | 1.0 |
| Date | 2026-08-22 |
| Status | Approved |
| Base URL | `http://127.0.0.1:8437/v1` |

---

## 1. Overview

BifrǫstApp exposes an HTTP + WebSocket API on the loopback interface. This document is the
authoritative contract between the app and the [JavaScript SDK](04-js-sdk-spec.md); both are
implemented against it and either may be replaced independently.

| Property | Value |
| --- | --- |
| Address | `127.0.0.1` only — never `0.0.0.0` (FR-504) |
| Default port | `8437`, configurable |
| Version prefix | `/v1`; a breaking change requires `/v2` (NFR-603) |
| Content type | `application/json; charset=utf-8` |
| Authentication | `Authorization: Bearer <token>` (FR-502) |
| Max request body | 2 MB (NFR-107) |

### 1.1 Endpoint summary

| Method | Path | Auth | Requirement |
| --- | --- | :-: | --- |
| `GET` | `/v1/status` | — | FR-204 |
| `POST` | `/v1/pair` | — | FR-501 |
| `GET` | `/v1/capabilities` | ✓ | FR-201 |
| `POST` | `/v1/print` | ✓ | FR-101 |
| `POST` | `/v1/preview` | ✓ | FR-202 *(v1.1)* |
| `GET` | `/v1/jobs` | ✓ | FR-207 |
| `GET` | `/v1/jobs/{id}` | ✓ | FR-205 |
| `POST` | `/v1/jobs/{id}/cancel` | ✓ | FR-108 |
| `GET` | `/v1/templates` | ✓ | FR-302 |
| `WS` | `/v1/events` | ✓ | FR-203 |

---

## 2. Authentication

### 2.1 Bearer token

```http
Authorization: Bearer q7Kx9mNp2vRt5wYz8aBc4dEf6gHj1kLm3nOpQrStUvW
```

The token is 32 bytes of CSPRNG output, base64url-encoded (NFR-303). Obtained through the pairing
flow in §3.2.

### 2.2 Origin allowlist

Every request is checked against the allowlist **before** the token (FR-503). A valid token from a
non-allowlisted origin is rejected with `403`.

| Origin header | Outcome |
| --- | --- |
| On the allowlist | Proceed to token check |
| Not on the allowlist | `403 ORIGIN_NOT_ALLOWED` |
| Absent (non-browser caller) | Proceed to token check; only the token protects this path |

### 2.3 CORS

For allowlisted origins:

```http
Access-Control-Allow-Origin: http://intranet.company.local
Access-Control-Allow-Methods: GET, POST, OPTIONS
Access-Control-Allow-Headers: Authorization, Content-Type, Idempotency-Key
Access-Control-Max-Age: 86400
Vary: Origin
```

For any other origin, **no** `Access-Control-Allow-*` headers are emitted (FR-508). `Vary: Origin`
is always present so caches cannot serve one origin's response to another.

> **Loopback and mixed content.** Chrome treats `http://127.0.0.1` as a potentially trustworthy
> origin, so an HTTPS page may call it without triggering mixed-content blocking. This is what lets
> the SDK behave identically from HTTP and HTTPS pages (FR-710) with no certificate on the device.

---

## 3. Endpoints

### 3.1 `GET /v1/status`

Bridge health. **Unauthenticated** so a page can detect the bridge before pairing (FR-204).

**Response `200`**

```json
{
  "bridge": {
    "version": "1.0.0",
    "apiVersion": "v1",
    "uptimeSeconds": 14520,
    "paired": true
  },
  "printer": {
    "state": "READY",
    "name": "ZQ521-A17",
    "transport": "BT_CLASSIC",
    "language": "CPCL",
    "batteryPercent": 62,
    "lastError": null
  },
  "queue": {
    "pending": 0,
    "retrying": 0,
    "capacity": 500
  }
}
```

**`printer.state`**

| Value | Meaning |
| --- | --- |
| `READY` | Connected and able to print |
| `CONNECTING` | Connection in progress |
| `DISCONNECTED` | No printer connected |
| `NOT_CONFIGURED` | No printer has been selected |
| `ERROR` | Connected but faulted — see `lastError` |

When unpaired, `printer` and `queue` are omitted and `bridge.paired` is `false`.

---

### 3.2 `POST /v1/pair`

Exchange a scanned pairing token for allowlist registration. **Unauthenticated** — the body carries
the token being presented (FR-501, FR-506).

**Request**

```json
{
  "token": "q7Kx9mNp2vRt5wYz8aBc4dEf6gHj1kLm3nOpQrStUvW",
  "origin": "http://intranet.company.local",
  "clientName": "Warehouse WMS"
}
```

**Response `200`**

```json
{
  "paired": true,
  "origin": "http://intranet.company.local",
  "pairedAt": "2026-08-22T09:14:03Z"
}
```

**Errors** — `401 INVALID_TOKEN` · `410 PAIRING_EXPIRED` (QR older than 5 minutes) ·
`409 PAIRING_ALREADY_USED` (single-use QR already consumed).

---

### 3.3 `GET /v1/capabilities`

What the connected printer can actually do, so the caller need not assume (FR-201).

**Response `200`**

```json
{
  "printer": {
    "name": "ZQ521-A17",
    "language": "CPCL",
    "transport": "BT_CLASSIC",
    "firmwareVersion": "V84.20.15Z"
  },
  "media": {
    "type": "LABEL_GAP",
    "printWidthDots": 832,
    "printWidthMm": 104,
    "dpi": 203,
    "maxLengthDots": 4064
  },
  "features": {
    "cutter": false,
    "statusQuery": true,
    "batteryReport": true,
    "imageSupport": true,
    "maxImageWidthDots": 832
  },
  "barcodes": ["CODE128", "CODE39", "EAN13", "ITF", "QR"],
  "fonts": [
    { "id": "0", "widthDots": 12, "heightDots": 24 },
    { "id": "1", "widthDots": 16, "heightDots": 32 }
  ]
}
```

**`media.type`** — `LABEL_GAP` (die-cut, gap sensor) · `LABEL_BLACKMARK` · `CONTINUOUS` (receipt) ·
`LINERLESS`.

**Errors** — `409 PRINTER_NOT_CONNECTED`. Stale cached capabilities are never returned.

---

### 3.4 `POST /v1/print`

Submit a print job. Accepts all three payload tiers, which are fully specified in
[DES-05](05-print-payload-schema.md).

**Headers**

| Header | Required | Notes |
| --- | :-: | --- |
| `Authorization` | ✓ | Bearer token |
| `Content-Type` | ✓ | `application/json` |
| `Idempotency-Key` | recommended | Any string ≤ 128 chars. The SDK generates a UUIDv4 if the caller omits one (FR-705) |

**Request — Tier 1 (Template)**

```json
{
  "tier": "template",
  "template": "part-label",
  "data": {
    "partNo": "6205-2RS",
    "lot": "L2408-0231",
    "qty": 50,
    "location": "A-12-03"
  },
  "options": { "copies": 1 }
}
```

**Request — Tier 2 (Layout DSL)**

```json
{
  "tier": "dsl",
  "document": {
    "widthDots": 832,
    "elements": [
      { "type": "text",    "value": "6205-2RS", "size": 3, "bold": true, "align": "center" },
      { "type": "text",    "value": "Lot L2408-0231", "size": 1, "align": "center" },
      { "type": "barcode", "format": "CODE128", "value": "6205-2RS", "heightDots": 80, "align": "center", "showText": true },
      { "type": "qr",      "value": "PN=6205-2RS;LOT=L2408-0231", "scale": 6, "align": "center" },
      { "type": "line",    "style": "solid" },
      { "type": "feed",    "lines": 3 }
    ]
  },
  "options": { "copies": 1 }
}
```

**Request — Tier 3 (Raw)**

```json
{
  "tier": "raw",
  "language": "ESCPOS",
  "data": "G0AbYQEGMjA1LTJSUwoKHWkA",
  "options": { "copies": 1 }
}
```

**Response `202 Accepted`** — job persisted and queued (FR-103)

```json
{
  "jobId": "job_01J8XKQ4M2N7P9R3T5V6W8Y0AB",
  "state": "QUEUED",
  "queuePosition": 1,
  "idempotencyKey": "3f2b1c9e-7a84-4d21-9f60-8c5e2a1b4d77",
  "createdAt": "2026-08-22T09:41:12Z"
}
```

**Response `200 OK`** — idempotency key already seen; returns the original job, prints nothing
(FR-102)

```json
{
  "jobId": "job_01J8XKQ4M2N7P9R3T5V6W8Y0AB",
  "state": "PRINTED",
  "idempotencyKey": "3f2b1c9e-7a84-4d21-9f60-8c5e2a1b4d77",
  "createdAt": "2026-08-22T09:41:12Z",
  "deduplicated": true
}
```

> The distinction between `202` and `200` is the caller's signal that deduplication occurred. Both
> are success.

**Errors** — `400 VALIDATION_ERROR` · `401` · `403` · `409 PRINTER_NOT_CONNECTED` ·
`413 PAYLOAD_TOO_LARGE` · `422 CONTENT_TOO_WIDE` · `422 UNSUPPORTED_ELEMENT` ·
`429 QUEUE_FULL` · `503 BRIDGE_NOT_READY`.

---

### 3.5 `POST /v1/preview` *(v1.1)*

Identical request body to `/v1/print`, plus optional `previewScale` (default `1.0`). Renders without
printing (FR-202).

**Response `200`**

```json
{
  "image": "iVBORw0KGgoAAAANSUhEUgAA…",
  "format": "png",
  "widthPx": 832,
  "heightPx": 406
}
```

---

### 3.6 `GET /v1/jobs/{id}`

**Response `200`**

```json
{
  "jobId": "job_01J8XKQ4M2N7P9R3T5V6W8Y0AB",
  "state": "FAILED",
  "tier": "template",
  "templateName": "part-label",
  "attemptCount": 2,
  "maxAttempts": 5,
  "nextRetryAt": "2026-08-22T09:43:20Z",
  "lastError": {
    "code": "PRINTER_OUT_OF_PAPER",
    "message": "Printer is out of paper. Load media and it will retry automatically.",
    "transient": true,
    "occurredAt": "2026-08-22T09:42:50Z"
  },
  "createdAt": "2026-08-22T09:41:12Z",
  "updatedAt": "2026-08-22T09:42:50Z"
}
```

**Errors** — `404 JOB_NOT_FOUND`.

---

### 3.7 `GET /v1/jobs`

**Query** — `state` (repeatable) · `limit` (default 50, max 200) · `cursor` · `since` (ISO-8601).

**Response `200`**

```json
{
  "jobs": [ { "jobId": "…", "state": "PRINTED", "createdAt": "…" } ],
  "nextCursor": "eyJvZmZzZXQiOjUwfQ",
  "total": 137
}
```

---

### 3.8 `POST /v1/jobs/{id}/cancel`

Cancels a job that has not begun transmitting (FR-108).

**Response `200`** — `{ "jobId": "…", "state": "CANCELLED" }`

**Errors** — `404 JOB_NOT_FOUND` · `409 JOB_NOT_CANCELLABLE` (already transmitting or terminal).

---

### 3.9 `GET /v1/templates`

**Response `200`**

```json
{
  "templates": [
    {
      "name": "part-label",
      "version": 3,
      "description": "Bearing part label, 104 × 50 mm",
      "requiredFields": ["partNo", "lot", "qty"],
      "optionalFields": ["location"],
      "updatedAt": "2026-08-20T11:02:00Z"
    }
  ]
}
```

---

### 3.10 `WS /v1/events`

Live printer and job state (FR-203). Because browsers cannot set headers on a WebSocket handshake,
the token is passed as a query parameter.

```
ws://127.0.0.1:8437/v1/events?token=<token>
```

The token is still validated, and the origin check still applies. Server → client messages only;
any client message is ignored.

**Message envelope**

```json
{
  "event": "job.state_changed",
  "timestamp": "2026-08-22T09:42:50Z",
  "data": { }
}
```

**Event types**

| Event | Emitted when | `data` |
| --- | --- | --- |
| `job.state_changed` | A job changes state | `jobId`, `state`, `previousState`, `attemptCount`, `error?` |
| `job.verified` | Print verification completes *(v1.1)* | `jobId`, `verified`, `scannedValue?` |
| `printer.state_changed` | Connection state changes | `state`, `name?`, `transport?` |
| `printer.error` | A printer fault is reported | `code`, `message`, `transient` |
| `printer.battery` | Battery level changes materially | `percent` |
| `queue.changed` | Queue depth changes | `pending`, `retrying` |
| `bridge.shutdown` | The bridge is stopping | `reason` |

**Example**

```json
{
  "event": "printer.error",
  "timestamp": "2026-08-22T09:42:50Z",
  "data": {
    "code": "PRINTER_OUT_OF_PAPER",
    "message": "Printer is out of paper. Load media and it will retry automatically.",
    "transient": true
  }
}
```

On connect the server sends a `printer.state_changed` and a `queue.changed` snapshot so a
newly-connected client needs no separate poll (US-504).

---

## 4. Error model

Every error response uses one shape:

```json
{
  "error": {
    "code": "CONTENT_TOO_WIDE",
    "message": "Barcode at elements[2] is 900 dots wide; printer maximum is 832.",
    "transient": false,
    "field": "document.elements[2].heightDots",
    "details": { "requiredDots": 900, "maxDots": 832 }
  }
}
```

| Field | Notes |
| --- | --- |
| `code` | Stable machine-readable identifier. Never localised, never renamed within a version |
| `message` | Plain English, actionable, safe to show an operator (NFR-501) |
| `transient` | Whether a retry could succeed. Drives `RetryPolicy` and the SDK's guidance |
| `field` | JSON path to the offending field, for validation errors (FR-308) |
| `details` | Optional structured context |

### 4.1 Error code reference

| HTTP | Code | Transient | Meaning |
| :-: | --- | :-: | --- |
| 400 | `VALIDATION_ERROR` | ✗ | Payload failed schema validation |
| 400 | `MALFORMED_JSON` | ✗ | Body is not valid JSON |
| 401 | `UNAUTHORIZED` | ✗ | Token missing or invalid |
| 401 | `INVALID_TOKEN` | ✗ | Pairing token not recognised |
| 403 | `ORIGIN_NOT_ALLOWED` | ✗ | Origin is not on the allowlist |
| 404 | `JOB_NOT_FOUND` | ✗ | No such job |
| 404 | `TEMPLATE_NOT_FOUND` | ✗ | Named template is not on the device |
| 409 | `PRINTER_NOT_CONNECTED` | ✓ | No printer connection available |
| 409 | `JOB_NOT_CANCELLABLE` | ✗ | Job is transmitting or already terminal |
| 409 | `PAIRING_ALREADY_USED` | ✗ | Single-use QR already consumed |
| 410 | `PAIRING_EXPIRED` | ✗ | QR older than 5 minutes |
| 413 | `PAYLOAD_TOO_LARGE` | ✗ | Body exceeds 2 MB |
| 422 | `CONTENT_TOO_WIDE` | ✗ | Rendered width exceeds the printer's print width |
| 422 | `UNSUPPORTED_ELEMENT` | ✗ | Element or symbology unsupported by this printer |
| 422 | `MISSING_TEMPLATE_FIELD` | ✗ | Required template field absent from `data` |
| 429 | `QUEUE_FULL` | ✓ | Pending queue at capacity (500) |
| 500 | `INTERNAL_ERROR` | ✓ | Unexpected failure |
| 503 | `BRIDGE_NOT_READY` | ✓ | Service starting or shutting down |

### 4.2 Printer error codes

Reported in `lastError` and in `printer.error` events (FR-206).

| Code | Transient | Operator action |
| --- | :-: | --- |
| `PRINTER_OUT_OF_PAPER` | ✓ | Load media |
| `PRINTER_COVER_OPEN` | ✓ | Close the cover |
| `PRINTER_BATTERY_LOW` | ✓ | Charge or swap the battery |
| `PRINTER_OVERHEATED` | ✓ | Wait for it to cool |
| `PRINTER_DISCONNECTED` | ✓ | Bring it into range / switch it on |
| `PRINTER_PAPER_JAM` | ✓ | Clear the jam |
| `TRANSMIT_TIMEOUT` | ✓ | Automatic retry |
| `PRINTER_UNSUPPORTED_COMMAND` | ✗ | Payload uses a feature this printer lacks |

---

## 5. Idempotency contract

Enforcing FR-102 and NFR-202.

1. `Idempotency-Key` is any caller-supplied string of at most 128 characters
2. A key is retained for **24 hours** from first receipt
3. A key seen inside that window returns the original job's current status with
   `deduplicated: true` and HTTP `200`. **Nothing prints**
4. A key seen after the window expires is treated as new
5. Deduplication compares the key **only**. A different body with a reused key still returns the
   original job — the key is the caller's promise that the request is the same one
6. `POST /v1/print` without a key is always treated as a new job. The SDK therefore always sends one
   (FR-705)
7. Reprints initiated from the app UI create a new job with a new key, deliberately bypassing
   deduplication (US-502)

```mermaid
flowchart TD
    A["POST /v1/print"] --> B{"Idempotency-Key present?"}
    B -->|no| E["create new job → 202"]
    B -->|yes| C{"key seen in last 24h?"}
    C -->|no| D["record key, create job → 202"]
    C -->|yes| F["return original job<br/>deduplicated: true → 200"]
```

---

## 6. Rate and size limits

| Limit | Value | Behaviour on breach |
| --- | --- | --- |
| Request body | 2 MB | `413 PAYLOAD_TOO_LARGE` |
| Pending queue depth | 500 | `429 QUEUE_FULL` |
| Concurrent WebSocket clients | 5 | Oldest connection closed |
| `Idempotency-Key` length | 128 chars | `400 VALIDATION_ERROR` |
| `GET /v1/jobs` page size | 200 | Clamped to 200 |

No request-rate limiting is applied: the only callers are on-device and already authenticated, and
throttling an operator's printing would be a defect rather than a protection.

---

## 7. OpenAPI skeleton

The full document lives at `sdk/openapi.yaml` in the repository and is the source for SDK type
generation. Outline:

```yaml
openapi: 3.1.0
info:
  title: Bifrǫst Local Print API
  version: 1.0.0
servers:
  - url: http://127.0.0.1:8437/v1
components:
  securitySchemes:
    bearerAuth: { type: http, scheme: bearer }
  schemas:
    PrintRequest:
      oneOf:
        - $ref: '#/components/schemas/TemplatePayload'
        - $ref: '#/components/schemas/DslPayload'
        - $ref: '#/components/schemas/RawPayload'
      discriminator: { propertyName: tier }
    ErrorResponse:
      type: object
      required: [error]
      properties:
        error:
          type: object
          required: [code, message, transient]
          properties:
            code:      { type: string }
            message:   { type: string }
            transient: { type: boolean }
            field:     { type: string }
            details:   { type: object }
security:
  - bearerAuth: []
```

---

## 8. Compatibility policy

| Change | Allowed within `/v1`? |
| --- | :-: |
| Adding an optional request field | ✓ |
| Adding a response field | ✓ |
| Adding an endpoint | ✓ |
| Adding an error code | ✓ |
| Adding an event type | ✓ |
| Making an optional field required | ✗ |
| Removing or renaming a field | ✗ |
| Changing an error code's meaning | ✗ |
| Changing an HTTP status for an existing case | ✗ |

Clients must ignore unknown response fields and unknown event types.

---

## 9. Related documents

- [JavaScript SDK Specification](04-js-sdk-spec.md)
- [Print Payload Schema](05-print-payload-schema.md)
- [Job Lifecycle](07-job-lifecycle.md)
- [Security Design](08-security-design.md)
- [ADR-004 — Ktor embedded server](02-adr/ADR-004-ktor-embedded-server.md)
