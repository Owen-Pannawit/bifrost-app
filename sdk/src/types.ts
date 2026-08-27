/**
 * The wire contract, as types — DES-03 (Local API) and DES-05 (Print Payload Schema).
 *
 * These are the SDK's real product. A wrong payload shape should fail at compile time in the web
 * app, not as a 400 discovered by an operator holding a blank label.
 */

import type { BifrostError, Result } from './errors.js';

// ---------------------------------------------------------------- payload — shared

export type Align = 'left' | 'center' | 'right';

export type BarcodeFormat = 'CODE128' | 'CODE39' | 'EAN13' | 'ITF' | 'UPCA';

export type EccLevel = 'L' | 'M' | 'Q' | 'H';

export type RawLanguage = 'ESCPOS' | 'ZPL' | 'CPCL' | 'TSPL';

export interface PrintOptions {
  /** 1–99. Default 1. */
  copies?: number;
  /** Cut once after the last copy, where the printer has a cutter. */
  cutAfter?: boolean;
}

// ---------------------------------------------------------------- payload — Tier 2 elements

export interface TextElement {
  type: 'text';
  /** ASCII only. The drivers encode with ASCII, so anything else is rejected rather than printed as `?` (D-09). */
  value: string;
  /** 1–8. A multiplier on the printer's base font, not a point size. */
  size?: number;
  bold?: boolean;
  underline?: boolean;
  invert?: boolean;
  align?: Align;
  /** Font id from {@link Capabilities.fonts}. Printer default when omitted. */
  font?: string;
  maxLines?: number;
  overflow?: 'truncate' | 'wrap' | 'error';
}

export interface BarcodeElement {
  type: 'barcode';
  format: BarcodeFormat;
  /** Validated against the symbology's character set before anything is sent (DES-05 §4.2). */
  value: string;
  heightDots?: number;
  /** 1–6. The narrow-bar width, and the main driver of whether the label scans. */
  moduleWidth?: number;
  showText?: boolean;
  align?: Align;
}

export interface QrElement {
  type: 'qr';
  /** Max 2953 bytes. */
  value: string;
  /** 1–16 dots per module. */
  scale?: number;
  errorCorrection?: EccLevel;
  align?: Align;
}

/**
 * Base64 PNG or JPEG, reduced to 1-bit.
 *
 * Requires `capabilities.features.imageSupport`; the 0.1 demo bridge rejects it with
 * `UNSUPPORTED_ELEMENT`.
 */
export interface ImageElement {
  type: 'image';
  data: string;
  widthDots?: number;
  align?: Align;
  dither?: 'NONE' | 'THRESHOLD' | 'FLOYD_STEINBERG';
}

/** Full-width rule. Not in the 0.1 demo bridge — see {@link ImageElement}. */
export interface LineElement {
  type: 'line';
  style?: 'solid' | 'dashed' | 'dotted';
  thicknessDots?: number;
}

/** Exactly one of `lines` or `dots`. */
export interface FeedElement {
  type: 'feed';
  lines?: number;
  dots?: number;
}

/** Silently ignored by a printer with no cutter. */
export interface CutElement {
  type: 'cut';
  mode?: 'FULL' | 'PARTIAL';
}

export type Element =
  | TextElement
  | BarcodeElement
  | QrElement
  | ImageElement
  | LineElement
  | FeedElement
  | CutElement;

export interface DslDocument {
  /** The connected printer's width is used when omitted — which is the safer default (DES-06 §8.1). */
  widthDots?: number;
  elements: Element[];
}

// ---------------------------------------------------------------- payload — the three tiers

/** Tier 1. Layout lives on the device, so a label change needs no web deployment (G-7). */
export interface TemplatePayload {
  tier: 'template';
  template: string;
  data: Record<string, string | number | boolean | null>;
  options?: PrintOptions;
}

/** Tier 2. `tier` is optional because the SDK stamps it — the bridge's discriminator always matches. */
export interface DslPayload {
  tier?: 'dsl';
  document: DslDocument;
  options?: PrintOptions;
}

/** Tier 3. Bytes reach the printer unmodified — no validation, no transformation (FR-306). */
export interface RawPayload {
  tier: 'raw';
  language: RawLanguage;
  /** Base64-encoded command bytes. See {@link toBase64} for a browser-safe encoder. */
  data: string;
  options?: PrintOptions;
}

export type PrintPayload = TemplatePayload | DslPayload | RawPayload;

// ---------------------------------------------------------------- jobs

/**
 * DES-07 §2. `FAILED` is terminal only when no retry is scheduled — the bridge owns that decision,
 * which is why {@link isTerminalState} does not treat it as ambiguous here.
 */
export type JobState =
  | 'QUEUED' | 'RENDERING' | 'SENDING' | 'RETRY_SCHEDULED'
  | 'PRINTED' | 'FAILED' | 'CANCELLED' | 'VERIFYING' | 'VERIFY_FAILED';

export interface Job {
  jobId: string;
  state: JobState;
  /** Present on the 0.1 bridge, which prints synchronously. */
  byteCount?: number;
  queuePosition?: number;
  idempotencyKey?: string;
  tier?: PrintPayload['tier'];
  templateName?: string;
  attemptCount?: number;
  maxAttempts?: number;
  nextRetryAt?: string;
  lastError?: BifrostError & { occurredAt?: string };
  createdAt?: string;
  updatedAt?: string;
  /** `true` when the idempotency key was already seen and nothing printed (FR-102). */
  deduplicated?: boolean;
}

export interface JobPage {
  jobs: Job[];
  nextCursor?: string;
  total?: number;
}

