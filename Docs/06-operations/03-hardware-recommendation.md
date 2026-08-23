# Hardware Recommendation

| Field | Value |
| --- | --- |
| Document ID | OPS-03 |
| Version | 1.0 |
| Date | 2026-08-22 |
| Status | Approved — awaiting purchase decision (Q-01) |

---

## 1. Purpose

The printer has not been purchased (D-07). This document evaluates the options against the confirmed
requirements and makes a specific recommendation, so that Q-01 can be closed.

---

## 2. Requirements the printer must meet

Derived from the interview. Requirements 1 and 2 are the ones that eliminate most of the market.

| # | Requirement | Source | Why it eliminates candidates |
| --- | --- | --- | --- |
| **1** | **One printer prints both die-cut labels and continuous receipts** | D-08 | Most mobile printers do one or the other. Label printers need a gap sensor; receipt printers have none |
| **2** | **Bluetooth Classic SPP or BLE** | D-03 | Excludes Wi-Fi-only and network-attached models |
| 3 | Battery powered, belt-worn | Operator works walking the aisles | Excludes desktop units |
| 4 | Speaks ESC/POS, ZPL, or CPCL | FR-605 | A proprietary-only language would need a new driver |
| 5 | Rugged enough for a warehouse | Operating environment | Drop rating and IP rating matter |
| 6 | Print width suits both label and receipt use | D-06 | 2 in is too narrow for useful part labels |
| 7 | Reports status where possible | FR-608 | Without it, "out of paper" cannot be distinguished from "disconnected" |
| 8 | Available with local support in Thailand | Practical | A printer that cannot be serviced is a single point of failure |

**Not required:** Thai font support (D-09), Wi-Fi, NFC, RFID encoding, colour.

---

## 3. Candidates

### 3.1 Zebra ZQ511 / ZQ521

| Property | ZQ511 | ZQ521 |
| --- | --- | --- |
| Print width | 72 mm (3 in) | **104 mm (4 in)** |
| Resolution | 203 dpi | 203 dpi |
| Languages | CPCL, ZPL | CPCL, ZPL |
| Connectivity | Bluetooth Classic + BLE, optional Wi-Fi | same |
| Battery | 3250 mAh PowerPrecision+ | 3250 mAh |
| Media | Die-cut labels **and** continuous receipt | same |
| Weight | ~0.64 kg | ~0.79 kg |
| Positioning | High-duty receipt, medium-duty label | same |

**Assessment.** The only candidate explicitly positioned by its manufacturer for *both* receipt and
label duty — which is requirement 1 exactly. CPCL and ZPL are both in scope for v1.0, status query
is supported, and Zebra has broad distribution and service in Thailand.

- **+** Meets every requirement without qualification
- **+** Both command languages already in the driver plan
- **+** Battery reporting supported, feeding the low-battery warning (FR-206)
- **+** Well-documented command reference — important when the developer has no printer vendor to call
- **−** Highest purchase price of the candidates
- **−** Consumables cost more than generic media

### 3.2 Zebra ZQ630

| Property | Value |
| --- | --- |
| Print width | 104 mm (4 in) |
| Languages | CPCL, ZPL, EPL |
| Battery | **6600 mAh** — roughly double the ZQ500 series |
| Positioning | Heavy-duty warehouse and retail |

**Assessment.** A heavier, longer-running ZQ521. Worth it only if operators run double shifts or
cannot swap batteries mid-shift. Otherwise it is extra weight on a belt for capacity that goes
unused.

- **+** Battery life removes mid-shift charging entirely
- **+** Same languages, so no additional driver work
- **−** Heavier and bulkier for belt-worn use all day
- **−** Highest price of all candidates

### 3.3 Honeywell RP4 / RP4f

| Property | Value |
| --- | --- |
| Print width | 104 mm (4 in) |
| Speed | Up to 5 in/s |
| Connectivity | Bluetooth, USB, NFC; RP4f adds Wi-Fi and BT 5 |
| Media | Label **and** receipt, with a **linerless** option |
| Rating | IP54, wide operating temperature range |

**Assessment.** A genuine alternative that also satisfies requirement 1. Its linerless option removes
backing waste entirely — attractive for high label volumes. The caveat is language support: the RP
series emulates several languages, and which are enabled varies by configuration, so this must be
confirmed with the supplier **before** purchase.

- **+** Explicit label + receipt design; IP54 rated
- **+** Linerless option eliminates liner waste and reloading frequency
- **+** Fast
- **−** Command language must be confirmed per configuration — a purchasing risk, not a technical one
- **−** Linerless media needs a compatible platen and a different cutting approach

