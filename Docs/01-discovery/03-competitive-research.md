# Competitive Research & Solution Ideas

| Field | Value |
| --- | --- |
| Document ID | DISC-03 |
| Version | 1.0 |
| Date | 2026-08-22 |
| Status | Approved |

---

## 1. Purpose

Survey the existing web-to-printer solutions, establish whether any of them meets the constraints in
[DISC-02](02-stakeholder-interview.md), and — where none does — define what Bifrǫst should do
differently.

---

## 2. Market survey

### 2.1 QZ Tray

Open-source desktop agent. A page loads `qz-tray.js`, connects to a local WebSocket, and sends raw
ESC/POS, ZPL, or CPCL. Signed requests prevent arbitrary sites from printing.

| | |
| --- | --- |
| **Strengths** | Mature; raw command passthrough; digital-signature security model; broad printer support; open source |
| **Fatal gap** | **Windows / macOS / Linux only.** No Android build exists |
| **Verdict** | The right architecture, on the wrong platform. Bifrǫst is closest to being "QZ Tray for Android" |

Its design is the strongest available reference: local agent, WebSocket transport, JS client library,
signature-based authorisation. Bifrǫst borrows the shape and adapts the security model
(see §4.3).

### 2.2 PrintNode

Commercial cloud relay. A local agent registers printers with a cloud service; applications POST
jobs to a REST API; the cloud pushes them to the agent.

| | |
| --- | --- |
| **Strengths** | Central management across sites; REST API; good reporting |
| **Fatal gaps** | Requires internet egress (violates D-02); per-printer recurring cost; round-trip latency on an interactive action; printing stops when the uplink drops |
| **Verdict** | Built for a problem we do not have — printing across sites you do not control |

### 2.3 Star CloudPRNT

The printer itself polls an HTTP endpoint for work. No agent software at all.

| | |
| --- | --- |
| **Strengths** | Zero client software; robust for unattended ordering terminals |
| **Fatal gaps** | Requires Star hardware with a network interface — belt-worn battery printers have no Wi-Fi radio; polling latency; locks hardware purchasing to one vendor |
| **Verdict** | Not applicable to mobile printers |

### 2.4 Epson ePOS-Print

XML over HTTP, accepted directly by Epson "intelligent" printers.

| | |
| --- | --- |
| **Strengths** | Clean API; no middleware; well documented |
| **Fatal gaps** | Epson intelligent models only, at a significant per-unit premium; network-attached models, not battery Bluetooth |
| **Verdict** | Vendor lock-in with no mobile story |

### 2.5 Android ESC/POS print-service plugins

Apps such as *ESC/POS Bluetooth Print Service* register with the Android Print Framework so a
Bluetooth printer appears in the browser's Print menu.

| | |
| --- | --- |
| **Strengths** | Free; no code changes to the web app; installs in minutes |
| **Fatal gaps** | **No programmatic API** — the page cannot pass structured data, choose a template, set label size, or learn the outcome; output goes through the A4-oriented Print Framework; every print needs manual menu interaction |
| **Verdict** | Adequate for ad-hoc printing, unusable as a system integration point |

### 2.6 POSBridge / escprintbridge and similar

Android bridge apps that poll a backend for print jobs and forward them to ESC/POS printers.

| | |
| --- | --- |
| **Strengths** | Closest in concept to Bifrǫst; prove the approach works |
| **Gaps** | Closed-source or early-stage; polling-based rather than local-call; ESC/POS only, no label languages; no template layer; no idempotency guarantees; no supported extension path |
| **Verdict** | Validates the direction; not something to build a warehouse process on |

---

## 3. Comparison matrix

Scored against the constraints from [DISC-02](02-stakeholder-interview.md).