export interface JobQuery {
  state?: JobState | JobState[];
  limit?: number;
  cursor?: string;
  /** ISO-8601. */
  since?: string;
}

/** Terminal states, per DES-07 §2. A `FAILED` job with `nextRetryAt` set is not one of them. */
const TERMINAL: ReadonlySet<JobState> = new Set<JobState>([
  'PRINTED', 'CANCELLED', 'VERIFY_FAILED',
]);

export function isTerminalState(job: Pick<Job, 'state' | 'nextRetryAt'>): boolean {
  if (TERMINAL.has(job.state)) return true;
  // A failure with no retry scheduled is as final as it gets.
  return job.state === 'FAILED' && !job.nextRetryAt;
}

// ---------------------------------------------------------------- status and capabilities

export type PrinterState = 'READY' | 'CONNECTING' | 'DISCONNECTED' | 'NOT_CONFIGURED' | 'ERROR';

export interface BridgeInfo {
  version: string;
  apiVersion: string;
  uptimeSeconds?: number;
  paired: boolean;
}

export interface PrinterStatus {
  state: PrinterState;
  name?: string;
  transport?: string;
  language?: string;
  printWidthDots?: number;
  batteryPercent?: number;
  /**
   * A bare code on the 0.1 bridge, an error envelope from 1.0 onwards. Use
   * {@link lastErrorMessage} rather than reading it directly.
   */
  lastError?: string | (BifrostError & { occurredAt?: string }) | null;
}

export interface QueueStatus {
  pending: number;
  retrying: number;
  capacity?: number;
}

export interface BridgeStatus {
  bridge: BridgeInfo;
  /** Omitted by the bridge while unpaired. */
  printer?: PrinterStatus;
  queue?: QueueStatus;
}

/** Reads `printer.lastError` in either of the two shapes the bridge may send. */
export function lastErrorMessage(printer: PrinterStatus | undefined): string | undefined {
  const e = printer?.lastError;
  if (!e) return undefined;
  return typeof e === 'string' ? e : e.message;
}

export interface Capabilities {
  printer: {
    name: string;
    language: string;
    transport: string;
    firmwareVersion?: string;
  };
  media: {
    type: 'LABEL_GAP' | 'LABEL_BLACKMARK' | 'CONTINUOUS' | 'LINERLESS';
    printWidthDots: number;
    printWidthMm?: number;
    dpi: number;
    maxLengthDots?: number;
  };
  features: {
    cutter: boolean;
    statusQuery: boolean;
    batteryReport: boolean;
    imageSupport: boolean;
    maxImageWidthDots?: number;
  };
  barcodes: string[];
  fonts?: Array<{ id: string; widthDots: number; heightDots: number }>;
}

export interface PairInfo {
  paired: boolean;
  origin: string;
  pairedAt: string;
}

export interface TemplateInfo {
  name: string;
  version: number;
  description?: string;
  requiredFields: string[];
  optionalFields?: string[];
  updatedAt?: string;
}

export interface PreviewImage {
  /** Base64 PNG, ready for `img.src = 'data:image/png;base64,' + image`. */
  image: string;
  format: string;
  widthPx: number;
  heightPx: number;
}

// ---------------------------------------------------------------- events

export interface BifrostEventMap {
  'job.state_changed': {
    jobId: string;
    state: JobState;
    previousState?: JobState;
    attemptCount?: number;
    error?: BifrostError;
  };
  'job.verified': { jobId: string; verified: boolean; scannedValue?: string };
  'printer.state_changed': { state: PrinterState; name?: string; transport?: string };
  'printer.error': { code: string; message: string; transient: boolean };
  'printer.battery': { percent: number };
  'queue.changed': { pending: number; retrying: number };
  'bridge.shutdown': { reason: string };
  /** Synthesised by the SDK from the socket's own health — never sent by the bridge. */
  'connection.changed': { connected: boolean };
}

export type BifrostEventName = keyof BifrostEventMap;

/** The envelope every server message arrives in (DES-03 §3.10). */
export interface BifrostEventEnvelope<K extends BifrostEventName = BifrostEventName> {
  event: K;
  timestamp: string;
  data: BifrostEventMap[K];
}

export type Unsubscribe = () => void;

// ---------------------------------------------------------------- client surface

export interface PrintCallOptions {
  /** Auto-generated per call when omitted (FR-705). Supply one to make a user's retry safe. */
  idempotencyKey?: string;
  copies?: number;
  /** Overrides the client default. */
  waitForCompletion?: boolean;
  signal?: AbortSignal;
}

/**
 * What both {@link BifrostClient} and `MockBifrostClient` satisfy.
 *
 * Typing against this rather than the concrete class is what lets a web app's tests run with
 * neither a bridge nor a printer present (NFR-602).
 */
export interface IBifrostClient {
  isAvailable(): Promise<boolean>;
  getStatus(): Promise<Result<BridgeStatus>>;
  pair(token: string, clientName?: string): Promise<Result<PairInfo>>;
  getCapabilities(): Promise<Result<Capabilities>>;
  print(payload: PrintPayload, options?: PrintCallOptions): Promise<Result<Job>>;
  preview(payload: PrintPayload, previewScale?: number): Promise<Result<PreviewImage>>;
  getJob(jobId: string): Promise<Result<Job>>;
  listJobs(query?: JobQuery): Promise<Result<JobPage>>;
  cancelJob(jobId: string): Promise<Result<Job>>;
  getTemplates(): Promise<Result<TemplateInfo[]>>;
  on<K extends BifrostEventName>(
    event: K,
    handler: (data: BifrostEventMap[K]) => void,
  ): Unsubscribe;
  close(): void;
}
