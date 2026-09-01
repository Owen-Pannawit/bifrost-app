import { afterEach, describe, expect, it, vi } from 'vitest';
import { EventStream } from '../src/index.js';

/**
 * The socket's real job is not to deliver the first message — it is to still be delivering messages
 * after the device has walked out of Wi-Fi range and back. That is what most of this exercises.
 */

class FakeSocket {
  static instances: FakeSocket[] = [];

  onopen: (() => void) | null = null;
  onclose: (() => void) | null = null;
  onerror: (() => void) | null = null;
  onmessage: ((event: { data: unknown }) => void) | null = null;
  closed = false;

  constructor(readonly url: string) {
    FakeSocket.instances.push(this);
  }

  close(): void {
    this.closed = true;
  }

  open(): void {
    this.onopen?.();
  }

  send(event: string, data: unknown): void {
    this.onmessage?.({ data: JSON.stringify({ event, timestamp: '2026-08-22T09:00:00Z', data }) });
  }

  drop(): void {
    this.onclose?.();
  }

  static get last(): FakeSocket {
    const socket = FakeSocket.instances[FakeSocket.instances.length - 1];
    if (!socket) throw new Error('no socket was opened');
    return socket;
  }

  static reset(): void {
    FakeSocket.instances = [];
  }
}

const streamOf = (url: string | null = 'ws://127.0.0.1:8437/v1/events?token=t') =>
  new EventStream({
    url: () => url,
    reconnectDelaysMs: [10],
    webSocket: FakeSocket as unknown as typeof WebSocket,
  });

afterEach(() => {
  FakeSocket.reset();
  vi.useRealTimers();
});

describe('EventStream', () => {
  it('opens on the first subscription, not on construction', () => {
    // FR-707: the socket is lazy. A page that only prints should not hold a connection open, and
    // the bridge only accepts five (DES-03 §6).
    const stream = streamOf();
    expect(FakeSocket.instances).toHaveLength(0);

    stream.on('printer.error', () => undefined);
    expect(FakeSocket.instances).toHaveLength(1);

    stream.close();
  });

  it('stays disconnected while there is no token, without failing', () => {
    // Unpaired is a normal state, not an error. A later connect() finds the token.
    const stream = streamOf(null);
    stream.on('printer.error', () => undefined);

    expect(FakeSocket.instances).toHaveLength(0);
    stream.close();
  });

  it('delivers a decoded event to its subscriber', () => {
    const stream = streamOf();
    const seen: unknown[] = [];
    stream.on('printer.state_changed', (data) => seen.push(data));

    FakeSocket.last.open();
    FakeSocket.last.send('printer.state_changed', { state: 'READY', name: 'ZQ521' });

    expect(seen).toEqual([{ state: 'READY', name: 'ZQ521' }]);
    stream.close();
  });

  it('synthesises connection.changed from the socket itself', () => {
    // The bridge cannot tell a page that the page lost the connection. Only the SDK can.
    const stream = streamOf();
    const seen: boolean[] = [];
    stream.on('connection.changed', ({ connected }) => seen.push(connected));

    FakeSocket.last.open();
    FakeSocket.last.drop();

    expect(seen).toEqual([true, false]);
    stream.close();
  });

  it('ignores an event type it has never heard of', () => {
    // DES-03 §8 makes adding an event a compatible change, so an older SDK must stay quiet when a
    // newer bridge sends one.
    const stream = streamOf();
    const seen: unknown[] = [];
    stream.on('printer.error', (d) => seen.push(d));

    FakeSocket.last.open();
    expect(() => FakeSocket.last.send('printer.telepathy', { mood: 'fine' })).not.toThrow();
    expect(seen).toHaveLength(0);

    stream.close();
  });

  it('survives a message that is not JSON', () => {
    const stream = streamOf();
    stream.on('printer.error', () => undefined);
    FakeSocket.last.open();

    expect(() => FakeSocket.last.onmessage?.({ data: 'not json' })).not.toThrow();
    stream.close();
  });

  it('reconnects after the bridge goes away', () => {
    vi.useFakeTimers();

    const stream = streamOf();
    stream.on('printer.error', () => undefined);
    FakeSocket.last.open();

    FakeSocket.last.drop();
    expect(FakeSocket.instances).toHaveLength(1);

    vi.advanceTimersByTime(10);
    expect(FakeSocket.instances).toHaveLength(2);

    stream.close();
  });

  it('does not reconnect after an explicit close', () => {
    // Otherwise a component that unmounts leaves a socket reopening behind it forever.
    vi.useFakeTimers();

    const stream = streamOf();
    stream.on('printer.error', () => undefined);
    FakeSocket.last.open();

    stream.close();
    vi.advanceTimersByTime(1_000);

    expect(FakeSocket.instances).toHaveLength(1);
  });

  it('stops delivering to a handler that unsubscribed', () => {
    const stream = streamOf();
    const seen: unknown[] = [];
    const off = stream.on('printer.battery', (d) => seen.push(d));

    FakeSocket.last.open();
    FakeSocket.last.send('printer.battery', { percent: 62 });
    off();
    FakeSocket.last.send('printer.battery', { percent: 61 });

    expect(seen).toEqual([{ percent: 62 }]);
    stream.close();
  });

  it('keeps delivering to the other handlers when one of them throws', () => {
    // A broken status badge must not stop the job list updating.
    const stream = streamOf();
    const seen: unknown[] = [];

    stream.on('queue.changed', () => { throw new Error('badge is broken'); });
    stream.on('queue.changed', (d) => seen.push(d));

    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    FakeSocket.last.open();
    FakeSocket.last.send('queue.changed', { pending: 2, retrying: 0 });

    expect(seen).toEqual([{ pending: 2, retrying: 0 }]);
    stream.close();
  });
});
