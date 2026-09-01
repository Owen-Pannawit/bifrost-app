import { describe, expect, it } from 'vitest';
import { createBifrostStore, doc, template } from '../src/index.js';
import { MockBifrostClient } from '../src/testing.js';

/**
 * The mock exists so a web app's own tests never touch a printer (NFR-602). If it does not behave
 * like the bridge in the ways a test asserts on, it is worse than useless — it is a green suite
 * over a broken page.
 */

describe('MockBifrostClient', () => {
  it('records what was submitted, which is the assertion most tests want', async () => {
    const bifrost = new MockBifrostClient();

    await bifrost.print(template('part-label', { partNo: '6205-2RS', lot: 'L2408-0231', qty: 50 }));

    expect(bifrost.printedJobs).toHaveLength(1);
    const payload = bifrost.lastPrint?.payload;
    expect(payload?.tier).toBe('template');
    if (payload?.tier === 'template') expect(payload.data.partNo).toBe('6205-2RS');
  });

  it('refuses to print when the printer is not ready, as the bridge would', async () => {
    const bifrost = new MockBifrostClient({ printerState: 'DISCONNECTED' });

    const r = await bifrost.print(doc().text('X').build());

    expect(r.ok).toBe(false);
    if (!r.ok) expect(r.error.code).toBe('PRINTER_NOT_CONNECTED');
    expect(bifrost.printedJobs).toHaveLength(0);
  });

  it('deduplicates a repeated idempotency key instead of printing twice', async () => {
    // FR-102. A component that retries on double-click is only testable if the mock honours this.
    const bifrost = new MockBifrostClient();
    const options = { idempotencyKey: 'same-key' };

    await bifrost.print(doc().text('X').build(), options);
    const second = await bifrost.print(doc().text('X').build(), options);

    expect(bifrost.printedJobs).toHaveLength(1);
    expect(second.ok).toBe(true);
    if (second.ok) expect(second.value.deduplicated).toBe(true);
  });

  it('reports the capabilities a test asked for, merged over the defaults', async () => {
    const bifrost = new MockBifrostClient({ capabilities: { media: { printWidthDots: 832 } } });

    const r = await bifrost.getCapabilities();

    expect(r.ok).toBe(true);
    if (r.ok) {
      expect(r.value.media.printWidthDots).toBe(832);
      expect(r.value.media.dpi).toBe(203); // untouched by the override
    }
  });

  it('delivers simulated events to subscribers', async () => {
    const bifrost = new MockBifrostClient();
    const seen: string[] = [];
    bifrost.on('printer.error', (e) => seen.push(e.code));

    bifrost.simulate('printer.error', {
      code: 'PRINTER_OUT_OF_PAPER',
      message: 'Load media.',
      transient: true,
    });

    expect(seen).toEqual(['PRINTER_OUT_OF_PAPER']);
  });

  it('fails the next call on demand, so error paths are reachable', async () => {
    const bifrost = new MockBifrostClient();
    bifrost.failNext({ code: 'QUEUE_FULL', message: 'The queue is full.', transient: true });

    const first = await bifrost.print(doc().text('X').build());
    const second = await bifrost.print(doc().text('X').build());

    expect(first.ok).toBe(false);
    expect(second.ok).toBe(true);
  });
});

describe('createBifrostStore', () => {
  const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

  it('starts unavailable and becomes ready once the bridge answers', async () => {
    const bifrost = new MockBifrostClient({ printerState: 'READY' });
    const store = createBifrostStore(bifrost, { pollIntervalMs: 0 });

    expect(store.getSnapshot().ready).toBe(false);
    await settle();

    expect(store.getSnapshot().ready).toBe(true);
    expect(store.getSnapshot().printerName).toBe('Mock Printer');

    store.destroy();
  });

  it('follows the printer through an event, without polling for it', async () => {
    const bifrost = new MockBifrostClient();
    const store = createBifrostStore(bifrost, { pollIntervalMs: 0 });
    await settle();

    bifrost.setPrinterState('DISCONNECTED');

    expect(store.getSnapshot().printerState).toBe('DISCONNECTED');
    expect(store.getSnapshot().ready).toBe(false);

    store.destroy();
  });

  it('clears a printer fault when the printer reports itself ready again', async () => {
    const bifrost = new MockBifrostClient();
    const store = createBifrostStore(bifrost, { pollIntervalMs: 0 });
    await settle();

    bifrost.simulate('printer.error', {
      code: 'PRINTER_OUT_OF_PAPER',
      message: 'Load media.',
      transient: true,
    });
    expect(store.getSnapshot().error?.code).toBe('PRINTER_OUT_OF_PAPER');

    bifrost.setPrinterState('READY');
    expect(store.getSnapshot().error).toBeUndefined();

    store.destroy();
  });

  it('hands a new subscriber the current state immediately', async () => {
    const bifrost = new MockBifrostClient();
    const store = createBifrostStore(bifrost, { pollIntervalMs: 0 });
    await settle();

    const seen: boolean[] = [];
    const off = store.subscribe((state) => seen.push(state.ready));

    expect(seen).toEqual([true]);

    off();
    store.destroy();
  });

  it('replaces the snapshot object rather than mutating it', async () => {
    // useSyncExternalStore compares snapshots by identity; a mutated object renders nothing.
    const bifrost = new MockBifrostClient();
    const store = createBifrostStore(bifrost, { pollIntervalMs: 0 });
    await settle();

    const before = store.getSnapshot();
    bifrost.setPrinterState('ERROR');

    expect(store.getSnapshot()).not.toBe(before);
    expect(before.printerState).toBe('READY');

    store.destroy();
  });

  it('stops emitting once destroyed', async () => {
    const bifrost = new MockBifrostClient();
    const store = createBifrostStore(bifrost, { pollIntervalMs: 0 });
    await settle();

    const seen: unknown[] = [];
    store.subscribe(() => seen.push(1));
    store.destroy();

    bifrost.setPrinterState('DISCONNECTED');
    expect(seen).toHaveLength(1); // the initial call only
  });
});
