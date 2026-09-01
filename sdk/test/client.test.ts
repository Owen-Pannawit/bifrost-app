import { describe, expect, it, vi, afterEach } from 'vitest';
import { BifrostClient, createMemoryTokenStore, doc, template, type BridgeStatus, type Job } from '../src/index.js';

/**
 * The SDK's job is to make failure impossible to ignore and success trivial to write.
 * These tests are mostly about the failure half — an unreachable bridge and a printer fault are
 * normal states on a warehouse floor, not exceptions.
 */

const READY: BridgeStatus = {
  bridge: { version: '0.1.0', apiVersion: 'v1', paired: true },
  printer: { state: 'READY', name: 'Demo 80mm', language: 'EscPos', printWidthDots: 576 },
};

function mockFetch(impl: (url: string, init?: RequestInit) => Promise<Response> | Response) {
  const spy = vi.fn(impl);
  vi.stubGlobal('fetch', spy);
  return spy;
}

/** Asserts the call happened, so every test below can destructure without a cast. */
function callAt(spy: ReturnType<typeof mockFetch>, index = 0): [string, RequestInit] {
  const call = spy.mock.calls[index];
  if (!call) throw new Error(`fetch was called ${spy.mock.calls.length} times; wanted call ${index + 1}`);
  return [call[0], call[1] ?? {}];
}

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

/** No stored token, no retry backoff, no waiting: the defaults a unit test wants. */
const testClient = (overrides = {}) =>
  new BifrostClient({
    tokenStore: createMemoryTokenStore(),
    retryDelaysMs: [],
    ...overrides,
  });

const headersOf = (init: RequestInit | undefined) => (init?.headers ?? {}) as Record<string, string>;

afterEach(() => vi.unstubAllGlobals());

// ---------------------------------------------------------------- availability

describe('isAvailable', () => {
  it('resolves false instead of throwing when the bridge is not running', async () => {
    // FR-708. A missing bridge is the normal state on a device where the app was never opened;
    // a page must be able to hide its Print button rather than show a stack trace.
    mockFetch(() => Promise.reject(new TypeError('Failed to fetch')));

    await expect(testClient().isAvailable()).resolves.toBe(false);
  });

  it('resolves true when the bridge answers', async () => {
    mockFetch(() => json(200, READY));

    await expect(testClient().isAvailable()).resolves.toBe(true);
  });
});

// ---------------------------------------------------------------- status

describe('getStatus', () => {
  it('calls the loopback address, not a network host', async () => {
    // ADR-001: the bridge is on the same device. Anything else would be a different product.
    const fetchSpy = mockFetch(() => json(200, READY));

    await testClient().getStatus();

    expect(fetchSpy).toHaveBeenCalledWith(
      'http://127.0.0.1:8437/v1/status',
      expect.objectContaining({ method: 'GET' }),
    );
  });

  it('returns the printer state so a page can disable Print before it is pressed', async () => {
    mockFetch(() => json(200, READY));

    const r = await testClient().getStatus();

    expect(r.ok).toBe(true);
    if (r.ok) {
      expect(r.value.printer?.state).toBe('READY');
      expect(r.value.printer?.printWidthDots).toBe(576);
    }
  });

  it('tolerates the unpaired shape, where printer and queue are absent', async () => {
    // DES-03 §3.1. Reading `printer.state` off this response is the obvious way to crash a page.
    mockFetch(() => json(200, { bridge: { version: '1.0.0', apiVersion: 'v1', paired: false } }));

    const r = await testClient().getStatus();

    expect(r.ok).toBe(true);
    if (r.ok) expect(r.value.printer).toBeUndefined();
  });

  it('honours a custom baseUrl', async () => {
    const fetchSpy = mockFetch(() => json(200, READY));

    await testClient({ baseUrl: 'http://127.0.0.1:9999' }).getStatus();

    expect(fetchSpy).toHaveBeenCalledWith('http://127.0.0.1:9999/v1/status', expect.anything());
  });
});

// ---------------------------------------------------------------- print

