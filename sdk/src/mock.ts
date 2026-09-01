/**
 * An in-memory bridge — DES-04 §10.
 *
 * A web app's tests should not need an Android device, a Bluetooth radio and a roll of labels to
 * assert that pressing Print sends the right payload (NFR-602). This implements the same interface
 * with no network at all, so the application code under test is the real code.
 */

import { type BifrostError, type Result, fail, ok } from './errors.js';
import { EventStream } from './events.js';
import type {
  BifrostEventMap,
  BifrostEventName,
  BridgeStatus,
  Capabilities,
  IBifrostClient,
  Job,
  JobPage,
  JobQuery,
  JobState,
  PairInfo,
  PreviewImage,
  PrintCallOptions,
  PrintPayload,
  PrinterState,
  TemplateInfo,
  Unsubscribe,
} from './types.js';

export interface PrintedJob {
  job: Job;
  payload: PrintPayload;
  options: PrintCallOptions;
  idempotencyKey: string;
}

export interface MockBifrostOptions {
  /** Default `READY`. Anything else makes `print()` fail the way the real bridge would. */
  printerState?: PrinterState;
  /** Merged over the defaults, so a test can set just `media.printWidthDots`. */
  capabilities?: DeepPartial<Capabilities>;
  templates?: TemplateInfo[];
  /** Default `true`. Set `false` to exercise the "bridge not running" path. */
  available?: boolean;
  paired?: boolean;
  bridgeVersion?: string;
  /** The state a submitted job reports. Default `PRINTED`. */
  jobState?: JobState;
}

type DeepPartial<T> = { [K in keyof T]?: T[K] extends object ? DeepPartial<T[K]> : T[K] };

const DEFAULT_CAPABILITIES: Capabilities = {
  printer: { name: 'Mock Printer', language: 'ESCPOS', transport: 'BT_CLASSIC' },
  media: { type: 'CONTINUOUS', printWidthDots: 576, printWidthMm: 72, dpi: 203 },
  features: { cutter: false, statusQuery: true, batteryReport: true, imageSupport: false },
  barcodes: ['CODE128', 'CODE39', 'EAN13', 'ITF', 'UPCA', 'QR'],
};

export class MockBifrostClient implements IBifrostClient {
  /** Every job submitted, in order. The assertion surface most tests want. */
  readonly printedJobs: PrintedJob[] = [];

  private readonly stream = new EventStream({ url: () => null });
  private readonly capabilities: Capabilities;
  private readonly templates: TemplateInfo[];
  private readonly bridgeVersion: string;
  private readonly jobState: JobState;

  private printerState: PrinterState;
  private available: boolean;
  private paired: boolean;
  private nextError: BifrostError | null = null;
  private sequence = 0;

  constructor(options: MockBifrostOptions = {}) {
    this.printerState = options.printerState ?? 'READY';
    this.available = options.available ?? true;
    this.paired = options.paired ?? true;
    this.bridgeVersion = options.bridgeVersion ?? '1.0.0';
    this.jobState = options.jobState ?? 'PRINTED';
    this.templates = options.templates ?? [];
    this.capabilities = merge(DEFAULT_CAPABILITIES, options.capabilities);
  }

  // ---------------------------------------------------------------- test controls

  /** Deliver an event to subscribers as though the bridge had sent it. */
  simulate<K extends BifrostEventName>(event: K, data: BifrostEventMap[K]): void {
    this.stream.emit(event, data);
  }

  /** Move the printer, emitting the event a real bridge would. */
  setPrinterState(state: PrinterState, name?: string): void {
    this.printerState = state;
    this.simulate('printer.state_changed', {
      state,
      ...(name ? { name } : { name: this.capabilities.printer.name }),
    });
  }

  /** Make the bridge appear stopped, so the "open the app" path can be tested. */
  setAvailable(available: boolean): void {
    this.available = available;
  }

  /** The next call — any call — fails with this. Cleared once used. */
  failNext(error: BifrostError): void {
    this.nextError = error;
  }

  /** The most recent submission, for the common single-print assertion. */
  get lastPrint(): PrintedJob | undefined {
    return this.printedJobs[this.printedJobs.length - 1];
  }

  reset(): void {
    this.printedJobs.length = 0;
    this.nextError = null;
    this.sequence = 0;
  }

  // ---------------------------------------------------------------- IBifrostClient

  async isAvailable(): Promise<boolean> {
    return this.available;
  }

  async getStatus(): Promise<Result<BridgeStatus>> {
    const taken = this.take<BridgeStatus>();
    if (taken) return taken;

    return ok<BridgeStatus>({
      bridge: { version: this.bridgeVersion, apiVersion: 'v1', paired: this.paired },
      printer: {
        state: this.printerState,
        name: this.capabilities.printer.name,
        transport: this.capabilities.printer.transport,
        language: this.capabilities.printer.language,
        printWidthDots: this.capabilities.media.printWidthDots,
      },
      queue: { pending: 0, retrying: 0, capacity: 500 },
    });
  }

