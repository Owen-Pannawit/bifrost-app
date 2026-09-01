# `@bearing/bifrost-sdk`

Print to a Bluetooth printer from a web page. One call, no ESC/POS, no Bluetooth, no plugin.

The bridge runs on the same device as the browser and listens on `http://127.0.0.1:8437`. Chrome
treats loopback as a potentially trustworthy origin, so an HTTPS page can call it with no
certificate on the device and no mixed-content block ([ADR-001](../Docs/03-design/02-adr/ADR-001-loopback-vs-cloud-relay.md)).

- **Zero runtime dependencies.** The core imports nothing (FR-703).
- **Framework-agnostic core**, with thin adapters for React and Angular and a `<script>` build for
  everything else.
- **Never throws for an expected state.** A missing bridge, an empty roll and a printer out of range
  come back as values, not exceptions.

---

## Which entry point

| Your stack | Use | Why |
| --- | --- | --- |
| React | `@bearing/bifrost-sdk/react` | Hooks; React stays a peer dependency |
| Angular | `@bearing/bifrost-sdk/angular` | DI providers that import no Angular package at all |
| Vue, Svelte, Solid, vanilla ESM | `@bearing/bifrost-sdk` | `subscribe`/`getSnapshot` fits every framework's reactivity |
| ASP.NET MVC / Razor Pages / **WebForms** | `dist/index.global.js` via `<script>` | No bundler, no build step; exposes `window.Bifrost` |
| Blazor Server | [`Bearing.Bifrost.Client.Blazor`](../clients/dotnet/README.md) | Server-side C# cannot reach the operator's loopback — the browser must make the call |
| Blazor WASM, MAUI, WPF, console | [`Bearing.Bifrost.Client`](../clients/dotnet/README.md) | Plain `HttpClient`, same contract |
| Tests | `@bearing/bifrost-sdk/testing` | `MockBifrostClient` — no bridge, no printer |

---

## Install and build

```bash
npm install          # dev dependencies only; the package itself has none
npm run verify       # typecheck + tests + build
```

Output in `dist/`:

| File | For |
| --- | --- |
| `index.js` / `index.cjs` | Bundlers — Vite, webpack, Rollup |
| `index.global.js` | `<script>` tag; defines `window.Bifrost` (ES2017, so it parses in older engines) |
| `react.js`, `angular.js`, `testing.js` | Sub-path entry points |
| `*.d.ts` | Types |

Because the network is intranet-only there is no public CDN — serve `index.global.js` from your own
web server alongside the application.

---

## Quick start

```ts
import { BifrostClient, doc } from '@bearing/bifrost-sdk';

const bifrost = new BifrostClient();

if (!(await bifrost.isAvailable())) {
  showBanner('Print bridge not running. Open BifrǫstApp on this device.');
  return;
}

const result = await bifrost.print(
  doc()
    .text('6205-2RS', { size: 3, bold: true, align: 'center' })
    .barcode('CODE128', '6205-2RS', { heightDots: 80, showText: true })
    .qr('PN=6205-2RS;LOT=L2408-0231', { scale: 6 })
    .feed(3)
    .build(),
);

if (result.ok) toast(`Printed — job ${result.value.jobId}`);
else toast(result.error.message);   // already operator-safe
```

`result` is a discriminated union, so TypeScript will not let the failure case go unhandled.

---

## Per-framework

### Plain `<script>` — Razor, WebForms, anything without a bundler

```html
<script src="/assets/bifrost.global.js"></script>
<script>
  var bifrost = new Bifrost.BifrostClient();

  document.getElementById('printBtn').addEventListener('click', function () {
    bifrost.print(
      Bifrost.doc()
        .text(document.getElementById('partNo').value, { size: 3, bold: true, align: 'center' })
        .barcode('CODE128', document.getElementById('partNo').value)
        .feed(3)
        .build()
    ).then(function (r) {
      alert(r.ok ? 'Printed' : r.error.message);
    });
  });
</script>
```