| Requirement | QZ Tray | PrintNode | CloudPRNT | ePOS | Print-service plugin | POSBridge | **Bifrǫst** |
| --- | :-: | :-: | :-: | :-: | :-: | :-: | :-: |
| Runs on Android | ✗ | ~ | n/a | n/a | ✓ | ✓ | **✓** |
| Bluetooth Classic SPP | ✓ | ~ | ✗ | ✗ | ✓ | ✓ | **✓** |
| Bluetooth LE | ~ | ✗ | ✗ | ✗ | ~ | ~ | **✓** |
| Programmatic JS API | ✓ | ✓ | ✗ | ✓ | ✗ | ~ | **✓** |
| Works offline / no internet | ✓ | ✗ | ✗ | ✓ | ✓ | ✗ | **✓** |
| Vendor-neutral hardware | ✓ | ✓ | ✗ | ✗ | ~ | ~ | **✓** |
| Label languages (ZPL/CPCL/TSPL) | ✓ | ✓ | ✗ | ✗ | ✗ | ✗ | **✓** |
| Persistent queue + retry | ~ | ✓ | ✓ | ✗ | ✗ | ~ | **✓** |
| Guaranteed no duplicate print | ✗ | ~ | ✗ | ✗ | ✗ | ✗ | **✓** |
| Template layer | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | **✓** |
| No recurring cost | ✓ | ✗ | ✓ | ✓ | ✓ | ~ | **✓** |

Legend: ✓ supported · ~ partial or conditional · ✗ not supported

---

## 4. Gap analysis

**No product on the market is a local, vendor-neutral, programmable print bridge for Android.**

Each competitor holds one corner of the problem: QZ Tray owns the architecture but not the platform;
the Android plugins own the platform but expose no API; the cloud relays own the API but require
internet and cost money per device.

Three capabilities are absent from *every* option surveyed:

1. **A template layer.** All of them require the caller to describe output at the command or element
   level. Nothing lets a web app send `{ partNo, lot, qty }` and have layout resolved on the device.
2. **Duplicate-print protection.** Retry is treated as a transport concern. None of them treat a
   duplicate physical label as a correctness failure.
3. **Use of the handheld's own hardware.** Every rugged handheld has a barcode scanner. Not one
   solution uses it — for pairing, or for verifying that the label it just printed is readable.

---

## 5. Build vs. buy

| Option | Assessment |
| --- | --- |
| **Buy PrintNode** | Rejected — violates the intranet-only constraint (D-02) and adds ongoing per-device cost |
| **Buy vendor hardware** (CloudPRNT / ePOS) | Rejected — no battery-powered mobile model fits, and it forecloses future hardware choice (D-07) |
| **Adopt a free print-service plugin** | Rejected — no API means the web app cannot drive it (D-04, D-05) |
| **Fork an open-source bridge** | Rejected — the available projects are ESC/POS-only with no label-language or template layer; the parts we would keep are the parts that are easy to write |
| **Build Bifrǫst** | **Selected** — the required behaviour does not exist in any product, the scope is bounded by the same-device topology, and the result is owned outright with no recurring cost |

The same-device decision (D-01) is what makes building tractable for one developer: it eliminates
discovery, relay infrastructure, remote queueing, and push delivery — the parts of this problem that
normally consume the budget.

---

## 6. Solution ideas

Ten ideas carried forward into the requirements. Each names the gap it closes.

### 6.1 Scan-to-pair — *closes gap 3*

The app renders a QR code containing the pairing token. The operator scans it with the handheld's
own scanner; the SDK reads it from the keyboard-wedge input and stores it. Pairing becomes a
one-second gesture instead of typing a 32-character secret.

→ [FR-501](../02-requirements/02-srs.md), [Security Design §4](../03-design/08-security-design.md)

### 6.2 Print verification loop — *closes gap 3*

After a label prints, the app can require the operator to scan it. The scan is compared against the
data sent, and the result is reported back to the web app.

This catches a failure mode nothing else addresses: a label that prints but is unreadable — low
battery producing faint output, a dirty printhead, media loaded off-centre. Without verification the
defect is discovered weeks later when the bin is picked. This is the strongest differentiator in the
list.

→ [FR-405](../02-requirements/02-srs.md)

### 6.3 Capability negotiation — *closes gap 1*

