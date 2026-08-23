# Technology Stack

| Field | Value |
| --- | --- |
| Document ID | IMP-01 |
| Version | 2.0 |
| Date | 2026-08-22 |
| Status | Approved |
| Platform | .NET for Android — see [ADR-008](../03-design/02-adr/ADR-008-dotnet-for-android.md) |

> **Version 2.0** replaces the Kotlin stack of v1.0. The platform decision changed to .NET for
> Android; every library below follows from that. See
> [ADR-008](../03-design/02-adr/ADR-008-dotnet-for-android.md).

---

## 1. Selection criteria

Every choice below was made against the same four filters, in this order:

1. **Does one developer have to maintain it?** (D-16) — favour few, well-known, stable dependencies
   that a .NET developer already recognises
2. **Does it work offline?** (D-02) — no dependency may require network access at runtime
3. **Is it testable without hardware?** (NFR-601, NFR-602) — anything on the critical path must have
   a substitutable interface
4. **Does it actually run on Android?** — the constraint that eliminated the obvious server choice,
   and the one to check first for any new dependency

Criterion 4 is not theoretical. **ASP.NET Core does not run on `net*-android`**, and discovering that
late would have been expensive. Every dependency below is confirmed to target `netstandard2.0` or to
support `net10.0-android` explicitly.

---

## 2. Android application

### 2.1 Core

| Concern | Choice | Version | Rationale |
| --- | --- | --- | --- |
| Language | C# | 13 | [ADR-008](../03-design/02-adr/ADR-008-dotnet-for-android.md). Organisation's development competence |
| Runtime | .NET | **10 (LTS)** | LTS is the correct choice for a fleet application maintained by one person. `net8.0-android` is already out of support |
| Platform | .NET for Android | `net10.0-android` | No MAUI — the UI is six screens; the substance is Bluetooth |
| Build | MSBuild / `dotnet` CLI | — | Standard; `Directory.Packages.props` centralises versions |
| Min SDK | 29 (Android 10) | — | NFR-401. Covers the rugged fleet |
| Target SDK | 36 (Android 16) | — | Derived from the TFM platform version, not a property. Targeting a newer API does not narrow the supported range in NFR-401 |
| Async | `async`/`await`, `Task`, `System.Threading.Channels` | built in | Channels replace Kotlin's `Channel`/`Flow` for the queue and event hub |

### 2.2 Libraries

