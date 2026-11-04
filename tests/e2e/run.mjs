#!/usr/bin/env node
// Browser smoke tests (DESIGN.md §24.7). Plain Node, no packages: headless Firefox over WebDriver BiDi.
//
//   node tests/e2e/run.mjs --app <publish dir>/Telrang.Web.dll [--artifacts <dir>] [--only <flow,...>]
//   node tests/e2e/run.mjs --app <release dir>/Telrang.Web ...     # a self-contained build (DESIGN.md §29)
//   node tests/e2e/run.mjs --image telrang:1.2.0 ...               # the Docker image
//   node tests/e2e/run.mjs --base https://localhost --container <name>   # a running container behind a proxy (scripts/npm-stack-test.sh)
//
// Usually run through scripts/e2e.sh, which publishes the app first. FIREFOX names the browser binary (default
// `firefox` on PATH). The app runs in the Production environment on http://localhost:<free port> with a scratch data
// directory; the flows share it and run in order (later flows use the admin and the polls of earlier ones).
// With --base the app is not started: it already runs (fresh, no admin yet) in the container named by --container, whose
// log is followed. --ca <pem> (default NODE_EXTRA_CA_CERTS, which Node's own checks use) is the test CA that signed the
// proxy's certificate, trusted in the browser profile; --resolve <address> makes the base URL's host name (a test name) stand for
// that address in the browser and in Node.
// Exit code 0 when every flow passed. The artifacts directory gets app.log, failure screenshots and results.json.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { parseArgs } from 'node:util';
import { attachApp, startApp } from './lib/app.mjs';
import { startFirefox } from './lib/bidi.mjs';
import { createRun } from './lib/harness.mjs';
import { flows } from './flows/index.mjs';

const { values: args } = parseArgs({
  options: {
    app: { type: 'string', default: process.env.E2E_APP },
    image: { type: 'string', default: process.env.E2E_IMAGE },
    base: { type: 'string', default: process.env.E2E_BASE },
    container: { type: 'string', default: process.env.E2E_CONTAINER },
    resolve: { type: 'string', default: process.env.E2E_RESOLVE },
    ca: { type: 'string', default: process.env.NODE_EXTRA_CA_CERTS },
    artifacts: { type: 'string', default: process.env.E2E_ARTIFACTS },
    only: { type: 'string' },
  },
});
if (args.base ? !args.container : !args.image && (!args.app || !fs.existsSync(args.app))) {
  console.error('usage: node tests/e2e/run.mjs --app <publish dir>/Telrang.Web.dll | --image <docker image> | --base <url> --container <name> [--artifacts <dir>] [--only <flow,...>]');
  process.exit(2);
}

const scratch = fs.mkdtempSync(path.join(os.tmpdir(), 'telrang-e2e-'));
const artifacts = path.resolve(args.artifacts ?? path.join(scratch, 'artifacts'));
fs.mkdirSync(artifacts, { recursive: true });
const started = Date.now();
let app = null, browser = null, ok = false;

const cleanup = async () => {
  if (browser) { const b = browser; browser = null; await b.close().catch(() => { }); }
  if (app) { const a = app; app = null; await a.stop().catch(() => { }); }
  // The scratch data directory and Firefox profile go; the artifacts stay.
  const keep = artifacts.startsWith(scratch + path.sep);
  for (const p of keep ? [path.join(scratch, 'data'), path.join(scratch, 'firefox')] : [scratch]) {
    try { fs.rmSync(p, { recursive: true, force: true }); } catch { }
  }
};
for (const sig of ['SIGINT', 'SIGTERM']) process.on(sig, async () => { await cleanup(); process.exit(130); });

try {
  app = args.base
    ? await attachApp({ base: args.base.replace(/\/$/, ''), container: args.container, logFile: path.join(artifacts, 'app.log'), resolveTo: args.resolve })
    : await startApp({ dll: args.image ? null : path.resolve(args.app), image: args.image, dataDir: path.join(scratch, 'data'), logFile: path.join(artifacts, 'app.log') });
  console.log(`app      ${app.base}  (log: ${path.join(artifacts, 'app.log')})`);
  browser = await startFirefox({ binary: process.env.FIREFOX || 'firefox', profileDir: path.join(scratch, 'firefox'), trustCert: args.base && args.ca ? path.resolve(args.ca) : null,
    localDomains: args.base && args.resolve ? [new URL(args.base).hostname] : [] });
  console.log(`browser  ${browser.version}`);

  const run = createRun({ app, browser, artifacts });
  const only = args.only?.split(',').map(s => s.trim());
  let blocked = null;
  for (const flow of flows) {
    if (only && !only.includes(flow.name) && !flow.required) continue;
    if (flow.when && !flow.when()) continue;
    if (blocked) { console.log(`\n== ${flow.name}: skipped (${blocked} stopped)`); continue; }
    const { stopped } = await run.flow(flow.name, flow.run);
    if (flow.required && stopped) blocked = flow.name;
  }
  ok = run.summary() && !blocked;
} catch (e) {
  console.error(`\nharness error: ${e.stack ?? e.message}`);
  ok = false;
} finally {
  await cleanup();
}
console.log(`\n${ok ? 'PASSED' : 'FAILED'} in ${((Date.now() - started) / 1000).toFixed(1)} s; artifacts: ${artifacts}`);
process.exit(ok ? 0 : 1);