describe('print', () => {
  const printed: Job = { jobId: 'job_000001', state: 'PRINTED', byteCount: 128 };

  it('POSTs JSON and stamps the tier the bridge expects', async () => {
    const fetchSpy = mockFetch(() => json(202, printed));

    await testClient().print({ document: { elements: [{ type: 'text', value: 'X' }] } });

    const [, init] = callAt(fetchSpy);
    expect(init.method).toBe('POST');
    expect(headersOf(init)['Content-Type']).toBe('application/json');

    // The caller may omit `tier`; the SDK supplies it so the bridge's discriminator always matches.
    expect(JSON.parse(init.body as string)).toMatchObject({
      tier: 'dsl',
      document: { elements: [{ type: 'text', value: 'X' }] },
    });
  });

  it('returns the job on success', async () => {
    mockFetch(() => json(202, printed));

    const r = await testClient().print({ document: { elements: [{ type: 'text', value: 'X' }] } });

    expect(r.ok).toBe(true);
    if (r.ok) expect(r.value.jobId).toBe('job_000001');
  });

  it('sends an idempotency key without being asked (FR-705)', async () => {
    const fetchSpy = mockFetch(() => json(202, printed));

    await testClient().print(template('part-label', { partNo: '6205-2RS' }));

    const [, init] = callAt(fetchSpy);
    expect(headersOf(init)['Idempotency-Key']).toMatch(/\S/);
  });

  it('reuses one key across its own retries, so an ambiguous timeout cannot print twice', async () => {
    // NFR-202. This is the whole reason the key exists: attempt 2 must be recognisable as attempt 1.
    let call = 0;
    const fetchSpy = mockFetch(() => {
      call++;
      return call < 3 ? Promise.reject(new TypeError('Failed to fetch')) : json(202, printed);
    });

    const r = await testClient({ retryDelaysMs: [0, 0] })
      .print({ document: { elements: [{ type: 'text', value: 'X' }] } });

    expect(r.ok).toBe(true);
    expect(fetchSpy).toHaveBeenCalledTimes(3);

    const keys = fetchSpy.mock.calls.map(([, init]) => headersOf(init)['Idempotency-Key']);
    expect(new Set(keys).size).toBe(1);
  });

  it('never retries an error the bridge actually answered with', async () => {
    // A 4xx is a considered reply. Asking again wastes a second and changes nothing.
    const fetchSpy = mockFetch(() => json(422, {
      error: { code: 'CONTENT_TOO_WIDE', message: 'Too wide.', transient: false },
    }));

    await testClient({ retryDelaysMs: [0, 0] })
      .print({ document: { elements: [{ type: 'text', value: 'X' }] } });

    expect(fetchSpy).toHaveBeenCalledTimes(1);
  });

  it('folds a per-call copy count into the payload', async () => {
    const fetchSpy = mockFetch(() => json(202, printed));

    await testClient().print(
      { document: { elements: [{ type: 'text', value: 'X' }] } },
      { copies: 3 },
    );

    const [, init] = callAt(fetchSpy);
    expect(JSON.parse(init.body as string).options).toEqual({ copies: 3 });
  });

  it('does not poll when the bridge already answered with a terminal state', async () => {
    // The 0.1 bridge prints synchronously. Following a finished job would be a wasted round trip
    // against an endpoint that build does not even have.
    const fetchSpy = mockFetch(() => json(202, printed));

    await testClient({ waitForCompletion: true })
      .print({ document: { elements: [{ type: 'text', value: 'X' }] } });

    expect(fetchSpy).toHaveBeenCalledTimes(1);
  });

  it('follows a queued job to its terminal state when asked to wait', async () => {
    const fetchSpy = mockFetch((url) =>
      url.endsWith('/v1/print')
        ? json(202, { jobId: 'job_1', state: 'QUEUED' })
        : json(200, { jobId: 'job_1', state: 'PRINTED' }));

    const r = await testClient({ waitForCompletion: true, pollIntervalMs: 1 })
      .print({ document: { elements: [{ type: 'text', value: 'X' }] } });

    expect(r.ok).toBe(true);
    if (r.ok) expect(r.value.state).toBe('PRINTED');
    expect(fetchSpy.mock.calls.length).toBeGreaterThan(1);
  });

  it('resolves with the accepted job when the bridge has no jobs endpoint to follow', async () => {
    // A 0.1 bridge that returned QUEUED cannot be polled. The job was accepted, so reporting a
    // failure that did not happen would be worse than reporting what is known.
    mockFetch((url) =>
      url.endsWith('/v1/print')
        ? json(202, { jobId: 'job_1', state: 'QUEUED' })
        : new Response('', { status: 404 }));

    const r = await testClient({ waitForCompletion: true, pollIntervalMs: 1 })
      .print({ document: { elements: [{ type: 'text', value: 'X' }] } });

    expect(r.ok).toBe(true);
    if (r.ok) expect(r.value.state).toBe('QUEUED');
  });

  it('gives up on a job that never finishes, naming the job so it can be looked up', async () => {
    mockFetch((url) =>
      url.endsWith('/v1/print')
        ? json(202, { jobId: 'job_1', state: 'QUEUED' })
        : json(200, { jobId: 'job_1', state: 'QUEUED' }));

    const r = await testClient({ waitForCompletion: true, pollIntervalMs: 1, completionTimeoutMs: 20 })
      .print({ document: { elements: [{ type: 'text', value: 'X' }] } });

    expect(r.ok).toBe(false);
    if (!r.ok) {
      expect(r.error.code).toBe('JOB_TIMEOUT');
      expect(r.error.details).toEqual({ jobId: 'job_1' });
    }
  });

  it('returns as soon as the job is accepted when waiting is switched off', async () => {
    const fetchSpy = mockFetch(() => json(202, { jobId: 'job_1', state: 'QUEUED' }));

    const r = await testClient().print(
      { document: { elements: [{ type: 'text', value: 'X' }] } },
      { waitForCompletion: false },
    );

    expect(r.ok).toBe(true);
    if (r.ok) expect(r.value.state).toBe('QUEUED');
    expect(fetchSpy).toHaveBeenCalledTimes(1);
  });
});

