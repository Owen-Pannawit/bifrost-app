import { describe, expect, it, vi, afterEach } from 'vitest';
import { BifrostClient, doc, type BridgeStatus, type Job } from '../src/index.js';

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
  const spy = vi.fn(impl as never);
  vi.stubGlobal('fetch', spy);
  return spy;
}

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

afterEach(() => vi.unstubAllGlobals());

// ---------------------------------------------------------------- availability

describe('isAvailable', () => {
  it('resolves false instead of throwing when the bridge is not running', async () => {
    // FR-708. A missing bridge is the normal state on a device where the app was never opened;
    // a page must be able to hide its Print button rather than show a stack trace.
    mockFetch(() => Promise.reject(new TypeError('Failed to fetch')));

    await expect(new BifrostClient().isAvailable()).resolves.toBe(false);
  });

  it('resolves true when the bridge answers', async () => {
    mockFetch(() => json(200, READY));

    await expect(new BifrostClient().isAvailable()).resolves.toBe(true);
  });
});

// ---------------------------------------------------------------- status

describe('getStatus', () => {
  it('calls the loopback address, not a network host', async () => {
    // ADR-001: the bridge is on the same device. Anything else would be a different product.
    const fetchSpy = mockFetch(() => json(200, READY));

    await new BifrostClient().getStatus();

    expect(fetchSpy).toHaveBeenCalledWith(
      'http://127.0.0.1:8437/v1/status',
      expect.objectContaining({ method: 'GET' }),
    );
  });

  it('returns the printer state so a page can disable Print before it is pressed', async () => {
    mockFetch(() => json(200, READY));

    const r = await new BifrostClient().getStatus();

    expect(r.ok).toBe(true);
    if (r.ok) {
      expect(r.value.printer.state).toBe('READY');
      expect(r.value.printer.printWidthDots).toBe(576);
    }
  });

  it('honours a custom baseUrl', async () => {
    const fetchSpy = mockFetch(() => json(200, READY));

    await new BifrostClient({ baseUrl: 'http://127.0.0.1:9999' }).getStatus();

    expect(fetchSpy).toHaveBeenCalledWith(
      'http://127.0.0.1:9999/v1/status',
      expect.anything(),
    );
  });
});

// ---------------------------------------------------------------- print

describe('print', () => {
  const printed: Job = { jobId: 'job_000001', state: 'PRINTED', byteCount: 128 };

  it('POSTs JSON and stamps the tier the bridge expects', async () => {
    const fetchSpy = mockFetch(() => json(202, printed));

    await new BifrostClient().print({ document: { elements: [{ type: 'text', value: 'X' }] } });

    const [, init] = fetchSpy.mock.calls[0] as [string, RequestInit];
    expect(init.method).toBe('POST');
    expect((init.headers as Record<string, string>)['Content-Type']).toBe('application/json');

    // The caller may omit `tier`; the SDK supplies it so the bridge's discriminator always matches.
    expect(JSON.parse(init.body as string)).toMatchObject({
      tier: 'dsl',
      document: { elements: [{ type: 'text', value: 'X' }] },
    });
  });

  it('returns the job on success', async () => {
    mockFetch(() => json(202, printed));

    const r = await new BifrostClient().print({ document: { elements: [{ type: 'text', value: 'X' }] } });

    expect(r).toEqual({ ok: true, value: printed });
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

    const r = await new BifrostClient().print({ document: { elements: [{ type: 'text', value: 'X' }] } });

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

    const r = await new BifrostClient().print({
      document: { elements: [{ type: 'barcode', format: 'CODE39', value: 'lowercase' }] },
    });

    expect(r.ok).toBe(false);
    if (!r.ok) expect(r.error.field).toBe('document.elements[0].value');
  });

  it('reports an unreachable bridge distinctly from a wedged one', async () => {
    // Different problems for the operator: one means the app is not running, the other that it is
    // running but stuck. Collapsing them into "error" loses the only actionable difference.
    mockFetch(() => Promise.reject(new TypeError('Failed to fetch')));

    const r = await new BifrostClient().getStatus();

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

    const r = await new BifrostClient({ timeoutMs: 10 }).getStatus();

    expect(r.ok).toBe(false);
    if (!r.ok) expect(r.error.code).toBe('BRIDGE_TIMEOUT');
  });

  it('does not throw when the bridge returns a body it cannot parse', async () => {
    // NFR-205 in spirit: a broken bridge must not take the page down with it.
    mockFetch(() => new Response('<html>gateway error</html>', { status: 502 }));

    const r = await new BifrostClient().getStatus();

    expect(r.ok).toBe(false);
    if (!r.ok) expect(r.error.transient).toBe(true);
  });

  it('falls back to a synthetic error when a failure carries no envelope', async () => {
    mockFetch(() => json(500, {}));

    const r = await new BifrostClient().getStatus();

    expect(r.ok).toBe(false);
    if (!r.ok) {
      expect(r.error.code).toBe('INTERNAL_ERROR');
      expect(r.error.transient).toBe(true);
    }
  });
});

// ---------------------------------------------------------------- builder

describe('doc() builder', () => {
  it('produces the payload shape DslCompiler parses', async () => {
    const payload = doc()
      .text('6205-2RS', { size: 3, bold: true, align: 'center' })
      .text('Lot L2408-0231', { size: 1, align: 'center' })
      .barcode('CODE128', '6205-2RS', { heightDots: 80, moduleWidth: 3 })
      .feed(3)
      .build();

    expect(payload).toEqual({
      tier: 'dsl',
      document: {
        widthDots: undefined,
        elements: [
          { type: 'text', value: '6205-2RS', size: 3, bold: true, align: 'center' },
          { type: 'text', value: 'Lot L2408-0231', size: 1, align: 'center' },
          { type: 'barcode', format: 'CODE128', value: '6205-2RS', heightDots: 80, moduleWidth: 3 },
          { type: 'feed', lines: 3 },
        ],
      },
    });
  });

  it('carries widthDots when the caller pins it', () => {
    expect(doc(576).text('X').build().document.widthDots).toBe(576);
  });

  it('appends a cut element', () => {
    const elements = doc().text('X').cut('PARTIAL').build().document.elements;

    expect(elements[1]).toEqual({ type: 'cut', mode: 'PARTIAL' });
  });

  it('keeps element order, because the printer prints in it', () => {
    // ESC/POS is a sequential model: order on the wire is order on the paper (DES-06 §4.1).
    const elements = doc().text('first').barcode('CODE128', 'x').text('last').build().document.elements;

    expect(elements.map(e => e.type)).toEqual(['text', 'barcode', 'text']);
  });
});
