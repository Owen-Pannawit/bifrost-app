/**
 * `@bearing/bifrost-sdk/react` — hooks over the framework-agnostic core.
 *
 * React is a peer dependency, not a dependency: this entry point is the only file that imports it,
 * so an Angular or vanilla application never pulls React in.
 */

import {
  createContext,
  createElement,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
  useSyncExternalStore,
  type ReactNode,
} from 'react';

import { BifrostClient, type BifrostOptions } from './client.js';
import type { BifrostError, Result } from './errors.js';
import { createBifrostStore, type BifrostState, type BifrostStore } from './store.js';
import type {
  BifrostEventMap,
  BifrostEventName,
  IBifrostClient,
  Job,
  PrintCallOptions,
  PrintPayload,
} from './types.js';

// ---------------------------------------------------------------- provider

const BifrostContext = createContext<IBifrostClient | null>(null);

export interface BifrostProviderProps {
  /** Supply a client — a `MockBifrostClient` in tests — or let the provider build one. */
  client?: IBifrostClient;
  options?: BifrostOptions;
  children?: ReactNode;
}

/**
 * Shares one client across the tree.
 *
 * One client per application, not per component: the event socket is a single connection, and the
 * bridge closes the oldest when a sixth arrives (DES-03 §6).
 *
 * @example
 * createRoot(el).render(
 *   <BifrostProvider>
 *     <App />
 *   </BifrostProvider>,
 * );
 */
export function BifrostProvider({ client, options, children }: BifrostProviderProps) {
  const owned = useRef<BifrostClient | null>(null);

  const value = useMemo<IBifrostClient>(() => {
    if (client) return client;
    owned.current ??= new BifrostClient(options);
    return owned.current;
    // The options object is usually a fresh literal each render; rebuilding the client on every
    // one of them would reconnect the socket continuously. Changes after mount are ignored by
    // design — construct a client yourself and pass it if it has to vary.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [client]);

  useEffect(() => () => owned.current?.close(), []);

  return createElement(BifrostContext.Provider, { value }, children);
}

/**
 * The client from the nearest {@link BifrostProvider}.
 *
 * Falls back to a lazily-created default client when there is no provider, so a single Print button
 * needs no setup at all.
 */
export function useBifrost(options?: BifrostOptions): IBifrostClient {
  const fromContext = useContext(BifrostContext);
  const fallback = useRef<BifrostClient | null>(null);

  if (fromContext) return fromContext;
  fallback.current ??= new BifrostClient(options);
  return fallback.current;
}

// ---------------------------------------------------------------- state

/**
 * Live bridge and printer state, kept current by the event socket.
 *
 * @example
 * const { ready, printerState, error } = useBifrostState();
 * <button disabled={!ready}>Print</button>
 * {!ready && <p>{error?.message ?? 'Open BifrǫstApp on this device.'}</p>}
 */
export function useBifrostState(client?: IBifrostClient): BifrostState {
  const resolved = useBifrost();
  const target = client ?? resolved;

  const store: BifrostStore = useMemo(() => createBifrostStore(target), [target]);
  useEffect(() => () => store.destroy(), [store]);

  return useSyncExternalStore(store.subscribe, store.getSnapshot, store.getSnapshot);
}

/**
 * Subscribe to one event for the life of the component.
 *
 * The handler is held in a ref, so an inline arrow function does not resubscribe on every render.
 */
export function useBifrostEvent<K extends BifrostEventName>(
  event: K,
  handler: (data: BifrostEventMap[K]) => void,
  client?: IBifrostClient,
): void {
  const resolved = useBifrost();
  const target = client ?? resolved;

  const latest = useRef(handler);
  latest.current = handler;

  useEffect(
    () => target.on(event, (data) => latest.current(data)),
    [target, event],
  );
}

// ---------------------------------------------------------------- printing

export interface UsePrintResult {
  print: (payload: PrintPayload, options?: PrintCallOptions) => Promise<Result<Job>>;
  /** True while a submission is in flight. Bind a button's `disabled` to it. */
  printing: boolean;
  /** The last failure, already carrying an operator-safe message. */
  error?: BifrostError;
  /** The last job that printed. */
  job?: Job;
  reset: () => void;
}

/**
 * A print call with the state a button needs around it.
 *
 * @example
 * const { print, printing, error } = usePrint();
 *
 * <button disabled={printing} onClick={() => print(doc().text('6205-2RS').feed(3).build())}>
 *   {printing ? 'Printing…' : 'Print label'}
 * </button>
 * {error && <p role="alert">{error.message}</p>}
 */
export function usePrint(client?: IBifrostClient): UsePrintResult {
  const resolved = useBifrost();
  const target = client ?? resolved;

  const [printing, setPrinting] = useState(false);
  const [error, setError] = useState<BifrostError | undefined>();
  const [job, setJob] = useState<Job | undefined>();
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    return () => { mounted.current = false; };
  }, []);

  const print = useCallback(
    async (payload: PrintPayload, options?: PrintCallOptions): Promise<Result<Job>> => {
      setPrinting(true);
      setError(undefined);

      const result = await target.print(payload, options);

      // The component may have unmounted while the label printed; setting state then is a React
      // warning about a bug that is not there.
      if (mounted.current) {
        setPrinting(false);
        if (result.ok) setJob(result.value);
        else setError(result.error);
      }

      return result;
    },
    [target],
  );

  const reset = useCallback(() => {
    setError(undefined);
    setJob(undefined);
  }, []);

  return { print, printing, error, job, reset };
}
