/**
 * Bifrǫst SDK — print to a Bluetooth printer from an ordinary web page.
 *
 * The bridge runs on the same device as the browser, so this talks to 127.0.0.1. Chrome treats
 * loopback as a potentially trustworthy origin, which is why an HTTPS page can call it without a
 * certificate on the device and without a mixed-content block (ADR-001).
 *
 * Demo scope: print, status and availability. Pairing, events, jobs and templates are Phase 6.
 */

// ---------------------------------------------------------------- result and errors

export type Result<T> =
  | { ok: true; value: T }
  | { ok: false; error: BifrostError };

export interface BifrostError {
  code: BifrostErrorCode;
  /** Plain English. For printer faults this is safe to show an operator verbatim. */
  message: string;
  /** Whether a retry could succeed. */
  transient: boolean;
  /** JSON path to the offending field, for validation errors. */
  field?: string;
}

export type BifrostErrorCode =
  // raised by the SDK, not the bridge
  | 'BRIDGE_UNAVAILABLE' | 'BRIDGE_TIMEOUT'
  // access
  | 'ORIGIN_NOT_ALLOWED' | 'UNAUTHORIZED'
  // payload
  | 'VALIDATION_ERROR' | 'MALFORMED_JSON' | 'CONTENT_TOO_WIDE' | 'UNSUPPORTED_ELEMENT'
  // printer
  | 'PRINTER_NOT_CONNECTED' | 'PRINTER_DISCONNECTED' | 'PRINTER_OUT_OF_PAPER'
  | 'PRINTER_COVER_OPEN' | 'PRINTER_BATTERY_LOW' | 'PRINTER_OVERHEATED'
  | 'PRINTER_PAPER_JAM' | 'TRANSMIT_TIMEOUT'
  // other
  | 'INTERNAL_ERROR' | 'NOT_FOUND';

// ---------------------------------------------------------------- payload

export type Align = 'left' | 'center' | 'right';

export type BarcodeFormat = 'CODE128' | 'CODE39' | 'EAN13' | 'ITF' | 'UPCA';

export type Element =
  | { type: 'text'; value: string; size?: number; bold?: boolean; underline?: boolean; align?: Align }
  | { type: 'barcode'; format: BarcodeFormat; value: string; heightDots?: number; moduleWidth?: number; showText?: boolean; align?: Align }
  | { type: 'feed'; lines?: number; dots?: number }
  | { type: 'cut'; mode?: 'FULL' | 'PARTIAL' };

export interface PrintPayload {
  tier?: 'dsl';
  document: { widthDots?: number; elements: Element[] };
  options?: { copies?: number; cutAfter?: boolean };
}

export interface Job {
  jobId: string;
  state: 'QUEUED' | 'PRINTED' | 'FAILED';
  byteCount: number;
}

export type PrinterState = 'READY' | 'CONNECTING' | 'DISCONNECTED' | 'NOT_CONFIGURED' | 'ERROR';

export interface BridgeStatus {
  bridge: { version: string; apiVersion: string; paired: boolean };
  printer: {
    state: PrinterState;
    name?: string;
    transport?: string;
    language?: string;
    printWidthDots?: number;
    lastError?: string;
  };
}

export interface BifrostOptions {
  baseUrl?: string;
  timeoutMs?: number;
}

// ---------------------------------------------------------------- client

export class BifrostClient {
  private readonly baseUrl: string;
  private readonly timeoutMs: number;

  constructor(options: BifrostOptions = {}) {
    this.baseUrl = options.baseUrl ?? 'http://127.0.0.1:8437';
    this.timeoutMs = options.timeoutMs ?? 10_000;
  }

  /**
   * Is the bridge running? Resolves `false` rather than throwing — an absent bridge is a normal
   * state, so a page can hide its Print button instead of showing an error.
   */
  async isAvailable(): Promise<boolean> {
    const r = await this.getStatus();
    return r.ok;
  }

  async getStatus(): Promise<Result<BridgeStatus>> {
    return this.request<BridgeStatus>('GET', '/v1/status');
  }

  /**
   * Submit a print job.
   *
   * @example
   * const r = await bifrost.print({
   *   document: {
   *     elements: [
   *       { type: 'text', value: '6205-2RS', size: 3, bold: true, align: 'center' },
   *       { type: 'barcode', format: 'CODE128', value: '6205-2RS' },
   *       { type: 'feed', lines: 3 },
   *     ],
   *   },
   * });
   * if (!r.ok) toast(r.error.message);
   */
  async print(payload: PrintPayload): Promise<Result<Job>> {
    return this.request<Job>('POST', '/v1/print', { tier: 'dsl', ...payload });
  }

  private async request<T>(method: string, path: string, body?: unknown): Promise<Result<T>> {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), this.timeoutMs);

    try {
      const response = await fetch(this.baseUrl + path, {
        method,
        headers: body ? { 'Content-Type': 'application/json' } : undefined,
        body: body ? JSON.stringify(body) : undefined,
        signal: controller.signal,
      });

      const text = await response.text();
      const parsed: unknown = text ? JSON.parse(text) : {};

      if (!response.ok) {
        const envelope = parsed as { error?: BifrostError };
        return {
          ok: false,
          error: envelope.error ?? {
            code: 'INTERNAL_ERROR',
            message: `Bridge returned ${response.status}.`,
            transient: response.status >= 500,
          },
        };
      }

      return { ok: true, value: parsed as T };
    } catch (e) {
      // A connection refused and a timeout are different problems for the operator: one means the
      // app is not running, the other that it is wedged.
      const aborted = e instanceof DOMException && e.name === 'AbortError';
      return {
        ok: false,
        error: aborted
          ? { code: 'BRIDGE_TIMEOUT', message: 'The print bridge did not respond.', transient: true }
          : {
              code: 'BRIDGE_UNAVAILABLE',
              message: 'Print bridge not running. Open BifrǫstApp on this device.',
              transient: true,
            },
      };
    } finally {
      clearTimeout(timer);
    }
  }
}

// ---------------------------------------------------------------- builder

/** Fluent sugar over the element array. Optional — the plain object form works identically. */
export function doc(widthDots?: number) {
  const elements: Element[] = [];

  const api = {
    text(value: string, opts: Omit<Extract<Element, { type: 'text' }>, 'type' | 'value'> = {}) {
      elements.push({ type: 'text', value, ...opts });
      return api;
    },
    barcode(
      format: BarcodeFormat,
      value: string,
      opts: Omit<Extract<Element, { type: 'barcode' }>, 'type' | 'format' | 'value'> = {},
    ) {
      elements.push({ type: 'barcode', format, value, ...opts });
      return api;
    },
    feed(lines: number) {
      elements.push({ type: 'feed', lines });
      return api;
    },
    cut(mode: 'FULL' | 'PARTIAL' = 'FULL') {
      elements.push({ type: 'cut', mode });
      return api;
    },
    build(): PrintPayload {
      return { tier: 'dsl', document: { widthDots, elements } };
    },
  };

  return api;
}
