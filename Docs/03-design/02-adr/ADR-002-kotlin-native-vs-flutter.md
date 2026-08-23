# ADR-002 — Native Kotlin rather than Flutter or Kotlin Multiplatform

| Field | Value |
| --- | --- |
| Status | ⚠️ **SUPERSEDED by [ADR-008](ADR-008-dotnet-for-android.md)** |
| Date | 2026-08-22 |
| Superseded | 2026-08-22 |
| Deciders | Bearing Team |

> **This decision no longer holds.** The stakeholder subsequently confirmed that the organisation's
> development competence is .NET. Under a single-developer constraint (D-16), the language the
> surrounding organisation can maintain outweighs the language with the cleanest platform bindings.
> The app is built with **.NET for Android** — see [ADR-008](ADR-008-dotnet-for-android.md).
>
> The analysis below is retained because its **rejection of Flutter and KMP still stands**, and for
> the same reasons: the work is overwhelmingly platform-level, so a cross-platform abstraction layer
> buys nothing and costs a bridging layer. ADR-008 rejects .NET MAUI on precisely this argument.

---

## Context

BifrǫstApp is an Android-only application (NG-3) built by one developer (D-16). Its work is
overwhelmingly platform-level: Bluetooth Classic RFCOMM sockets, BLE GATT with MTU negotiation, a
typed foreground service, runtime permission models that differ across API 29–35, and an embedded
HTTP server.

The UI, by contrast, is small: setup, queue, history, settings, diagnostics.

## Options considered

### A. Native Kotlin

- **+** Direct access to `BluetoothSocket`, `BluetoothGatt`, foreground service types, and permission
  APIs with no bridging layer
- **+** Android platform documentation and error messages apply literally
- **+** Ktor and Room are first-class Kotlin libraries with no wrapper needed
- **+** Core logic is plain Kotlin, so it unit-tests on the JVM (NFR-601)
- **−** UI code is Android-only — irrelevant, since there is no second platform

### B. Flutter

- **+** Excellent UI productivity
- **−** **Every** platform capability this app needs — SPP, GATT chunking, typed foreground service,
  encrypted storage, permission flows — must cross a platform channel. The plugin ecosystem for
  Bluetooth Classic on Android is thin and inconsistently maintained
- **−** Effectively means writing the hard 80% in Kotlin anyway, then maintaining a channel layer on
  top of it
- **−** Cross-platform benefit is zero: there is no iOS target and never will be under current
  hardware policy
- **−** Debugging a BLE flow-control bug across a platform channel is materially harder

### C. Kotlin Multiplatform

- **+** Shared core with a possible future non-Android target
- **−** The core here — queue and rendering — is the part that already ports easily; the transport
  and service layers are irreducibly Android
- **−** Adds build complexity for a benefit no requirement asks for

## Decision

**Build BifrǫstApp as a native Kotlin Android application, with Jetpack Compose for the UI.**

The SDK remains a separate TypeScript artefact. There is no shared code between them — only the
shared API contract in [DES-03](../03-local-api-spec.md), which is the correct coupling point.

## Consequences

**Positive**

- No bridging layer between the app and the APIs it spends all its time calling
- One language for the app, one for the SDK, one contract between them — the minimum for a solo
  developer (D-16)
- Core modules stay Android-free and JVM-testable, keeping the mock printer harness simple
  (NFR-601, NFR-602)

**Negative**

- Zero code reuse if an iOS version is ever required. Accepted — NG-3 excludes it, and the Bluetooth
  Classic restrictions on iOS would force a different design regardless

**Neutral**

- Compose is the current Android UI standard; the small UI surface means the choice carries little
  risk either way

## Verification

- NFR-601: core module tests run under `./gradlew :core:test` with no emulator
- NFR-401/402: instrumented tests pass on API 29 and API 35 for both permission models

## Related

- [Tech Stack](../../04-implementation/01-tech-stack.md)
- [Project Structure](../../04-implementation/02-project-structure.md)
