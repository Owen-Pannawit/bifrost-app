/**
 * `@bearing/bifrost-sdk/testing` — the in-memory bridge.
 *
 * Kept as its own entry point so a production bundle never pulls the mock in by accident.
 */

export { MockBifrostClient, type MockBifrostOptions, type PrintedJob } from './mock.js';
export type { IBifrostClient } from './types.js';
