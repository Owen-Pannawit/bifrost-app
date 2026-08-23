# ADR-007 — Driver and transport abstractions defined before any implementation

| Field | Value |
| --- | --- |
| Status | Accepted |
| Date | 2026-08-22 |
| Deciders | Bearing Team |

---

## Context

**D-07: the printer has not been purchased.** Development must begin before the hardware decision is
made, and the choice may later change or expand.

Two dimensions vary independently:

| Dimension | Variants in scope |
| --- | --- |
| **Command language** | ESC/POS (receipts), ZPL and CPCL (labels, Zebra class), TSPL (v1.1) |
| **Transport** | Bluetooth Classic SPP (RFCOMM), Bluetooth LE (GATT) |

They are genuinely orthogonal: a ZPL printer may be reached over SPP or BLE, and an ESC/POS printer
likewise. Four combinations exist today from three languages and two transports, and both axes will
grow.

## Options considered

### A. Write one driver first, generalise when a second is needed

- **+** Fastest start
- **−** The first implementation's assumptions leak everywhere — this is the standard way vendor
  lock-in gets built accidentally
- **−** Under D-07 the *first* driver written may not be the one the purchased printer needs, so the
  rework is likely rather than hypothetical

### B. Use a vendor SDK (e.g. Zebra Link-OS) as the abstraction

- **+** Vendor-tested, handles device quirks
- **−** **Fails NFR-404** — locks the architecture to one manufacturer
- **−** Non-Zebra printers would need a parallel path, which is option A with extra steps

### C. Define `PrinterDriver` and `PrinterTransport` interfaces first, implement against them

- **+** Language and transport vary independently
- **+** Concrete printer choice becomes a configuration matter, not an architectural one
- **+** A mock implementation of each interface enables hardware-free testing (NFR-602)
- **−** Interfaces designed before real hardware experience may need revision

## Decision

**Adopt option C.** Both abstractions are defined before any concrete implementation, and both are
consumed only through their interfaces.

```csharp
/// <summary>Serialises a rendered document into one printer command language.</summary>
public interface IPrinterDriver
{
    PrinterLanguage Language { get; }
    DriverCapabilities Capabilities { get; }

    byte[] Serialise(PrintDocument document, PrinterProfile printer);
    byte[]? StatusQuery();                              // null if the printer cannot be asked
    PrinterStatus ParseStatus(ReadOnlySpan<byte> response);
    bool Matches(ReadOnlySpan<byte> identificationResponse);
}

/// <summary>Moves bytes to the printer. Knows nothing about command languages.</summary>
public interface IPrinterTransport : IAsyncDisposable
{
    TransportType Type { get; }
    IStateStream<ConnectionState> ConnectionState { get; }

    Task<Result> ConnectAsync(string address, CancellationToken ct);
    Task<Result> WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct);  // chunking is internal
    Task<Result<byte[]>> ReadAsync(TimeSpan timeout, CancellationToken ct);
    Task DisconnectAsync();
}
```

`IStateStream<T>` is a small in-house helper replacing Kotlin's `StateFlow`: it exposes a current
value plus an `IAsyncEnumerable<T>` of subsequent changes, built on
`System.Threading.Channels`. It is a dozen lines and avoids taking a reactive-extensions dependency
for one concept.

**Invariants**

1. `PrintWorker` depends only on the interfaces, never on a concrete driver or transport
2. Drivers never touch Bluetooth; transports never interpret command bytes
3. Chunking, MTU negotiation, and flow control live entirely inside `BleTransport` — no other
   component knows the MTU exists (FR-602, FR-604)
4. Capability differences are expressed as data in `DriverCapabilities`, not as conditionals in
   calling code
5. Adding a language means adding one `IPrinterDriver`; adding a transport means adding one
   `IPrinterTransport`. Neither touches the API, queue, or rendering layers (FR-610)
6. Both interfaces live in `Bifrost.Core`, which targets `net10.0` — **not** `net10.0-android`. The
   compiler therefore cannot resolve `Android.*` types there, so rule 2 is enforced by the build
   rather than by discipline ([ADR-008](ADR-008-dotnet-for-android.md))

**Implementation order** — mock first, so the pipeline is testable end-to-end before any printer is
purchased; then ESC/POS (most common); then CPCL and ZPL; TSPL only if non-Zebra label hardware is
selected.

## Consequences

**Positive**

- The hardware decision (Q-01) stops blocking development. Everything up to the driver boundary can
  be built and tested against the mock
- Vendor neutrality (NFR-404) is structural rather than a matter of discipline
- A printer that changes model or firmware affects one class

**Negative**

- Some abstraction cost before it pays for itself. Justified because D-07 makes multiple
  implementations certain, not speculative
- Interfaces designed without hardware in hand may need revision once real printers arrive. Contained
  — the interfaces are small and have few call sites. `statusQuery()` returning nullable is the
  explicit acknowledgement that not every printer answers (FR-608)

**Neutral**

- Driver output is not identical across languages; ZPL positions absolutely while ESC/POS streams
  sequentially. `PrintDocument` therefore models *intent* — "barcode, CODE128, this value, this
  height" — and each driver realises it in its own idiom

## Verification

- FR-610: adding a new driver requires changes only inside the driver module
- NFR-602: the full print path runs end-to-end against `MockTransport` with no hardware
- FR-607: language auto-detection succeeds where supported and falls back to manual configuration
- US-203: the same tier 1 payload produces equivalent output across ESC/POS, ZPL, and CPCL

## Related

- [Printer Abstraction](../06-printer-abstraction.md)
- [Hardware Recommendation](../../06-operations/03-hardware-recommendation.md)
- [ADR-003](ADR-003-three-tier-payload-api.md)