### 3.4 Brother RuggedJet RJ-4230B / RJ-4250WB

| Property | Value |
| --- | --- |
| Print width | 104 mm (4 in) |
| Connectivity | Bluetooth (4250WB adds Wi-Fi) |
| Languages | ESC/P plus emulations |

**Assessment.** Capable hardware and generally cheaper than Zebra, but its native language is Brother
ESC/P. Label-language support depends on the emulation mode available on the specific model. That is
an additional driver — and therefore additional risk — for a saving that the driver work would
partly consume.

- **+** Lower price than Zebra; rugged
- **−** Native language is not one of the three planned drivers
- **−** Emulation coverage varies by model and firmware

### 3.5 Low-cost ESC/POS units (Xprinter, Goojprt, generic)

| Property | Value |
| --- | --- |
| Print width | 58 mm or 80 mm |
| Languages | ESC/POS |
| Price | A fraction of the branded units |

**Assessment.** Not viable as the production printer. Print width is too narrow for a useful part
label, there is no gap sensor for die-cut media, build quality is not warehouse-grade, and status
query support is unreliable — which is precisely the FR-608 degradation path.

**They are, however, worth buying one of.** A single cheap unit is the ideal *test* device: it
exercises the write-only, no-status code path that expensive printers never reach, and it makes the
ESC/POS driver real rather than theoretical (see [TST-01 §5.2](../05-testing/01-test-strategy.md)).

---

## 4. Comparison

| Criterion | ZQ511/521 | ZQ630 | RP4/RP4f | RJ-4230B | Low-cost |
| --- | :-: | :-: | :-: | :-: | :-: |
| Label **and** receipt (req 1) | ✓ | ✓ | ✓ | ~ | ✗ |
| Bluetooth Classic / BLE (req 2) | ✓ | ✓ | ✓ | ✓ | ✓ |
| Battery, belt-worn (req 3) | ✓ | ✓ | ✓ | ✓ | ~ |
| CPCL / ZPL / ESC/POS (req 4) | ✓ | ✓ | ~ | ~ | ✓ |
| Rugged (req 5) | ✓ | ✓ | ✓ | ✓ | ✗ |
| Adequate width (req 6) | ✓ 104 mm | ✓ 104 mm | ✓ 104 mm | ✓ 104 mm | ✗ 58/80 mm |
| Status query (req 7) | ✓ | ✓ | ✓ | ~ | ✗ |
| Support in Thailand (req 8) | ✓ | ✓ | ✓ | ~ | ~ |
| Driver work required | **none** | **none** | verify first | new driver | none |
| Relative cost | high | highest | medium-high | medium | very low |

---

## 5. Recommendation

### Primary: **Zebra ZQ521** (104 mm) — or **ZQ511** (72 mm) if part labels fit

**Reasoning**

1. **It is the only candidate that satisfies requirement 1 with no verification step.** Zebra
   positions the ZQ500 series for high-duty receipt printing *and* label printing — the exact
   dual-media requirement from D-08. Everything else needs confirmation before purchase
2. **Zero additional driver work.** CPCL and ZPL are both already planned for v1.0
   ([DES-06](../03-design/06-printer-abstraction.md)). Choosing the RP4 or the Brother introduces a
   language question — and with one developer (D-16), a printer that costs a week of driver work is
   not cheaper
3. **Status query supported**, so the operator sees "out of paper" instead of a generic failure —
   which is most of the difference between a self-service recovery and a support call (M-5)
4. **Documented command reference.** The developer will be writing CPCL by hand against a
   specification. Zebra's is public, thorough, and stable
5. **Serviceable locally.** A dead printer in a 20–100 device fleet must be replaceable in days

**ZQ521 over ZQ511** if part labels need the full 104 mm, or if wider labels are likely later. The
ZQ511 is lighter and cheaper; if a 72 mm label is genuinely sufficient, it is the better belt-worn
device. **Measure a real part label before deciding** — this is the one dimension that cannot be
changed after purchase.

### Second choice: **Honeywell RP4f**

Choose it if the price difference is material to the fleet budget, or if linerless media is
attractive for label volume. **Condition:** obtain written confirmation from the supplier that the
units ship with a language mode among ESC/POS, ZPL, or CPCL, and test one before committing to
quantity.

### Not recommended: Brother RJ series, low-cost ESC/POS units

The Brother requires an additional driver for a saving that driver work would erode. Low-cost units
fail requirements 1, 5, 6, and 7.

