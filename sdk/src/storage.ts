/**
 * Token persistence — DES-04 §8.
 *
 * `localStorage` is scoped to the browser origin, which is already the unit the bridge's allowlist
 * works in. The token authorises printing on one device from one allowlisted origin; it grants no
 * data access and no lateral movement (DES-08 §6).
 */

export interface TokenStore {
  get(): string | undefined;
  set(token: string): void;
  clear(): void;
}

/**
 * Every access is guarded. `localStorage` throws rather than returns in two ordinary cases —
 * Safari private browsing, and any non-browser host such as a server-side render — and a print
 * button that explodes because storage is unavailable would be a worse defect than an unsaved
 * token.
 */
export function createTokenStore(key: string): TokenStore {
  const memory = { value: undefined as string | undefined };

  const storage = (): Storage | undefined => {
    try {
      return globalThis.localStorage ?? undefined;
    } catch {
      return undefined;
    }
  };

  return {
    get() {
      try {
        return storage()?.getItem(key) ?? memory.value;
      } catch {
        return memory.value;
      }
    },
    set(token: string) {
      memory.value = token;
      try {
        storage()?.setItem(key, token);
      } catch {
        // Held in memory for the life of the page instead. Better than failing the pairing.
      }
    },
    clear() {
      memory.value = undefined;
      try {
        storage()?.removeItem(key);
      } catch {
        // Nothing to do — the in-memory copy is already gone.
      }
    },
  };
}

/** A store for hosts with no browser storage, and for tests. */
export function createMemoryTokenStore(initial?: string): TokenStore {
  let value = initial;
  return {
    get: () => value,
    set: (token) => { value = token; },
    clear: () => { value = undefined; },
  };
}
