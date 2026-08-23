# ADR-008 — .NET for Android rather than native Kotlin

| Field | Value |
| --- | --- |
| Status | **Accepted** |
| Date | 2026-08-22 |
| Deciders | Bearing Team |
| Supersedes | [ADR-002](ADR-002-kotlin-native-vs-flutter.md) |

---

## Context

[ADR-002](ADR-002-kotlin-native-vs-flutter.md) selected native Kotlin, reasoning from the technical
shape of the work: the app is overwhelmingly platform-level — Bluetooth Classic RFCOMM, BLE GATT with
MTU negotiation, a typed foreground service, permission models across API 29–35.

That reasoning was sound but incomplete. It weighed only the technology and not the **team that has
to maintain the result**.

New constraint from the stakeholder: **the organisation's development competence is .NET.** The
project remains a single developer (D-16), but that developer, and anyone who might inherit the
project, works in C#.

This reframes the decision. Under D-16 and [R-04](../../07-project/02-risk-register.md) — one
developer as a single point of failure — the language the surrounding organisation can actually read
matters more than the language with the cleanest platform bindings. A Kotlin codebase in a .NET
organisation is a codebase with a maintainer count of one, permanently.

---

## Options reconsidered

### A. Native Kotlin *(the ADR-002 decision)*

- **+** Direct, idiomatic Android platform access
- **+** Android documentation and error messages apply literally
- **+** Ktor and Room are first-class
- **−** **Nobody else in the organisation can maintain it**
- **−** Every future change requires the one Kotlin-capable person

### B. .NET MAUI

- **+** XAML UI familiar to .NET developers; largest .NET mobile community
- **+** Cross-platform if ever needed
- **−** Every capability this app needs — SPP, GATT, typed foreground service, encrypted storage —
  lives in `Platforms/Android/` regardless, so the cross-platform layer buys nothing here
- **−** Materially larger APK and slower startup for a UI of six simple screens
- **−** Adds a UI abstraction between the developer and the platform work that dominates the project

### C. .NET for Android *(no MAUI)*

- **+** **Binds the complete Android SDK.** `Android.Bluetooth.BluetoothSocket`,
  `CreateRfcommSocketToServiceRecord`, `BluetoothGatt`, and
  `ServiceInfo.ForegroundServiceTypeConnectedDevice` are all directly available — nothing this
  project needs is missing
- **+** RFCOMM `InputStream`/`OutputStream` surface as ordinary .NET `Stream` objects, so the
  transport layer is plain C# I/O
- **+** C#, so the organisation can maintain it
- **+** Smaller and faster to start than MAUI; no UI framework carried for six screens
- **+** The `net10.0` / `net10.0-android` target-framework split enforces the core/platform boundary
  at compile time, exactly as the JVM/Android module split did
- **−** UI must be written with Android Views and AXML layouts — more verbose than Compose or XAML
- **−** Smaller community and thinner documentation than either Kotlin or MAUI
- **−** **ASP.NET Core does not run on Android** — see [ADR-009](ADR-009-embedded-http-server.md)
- **−** Larger APK and slower cold start than Kotlin

---

## Decision

**Build BifrǫstApp with .NET for Android (`net10.0-android`), without MAUI.**

MAUI is rejected because its single benefit — a cross-platform UI layer — is worth nothing to a
project whose UI is six screens and whose substance is Bluetooth and service lifecycle. Every line
MAUI would save is a line this app does not have; every line it costs is in the part that dominates.

The SDK remains a separate TypeScript artefact. No code is shared between app and SDK — only the API
contract in [DES-03](../03-local-api-spec.md), which is the correct coupling point.

### Technology substitutions

