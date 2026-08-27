/**
 * The one place that talks to the network.
 *
 * Everything here exists to turn the three ways an HTTP call can disappoint — no answer, a slow
 * answer, an error answer — into the single {@link Result} shape the rest of the SDK returns.
 */

import { type BifrostError, type Result, SdkErrors, fail, ok } from './errors.js';

export interface HttpConfig {
  baseUrl: string;
  timeoutMs: number;
  /** Looked up per request, so a `pair()` mid-session takes effect without rebuilding the client. */
  token: () => string | undefined;
  /** Called on a 401 so the caller can drop a token the bridge no longer accepts (DES-04 §8). */
  onUnauthorized: () => void;
  /** Backoff between SDK-level retries. Length also sets the retry count. */
  retryDelaysMs: readonly number[];
}

export interface RequestSpec {
  method: 'GET' | 'POST';
  path: string;
  body?: unknown;
  headers?: Record<string, string>;
  signal?: AbortSignal;
  /**
   * Retry network-level failures with backoff. Reserved for calls that carry an idempotency key —
   * retrying anything else risks printing twice (DES-04 §7).
   */
  retryOnNetworkFailure?: boolean;
}

export async function request<T>(config: HttpConfig, spec: RequestSpec): Promise<Result<T>> {
  const attempts = spec.retryOnNetworkFailure ? config.retryDelaysMs.length + 1 : 1;
  let last: BifrostError = SdkErrors.unavailable();

  for (let attempt = 0; attempt < attempts; attempt++) {
    if (attempt > 0) {
      await sleep(config.retryDelaysMs[attempt - 1] ?? 0);
      if (spec.signal?.aborted) return fail(SdkErrors.aborted());
    }

    const outcome = await attemptOnce<T>(config, spec);

    // A reply of any kind — success or a 4xx/5xx envelope — settles it. The bridge answered, so
    // the answer is the truth; only silence is worth asking twice about.
    if (outcome.ok || !outcome.retryable) return outcome.result;
    last = outcome.error;
  }

  return fail(last);
}

type Attempt<T> =
  | { ok: true; result: Result<T> }
  | { ok: false; retryable: false; result: Result<T>; error: BifrostError }
  | { ok: false; retryable: true; result: Result<T>; error: BifrostError };

async function attemptOnce<T>(config: HttpConfig, spec: RequestSpec): Promise<Attempt<T>> {
  const controller = new AbortController();

  // AbortSignal.any is Chrome 116+; the SDK supports 90+ (NFR-403), so the two signals are linked
  // by hand. The flag is what keeps "the caller changed its mind" distinct from "the bridge is
  // wedged" — different problems, different operator messages.
  let timedOut = false;
  const timer = setTimeout(() => {
    timedOut = true;
    controller.abort();
  }, config.timeoutMs);

  const onCallerAbort = () => controller.abort();
  spec.signal?.addEventListener('abort', onCallerAbort);

  try {
    if (spec.signal?.aborted) {
      const error = SdkErrors.aborted();
      return { ok: false, retryable: false, result: fail(error), error };
    }

    const token = config.token();
    const headers: Record<string, string> = { ...spec.headers };
    if (spec.body !== undefined) headers['Content-Type'] = 'application/json';
    if (token) headers['Authorization'] = `Bearer ${token}`;

    const response = await fetch(config.baseUrl + spec.path, {
      method: spec.method,
      headers,
      body: spec.body === undefined ? undefined : JSON.stringify(spec.body),
      signal: controller.signal,
    });

    const parsed = await readBody(response);

    if (!response.ok) {
      if (response.status === 401) config.onUnauthorized();
      const error = errorFrom(parsed, response.status);
      return { ok: false, retryable: false, result: fail(error), error };
    }

    return { ok: true, result: ok(parsed as T) };
  } catch (e) {
    const aborted = isAbortError(e);
    const error = aborted
      ? (timedOut ? SdkErrors.timeout() : SdkErrors.aborted())
      : SdkErrors.unavailable();

    // A caller-cancelled request must not be retried: the caller already said stop.
    const retryable = !aborted || timedOut;
    return retryable
      ? { ok: false, retryable: true, result: fail(error), error }
      : { ok: false, retryable: false, result: fail(error), error };
  } finally {
    clearTimeout(timer);
    spec.signal?.removeEventListener('abort', onCallerAbort);
  }
}

/**
 * A bridge that answers with an HTML error page must not take the calling page down with it, so a
 * body that will not parse is treated as an absent one.
 */
async function readBody(response: Response): Promise<unknown> {
  let text = '';
  try {
    text = await response.text();
  } catch {
    return {};
  }

  if (!text) return {};

  try {
    return JSON.parse(text) as unknown;
  } catch {
    return {};
  }
}

function errorFrom(parsed: unknown, status: number): BifrostError {
  const envelope = (parsed as { error?: Partial<BifrostError> } | null)?.error;

  if (envelope && typeof envelope.code === 'string' && typeof envelope.message === 'string') {
    // Passed through verbatim. DES-09 §9 tells the web developer to show `message` as-is, which
    // only holds if the SDK never rewrites it — two vocabularies for one fault is how a support
    // call becomes two tickets.
    return {
      code: envelope.code as BifrostError['code'],
      message: envelope.message,
      transient: envelope.transient ?? status >= 500,
      ...(envelope.field !== undefined ? { field: envelope.field } : {}),
      ...(envelope.details !== undefined ? { details: envelope.details } : {}),
    };
  }

  return SdkErrors.unrecognised(status);
}

function isAbortError(e: unknown): boolean {
  return (
    (typeof DOMException !== 'undefined' && e instanceof DOMException && e.name === 'AbortError') ||
    (e instanceof Error && e.name === 'AbortError')
  );
}

export function sleep(ms: number): Promise<void> {
  return ms <= 0 ? Promise.resolve() : new Promise((resolve) => setTimeout(resolve, ms));
}

/** RFC 4122 v4. `crypto.randomUUID` where it exists, which is every browser the SDK targets. */
export function uuid(): string {
  const c = globalThis.crypto;
  if (c && typeof c.randomUUID === 'function') return c.randomUUID();

  if (c && typeof c.getRandomValues === 'function') {
    const bytes = c.getRandomValues(new Uint8Array(16));
    bytes[6] = ((bytes[6] ?? 0) & 0x0f) | 0x40;
    bytes[8] = ((bytes[8] ?? 0) & 0x3f) | 0x80;
    const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
    return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
  }

  // Last resort. Weaker entropy, but an idempotency key only has to be unique among this page's
  // own requests within 24 hours, not unguessable.
  return `bifrost-${Date.now().toString(16)}-${Math.random().toString(16).slice(2, 14)}`;
}
