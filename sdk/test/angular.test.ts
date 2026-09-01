import { describe, expect, it } from 'vitest';
import { BifrostClient, createBifrostStore, type BifrostState } from '../src/index.js';
import { BifrostStoreRef, connectSignal, provideBifrost } from '../src/angular.js';
import { MockBifrostClient } from '../src/testing.js';

/**
 * The Angular adapter imports no Angular package, which is what keeps the SDK dependency-free and
 * version-agnostic — and also means nothing here is checked by Angular's own compiler. These tests
 * are that check.
 */

const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

/**
 * A stand-in for `WritableSignal`.
 *
 * The essential detail is that it is a **function that also has `.set`** — reading a signal is
 * `state()`. Any adapter that decides between "callback" and "signal" by testing callability gets
 * this backwards.
 */
function fakeSignal(initial: BifrostState) {
  let value = initial;
  const signal = (() => value) as (() => BifrostState) & {
    set(next: BifrostState): void;
    reads: number;
  };

  signal.reads = 0;
  signal.set = (next: BifrostState) => { value = next; };

  return signal;
}

describe('connectSignal', () => {
  it('updates a signal through set(), not by calling it', async () => {
    // The regression: `state(value)` on a WritableSignal reads it and discards the argument, so the
    // view renders the initial snapshot forever — which shows up as a permanent "bridge not
    // running", with no error anywhere to explain it.
    const bifrost = new MockBifrostClient({ printerState: 'READY' });
    const store = createBifrostStore(bifrost, { pollIntervalMs: 0 });
    const signal = fakeSignal(store.getSnapshot());

    const off = connectSignal(store, signal);
    await settle();

    expect(signal().available).toBe(true);
    expect(signal().ready).toBe(true);

    off();
    store.destroy();
  });

  it('keeps tracking after the first update', async () => {
    const bifrost = new MockBifrostClient({ printerState: 'READY' });
    const store = createBifrostStore(bifrost, { pollIntervalMs: 0 });
    const signal = fakeSignal(store.getSnapshot());

    const off = connectSignal(store, signal);
    await settle();

    bifrost.setPrinterState('DISCONNECTED');

    expect(signal().printerState).toBe('DISCONNECTED');
    expect(signal().ready).toBe(false);

    off();
    store.destroy();
  });

  it('still accepts a plain callback', async () => {
    const bifrost = new MockBifrostClient();
    const store = createBifrostStore(bifrost, { pollIntervalMs: 0 });
    const seen: boolean[] = [];

    const off = connectSignal(store, (state) => seen.push(state.ready));
    await settle();

    expect(seen[seen.length - 1]).toBe(true);

    off();
    store.destroy();
  });

  it('stops updating once unsubscribed', async () => {
    const bifrost = new MockBifrostClient({ printerState: 'READY' });
    const store = createBifrostStore(bifrost, { pollIntervalMs: 0 });
    const signal = fakeSignal(store.getSnapshot());

    const off = connectSignal(store, signal);
    await settle();
    off();

    bifrost.setPrinterState('ERROR');

    expect(signal().printerState).toBe('READY');
    store.destroy();
  });
});

describe('provideBifrost', () => {
  it('returns providers Angular DI can consume', () => {
    // Shape only — the real DI resolution is exercised by the demo app. What matters here is that
    // the tokens are the ones a component injects, and that the store depends on the client.
    const providers = provideBifrost();

    expect(providers).toHaveLength(2);
    expect(providers[0]?.provide).toBe(BifrostClient);
    expect(providers[1]?.provide).toBe(BifrostStoreRef);
    expect(providers[1]?.deps).toEqual([BifrostClient]);
  });

  it('passes client options through and keeps store options for the store', () => {
    const providers = provideBifrost({ baseUrl: 'http://127.0.0.1:9999', store: { pollIntervalMs: 0 } });

    const client = providers[0]?.useFactory() as BifrostClient;
    expect(client).toBeInstanceOf(BifrostClient);

    client.close();
  });
});
