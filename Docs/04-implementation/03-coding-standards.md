# Coding Standards

| Field | Value |
| --- | --- |
| Document ID | IMP-03 |
| Version | 2.0 |
| Date | 2026-08-22 |
| Status | Approved |

> **Version 2.0** — rewritten for C# following
> [ADR-008](../03-design/02-adr/ADR-008-dotnet-for-android.md). The intent of every rule is
> unchanged; the idioms are C#.

---

## 1. Scope

Conventions for a codebase maintained by one developer with AI assistance (D-16), in an organisation
whose competence is .NET. The standards below are chosen for a specific reason: **they make wrong
code hard to write**, rather than relying on a reviewer to catch it — because there is no second
reviewer.

Base conventions are the .NET runtime team's C# coding style and the TypeScript official style guide.
Only the deviations and the project-specific rules are documented here.

`Directory.Build.props` sets `Nullable=enable` and `TreatWarningsAsErrors=true` for every project.
Both are non-negotiable: nullable reference types are how "this can be absent" becomes checkable, and
a warning that can be ignored eventually is.

---

## 2. Error handling

### 2.1 `Result`, not exceptions, for expected failures

A printer being out of paper is not exceptional — it happens several times a day. Exceptions are
reserved for programming errors.

```csharp
// Good — the failure is in the type, so the caller cannot ignore it
Task<Result> WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct);

// Bad — the caller has no signal that this can fail
Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct);
```

`Result` and `Result<T>` are small in-house types in `Bifrost.Core`, carrying either success or a
`PrinterError`. No third-party functional library — one concept does not justify the dependency.

### 2.2 One error hierarchy

Every failure in the system is a `PrinterError`, and each one declares whether retrying could help.

```csharp
public abstract record PrinterError(string Code, string OperatorMessage, bool Transient)
{
    public sealed record OutOfPaper() : PrinterError(
        "PRINTER_OUT_OF_PAPER",
        "Printer is out of paper. Load media and printing will resume automatically.",
        Transient: true);

    public sealed record ContentTooWide(int RequiredDots, int MaxDots) : PrinterError(
        "CONTENT_TOO_WIDE",
        "This label is too wide for the printer.",
        Transient: false);
}
```

Putting `Transient` on the error type means `RetryPolicy` is a single `switch` expression over the
hierarchy, and — because the hierarchy is a closed set of nested records — the compiler warns when a
new error is added without deciding its retry disposition (FR-107).

Putting `OperatorMessage` on the error type means the API, the app UI, and the SDK all surface the
same words for the same fault (NFR-501) — one vocabulary, not three.

### 2.3 Never swallow

```csharp
// Forbidden
try { await transport.WriteAsync(bytes, ct); } catch { }

// Correct
var result = await transport.WriteAsync(bytes, ct);
if (result.IsFailure)
{
    logger.Error("Write failed: {Code}", result.Error.Code);
    return result;
}
```

A silent failure in a print bridge means the operator believes a label printed when it did not. That
is worse than a crash.

An empty `catch` block is banned by analyzer rule, not by convention.

---

## 3. Asynchrony

| Rule | Reason |
| --- | --- |
| Every `async` method takes a `CancellationToken` and honours it | The 30 s transmit timeout (FR-609) depends on it |
| Never `async void` except in Android event handlers, which must wrap their body in try/catch | An unobserved exception in `async void` terminates the process |
| Never `.Result` or `.Wait()` | Deadlocks, and it discards the cancellation path |
| `ConfigureAwait(false)` in every library project | `Bifrost.Core`, `.Drivers`, `.Server`, `.Data` must not capture a UI context |
| All GATT calls flow through `GattOperationQueue` | Android's BLE stack is not concurrency-safe ([DES-06 §7.3](../03-design/06-printer-abstraction.md) rule 6) |
| `System.Threading.Channels` for producer/consumer; `IStateStream<T>` for current-value state | Replaces Kotlin `Channel` and `StateFlow`. No reactive framework dependency |
| Use `CancellationTokenSource.CancelAfter`, never a manual timer | Cancellation propagates correctly through the whole call tree |

