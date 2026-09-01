/**
 * Fluent sugar over the three payload tiers.
 *
 * Entirely optional — the plain object form is identical and equally supported. It exists because a
 * label read top-to-bottom in code is easier to compare against the label in your hand.
 */

import type {
  Align,
  BarcodeElement,
  BarcodeFormat,
  CutElement,
  DslPayload,
  Element,
  ImageElement,
  LineElement,
  QrElement,
  RawLanguage,
  RawPayload,
  TemplatePayload,
  TextElement,
} from './types.js';

type Opts<T extends Element, K extends keyof T> = Omit<T, 'type' | K>;

export interface DocumentBuilder {
  text(value: string, opts?: Opts<TextElement, 'value'>): DocumentBuilder;
  barcode(
    format: BarcodeFormat,
    value: string,
    opts?: Opts<BarcodeElement, 'format' | 'value'>,
  ): DocumentBuilder;
  qr(value: string, opts?: Opts<QrElement, 'value'>): DocumentBuilder;
  image(data: string, opts?: Opts<ImageElement, 'data'>): DocumentBuilder;
  line(opts?: Omit<LineElement, 'type'>): DocumentBuilder;
  /** Blank lines. One line is roughly 24 dots at 203 dpi. */
  feed(lines: number): DocumentBuilder;
  /** Blank space in dots, when the exact gap matters more than the line count. */
  feedDots(dots: number): DocumentBuilder;
  cut(mode?: CutElement['mode']): DocumentBuilder;
  /** Escape hatch for an element type newer than this SDK. */
  element(element: Element): DocumentBuilder;
  /** 1–99. */
  copies(count: number): DocumentBuilder;
  cutAfter(enabled?: boolean): DocumentBuilder;
  /** Centre everything appended from here on, unless the element sets its own alignment. */
  align(align: Align): DocumentBuilder;
  build(): DslPayload;
}

/**
 * Start a Tier 2 document.
 *
 * @param widthDots Omit it and the connected printer's own width is used, which is almost always
 * what you want — a hard-coded width is the most common cause of a clipped label (DES-06 §8.1).
 *
 * @example
 * await bifrost.print(
 *   doc()
 *     .text('6205-2RS', { size: 3, bold: true, align: 'center' })
 *     .barcode('CODE128', '6205-2RS', { heightDots: 80, showText: true })
 *     .qr('PN=6205-2RS;LOT=L2408-0231', { scale: 6 })
 *     .feed(3)
 *     .build(),
 * );
 */
export function doc(widthDots?: number): DocumentBuilder {
  const elements: Element[] = [];
  let copies: number | undefined;
  let cutAfter: boolean | undefined;
  let defaultAlign: Align | undefined;

  const withAlign = <T extends { align?: Align }>(opts: T): T =>
    defaultAlign !== undefined && opts.align === undefined ? { ...opts, align: defaultAlign } : opts;

  const api: DocumentBuilder = {
    text(value, opts = {}) {
      elements.push({ type: 'text', value, ...withAlign(opts) });
      return api;
    },
    barcode(format, value, opts = {}) {
      elements.push({ type: 'barcode', format, value, ...withAlign(opts) });
      return api;
    },
    qr(value, opts = {}) {
      elements.push({ type: 'qr', value, ...withAlign(opts) });
      return api;
    },
    image(data, opts = {}) {
      elements.push({ type: 'image', data, ...withAlign(opts) });
      return api;
    },
    line(opts = {}) {
      elements.push({ type: 'line', ...opts });
      return api;
    },
    feed(lines) {
      elements.push({ type: 'feed', lines });
      return api;
    },
    feedDots(dots) {
      elements.push({ type: 'feed', dots });
      return api;
    },
    cut(mode = 'FULL') {
      elements.push({ type: 'cut', mode });
      return api;
    },
    element(element) {
      elements.push(element);
      return api;
    },
    copies(count) {
      copies = count;
      return api;
    },
    cutAfter(enabled = true) {
      cutAfter = enabled;
      return api;
    },
    align(align) {
      defaultAlign = align;
      return api;
    },
    build() {
      const payload: DslPayload = { tier: 'dsl', document: { widthDots, elements } };

      if (copies !== undefined || cutAfter !== undefined) {
        payload.options = {
          ...(copies !== undefined ? { copies } : {}),
          ...(cutAfter !== undefined ? { cutAfter } : {}),
        };
      }

      return payload;
    },
  };

  return api;
}

/**
 * Tier 1 — render a template that lives on the device.
 *
 * @example
 * await bifrost.print(template('part-label', { partNo: '6205-2RS', lot: 'L2408-0231', qty: 50 }));
 */
export function template(
  name: string,
  data: TemplatePayload['data'],
  options?: TemplatePayload['options'],
): TemplatePayload {
  return { tier: 'template', template: name, data, ...(options ? { options } : {}) };
}

/**
 * Tier 3 — pre-encoded printer commands, passed through unmodified.
 *
 * Accepts bytes directly so callers need not think about base64 at all.
 *
 * @example
 * await bifrost.print(raw('ESCPOS', new Uint8Array([0x1b, 0x40])));
 */
export function raw(
  language: RawLanguage,
  data: Uint8Array | ArrayBuffer | string,
  options?: RawPayload['options'],
): RawPayload {
  return {
    tier: 'raw',
    language,
    data: toBase64(data),
    ...(options ? { options } : {}),
  };
}

/**
 * Base64 for the wire.
 *
 * A string is treated as Latin-1 bytes, because that is what a printer command string is — `\x1B@`
 * means two bytes, and running it through a UTF-8 encoder would turn the escape into two.
 */
export function toBase64(data: Uint8Array | ArrayBuffer | string): string {
  const bytes =
    typeof data === 'string'
      ? Uint8Array.from(data, (ch) => ch.charCodeAt(0) & 0xff)
      : data instanceof Uint8Array
        ? data
        : new Uint8Array(data);

  let binary = '';
  // Chunked: String.fromCharCode with a very large spread blows the argument limit on a big label.
  const chunk = 0x8000;
  for (let i = 0; i < bytes.length; i += chunk) {
    binary += String.fromCharCode(...bytes.subarray(i, i + chunk));
  }

  if (typeof btoa === 'function') return btoa(binary);

  // Node, for tests and server-side rendering.
  const maybeBuffer = (globalThis as { Buffer?: { from(s: string, e: string): { toString(e: string): string } } }).Buffer;
  if (maybeBuffer) return maybeBuffer.from(binary, 'binary').toString('base64');

  throw new Error('No base64 encoder available in this environment.');
}
