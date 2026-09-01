/**
 * The client a web page actually holds — DES-04.
 *
 * One object, one call to print, and no knowledge of ESC/POS, CPCL or Bluetooth anywhere in the
 * calling application.
 */

import { type BifrostError, type Result, SdkErrors, fail, ok } from './errors.js';
import { EventStream } from './events.js';
import { type HttpConfig, request, uuid } from './http.js';
import { type TokenStore, createTokenStore } from './storage.js';
import {
  type BifrostEventMap,
  type BifrostEventName,
  type BridgeStatus,
  type Capabilities,
  type IBifrostClient,
  type Job,
  type JobPage,
  type JobQuery,
  type PairInfo,
  type PreviewImage,
  type PrintCallOptions,
  type PrintPayload,
  type TemplateInfo,
  type Unsubscribe,
  isTerminalState,
} from './types.js';

export interface BifrostOptions {
  /** Default `http://127.0.0.1:8437`. */
  baseUrl?: string;
  /** Explicit token. Omit to use the one stored by a previous {@link BifrostClient.pair}. */
  token?: string;
  /** `localStorage` key for the token. Default `bifrost.token`. */
  storageKey?: string;
  /** Per-request timeout in ms. Default 10000. */
  timeoutMs?: number;
  /** Wait for a job to reach a terminal state before resolving. Default `true`. */
  waitForCompletion?: boolean;
  /** Max wait for a terminal state in ms. Default 30000. */
  completionTimeoutMs?: number;
  /** Open the event socket on the first `on()` subscription. Default `true`. */
  autoConnectEvents?: boolean;
  /** Fallback poll interval while waiting for completion without a socket. Default 750. */
  pollIntervalMs?: number;
  /** Backoff between SDK retries of a network-level failure. Default `[500, 1500, 4000]`. */
  retryDelaysMs?: readonly number[];
  /** Injectable for tests and non-browser hosts. */
  webSocket?: typeof WebSocket;
  /** Injectable token storage. Defaults to `localStorage` with an in-memory fallback. */
  tokenStore?: TokenStore;
}

const DEFAULTS = {
  baseUrl: 'http://127.0.0.1:8437',
  storageKey: 'bifrost.token',
  timeoutMs: 10_000,
  waitForCompletion: true,
  completionTimeoutMs: 30_000,
  pollIntervalMs: 750,
  retryDelaysMs: [500, 1_500, 4_000] as readonly number[],
} as const;

/** The API version this SDK is written against. */
export const API_VERSION = 'v1';

export class BifrostClient implements IBifrostClient {
  private readonly http: HttpConfig;
  private readonly tokens: TokenStore;
  private readonly waitByDefault: boolean;
  private readonly completionTimeoutMs: number;
  private readonly pollIntervalMs: number;
  private readonly baseUrl: string;
  private readonly autoConnectEvents: boolean;

  private eventStream: EventStream | null = null;
  private readonly makeEventStream: () => EventStream;
  private warnedAboutVersion = false;

  constructor(options: BifrostOptions = {}) {
    this.baseUrl = stripTrailingSlash(options.baseUrl ?? DEFAULTS.baseUrl);
    this.tokens = options.tokenStore ?? createTokenStore(options.storageKey ?? DEFAULTS.storageKey);
    if (options.token) this.tokens.set(options.token);

    this.waitByDefault = options.waitForCompletion ?? DEFAULTS.waitForCompletion;
    this.completionTimeoutMs = options.completionTimeoutMs ?? DEFAULTS.completionTimeoutMs;
    this.pollIntervalMs = options.pollIntervalMs ?? DEFAULTS.pollIntervalMs;

    this.http = {
      baseUrl: this.baseUrl,
      timeoutMs: options.timeoutMs ?? DEFAULTS.timeoutMs,
      token: () => this.tokens.get(),
      // A token the bridge no longer accepts is worse than no token: it makes every later call fail
      // the same way. Dropping it means the page's next `pair()` starts clean (DES-04 §8).
      onUnauthorized: () => this.tokens.clear(),
      retryDelaysMs: options.retryDelaysMs ?? DEFAULTS.retryDelaysMs,
    };

    this.autoConnectEvents = options.autoConnectEvents ?? true;
    this.makeEventStream = () =>
      new EventStream({
        url: () => {
          const token = this.tokens.get();
          if (!token) return null;
          // Browsers cannot set headers on a WebSocket handshake, so the token rides in the query
          // string. It is still validated, and the origin check still applies (DES-03 §3.10).
          const ws = this.baseUrl.replace(/^http/, 'ws');
          return `${ws}/v1/events?token=${encodeURIComponent(token)}`;
        },
        ...(options.webSocket ? { webSocket: options.webSocket } : {}),
      });
  }

  // ---------------------------------------------------------------- token

  /** The token in use, if any. */
  get token(): string | undefined {
    return this.tokens.get();
  }