| Concern | ADR-002 (Kotlin) | This decision (.NET) |
| --- | --- | --- |
| Language / runtime | Kotlin 2.0, JVM | C# 13, .NET 10 (LTS) |
| Target framework | `com.android.application` | `net10.0-android` |
| Build | Gradle | MSBuild / `dotnet build` |
| HTTP + WebSocket server | Ktor CIO | EmbedIO — see [ADR-009](ADR-009-embedded-http-server.md) |
| Database | Room | Microsoft.Data.Sqlite + Dapper — see [ADR-005 revision](ADR-005-persistent-queue-room.md#revision--net-substitution) |
| UI | Jetpack Compose | Android Views + AXML, Material Components |
| DI | Hilt | Microsoft.Extensions.DependencyInjection |
| Async | Coroutines + Flow | `async`/`await`, `Task`, `System.Threading.Channels` |
| Serialisation | kotlinx.serialization | `System.Text.Json`, source-generated |
| Barcode generation | ZXing (Java) | ZXing.Net |
| Logging | Timber | Serilog |
| Encrypted storage | androidx.security-crypto | Same library, via `Xamarin.AndroidX.Security.Crypto` |
| Background recovery | WorkManager | Same, via `Xamarin.AndroidX.Work.Runtime` |
| Unit tests | JUnit 5 | xUnit |

Note that the **Android** components — EncryptedSharedPreferences, WorkManager, the foreground
service, the manifest — are unchanged. Only the language binding them changes. Everything in
[DES-08 §6](../08-security-design.md) about permissions and manifest hardening applies verbatim.

### What does not change

[ADR-001](ADR-001-loopback-vs-cloud-relay.md) (loopback topology),
[ADR-003](ADR-003-three-tier-payload-api.md) (three payload tiers),
[ADR-005](ADR-005-persistent-queue-room.md) (durable queue, single consumer),
[ADR-006](ADR-006-origin-allowlist-token-auth.md) (origin allowlist + token), and
[ADR-007](ADR-007-printer-language-abstraction.md) (driver and transport abstractions) are all
**unaffected**. They are decisions about topology, contract, and structure — not about language.

That they survive a platform change intact is a useful signal that they were made at the right level.

---

## Consequences

**Positive**

- The codebase is maintainable by the organisation, not by one person. This is a direct and
  substantial mitigation of [R-04](../../07-project/02-risk-register.md), the project's highest-scoring
  people risk
- Full Android SDK access with no bridging layer, since .NET for Android binds the platform directly
- The core/platform boundary is still compiler-enforced: `Bifrost.Core` targets `net10.0`, so
  `Android.*` types are not referenceable and a violation fails to build
- RFCOMM streams are ordinary .NET `Stream`s, which makes the SPP transport simpler than its Kotlin
  equivalent

**Negative**

- **ASP.NET Core and Kestrel are unavailable on Android.** There is no `Microsoft.AspNetCore.App`
  runtime pack for `android-arm64`; it is an acknowledged product gap, not a configuration error.
  A third-party embedded server is therefore mandatory —
  [ADR-009](ADR-009-embedded-http-server.md), and [R-16](../../07-project/02-risk-register.md)
- UI code is more verbose than Compose. Acceptable: six screens, deliberately plain, and the
  [UI/UX specification](../09-ui-ux-spec.md) prescribes flat layouts with no animation
- APK size rises from a ~15 MB budget to ~30 MB, and cold start is slower. Acceptable for an
  MDM-distributed internal app; both are tracked in [IMP-01 §6](../../04-implementation/01-tech-stack.md)
- Smaller community for Android-specific problems in C#. Mitigated by the fact that Android
  documentation still applies — the API names are identical, only the casing differs

**Neutral**

- Android platform behaviour is unchanged. Every permission, manifest setting, service type, and
  BLE rule in this documentation set applies as written

---

## Verification

- `Bifrost.Core` and `Bifrost.Drivers` target `net10.0` and fail to compile on any `Android.*`
  reference (TC-815, and [IMP-02 §2.1](../../04-implementation/02-project-structure.md))
- SPP transport connects and prints via `BluetoothSocket` on API 29 and API 35 (TC-401, TC-417)
- BLE transport negotiates MTU and chunks correctly via `BluetoothGatt` (TC-403 … TC-408)
- Foreground service starts with `ForegroundServiceTypeConnectedDevice` on API 34 (TC-509)
- APK size within the revised budget (TC-814)

---

## Related

- [ADR-002 — superseded](ADR-002-kotlin-native-vs-flutter.md)
- [ADR-009 — embedded HTTP server](ADR-009-embedded-http-server.md)
- [Technology Stack](../../04-implementation/01-tech-stack.md)
- [Project Structure](../../04-implementation/02-project-structure.md)
- [R-16 — third-party server dependency](../../07-project/02-risk-register.md)

---

## Sources

- [BluetoothSocket Class (Android.Bluetooth) — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/android.bluetooth.bluetoothsocket?view=net-android-35.0)
- [ServiceInfo.ForegroundServiceTypeConnectedDevice — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/android.content.pm.serviceinfo.foregroundservicetypeconnecteddevice?view=net-android-35.0)
- [Which is better for Android app development with .NET: MAUI or .NET for Android? — Microsoft Q&A](https://learn.microsoft.com/en-us/answers/questions/5520207/which-is-better-for-android-app-development-with-n)
- [Support for MAUI runtimes in ASP.NET Core? — dotnet/aspnetcore#35077](https://github.com/dotnet/aspnetcore/issues/35077)
