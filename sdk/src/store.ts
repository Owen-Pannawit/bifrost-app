/**
 * A framework-neutral reactive view of the bridge.
 *
 * Every UI framework has its own idea of state, and none of them agree. What they all accept is a
 * `subscribe(listener) => unsubscribe` pair with a synchronous snapshot: React's
 * `useSyncExternalStore` takes it directly, RxJS wraps it through the interop symbol, and anything
 * else can call it by hand. So that is the whole contract, and the adapters stay thin.
 */

import type { BifrostError } from './errors.js';
import { lastErrorMessage, type BridgeStatus, type IBifrostClient, type PrinterState, type QueueStatus, type Unsubscribe } from './types.js';

export interface BifrostState {
  /** `false` until the first status call succeeds. Drives "open the app" banners. */
  available: boolean;
  /** `undefined` until the first status call resolves either way. */
  status?: BridgeStatus;
  printerState: PrinterState;
  printerName?: string;
  queue?: QueueStatus;
  batteryPercent?: number;
  /** The last printer fault reported, cleared when the printer returns to `READY`. */
  error?: BifrostError;
  /** Whether the event socket is up. `false` does not mean the bridge is down. */
  connected: boolean;
  /** True while the printer can accept work — the condition a Print button should bind to. */
  ready: boolean;
}

export interface BifrostStore {
  getSnapshot(): BifrostState;
  /** The listener is called immediately with the current state, then on every change. */
  subscribe(listener: Listener): Unsubscribe & { unsubscribe: Unsubscribe };
  /** Re-read `GET /v1/status`. Called on construction and after a reconnect. */
  refresh(): Promise<void>;
  /** Stop polling and release the event subscriptions. */
  destroy(): void;
}

type Listener = ((state: BifrostState) => void) | { next?: (state: BifrostState) => void };

export interface BifrostStoreOptions {
  /**
   * Poll `GET /v1/status` on this interval as a backstop, in ms. Default 15000; `0` disables it.
   * The socket carries the changes that matter — this only catches a bridge that restarted while
   * the socket was down.
   */
  pollIntervalMs?: number;
}

const INITIAL: BifrostState = {
  available: false,
  printerState: 'DISCONNECTED',
  connected: false,
  ready: false,
};

/**
 * @example React
 * const store = useMemo(() => createBifrostStore(client), [client]);
 * const state = useSyncExternalStore(store.subscribe, store.getSnapshot);
 *
 * @example Angular
 * readonly state = toSignal(from(createBifrostStore(client)), { initialValue: … });
 */
export function createBifrostStore(
  client: IBifrostClient,
  options: BifrostStoreOptions = {},
): BifrostStore {
  const listeners = new Set<(state: BifrostState) => void>();
  const subscriptions: Unsubscribe[] = [];

  // Replaced wholesale rather than mutated: useSyncExternalStore compares snapshots by identity,
  // and a mutated object would render nothing at all.
  let state: BifrostState = INITIAL;
  let destroyed = false;

  const emit = (next: Partial<BifrostState>) => {
    const merged: BifrostState = { ...state, ...next };
    merged.ready = merged.available && merged.printerState === 'READY';

    if (shallowEqual(state, merged)) return;
    state = merged;
    for (const listener of Array.from(listeners)) listener(state);
  };

  const refresh = async (): Promise<void> => {
    if (destroyed) return;

    const r = await client.getStatus();
    if (destroyed) return;

    if (!r.ok) {
      emit({ available: false, printerState: 'DISCONNECTED', error: r.error });
      return;
    }

    const printer = r.value.printer;
    const message = lastErrorMessage(printer);

    emit({
      available: true,
      status: r.value,
      printerState: printer?.state ?? 'NOT_CONFIGURED',
      printerName: printer?.name,
      batteryPercent: printer?.batteryPercent,
      queue: r.value.queue,
      error:
        printer?.state === 'ERROR' && message
          ? { code: 'INTERNAL_ERROR', message, transient: true }
          : undefined,
    });
  };

  subscriptions.push(
    client.on('printer.state_changed', (e) =>
      emit({
        printerState: e.state,
        printerName: e.name ?? state.printerName,
        // A printer that reports READY has cleared whatever was wrong with it.
        error: e.state === 'READY' ? undefined : state.error,
      })),

    client.on('printer.error', (e) =>
      emit({ error: { code: e.code as BifrostError['code'], message: e.message, transient: e.transient } })),

    client.on('printer.battery', (e) => emit({ batteryPercent: e.percent })),

    client.on('queue.changed', (e) => emit({ queue: { pending: e.pending, retrying: e.retrying } })),

    client.on('connection.changed', (e) => {
      emit({ connected: e.connected });
      // A socket that just came back may have missed a state change while it was away.
      if (e.connected) void refresh();
    }),

    client.on('bridge.shutdown', () => emit({ available: false, printerState: 'DISCONNECTED' })),
  );

  const interval = options.pollIntervalMs ?? 15_000;
  const timer = interval > 0 ? setInterval(() => void refresh(), interval) : null;

  void refresh();

  return {
    getSnapshot: () => state,

    subscribe(listener: Listener) {
      const notify = typeof listener === 'function' ? listener : listener.next?.bind(listener);
      if (!notify) return asUnsubscribe(() => undefined);

      listeners.add(notify);
      notify(state);

      return asUnsubscribe(() => listeners.delete(notify));
    },

    refresh,

    destroy() {
      destroyed = true;
      if (timer !== null) clearInterval(timer);
      for (const off of subscriptions) off();
      listeners.clear();
    },

    // RxJS — and therefore Angular's `toSignal(from(store))` — finds a subscribable this way.
    [observableSymbol]() {
      return this;
    },
  } as BifrostStore;
}

/**
 * Returned unsubscribers are callable *and* carry `.unsubscribe()`.
 *
 * React expects a function; RxJS expects an object. Being both is what lets one store serve both
 * without an adapter that exists only to change a shape.
 */
function asUnsubscribe(fn: Unsubscribe): Unsubscribe & { unsubscribe: Unsubscribe } {
  const wrapped = fn as Unsubscribe & { unsubscribe: Unsubscribe };
  wrapped.unsubscribe = fn;
  return wrapped;
}

const observableSymbol: symbol =
  (typeof Symbol === 'function' && (Symbol as unknown as { observable?: symbol }).observable) ||
  ('@@observable' as unknown as symbol);

function shallowEqual(a: BifrostState, b: BifrostState): boolean {
  const keys = Object.keys({ ...a, ...b }) as Array<keyof BifrostState>;
  return keys.every((key) => Object.is(a[key], b[key]));
}