In WebForms the button must not post back — `OnClientClick="printLabel(); return false;"`, or use a
plain `<button type="button">`. The print call is a browser-to-device call; the server is not
involved and a postback would only reload the page underneath it.

### React

```tsx
import { BifrostProvider, useBifrostState, usePrint } from '@bearing/bifrost-sdk/react';
import { doc } from '@bearing/bifrost-sdk';

// once, at the root — one client means one event socket
createRoot(el).render(<BifrostProvider><App /></BifrostProvider>);

function PrintButton({ partNo }: { partNo: string }) {
  const { ready, printerState, error } = useBifrostState();
  const { print, printing } = usePrint();

  return (
    <>
      <button
        disabled={!ready || printing}
        onClick={() => print(doc().text(partNo, { size: 3, align: 'center' }).feed(3).build())}
      >
        {printing ? 'Printing…' : 'Print label'}
      </button>
      {!ready && <p role="alert">{error?.message ?? `Printer ${printerState}`}</p>}
    </>
  );
}
```

`useBifrostState` is backed by `useSyncExternalStore`, so it is concurrent-safe and re-renders only
when the state actually changes.

### Angular

The adapter imports **no** `@angular/core`, so it works from Angular 14 to 20 and drags neither
`rxjs` nor `zone.js` into a React consumer's bundle.

```ts
// app.config.ts
import { provideBifrost } from '@bearing/bifrost-sdk/angular';

export const appConfig: ApplicationConfig = {
  providers: [provideBifrost()],
};
```

```ts
// print-button.component.ts
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { BifrostClient, doc } from '@bearing/bifrost-sdk';
import { BifrostStoreRef, connectSignal } from '@bearing/bifrost-sdk/angular';

@Component({ /* … */ })
export class PrintButtonComponent {
  private readonly bifrost = inject(BifrostClient);
  private readonly store = inject(BifrostStoreRef);

  readonly state = signal(this.store.getSnapshot());

  constructor() {
    inject(DestroyRef).onDestroy(connectSignal(this.store, this.state));
  }

  async print(partNo: string) {
    const r = await this.bifrost.print(doc().text(partNo, { size: 3 }).feed(3).build());
    if (!r.ok) this.toast(r.error.message);
  }
}
```

Prefer observables? The store carries the `Symbol.observable` interop, so `from(store as never)`
works with RxJS and `toSignal(from(store as never))` with Angular's interop layer.

### Vue, Svelte, and everything else

The core store is `subscribe(listener) => unsubscribe` plus a synchronous `getSnapshot()`, which is
what every framework's reactivity primitive is built on:

```ts
import { createBifrostStore, BifrostClient } from '@bearing/bifrost-sdk';

const bifrost = new BifrostClient();
const store = createBifrostStore(bifrost);

// Vue
const state = ref(store.getSnapshot());
onMounted(() => { const off = store.subscribe(s => (state.value = s)); onUnmounted(off); });

// Svelte — this is already a valid store
$: ready = $store.ready;
```

---

## API

| Call | Purpose |
| --- | --- |
| `isAvailable()` | Is the bridge running? Never throws (FR-708) |
| `getStatus()` | Bridge, printer and queue state. Works unpaired (FR-204) |
| `pair(token, clientName?)` | Complete pairing from a scanned QR code; persists the token (FR-501) |
| `getCapabilities()` | Print width, dpi, symbologies — read them instead of assuming (FR-201) |
| `print(payload, options?)` | All three tiers (FR-701) |
| `preview(payload, scale?)` | Render without printing (FR-202) |
| `getJob` · `listJobs` · `cancelJob` | Job inspection and cancellation |
| `getTemplates()` | Templates on the device, with their required fields (FR-302) |
| `on(event, handler)` | Live events; returns its own unsubscribe (FR-707) |
| `close()` | Release the event socket |

### The three payload tiers

