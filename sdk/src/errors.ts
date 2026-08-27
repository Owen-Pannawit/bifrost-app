/**
 * The result and error model — DES-04 §6.
 *
 * Nothing in this SDK throws for a condition the warehouse can produce. A missing bridge, an empty
 * paper roll and a printer out of range are all normal states on a shop floor, so they arrive as
 * values the type system forces the caller to handle (FR-706, FR-708).
 */

export type Result<T> =
  | { ok: true; value: T }
  | { ok: false; error: BifrostError };

export interface BifrostError {
  code: BifrostErrorCode;
  /** Plain English. For printer faults this is safe to show an operator verbatim (NFR-501). */
  message: string;
  /** Whether a retry could succeed. */
  transient: boolean;
  /** JSON path to the offending field, for validation errors (FR-308). */
  field?: string;
  /** Optional structured context, passed through from the bridge untouched. */
  details?: unknown;
}

/**
 * Mirrors the bridge's error table (DES-03 §4.1) plus the codes the SDK raises locally, which the
 * bridge can never send because they describe the bridge itself being unreachable.
 */
export type BifrostErrorCode =
  // raised by the SDK, never by the bridge
  | 'BRIDGE_UNAVAILABLE' | 'BRIDGE_TIMEOUT' | 'JOB_TIMEOUT' | 'REQUEST_ABORTED'
  // access
  | 'UNAUTHORIZED' | 'ORIGIN_NOT_ALLOWED' | 'INVALID_TOKEN'
  | 'PAIRING_EXPIRED' | 'PAIRING_ALREADY_USED'
  // payload
  | 'VALIDATION_ERROR' | 'MALFORMED_JSON' | 'CONTENT_TOO_WIDE' | 'UNSUPPORTED_ELEMENT'
  | 'MISSING_TEMPLATE_FIELD' | 'TEMPLATE_NOT_FOUND' | 'PAYLOAD_TOO_LARGE'
  // printer
  | 'PRINTER_NOT_CONNECTED' | 'PRINTER_DISCONNECTED' | 'PRINTER_OUT_OF_PAPER'
  | 'PRINTER_COVER_OPEN' | 'PRINTER_BATTERY_LOW' | 'PRINTER_OVERHEATED'
  | 'PRINTER_PAPER_JAM' | 'PRINTER_UNSUPPORTED_COMMAND' | 'TRANSMIT_TIMEOUT'
  // queue and job
  | 'QUEUE_FULL' | 'JOB_NOT_FOUND' | 'JOB_NOT_CANCELLABLE'
  // other
  | 'INTERNAL_ERROR' | 'BRIDGE_NOT_READY' | 'NOT_FOUND';

export function ok<T>(value: T): Result<T> {
  return { ok: true, value };
}

export function fail<T = never>(error: BifrostError): Result<T> {
  return { ok: false, error };
}

/**
 * The errors the SDK synthesises. They are collected here so their wording is written once: an
 * operator who sees two different sentences for one fault reports it as two faults.
 */
export const SdkErrors = {
  unavailable: (): BifrostError => ({
    code: 'BRIDGE_UNAVAILABLE',
    message: 'Print bridge not running. Open BifrǫstApp on this device.',
    transient: true,
  }),

  timeout: (): BifrostError => ({
    code: 'BRIDGE_TIMEOUT',
    message: 'The print bridge did not respond.',
    transient: true,
  }),

  aborted: (): BifrostError => ({
    code: 'REQUEST_ABORTED',
    message: 'The print request was cancelled.',
    transient: false,
  }),

  jobTimeout: (jobId: string): BifrostError => ({
    code: 'JOB_TIMEOUT',
    message: 'The job was accepted but did not finish in time. Check the app for its status.',
    transient: true,
    details: { jobId },
  }),

  /** A failure response whose body carried no error envelope — a bridge behaving badly. */
  unrecognised: (status: number): BifrostError => ({
    code: status === 404 ? 'NOT_FOUND' : 'INTERNAL_ERROR',
    message:
      status === 404
        ? 'This bridge build does not provide that endpoint.'
        : `Bridge returned ${status}.`,
    transient: status >= 500,
  }),
} as const;

/** Narrowing helper for callers that would rather branch than switch. */
export function isTransient(error: BifrostError): boolean {
  return error.transient;
}