  /** Adopt a token obtained outside the pairing flow — from a server-rendered page, say. */
  setToken(token: string): void {
    this.tokens.set(token);
    this.eventStream?.reconnect();
  }

  clearToken(): void {
    this.tokens.clear();
    this.eventStream?.close();
  }

  // ---------------------------------------------------------------- reachability

  /**
   * Is the bridge running?
   *
   * Resolves `false` rather than throwing. An absent bridge is the normal state on a device where
   * the app was never opened, so a page should hide its Print button rather than show an error
   * (FR-708).
   */
  async isAvailable(): Promise<boolean> {
    const r = await this.getStatus();
    return r.ok;
  }

  /** Bridge, printer and queue state. Works unpaired (FR-204). */
  async getStatus(): Promise<Result<BridgeStatus>> {
    const r = await request<BridgeStatus>(this.http, { method: 'GET', path: '/v1/status' });
    if (r.ok) this.checkVersion(r.value);
    return r;
  }

  // ---------------------------------------------------------------- pairing

  /**
   * Complete pairing with a token scanned from the app's QR code (FR-501).
   *
   * On success the token is persisted, so a reload does not send the operator back to the app.
   */
  async pair(token: string, clientName?: string): Promise<Result<PairInfo>> {
    const origin = globalThis.location?.origin;

    const r = await request<PairInfo>(this.http, {
      method: 'POST',
      path: '/v1/pair',
      body: {
        token,
        ...(origin ? { origin } : {}),
        ...(clientName ? { clientName } : {}),
      },
    });

    if (r.ok) {
      this.tokens.set(token);
      this.eventStream?.reconnect();
    }

    return r;
  }

  // ---------------------------------------------------------------- capabilities

  /**
   * What the connected printer can actually do (FR-201).
   *
   * Prefer this over a hard-coded print width; the same page then works on a 2-inch and a 4-inch
   * printer without a deployment.
   */
  getCapabilities(): Promise<Result<Capabilities>> {
    return request<Capabilities>(this.http, { method: 'GET', path: '/v1/capabilities' });
  }

  // ---------------------------------------------------------------- printing

  /**
   * Submit a print job. Accepts all three tiers (FR-701).
   *
   * An idempotency key is generated per call unless one is supplied, which is what makes the SDK's
   * own retry of an ambiguous timeout safe: if the bridge already accepted the job, the retry
   * returns that job and nothing prints twice (NFR-202, FR-705).
   *
   * @example
   * const r = await bifrost.print({
   *   tier: 'template',
   *   template: 'part-label',
   *   data: { partNo: '6205-2RS', lot: 'L2408-0231', qty: 50 },
   * });
   * if (!r.ok) toast(r.error.message);
   */
  async print(payload: PrintPayload, options: PrintCallOptions = {}): Promise<Result<Job>> {
    const key = options.idempotencyKey ?? uuid();

    const submitted = await request<Job>(this.http, {
      method: 'POST',
      path: '/v1/print',
      body: withCopies(payload, options.copies),
      headers: { 'Idempotency-Key': key },
      ...(options.signal ? { signal: options.signal } : {}),
      retryOnNetworkFailure: true,
    });

    if (!submitted.ok) return submitted;

    const job = { ...submitted.value, idempotencyKey: submitted.value.idempotencyKey ?? key };
    const wait = options.waitForCompletion ?? this.waitByDefault;

    // The 0.1 bridge prints synchronously and answers with a terminal state, so the common case
    // never reaches the tracking code below.
    if (!wait || isTerminalState(job)) return ok(job);

    return this.awaitCompletion(job, options.signal);
  }

  /** Render without printing (FR-202). Returns a base64 PNG suitable for an `<img src>`. */
  preview(payload: PrintPayload, previewScale?: number): Promise<Result<PreviewImage>> {
    return request<PreviewImage>(this.http, {
      method: 'POST',
      path: '/v1/preview',
      body: {
        ...withCopies(payload, undefined),
        ...(previewScale !== undefined ? { previewScale } : {}),
      },
    });
  }

  // ---------------------------------------------------------------- jobs

  getJob(jobId: string): Promise<Result<Job>> {
    return request<Job>(this.http, { method: 'GET', path: `/v1/jobs/${encodeURIComponent(jobId)}` });
  }

  listJobs(query: JobQuery = {}): Promise<Result<JobPage>> {
    const params = new URLSearchParams();

    const states = query.state === undefined ? [] : ([] as string[]).concat(query.state);
    for (const state of states) params.append('state', state);
    if (query.limit !== undefined) params.set('limit', String(query.limit));
    if (query.cursor !== undefined) params.set('cursor', query.cursor);
    if (query.since !== undefined) params.set('since', query.since);

    const qs = params.toString();
    return request<JobPage>(this.http, { method: 'GET', path: `/v1/jobs${qs ? `?${qs}` : ''}` });
  }