// ---------------------------------------------------------------- auth

describe('token handling', () => {
  it('sends the stored token as a bearer', async () => {
    const fetchSpy = mockFetch(() => json(200, { templates: [] }));

    await testClient({ tokenStore: createMemoryTokenStore('tok_123') }).getTemplates();

    const [, init] = callAt(fetchSpy);
    expect(headersOf(init)['Authorization']).toBe('Bearer tok_123');
  });

  it('discards a token the bridge rejects, so the page can re-pair', async () => {
    // DES-04 §8. Keeping a dead token turns every later call into the same opaque failure.
    const store = createMemoryTokenStore('stale');
    mockFetch(() => json(401, {
      error: { code: 'UNAUTHORIZED', message: 'Pair this page first.', transient: false },
    }));

    const client = testClient({ tokenStore: store });
    const r = await client.getCapabilities();

    expect(r.ok).toBe(false);
    expect(store.get()).toBeUndefined();
    expect(client.token).toBeUndefined();
  });

  it('persists the token presented at pairing', async () => {
    const store = createMemoryTokenStore();
    mockFetch(() => json(200, { paired: true, origin: 'http://x', pairedAt: '2026-08-22T09:00:00Z' }));

    const r = await testClient({ tokenStore: store }).pair('tok_scanned', 'Warehouse WMS');

    expect(r.ok).toBe(true);
    expect(store.get()).toBe('tok_scanned');
  });

  it('keeps no token when pairing is refused', async () => {
    const store = createMemoryTokenStore();
    mockFetch(() => json(410, {
      error: { code: 'PAIRING_EXPIRED', message: 'That code has expired.', transient: false },
    }));

    const r = await testClient({ tokenStore: store }).pair('tok_old');

    expect(r.ok).toBe(false);
    if (!r.ok) expect(r.error.code).toBe('PAIRING_EXPIRED');
    expect(store.get()).toBeUndefined();
  });
});

// ---------------------------------------------------------------- jobs and templates

describe('jobs and templates', () => {
  it('builds a repeatable state filter into the query string', async () => {
    const fetchSpy = mockFetch(() => json(200, { jobs: [] }));

    await testClient().listJobs({ state: ['FAILED', 'QUEUED'], limit: 10 });

    const [url] = callAt(fetchSpy);
    expect(url).toContain('state=FAILED');
    expect(url).toContain('state=QUEUED');
    expect(url).toContain('limit=10');
  });

  it('unwraps the templates envelope, because callers want the array', async () => {
    mockFetch(() => json(200, { templates: [{ name: 'part-label', version: 3, requiredFields: ['partNo'] }] }));

    const r = await testClient().getTemplates();

    expect(r.ok).toBe(true);
    if (r.ok) expect(r.value[0]?.name).toBe('part-label');
  });

  it('escapes a job id rather than pasting it into the path', async () => {
    const fetchSpy = mockFetch(() => json(200, { jobId: 'x', state: 'PRINTED' }));

    await testClient().cancelJob('job/../evil');

    const [url] = callAt(fetchSpy);
    expect(url).toBe('http://127.0.0.1:8437/v1/jobs/job%2F..%2Fevil/cancel');
  });
});

// ---------------------------------------------------------------- errors