```ts
// Tier 1 — template on the device. Layout changes without a web deployment.
await bifrost.print(template('part-label', { partNo: '6205-2RS', lot: 'L2408-0231', qty: 50 }));

// Tier 2 — layout DSL
await bifrost.print(doc(832).text('6205-2RS', { size: 3 }).qr('PN=6205-2RS').feed(3).build());

// Tier 3 — raw commands, passed through untouched. Bytes in; the base64 is handled for you.
await bifrost.print(raw('ESCPOS', new Uint8Array([0x1b, 0x40])));
```

### Events

```ts
const off = bifrost.on('printer.state_changed', ({ state }) => {
  printButton.disabled = state !== 'READY';
});

bifrost.on('printer.error', ({ message }) => toast(message));
bifrost.on('connection.changed', ({ connected }) => setBadge(connected));
```

The socket opens on the first subscription and reconnects with backoff. `connection.changed` is
synthesised by the SDK — the bridge cannot tell you that you stopped being able to hear it.

### Errors

```ts
const r = await bifrost.print(payload);
if (r.ok) return onPrinted(r.value);

switch (r.error.code) {
  case 'UNAUTHORIZED':            return showPairingDialog();
  case 'PRINTER_NOT_CONNECTED':
  case 'PRINTER_OUT_OF_PAPER':    return toast(r.error.message);   // already actionable
  case 'CONTENT_TOO_WIDE':        return console.error('Layout bug at', r.error.field);
  default:                        return toast(r.error.transient ? 'Temporary problem, try again'
                                                                 : r.error.message);
}
```

Codes mirror [the API error table](../Docs/03-design/03-local-api-spec.md#41-error-code-reference)
exactly. Four are raised locally by the SDK and never by the bridge: `BRIDGE_UNAVAILABLE`,
`BRIDGE_TIMEOUT`, `JOB_TIMEOUT`, and `REQUEST_ABORTED` (a caller-supplied `AbortSignal` firing —
kept distinct from a timeout, because "the user navigated away" and "the bridge is wedged" call for
different handling).

### Idempotency

Every `print()` sends a generated `Idempotency-Key` (FR-705), and the **same** key is reused across
the SDK's own retries. That is what makes an ambiguous timeout safe: if the bridge already accepted
the job, the retry returns that job and nothing prints twice (NFR-202). Only network-level failures
are retried — never a 4xx, never a 5xx.

---

## Testing

```ts
import { MockBifrostClient } from '@bearing/bifrost-sdk/testing';

const bifrost = new MockBifrostClient({
  printerState: 'READY',
  capabilities: { media: { printWidthDots: 832 } },
});

await clickPrint(bifrost);

expect(bifrost.printedJobs).toHaveLength(1);
expect(bifrost.lastPrint?.payload).toMatchObject({ template: 'part-label' });

bifrost.setPrinterState('DISCONNECTED');        // emits the event a real bridge would
bifrost.failNext({ code: 'QUEUE_FULL', message: 'The queue is full.', transient: true });
```

The mock deduplicates repeated idempotency keys and refuses to print when the printer is not
`READY`, so a component that retries on double-click is testable without hardware.

---

## What the current bridge implements

The SDK covers all of [DES-04](../Docs/03-design/04-js-sdk-spec.md). The **0.1 demo bridge**
implements two of the ten endpoints: `GET /v1/status` and `POST /v1/print` (Tier 2, elements `text`,
`barcode`, `qr`, `feed`, `cut`). Everything else answers `404`, which the SDK surfaces as
`NOT_FOUND` — "this bridge build does not provide that endpoint" — rather than as a crash.

So today: `isAvailable`, `getStatus`, `print` and the builders work end to end. `pair`,
`getCapabilities`, jobs, templates, preview and the event socket are written against the spec and
wait on the bridge.

---

## Related

- [JS SDK Specification](../Docs/03-design/04-js-sdk-spec.md) — the contract this implements
- [Local API Specification](../Docs/03-design/03-local-api-spec.md)
- [Print Payload Schema](../Docs/03-design/05-print-payload-schema.md)
- [.NET clients](../clients/dotnet/README.md)
