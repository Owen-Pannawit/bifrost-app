/**
 * Bifrǫst SDK — print to a Bluetooth printer from an ordinary web page.
 *
 * The bridge runs on the same device as the browser, so this talks to 127.0.0.1. Chrome treats
 * loopback as a potentially trustworthy origin, which is why an HTTPS page can call it without a
 * certificate on the device and without a mixed-content block (ADR-001).
 *
 * Framework-agnostic by construction: one class, plain promises, and an `on()` that returns its own
 * unsubscribe. React and Angular helpers live in `@bearing/bifrost-sdk/react` and `/angular`; a
 * `<script>` tag build that exposes the global `Bifrost` lives at `/global`, which is what Razor,
 * WebForms and any framework without a bundler use.
 *
 * @packageDocumentation
 */

export { BifrostClient, API_VERSION, type BifrostOptions } from './client.js';

export {
  type BifrostError,
  type BifrostErrorCode,
  type Result,
  isTransient,
  fail,
  ok,
} from './errors.js';

export { doc, template, raw, toBase64, type DocumentBuilder } from './builder.js';

export { EventStream, type EventStreamConfig } from './events.js';

export {
  createBifrostStore,
  type BifrostState,
  type BifrostStore,
  type BifrostStoreOptions,
} from './store.js';

export { createMemoryTokenStore, createTokenStore, type TokenStore } from './storage.js';

export {
  isTerminalState,
  lastErrorMessage,
  type Align,
  type BarcodeElement,
  type BarcodeFormat,
  type BifrostEventEnvelope,
  type BifrostEventMap,
  type BifrostEventName,
  type BridgeInfo,
  type BridgeStatus,
  type Capabilities,
  type CutElement,
  type DslDocument,
  type DslPayload,
  type EccLevel,
  type Element,
  type FeedElement,
  type IBifrostClient,
  type ImageElement,
  type Job,
  type JobPage,
  type JobQuery,
  type JobState,
  type LineElement,
  type PairInfo,
  type PreviewImage,
  type PrintCallOptions,
  type PrintOptions,
  type PrintPayload,
  type PrinterState,
  type PrinterStatus,
  type QrElement,
  type QueueStatus,
  type RawLanguage,
  type RawPayload,
  type TemplateInfo,
  type TemplatePayload,
  type TextElement,
  type Unsubscribe,
} from './types.js';

// The mock ships in the main bundle as well as at `/testing`, because the plain <script> build has
// no sub-path imports and a demo page benefits from being runnable with no bridge at all.
export {
  MockBifrostClient,
  type MockBifrostOptions,
  type PrintedJob,
} from './mock.js';
