/**
 * The live half of the API — `WS /v1/events` (DES-03 §3.10, FR-203, FR-707).
 *
 * A warehouse page cannot poll its way to a good experience: the operator needs the Print button to
 * grey out the moment the roll runs out, not up to a poll interval later. This keeps a socket open
 * and, more importantly, keeps it open across the reconnects a device on Wi-Fi will produce.
 */

import type { BifrostEventEnvelope, BifrostEventMap, BifrostEventName, Unsubscribe } from './types.js';

type AnyHandler = (data: never) => void;

export interface EventStreamConfig {
  /**
   * Resolved per connection attempt. Returns `null` when there is no token yet, which is a reason
   * to stay disconnected rather than an error — the page may pair later.
   */
  url: () => string | null;
  /** Backoff schedule. The last entry repeats for as long as the bridge stays away. */
  reconnectDelaysMs?: readonly number[];
  /** Injectable for tests; defaults to the global. */
  webSocket?: typeof WebSocket;
}

const DEFAULT_BACKOFF = [500, 1_000, 2_000, 5_000, 10_000, 30_000] as const;

export class EventStream {
  private readonly handlers = new Map<string, Set<AnyHandler>>();
  private readonly backoff: readonly number[];

  private socket: WebSocket | null = null;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private attempt = 0;
  private closed = false;
  private connectedFlag = false;

  constructor(private readonly config: EventStreamConfig) {
    this.backoff = config.reconnectDelaysMs ?? DEFAULT_BACKOFF;
  }

  get connected(): boolean {
    return this.connectedFlag;
  }

  /**
   * Subscribe. The socket opens on the first subscription and stays open; the returned function is
   * the whole cleanup contract, which is all any framework's teardown needs (DES-04 §9).
   */
  on<K extends BifrostEventName>(event: K, handler: (data: BifrostEventMap[K]) => void): Unsubscribe {
    let set = this.handlers.get(event);
    if (!set) {
      set = new Set();
      this.handlers.set(event, set);
    }
    set.add(handler as AnyHandler);

    this.connect();

    let live = true;
    return () => {
      if (!live) return;
      live = false;
      set.delete(handler as AnyHandler);
    };
  }

  /** Open the socket if it is not already open. Safe to call repeatedly. */
  connect(): void {
    this.closed = false;
    if (this.socket || this.timer) return;

    const WebSocketImpl = this.config.webSocket ?? globalThis.WebSocket;
    if (!WebSocketImpl) return; // No socket in this host — HTTP still works, so this is not fatal.

    const url = this.config.url();
    if (!url) return; // Unpaired. A later connect() call will find a token.

    let socket: WebSocket;
    try {
      socket = new WebSocketImpl(url);
    } catch {
      this.scheduleReconnect();
      return;
    }

    this.socket = socket;

    socket.onopen = () => {
      this.attempt = 0;
      this.setConnected(true);
    };

    socket.onmessage = (message: MessageEvent) => this.dispatch(message.data);

    socket.onerror = () => {
      // Chrome fires error then close. Closing here would double-schedule the reconnect.
    };

    socket.onclose = () => {
      this.socket = null;
      this.setConnected(false);
      if (!this.closed) this.scheduleReconnect();
    };
  }

  /** Close for good. No reconnect follows; call {@link connect} to start again. */
  close(): void {
    this.closed = true;

    if (this.timer !== null) {
      clearTimeout(this.timer);
      this.timer = null;
    }

    const socket = this.socket;
    this.socket = null;

    if (socket) {
      socket.onclose = null;
      socket.onerror = null;
      socket.onmessage = null;
      socket.onopen = null;
      try {
        socket.close();
      } catch {
        // Already closing. Nothing left to do.
      }
    }

    this.setConnected(false);
  }

  /** Drop and rebuild the connection — used after pairing supplies a token. */
  reconnect(): void {
    this.close();
    this.attempt = 0;
    this.connect();
  }

  /** Deliver an event to subscribers as though the bridge had sent it. Used by the mock client. */
  emit<K extends BifrostEventName>(event: K, data: BifrostEventMap[K]): void {
    const set = this.handlers.get(event);
    if (!set) return;

    for (const handler of Array.from(set)) {
      try {
        (handler as (d: BifrostEventMap[K]) => void)(data);
      } catch (e) {
        // One page's broken handler must not stop the other subscribers, or a UI bug in a status
        // badge silently stops the job list updating too.
        console.error('[bifrost] event handler threw', e);
      }
    }
  }

  private dispatch(raw: unknown): void {
    if (typeof raw !== 'string') return;

    let envelope: BifrostEventEnvelope;
    try {
      envelope = JSON.parse(raw) as BifrostEventEnvelope;
    } catch {
      return;
    }

    // Unknown event types are ignored rather than logged as errors — DES-03 §8 makes adding one a
    // compatible change, so an older SDK meeting a newer bridge must stay quiet about it.
    if (!envelope || typeof envelope.event !== 'string') return;

    this.emit(envelope.event, envelope.data as never);
  }

  private setConnected(connected: boolean): void {
    if (this.connectedFlag === connected) return;
    this.connectedFlag = connected;
    this.emit('connection.changed', { connected });
  }

  private scheduleReconnect(): void {
    if (this.timer !== null) return;

    const index = Math.min(this.attempt, this.backoff.length - 1);
    const delay = this.backoff[index] ?? 30_000;
    this.attempt++;

    this.timer = setTimeout(() => {
      this.timer = null;
      if (!this.closed) this.connect();
    }, delay);
  }
}