```csharp
// Job transmit timeout
using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
cts.CancelAfter(TransmitTimeout);          // 30 s
var result = await transport.WriteAsync(bytes, cts.Token).ConfigureAwait(false);
```

---

## 4. Immutability

| Rule | Reason |
| --- | --- |
| `record` for models; `with` for updates | Job state transitions become explicit, auditable values |
| `readonly` fields; `init`-only properties | State that cannot change cannot be corrupted |
| `IReadOnlyList<T>`, not `List<T>`, in public signatures | Callers cannot mutate a document mid-render |
| No mutable static state | The single-consumer model ([ADR-005](../03-design/02-adr/ADR-005-persistent-queue-room.md)) depends on it |

---

## 5. The dependency rules

Two boundaries, both enforced by the build rather than by review.

### 5.1 Core must not see Android

`Bifrost.Core` and `Bifrost.Drivers` target `net10.0`, **not** `net10.0-android`, so `Android.*`
types cannot be resolved there at all ([IMP-02 §2.1](02-project-structure.md)).

```csharp
// Bifrost.Core — correct
public sealed record PrintDocument(int WidthDots, IReadOnlyList<PrintBlock> Blocks);

// Bifrost.Core — will not compile
using Android.Graphics;        // ✗ type not found for this target framework
```

Where a domain concept genuinely needs a platform type — a bitmap, for instance — `Bifrost.Core`
defines its own (`MonochromeBitmap`) and `Bifrost.App` converts at the boundary.

### 5.2 EmbedIO stays in its adapter

Only `Bifrost.Server.EmbedIO` may reference EmbedIO, enforced by a banned-symbols analyzer entry
([IMP-02 §2.2](02-project-structure.md)). Routes and interceptors work with `BridgeRequest` and
`BridgeResponse`.

This is what keeps [ADR-009](../03-design/02-adr/ADR-009-embedded-http-server.md)'s escape route
real. The server library was forced on us by ASP.NET Core's absence on Android; it should not become
load-bearing throughout the codebase.

---

## 6. Logging

```csharp
logger.Information("Job {JobId} → {State} (attempt {Attempt})", job.Id, newState, job.AttemptCount);  // ✓
logger.Debug("Token: {Token}", token);                                                                // ✗ NEVER
logger.Debug("Payload: {Payload}", payloadJson);                                                      // ✗ NEVER
```

| Level | Use |
| --- | --- |
| `Error` | Failures needing attention; always includes an error code |
| `Warning` | Recoverable problems — retry scheduled, reconnecting |
| `Information` | State transitions: job states, printer connection, server start/stop |
| `Debug` | Development detail. Not emitted in release builds |
| `Verbose` | Not used |

Always use Serilog's **structured message templates**, never interpolated strings — interpolation
defeats the redaction enricher and destroys the ability to filter by property.

**Never logged, anywhere:** pairing tokens or any fragment of them, `Authorization` header values,
print payload content, raw command bytes (NFR-304, NFR-306). A redacting Serilog enricher enforces
this at the sink, so a careless call site cannot leak; a CI test greps a generated diagnostics bundle
for the active token and fails the build if it appears.

Log **metadata** freely: tier, template name, byte length, job ID, error code.

---

## 7. Testing conventions

| Rule | Reason |
| --- | --- |
| Test names describe behaviour, not methods | `Retry_is_not_scheduled_for_a_permanent_error` beats `TestRetry2` |
| Arrange/Act/Assert with blank lines | Matches the acceptance criteria in [REQ-03](../02-requirements/03-user-stories.md) |
| One assertion concept per test | A failure names the broken behaviour precisely |
| Every test carries the requirement ID it verifies | Traceability (NFR-605) |
| Driver tests are golden-output tests | `PrintDocument` in, exact bytes out — the only way to detect a subtle command regression |
| Never use a real printer in an automated test | `MockTransport` (NFR-602) |
| Use `NSubstitute` for interfaces, real objects for value types | Over-mocking tests the mocks |

```csharp
[Fact]
public void FR_107_permanent_errors_are_not_retried()
{
    // arrange
    var job = Job.Failed(new PrinterError.ContentTooWide(900, 832), attemptCount: 1);

    // act
    var disposition = _retryPolicy.Evaluate(job);

    // assert
    Assert.Equal(Disposition.Fail, disposition);
}
```