| Concern | Choice | Why this and not the alternative |
| --- | --- | --- |
| HTTP + WebSocket server | **EmbedIO**, behind `IBridgeServer` | [ADR-009](../03-design/02-adr/ADR-009-embedded-http-server.md). ASP.NET Core has no `android-arm64` runtime pack. EmbedIO is `netstandard2.0` with a proven Xamarin.Android history |
| Serialisation | **System.Text.Json**, source-generated | Built in; source generation avoids reflection, which keeps trimming predictable on Android |
| Database | **Microsoft.Data.Sqlite** | [ADR-005 revision](../03-design/02-adr/ADR-005-persistent-queue-room.md#revision--net-substitution). Official provider, no ORM layer, full control over index-sensitive queue queries |
| Query mapping | **Dapper** | ~50 KB, `netstandard2.0`, no code generation. Mapping convenience without EF Core's startup and trimming cost |
| DI | **Microsoft.Extensions.DependencyInjection** | Built in, familiar, and what keeps `IPrinterDriver` / `IPrinterTransport` swappable for tests |
| Logging | **Serilog** + `Serilog.Sinks.File` | The file sink does size-bounded rolling natively, which is exactly NFR-703. A redacting enricher enforces NFR-304 at the sink |
| Barcode + QR generation | **ZXing.Net** | Pure managed port of ZXing; runs in `Bifrost.Drivers` with no Android dependency, so barcode logic unit-tests off-device |
| JSON Schema validation | **JsonSchema.Net** (json-everything) | Draft 2020-12 support, which the schemas in [DES-05 §7](../03-design/05-print-payload-schema.md) require. `netstandard2.0` |
| Encrypted storage | **Xamarin.AndroidX.Security.Crypto** | The same AndroidX EncryptedSharedPreferences, bound to C# (FR-507) |
| Background recovery | **Xamarin.AndroidX.Work.Runtime** | The same WorkManager, used only as a recovery trigger ([ADR-005](../03-design/02-adr/ADR-005-persistent-queue-room.md)) |
| UI | **Android Views + AXML**, `Xamarin.Google.Android.Material` | No MAUI. Six flat screens, no animation — see [DES-09](../03-design/09-ui-ux-spec.md) |
| Lifecycle | **Xamarin.AndroidX.Lifecycle.\*** | Standard Android lifecycle handling for activities and the service |

### 2.3 Explicitly not used

| Rejected | Reason |
| --- | --- |
| **ASP.NET Core / Kestrel** | **Does not run on Android.** No `Microsoft.AspNetCore.App` runtime pack for `android-arm64`; an acknowledged product gap ([ADR-009](../03-design/02-adr/ADR-009-embedded-http-server.md)) |
| **.NET MAUI** | Its one benefit is a cross-platform UI layer, worth nothing for six screens on one platform, while every Bluetooth and service concern still lands in platform code ([ADR-008](../03-design/02-adr/ADR-008-dotnet-for-android.md)) |
| **Entity Framework Core** | Startup cost against NFR-105, APK size, and fragile trimming on Android — for a six-table schema whose queries are hand-tuned |
| Vendor printer SDKs (Zebra Link-OS, Epson ePOS) | Would bind the architecture to one manufacturer, violating NFR-404 and [ADR-007](../03-design/02-adr/ADR-007-printer-language-abstraction.md) |
| `HttpClient`, Refit, any HTTP client | The app makes **no outbound HTTP calls**. It is a server, not a client |
| Firebase, App Center, any analytics | Requires internet egress; violates D-02 and NFR-306 |
| System.Reactive | `System.Threading.Channels` plus a small `IStateStream<T>` helper covers every need. A reactive framework for one concept is not worth the surface area |
| SQLCipher | Analysed in [DES-08 §6.3](../03-design/08-security-design.md) — real risk reduction does not justify the cost here |
| A third-party ESC/POS library | The driver abstraction is the product's core; delegating it would import someone else's model of what a printer is |

### 2.4 Central package management

`Directory.Packages.props` — single source of truth for versions across every project:

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="EmbedIO"                            Version="3.5.2" />
    <PackageVersion Include="Microsoft.Data.Sqlite"              Version="10.0.0" />
    <PackageVersion Include="Dapper"                             Version="2.1.66" />
    <PackageVersion Include="Microsoft.Extensions.DependencyInjection" Version="10.0.0" />
    <PackageVersion Include="Serilog"                            Version="4.2.0" />
    <PackageVersion Include="Serilog.Sinks.File"                 Version="6.0.0" />
    <PackageVersion Include="ZXing.Net"                          Version="0.16.10" />
    <PackageVersion Include="JsonSchema.Net"                     Version="7.3.3" />
    <PackageVersion Include="Xamarin.AndroidX.Security.Crypto"   Version="1.1.0.3" />
    <PackageVersion Include="Xamarin.AndroidX.Work.Runtime"      Version="2.10.0.1" />
    <PackageVersion Include="Xamarin.Google.Android.Material"    Version="1.12.0.1" />
    <!-- test -->
    <PackageVersion Include="xunit"                              Version="2.9.3" />
    <PackageVersion Include="NSubstitute"                        Version="5.3.0" />
  </ItemGroup>
</Project>
```

Versions are indicative and pinned exactly at project start; no floating ranges.

---

## 3. JavaScript SDK

**Unchanged by the platform decision.** The SDK talks HTTP to a contract, not to a runtime.

| Concern | Choice | Rationale |
| --- | --- | --- |
| Language | **TypeScript 5.6+** | FR-702. Types are the SDK's main ergonomic contribution |
| Bundler | **tsup** (esbuild) | Zero-config dual ESM + UMD output with declarations (FR-704) |
| Runtime dependencies | **none** | FR-703. Nothing to audit, nothing to update transitively |
| Test runner | **Vitest** | Fast, TypeScript-native |
| Target | **ES2020** | Chrome 90+ (NFR-403); no polyfills |
| Distribution | Company web server | No public CDN — D-02 |

The empty `dependencies` block is a requirement, not an accident — it is checked in CI.

---

## 4. Testing stack

| Layer | Tool | Runs on |
| --- | --- | --- |
| C# unit | **xUnit** | .NET, no device (NFR-601) |
| Mocking | **NSubstitute** | .NET |
| Database | Microsoft.Data.Sqlite **in-memory** | .NET |
| Server endpoints | `IBridgeServer` test double + in-process request dispatch | .NET — the full auth and routing pipeline with no socket |
| Printer | **`MockTransport`** | .NET (NFR-602) |
| Android instrumented | xUnit via **XHarness** | Device / emulator |
| SDK unit | Vitest | Node |
| Schema validation | ajv-cli in CI | Node — validates every example in the docs |

Because [ADR-009](../03-design/02-adr/ADR-009-embedded-http-server.md) puts EmbedIO behind
`IBridgeServer`, endpoint tests dispatch `BridgeRequest` objects directly through the interceptor and
routes. No socket is opened, so the security tests in
[TST-02 §6](../05-testing/02-test-cases.md) run at unit-test speed.

`MockTransport` ([DES-06 §10](../03-design/06-printer-abstraction.md)) is what allows the entire
print path to be exercised before a printer is purchased (Q-01).

---

## 5. Tooling

| Concern | Choice |
| --- | --- |
| IDE | Visual Studio 2026 or JetBrains Rider |
| Version control | Git |
| Static analysis | Roslyn analyzers, `.editorconfig`, `dotnet format` |
| Analyzer rules | `TreatWarningsAsErrors`; a banned-symbols rule forbids `EmbedIO.*` outside the server adapter |
| CI | Any runner able to execute `dotnet build` and `npm` — intranet-hosted, given D-02 |
| APK signing | Local keystore, backed up outside the repository |
| Distribution | MDM (see [Deployment Guide](../06-operations/01-deployment-guide.md)) |

---

## 6. Runtime footprint targets

Revised from the v1.0 Kotlin figures. .NET for Android carries a runtime that Kotlin does not.

| Metric | Target | Was (Kotlin) | Verified by |
| --- | --- | --- | --- |
| APK size, release, trimmed, per-ABI | **≤ 30 MB** — measured **5 MB** on the Day 1 skeleton | ≤ 15 MB | TC-814 |
| Cold start to listening | ≤ 3 s | ≤ 3 s | NFR-105, TC-805 |
| Idle memory | ≤ 120 MB | ≤ 80 MB | Profiler |
| Idle battery over 8 h | ≤ 3% | ≤ 3% | NFR-106, TC-806 |
| SDK bundle, minified + gzip | ≤ 12 KB | ≤ 12 KB | TC-813 |

**Build configuration for size and startup.** Release builds enable `PublishTrimmed`,
`RunAOTCompilation`, and per-ABI splitting (`arm64-v8a` only, given the fleet). Without trimming the
APK roughly doubles.

> **Day 1 finding — `TrimMode` must be `partial`, not `full`.** EmbedIO's dependency `Swan.Lite` is
> reflection-heavy and emits IL2104 trim warnings, which `TreatWarningsAsErrors` turns into a failed
> Release build. `partial` trims only assemblies declaring `IsTrimmable`, leaving third-party
> reflection users whole. This is a direct consequence of ASP.NET Core being unavailable on Android
> ([ADR-009](../03-design/02-adr/ADR-009-embedded-http-server.md)) and is tracked as
> [R-16](../07-project/02-risk-register.md). The size cost proved negligible — the skeleton APK is
> 5 MB against a 30 MB budget.

Cold start is the target most at risk under .NET. If NFR-105 proves unreachable, the mitigation is
startup profiling and deferring non-essential initialisation — the server and the printer connection
must come up first; UI and history can wait.

---

## 7. Dependency policy

| Rule | Reason |
| --- | --- |
| **Confirm `netstandard2.0` or `net10.0-android` support before adopting anything** | The ASP.NET Core discovery is the precedent. A package that does not load on Android is found at deploy time otherwise |
| A new dependency needs a written justification in the PR | One maintainer — every dependency is a permanent obligation |
| Prefer the BCL, then Microsoft.Extensions.\*, then a well-known third party | Fewest surprises across .NET and Android versions |
| No dependency that requires network access at runtime | D-02 |
| SDK runtime dependencies stay at zero | FR-703, enforced in CI |
| Pin exact versions in `Directory.Packages.props`; no floating ranges | Reproducible builds |
| Verify trimming compatibility for anything on the startup path | Trim warnings become runtime failures on Android |
| Review dependency updates quarterly, not automatically | Automated bumps without a second reviewer are churn, not safety |

---

## 8. Related documents

- [ADR-008 — .NET for Android](../03-design/02-adr/ADR-008-dotnet-for-android.md)
- [ADR-009 — EmbedIO behind an abstraction](../03-design/02-adr/ADR-009-embedded-http-server.md)
- [ADR-005 — durable queue, revised for .NET](../03-design/02-adr/ADR-005-persistent-queue-room.md)
- [Project Structure](02-project-structure.md)
- [Coding Standards](03-coding-standards.md)
- [Test Strategy](../05-testing/01-test-strategy.md)