---

## 6. Purchasing plan

### Phase 1 — evaluation, before the fleet order

| Item | Qty | Purpose |
| --- | :-: | --- |
| Zebra ZQ521 (or ZQ511) | 1 | Primary development and driver validation |
| Any BLE-capable printer | 1 | Exercises BLE chunking and flow control on real hardware — the project's highest technical risk |
| Low-cost ESC/POS unit | 1 | Validates the ESC/POS driver and the no-status degradation path (FR-608) |

Three printers, one of them inexpensive, retire the majority of the hardware risk before any fleet
commitment is made.

### Phase 2 — fleet

Order after the evaluation unit has passed the field scenarios in
[TST-01 §6](../05-testing/01-test-strategy.md). Quantity per the fleet size (D-11), plus:

| Item | Guidance |
| --- | --- |
| Spare printers | ~10% of fleet, so a failure is a swap not an outage |
| Spare batteries | One per printer if shifts exceed 8 hours |
| Chargers | Multi-bay charging cradles at shift-change points |
| Media | See §7 |

---

## 7. Media

### 7.1 Two media types, one printer

Requirement 1 means operators switch media between label and receipt work. Confirm before purchase:

- Does the printer auto-detect media type, or must it be told?
- How long does a media change take in practice?
- If switching is frequent, does the workflow justify revisiting D-08 and issuing two printers to
  high-volume operators?

The app supports the switch (`options.mediaType` in
[DES-05 §2](../03-design/05-print-payload-schema.md)), but a physical media change every few minutes
would be an operational problem no software can solve.

### 7.2 Label specification — to be confirmed (Q-04)

| Property | Guidance |
| --- | --- |
| Size | Measure against the longest part number and the barcode at `moduleWidth: 2` |
| Type | Die-cut with gap sensing is standard; black-mark is an alternative |
| Adhesive | Must hold on the actual bin, rack, and packaging surfaces — test on cold metal |
| Finish | Matt. Gloss reflects under warehouse lighting and defeats scanners |
| Direct thermal vs transfer | Direct thermal fades with heat and UV. If a label must survive a year on a rack, sample-test first |

**Test scanning before ordering in volume.** A label that prints beautifully and scans unreliably is
the defect this project must not ship — and it is exactly what field scenario F-10 and the print
verification loop (idea 6.2) exist to catch.

---

## 8. Open items

| ID | Question | Blocks | Owner |
| --- | --- | --- | --- |
| Q-01 | Which model is purchased? | Driver implementation (roadmap Phase 3) | Bearing Team |
| Q-04 | Final label dimensions and media type | Template authoring | Warehouse operations |
| — | Does the chosen unit auto-detect media type? | Operator workflow design | Bearing Team |
| — | Supplier and lead time in Thailand | Fleet ordering | Procurement |

Development is **not blocked** by Q-01. The driver and transport abstractions
([ADR-007](../03-design/02-adr/ADR-007-printer-language-abstraction.md)) and `MockTransport` allow
the entire system to be built and tested before any printer arrives — which is why that decision was
made before this one.

---

## 9. Sources

- [Zebra ZQ511/ZQ521 specification sheet](https://www.zebra.com/us/en/products/spec-sheets/printers/mobile/zq511-zq521.html)
- [Zebra ZQ500 series](https://www.zebra.com/us/en/products/printers/mobile/zq500.html)
- [Zebra ZQ600 series spec sheet](https://www.zebra.com/content/dam/zebra_dam/en/spec-sheets/zq610-zq620-zq630-spec-sheet-en-us.pdf)
- [Zebra — using ZPL/CPCL/EPL commands](https://docs.zebra.com/us/en/printers/mobile/zq610-620-630-plus-ug/c-zq6x0plus-using-the-printer/c-zq6x0plus-creating-labels/c-zq6x0plus-using-zpl-cpcl-epl-commands.html)
- [Honeywell RP4f mobile thermal printers](https://automation.honeywell.com/us/en/products/productivity-solutions/printers/mobile-printers/rp4f-mobile-thermal-printers)
- [Honeywell RP4 product page](https://www.general-data.com/products/printers/thermal/mobile/honeywell-rp4-mobile-printer)

---

## 10. Related documents

- [Printer Abstraction](../03-design/06-printer-abstraction.md)
- [ADR-007 — Driver and transport abstractions](../03-design/02-adr/ADR-007-printer-language-abstraction.md)
- [Test Strategy §5](../05-testing/01-test-strategy.md)
- [Risk Register — R-01](../07-project/02-risk-register.md)