`GET /v1/capabilities` returns the connected printer's print width in dots, DPI, supported barcode
symbologies, cutter presence, and current media type. The web app adapts its layout instead of
hard-coding assumptions about a printer it cannot see.

→ [FR-201](../02-requirements/02-srs.md), [Local API Spec](../03-design/03-local-api-spec.md)

### 6.4 Preview API — *closes gap 1*

`POST /v1/preview` accepts the same payload as `/v1/print` and returns a rendered PNG instead of
printing. The operator confirms on screen before media is consumed, and template authors get a
tight edit-and-check loop with no paper waste.

→ [FR-202](../02-requirements/02-srs.md)

### 6.5 Device-side templates — *closes gap 1*

Templates are stored on the device with a version number. Changing a label's layout means updating
the template — not editing, testing, and redeploying the web application. The web app sends data;
the device owns presentation.

→ [FR-301](../02-requirements/02-srs.md), [Payload Schema §3](../03-design/05-print-payload-schema.md)

### 6.6 Idempotency keys — *closes gap 2*

Every job carries a caller-supplied `Idempotency-Key`. A repeat of a key seen inside the dedup
window returns the original job's status without printing again. Modelled on the Stripe API. This
makes retry safe for the web app, the queue, and the operator simultaneously.

→ [FR-102](../02-requirements/02-srs.md), [Job Lifecycle §5](../03-design/07-job-lifecycle.md)

### 6.7 Health push over WebSocket — *closes gap 1*

`WS /v1/events` streams printer state — connected, paper out, cover open, battery low — and job
transitions. The web app can disable its Print button before the operator discovers the problem by
pressing it.

→ [FR-203](../02-requirements/02-srs.md)

### 6.8 Local reprint

Job history is retained on the device. When a sticker jams or tears, the operator reprints from the
app without navigating back through the web app to find the record.

→ [FR-404](../02-requirements/02-srs.md)

### 6.9 Custom URL scheme fallback

`bifrost://print?job=<base64>` triggers a print via an Android deep link. If device policy ever
blocks loopback HTTP, printing degrades rather than stops. Fire-and-forget: no response channel, so
it is a fallback, not a peer of the HTTP API.

→ [FR-104](../02-requirements/02-srs.md)

### 6.10 Diagnostics export

One button produces a single file containing app version, Android version, printer identity and
capabilities, permission states, recent job history, and the error log — for a fleet supported by
one person who cannot be physically present at the device.

→ [FR-406](../02-requirements/02-srs.md)

---

## 7. Positioning

> **Bifrǫst is QZ Tray for Android** — a local, vendor-neutral print bridge with a JavaScript API —
> **plus** the three things no existing solution provides: a template layer, guaranteed
> single-printing under retry, and use of the handheld's own scanner to pair and to verify output.

---

## 8. Sources

- [Web Bluetooth: browser support and limitations](https://www.testmuai.com/learning-hub/web-bluetooth-browser-support/)
- [Chrome — Local Network Access permission prompt](https://developer.chrome.com/blog/local-network-access)
- [MDN — Mixed content](https://developer.mozilla.org/en-US/docs/Web/Security/Defenses/Mixed_content)
- [MDN — Local network access](https://developer.mozilla.org/en-US/docs/Web/Security/Defenses/Local_network_access)
- [QZ Tray](https://qz.io/)
- [Receipt Printer API: the Complete Guide](https://www.proxynodes.com/guides/receipt-printer-api)
- [Android — Foreground service types](https://developer.android.com/develop/background-work/services/fgs/service-types)
- [DantSu/ESCPOS-ThermalPrinter-Android](https://github.com/DantSu/ESCPOS-ThermalPrinter-Android)
- [teknosuper/escprintbridge](https://github.com/teknosuper/escprintbridge)
- [Zebra ZQ511/ZQ521 specification sheet](https://www.zebra.com/us/en/products/spec-sheets/printers/mobile/zq511-zq521.html)
- [Honeywell RP4f mobile thermal printers](https://automation.honeywell.com/us/en/products/productivity-solutions/printers/mobile-printers/rp4f-mobile-thermal-printers)
