import { defineConfig } from 'tsup';

/**
 * Two builds, because two kinds of consumer.
 *
 * The bundler build is per-entry so a React application does not ship the Angular adapter and vice
 * versa. The script-tag build is one file at a lower target: it is what a Razor view or a WebForms
 * page loads, and those run wherever the customer's browser happens to be, so it must parse in an
 * engine older than the one a bundler would target.
 */
export default defineConfig([
  {
    entry: {
      index: 'src/index.ts',
      react: 'src/react.ts',
      angular: 'src/angular.ts',
      testing: 'src/testing.ts',
    },
    format: ['esm', 'cjs'],
    target: 'es2020',
    dts: true,
    minify: true,
    sourcemap: true,
    clean: true,
    // Peer dependencies only — the core has none, and this keeps it that way.
    external: ['react'],
  },
  {
    entry: { index: 'src/index.ts' },
    format: ['iife'],
    globalName: 'Bifrost',
    target: 'es2017',
    minify: true,
    sourcemap: true,
    // The single-file build carries the core only; the framework adapters have no meaning without
    // a module system to import them from.
    external: ['react'],
  },
]);