  async pair(token: string, _clientName?: string): Promise<Result<PairInfo>> {
    const taken = this.take<PairInfo>();
    if (taken) return taken;

    if (!token) {
      return fail<PairInfo>({
        code: 'INVALID_TOKEN',
        message: 'That pairing code was not recognised. Scan the QR code again.',
        transient: false,
      });
    }

    this.paired = true;
    return ok<PairInfo>({
      paired: true,
      origin: globalThis.location?.origin ?? 'http://localhost',
      pairedAt: new Date().toISOString(),
    });
  }

  async getCapabilities(): Promise<Result<Capabilities>> {
    const taken = this.take<Capabilities>();
    if (taken) return taken;

    return this.printerState === 'READY'
      ? ok(this.capabilities)
      : fail<Capabilities>(NOT_CONNECTED);
  }

  async print(payload: PrintPayload, options: PrintCallOptions = {}): Promise<Result<Job>> {
    const taken = this.take<Job>();
    if (taken) return taken;

    if (!this.available) {
      return fail<Job>({
        code: 'BRIDGE_UNAVAILABLE',
        message: 'Print bridge not running. Open BifrǫstApp on this device.',
        transient: true,
      });
    }

    if (this.printerState !== 'READY') return fail<Job>(NOT_CONNECTED);

    const idempotencyKey = options.idempotencyKey ?? `mock-key-${++this.sequence}`;

    // Deduplication is part of the contract a test may depend on (FR-102): a component that retries
    // on click must not print twice, and that is only assertable if the mock behaves this way too.
    const seen = this.printedJobs.find((p) => p.idempotencyKey === idempotencyKey);
    if (seen) return ok<Job>({ ...seen.job, deduplicated: true });

    const job: Job = {
      jobId: `job_mock_${String(this.printedJobs.length + 1).padStart(6, '0')}`,
      state: this.jobState,
      idempotencyKey,
      createdAt: new Date().toISOString(),
      ...(payload.tier ? { tier: payload.tier } : { tier: 'dsl' as const }),
      ...(payload.tier === 'template' ? { templateName: payload.template } : {}),
    };

    this.printedJobs.push({ job, payload, options, idempotencyKey });
    this.simulate('job.state_changed', { jobId: job.jobId, state: job.state, previousState: 'QUEUED' });

    return ok(job);
  }

  async preview(_payload: PrintPayload, _previewScale?: number): Promise<Result<PreviewImage>> {
    const taken = this.take<PreviewImage>();
    if (taken) return taken;

    return ok<PreviewImage>({
      image: '',
      format: 'png',
      widthPx: this.capabilities.media.printWidthDots,
      heightPx: 0,
    });
  }

  async getJob(jobId: string): Promise<Result<Job>> {
    const taken = this.take<Job>();
    if (taken) return taken;

    const found = this.printedJobs.find((p) => p.job.jobId === jobId);
    return found
      ? ok(found.job)
      : fail<Job>({ code: 'JOB_NOT_FOUND', message: 'No such job.', transient: false });
  }

  async listJobs(query: JobQuery = {}): Promise<Result<JobPage>> {
    const taken = this.take<JobPage>();
    if (taken) return taken;

    const wanted = query.state === undefined ? null : new Set(([] as JobState[]).concat(query.state));
    const jobs = this.printedJobs
      .map((p) => p.job)
      .filter((job) => wanted === null || wanted.has(job.state))
      .slice(0, query.limit ?? 50);

    return ok<JobPage>({ jobs, total: jobs.length });
  }

  async cancelJob(jobId: string): Promise<Result<Job>> {
    const taken = this.take<Job>();
    if (taken) return taken;

    const found = this.printedJobs.find((p) => p.job.jobId === jobId);
    if (!found) {
      return fail<Job>({ code: 'JOB_NOT_FOUND', message: 'No such job.', transient: false });
    }

    if (found.job.state !== 'QUEUED') {
      return fail<Job>({
        code: 'JOB_NOT_CANCELLABLE',
        message: 'That job is already printing or finished.',
        transient: false,
      });
    }

    found.job.state = 'CANCELLED';
    this.simulate('job.state_changed', {
      jobId,
      state: 'CANCELLED',
      previousState: 'QUEUED',
    });

    return ok(found.job);
  }

  async getTemplates(): Promise<Result<TemplateInfo[]>> {
    const taken = this.take<TemplateInfo[]>();
    return taken ?? ok(this.templates);
  }

  on<K extends BifrostEventName>(event: K, handler: (data: BifrostEventMap[K]) => void): Unsubscribe {
    return this.stream.on(event, handler);
  }

  close(): void {
    this.stream.close();
  }

  private take<T>(): Result<T> | null {
    const error = this.nextError;
    if (!error) return null;
    this.nextError = null;
    return fail<T>(error);
  }
}

const NOT_CONNECTED: BifrostError = {
  code: 'PRINTER_NOT_CONNECTED',
  message: 'No printer is connected. Open BifrǫstApp and connect one.',
  transient: true,
};

function merge<T>(base: T, override: DeepPartial<T> | undefined): T {
  if (!override) return base;

  const result = { ...base } as Record<string, unknown>;
  for (const [key, value] of Object.entries(override as Record<string, unknown>)) {
    if (value === undefined) continue;
    const existing = result[key];
    result[key] =
      isPlainObject(existing) && isPlainObject(value)
        ? merge(existing, value as DeepPartial<typeof existing>)
        : value;
  }

  return result as T;
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
