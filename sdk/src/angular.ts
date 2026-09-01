/**
 * `@bearing/bifrost-sdk/angular` — dependency injection wiring.
 *
 * This file deliberately does not import `@angular/core`.
 *
 * An adapter that imported it would pin the SDK to one Angular major and drag `rxjs` and `zone.js`
 * into every consumer's tree, including the ones on React. Angular's DI needs neither: a factory
 * provider is a plain object, and a class is a valid token without a decorator. So the SDK stays
 * dependency-free (FR-703) and works from Angular 14 through 20 alike.
 *
 * @example app.config.ts
 * import { provideBifrost } from '@bearing/bifrost-sdk/angular';
 *
 * export const appConfig: ApplicationConfig = {
 *   providers: [provideBifrost({ waitForCompletion: true })],
 * };
 *
 * @example a component
 * export class LabelComponent {
 *   private readonly bifrost = inject(BifrostClient);
 *   private readonly store = inject(BifrostStoreRef);
 *
 *   readonly state = signal(this.store.getSnapshot());
 *
 *   constructor() {
 *     const off = this.store.subscribe(s => this.state.set(s));
 *     inject(DestroyRef).onDestroy(off);
 *   }
 *
 *   async print() {
 *     const r = await this.bifrost.print(doc().text('6205-2RS').feed(3).build());
 *     if (!r.ok) this.toast(r.error.message);
 *   }
 * }
 */

import { BifrostClient, type BifrostOptions } from './client.js';
import { createBifrostStore, type BifrostState, type BifrostStore, type BifrostStoreOptions } from './store.js';
import type { Unsubscribe } from './types.js';

/**
 * DI token for the reactive store.
 *
 * An abstract class rather than an `InjectionToken` because creating one of those would need
 * `@angular/core`. Angular treats any class as a token, so `inject(BifrostStoreRef)` works exactly
 * the same and stays typed.
 */
export abstract class BifrostStoreRef implements BifrostStore {
  abstract getSnapshot(): BifrostState;
  abstract subscribe(listener: (state: BifrostState) => void): Unsubscribe & { unsubscribe: Unsubscribe };
  abstract refresh(): Promise<void>;
  abstract destroy(): void;
}

/** The shape Angular's `Provider` union accepts, described without importing it. */
interface FactoryProviderLike {
  provide: unknown;
  useFactory: (...args: never[]) => unknown;
  deps?: unknown[];
}

export interface ProvideBifrostOptions extends BifrostOptions {
  /** Passed to {@link createBifrostStore}. */
  store?: BifrostStoreOptions;
}

/**
 * Providers for `bootstrapApplication` or an `NgModule`.
 *
 * Both `BifrostClient` and {@link BifrostStoreRef} become injectable, application-wide singletons —
 * which is what you want, because the event socket is one connection and the bridge accepts five
 * (DES-03 §6).
 */
export function provideBifrost(options: ProvideBifrostOptions = {}): FactoryProviderLike[] {
  const { store: storeOptions, ...clientOptions } = options;

  return [
    {
      provide: BifrostClient,
      useFactory: () => new BifrostClient(clientOptions),
    },
    {
      provide: BifrostStoreRef,
      useFactory: (client: BifrostClient) =>
        createBifrostStore(client, storeOptions ?? {}) as BifrostStoreRef,
      deps: [BifrostClient],
    },
  ];
}

/**
 * Bridges the store into a `WritableSignal`-shaped setter.
 *
 * Typed structurally so it compiles against any Angular version, and against a plain function in a
 * test.
 *
 * @example
 * readonly state = signal(store.getSnapshot());
 * constructor() {
 *   inject(DestroyRef).onDestroy(connectSignal(this.store, this.state));
 * }
 */
export function connectSignal(
  store: BifrostStore,
  signal: WritableSignalLike | ((value: BifrostState) => void),
): Unsubscribe {
  // `set` is tested BEFORE callability, and the order is the whole correctness of this function.
  //
  // An Angular WritableSignal is *both*: it is a function — `state()` reads it — and it carries
  // `.set`. Checking `typeof signal === 'function'` first therefore matches every signal, and the
  // update becomes `state(value)`, which reads the signal and discards the argument. No error, no
  // warning, and a view that silently never changes. That bug reads on screen as "the bridge is
  // not running", because the initial snapshot is the one the template keeps rendering.
  const set =
    typeof (signal as WritableSignalLike).set === 'function'
      ? (value: BifrostState) => (signal as WritableSignalLike).set(value)
      : (signal as (value: BifrostState) => void);

  return store.subscribe(set);
}

/** Structural stand-in for Angular's `WritableSignal<BifrostState>`, so no import is needed. */
interface WritableSignalLike {
  set(value: BifrostState): void;
}

export { BifrostClient, createBifrostStore };
export type { BifrostOptions, BifrostState, BifrostStore, BifrostStoreOptions };