---

## 8. TypeScript SDK

**Unchanged from v1.0** — the SDK is unaffected by the platform decision.

| Rule | Reason |
| --- | --- |
| `strict: true`, no exceptions | The SDK's value is its types |
| No `any`. `unknown` plus a narrowing check | `any` disables the feature the SDK exists to provide |
| Public API returns `Result<T>`, never throws (FR-706) | An unreachable bridge is a normal state, not an exception |
| Zero runtime dependencies (FR-703) | Verified in CI |
| Every exported symbol has a TSDoc comment with an example | The doc comment is the SDK's documentation |
| `types.ts` is generated, never hand-edited | Prevents drift from `openapi.yaml` |

```ts
/**
 * Submit a print job.
 *
 * @example
 * const r = await bifrost.print({
 *   tier: 'template',
 *   template: 'part-label',
 *   data: { partNo: '6205-2RS', lot: 'L2408-0231', qty: 50 },
 * });
 * if (!r.ok) toast(r.error.message);
 */
async print(payload: PrintPayload, options?: PrintOptions): Promise<Result<Job>>
```

---

## 9. Comments

Comment **why**, never what.

```csharp
// Bad
// increment the attempt count
job.AttemptCount++;

// Good
// Do not increment while the printer is disconnected: a job queued against an
// absent printer would otherwise exhaust its five attempts without ever being
// transmitted. See DES-07 §4.3.
if (connectionState is ConnectionState.Connected) job = job with { AttemptCount = job.AttemptCount + 1 };
```

Non-obvious constraints must carry a comment naming the document that explains them — every rule in
[DES-06 §7.3](../03-design/06-printer-abstraction.md), in particular, since each looks removable and
is not:

```csharp
// WriteType.Default, not NoResponse: without per-chunk acknowledgement the
// printer's buffer silently overruns and labels truncate mid-print.
// DES-06 §7.3 rule 1. Do not "optimise" this.
characteristic.WriteType = GattWriteType.Default;
```

XML doc comments (`///`) are required on every public type and member in `Bifrost.Core`,
`Bifrost.Drivers`, and `Bifrost.Server` — these are the interfaces a future maintainer meets first.

---

## 10. Git workflow

| Aspect | Convention |
| --- | --- |
| Default branch | `main`, always releasable |
| Branches | `feat/<short-name>`, `fix/<short-name>`, `docs/<short-name>` |
| Commits | Conventional Commits: `feat(transport): add BLE MTU negotiation` |
| Requirement reference | Include the ID in the body: `Implements FR-602.` |
| Merge | Squash, so `main` reads as one change per feature |
| Tags | `v1.0.0` for the app, `sdk-v1.0.0` for the SDK |

Referencing requirement IDs in commit bodies is what makes the traceability in
[Test Cases](../05-testing/02-test-cases.md) verifiable from history rather than by assertion.

---

## 11. Definition of done

A change is done when **all** of the following hold:

- [ ] `dotnet build` succeeds with `TreatWarningsAsErrors` — including nullable and trim warnings
- [ ] `dotnet format --verify-no-changes` passes
- [ ] Unit tests cover the new behaviour and the whole suite is green
- [ ] The requirement ID it implements appears in a test name and the commit body
- [ ] Operator-facing errors have a plain-English message (NFR-501)
- [ ] No token, payload content, or raw bytes reach any log
- [ ] No `Android.*` reference leaked into `Bifrost.Core` or `Bifrost.Drivers`; no `EmbedIO`
      reference outside `Bifrost.Server.EmbedIO`
- [ ] Documentation is updated if the API, schema, or a constant changed
- [ ] An architectural decision, if one was made, is recorded as an ADR (NFR-605)
- [ ] Manually verified against a real printer, or against `MockTransport` with a justification

---

## 12. Related documents

- [Technology Stack](01-tech-stack.md)
- [Project Structure](02-project-structure.md)
- [ADR-008 — .NET for Android](../03-design/02-adr/ADR-008-dotnet-for-android.md)
- [Test Strategy](../05-testing/01-test-strategy.md)
- [Printer Abstraction §7.3](../03-design/06-printer-abstraction.md)