  cancelJob(jobId: string): Promise<Result<Job>> {
    return request<Job>(this.http, {
      method: 'POST',
      path: `/v1/jobs/${encodeURIComponent(jobId)}/cancel`,
    });
  }

  /** Templates on the device, with their required and optional fields (FR-302). */
  async getTemplates(): Promise<Result<TemplateInfo[]>> {
    const r = await request<{ templates: TemplateInfo[] }>(this.http, {
      method: 'GET',
      path: '/v1/templates',
    });

    return r.ok ? ok(r.value.templates ?? []) : r;
  }

  // ---------------------------------------------------------------- events

  /**
   * Subscribe to live printer and job state (FR-707). Returns the unsubscribe function.
   *
   * @example
   * const off = bifrost.on('printer.state_changed', ({ state }) => {
   *   printButton.disabled = state !== 'READY';
   * });
   */
  on<K extends BifrostEventName>(event: K, handler: (data: BifrostEventMap[K]) => void): Unsubscribe {
    return this.events.on(event, handler);
  }

  /** The event socket. Exposed for `connect()`, `close()` and `connected`. */
  get events(): EventStream {
    if (!this.eventStream) this.eventStream = this.makeEventStream();
    return this.eventStream;
  }

  /** Release the event socket. HTTP calls continue to work afterwards. */
  close(): void {
    this.eventStream?.close();
  }

  // ---------------------------------------------------------------- internals

  /**
   * Follow a job to a terminal state, by socket where one is connected and by polling otherwise.
   *
   * Both paths run together on purpose: the socket gives an immediate answer, and the poll is what
   * makes the promise resolve on a bridge whose socket dropped at exactly the wrong moment.
   */
  private awaitCompletion(accepted: Job, signal?: AbortSignal): Promise<Result<Job>> {
    return new Promise<Result<Job>>((resolve) => {
      let settled = false;
      const cleanups: Array<() => void> = [];

      const finish = (result: Result<Job>) => {
        if (settled) return;
        settled = true;
        for (const cleanup of cleanups) cleanup();
        resolve(result);
      };

      const timer = setTimeout(
        () => finish(fail<Job>(SdkErrors.jobTimeout(accepted.jobId))),
        this.completionTimeoutMs,
      );
      cleanups.push(() => clearTimeout(timer));

      if (signal) {
        const onAbort = () => finish(fail<Job>(SdkErrors.aborted()));
        signal.addEventListener('abort', onAbort);
        cleanups.push(() => signal.removeEventListener('abort', onAbort));
        if (signal.aborted) return onAbort();
      }

      if (this.autoConnectEvents) {
        cleanups.push(
          this.events.on('job.state_changed', (event) => {
            if (event.jobId !== accepted.jobId) return;

            const job: Job = {
              ...accepted,
              state: event.state,
              ...(event.attemptCount !== undefined ? { attemptCount: event.attemptCount } : {}),
              ...(event.error ? { lastError: event.error } : {}),
            };

            if (isTerminalState(job)) finish(ok(job));
          }),
        );
      }

      const poll = async () => {
        if (settled) return;

        const r = await this.getJob(accepted.jobId);

        if (settled) return;

        if (r.ok) {
          if (isTerminalState(r.value)) finish(ok(r.value));
          return;
        }

        // A bridge without the jobs endpoint, or one that has forgotten the job, cannot be polled.
        // The job was accepted, so reporting the accepted state beats reporting a failure that did
        // not happen — DES-07 §2 makes the app's own queue responsible from here.
        if (untrackable(r.error)) finish(ok(accepted));
      };

      const interval = setInterval(() => void poll(), this.pollIntervalMs);
      cleanups.push(() => clearInterval(interval));
      void poll();
    });
  }

  /** Warn once, not per call: a version mismatch is a deployment fact, not a per-request event. */
  private checkVersion(status: BridgeStatus): void {
    const reported = status.bridge?.apiVersion;
    if (this.warnedAboutVersion || !reported || reported === API_VERSION) return;

    this.warnedAboutVersion = true;
    console.warn(
      `[bifrost] This SDK speaks ${API_VERSION}; the bridge reports ${reported}. ` +
        'Update @bearing/bifrost-sdk, or the app, so the two agree.',
    );
  }
}

function untrackable(error: BifrostError): boolean {
  return error.code === 'NOT_FOUND' || error.code === 'JOB_NOT_FOUND';
}

/** Stamps the tier the bridge's discriminator expects, and folds in a per-call copy count. */
function withCopies(payload: PrintPayload, copies: number | undefined): PrintPayload {
  const tiered: PrintPayload = payload.tier ? payload : { ...payload, tier: 'dsl' };

  if (copies === undefined) return tiered;
  return { ...tiered, options: { ...tiered.options, copies } };
}

function stripTrailingSlash(url: string): string {
  return url.endsWith('/') ? url.slice(0, -1) : url;
}