describe('error handling', () => {
  it('surfaces the bridge error envelope verbatim', async () => {
    // DES-09 §9 tells the web developer to show error.message as-is. That only works if the SDK
    // passes it through untouched — rewriting it would create a second vocabulary for one fault.
    mockFetch(() => json(409, {
      error: {
        code: 'PRINTER_OUT_OF_PAPER',
        message: 'Printer is out of paper. Load media and printing will resume automatically.',
        transient: true,
      },
    }));

    const r = await testClient().print({ document: { elements: [{ type: 'text', value: 'X' }] } });

    expect(r.ok).toBe(false);
    if (!r.ok) {
      expect(r.error.code).toBe('PRINTER_OUT_OF_PAPER');
      expect(r.error.transient).toBe(true);
      expect(r.error.message).toContain('Load media');
    }
  });

  it('keeps the field path on a validation error', async () => {
    // FR-308: the developer needs to know which element is wrong, not just that something is.
    mockFetch(() => json(400, {
      error: {
        code: 'VALIDATION_ERROR',
        message: 'CODE39 accepts 0-9, A-Z and - . $ / + % and space only.',
        transient: false,
        field: 'document.elements[0].value',
      },
    }));

    const r = await testClient().print({
      document: { elements: [{ type: 'barcode', format: 'CODE39', value: 'lowercase' }] },
    });

    expect(r.ok).toBe(false);
    if (!r.ok) expect(r.error.field).toBe('document.elements[0].value');
  });

  it('reports an unreachable bridge distinctly from a wedged one', async () => {
    // Different problems for the operator: one means the app is not running, the other that it is
    // running but stuck. Collapsing them into "error" loses the only actionable difference.
    mockFetch(() => Promise.reject(new TypeError('Failed to fetch')));

    const r = await testClient().getStatus();

    expect(r.ok).toBe(false);
    if (!r.ok) {
      expect(r.error.code).toBe('BRIDGE_UNAVAILABLE');
      expect(r.error.transient).toBe(true);
      expect(r.error.message).toContain('BifrǫstApp');
    }
  });

  it('reports a timeout as BRIDGE_TIMEOUT', async () => {
    mockFetch((_url, init) =>
      new Promise((_resolve, reject) => {
        init?.signal?.addEventListener('abort', () =>
          reject(new DOMException('The operation was aborted.', 'AbortError')));
      }));

    const r = await testClient({ timeoutMs: 10 }).getStatus();

    expect(r.ok).toBe(false);
    if (!r.ok) expect(r.error.code).toBe('BRIDGE_TIMEOUT');
  });

  it('reports a caller cancellation as its own thing, not as a bridge fault', async () => {
    const controller = new AbortController();
    mockFetch((_url, init) =>
      new Promise((_resolve, reject) => {
        init?.signal?.addEventListener('abort', () =>
          reject(new DOMException('The operation was aborted.', 'AbortError')));
      }));

    const promise = testClient({ retryDelaysMs: [0, 0] }).print(
      { document: { elements: [{ type: 'text', value: 'X' }] } },
      { signal: controller.signal },
    );
    controller.abort();

    const r = await promise;
    expect(r.ok).toBe(false);
    if (!r.ok) expect(r.error.code).toBe('REQUEST_ABORTED');
  });

  it('does not throw when the bridge returns a body it cannot parse', async () => {
    // NFR-205 in spirit: a broken bridge must not take the page down with it.
    mockFetch(() => new Response('<html>gateway error</html>', { status: 502 }));

    const r = await testClient().getStatus();

    expect(r.ok).toBe(false);
    if (!r.ok) expect(r.error.transient).toBe(true);
  });

  it('falls back to a synthetic error when a failure carries no envelope', async () => {
    mockFetch(() => json(500, {}));

    const r = await testClient().getStatus();

    expect(r.ok).toBe(false);
    if (!r.ok) {
      expect(r.error.code).toBe('INTERNAL_ERROR');
      expect(r.error.transient).toBe(true);
    }
  });

  it('names a missing endpoint as such, so a version mismatch reads as one', async () => {
    mockFetch(() => new Response('', { status: 404 }));

    const r = await testClient().getCapabilities();

    expect(r.ok).toBe(false);
    if (!r.ok) expect(r.error.code).toBe('NOT_FOUND');
  });
});

// ---------------------------------------------------------------- builder integration

describe('doc() builder', () => {
  it('produces a payload the client sends unchanged', async () => {
    const fetchSpy = mockFetch(() => json(202, { jobId: 'j', state: 'PRINTED' }));

    await testClient().print(
      doc(576)
        .text('6205-2RS', { size: 3, bold: true, align: 'center' })
        .qr('PN=6205-2RS', { scale: 6 })
        .feed(3)
        .build(),
    );

    const [, init] = callAt(fetchSpy);
    expect(JSON.parse(init.body as string)).toEqual({
      tier: 'dsl',
      document: {
        widthDots: 576,
        elements: [
          { type: 'text', value: '6205-2RS', size: 3, bold: true, align: 'center' },
          { type: 'qr', value: 'PN=6205-2RS', scale: 6 },
          { type: 'feed', lines: 3 },
        ],
      },
    });
  });
});
