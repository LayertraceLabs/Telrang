// Headless Firefox over WebDriver BiDi (built into Firefox; no geckodriver, no npm packages). Node's built-in WebSocket.
import { spawn, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

export const sleep = ms => new Promise(r => setTimeout(r, ms));

// Runs in every page before its own scripts (main realm). Reports to the harness over a BiDi channel:
// CSP violations, Blazor's reconnect states (a dropped circuit) and clipboard writes with the click's user activation.
// It also counts the Blazor circuit's WebSocket messages, so a flow can wait until an interactive page is wired up.
const PRELOAD = `(send) => {
  const report = (kind, data) => { try { send(JSON.stringify({ kind, url: location.href, ...data })); } catch { } };
  document.addEventListener('securitypolicyviolation', e =>
    report('csp', { directive: e.violatedDirective, blocked: e.blockedURI, source: e.sourceFile + ':' + e.lineNumber }), true);
  // Leaving the page closes the circuit's socket, and Blazor may start to reconnect before the page is gone; those
  // states are not a dropped circuit.
  let leaving = false;
  addEventListener('beforeunload', () => { leaving = true; }, true);
  addEventListener('pagehide', () => { leaving = true; }, true);
  document.addEventListener('components-reconnect-state-changed', e => report('reconnect', { state: e.detail?.state, leaving }), true);
  window.__e2e = { circuit: { opened: false, closed: false, messages: 0, last: 0 }, copies: [] };
  const NativeWebSocket = window.WebSocket;
  window.WebSocket = class extends NativeWebSocket {
    constructor(url, protocols) {
      super(url, protocols);
      if (String(url).includes('/_blazor')) {
        const c = window.__e2e.circuit;
        this.addEventListener('open', () => { c.opened = true; c.last = Date.now(); });
        this.addEventListener('message', () => { c.messages++; c.last = Date.now(); });
        this.addEventListener('close', () => { c.closed = true; });
      }
    }
  };
  if (navigator.clipboard?.writeText) {
    const write = navigator.clipboard.writeText.bind(navigator.clipboard);
    navigator.clipboard.writeText = text => {
      const entry = { text, active: navigator.userActivation.isActive };
      window.__e2e.copies.push(entry);
      return write(text).then(() => { entry.ok = true; }, e => { entry.error = String(e); throw e; });
    };
  }
}`;

export async function startFirefox({ binary = 'firefox', profileDir, timeZone = 'Europe/Amsterdam', trustCert = null, localDomains = [] }) {
  fs.mkdirSync(profileDir, { recursive: true });
  // A test CA (scripts/npm-stack-test.sh) is trusted in the profile's certificate store, not merely accepted: WebAuthn
  // refuses a page whose certificate error was overridden. Needs certutil (NSS tools).
  if (trustCert) {
    for (const args of [['-N', '--empty-password', '-d', `sql:${profileDir}`], ['-A', '-n', 'e2e-ca', '-t', 'C,,', '-i', trustCert, '-d', `sql:${profileDir}`]]) {
      const r = spawnSync('certutil', args, { encoding: 'utf8' });
      if (r.status !== 0) throw new Error(`certutil ${args[0]} failed (NSS tools needed for --ca): ${r.error?.message ?? r.stderr}`);
    }
  }
  fs.writeFileSync(path.join(profileDir, 'user.js'), [
    'user_pref("browser.shell.checkDefaultBrowser", false);',
    'user_pref("datareporting.policy.dataSubmissionEnabled", false);',
    'user_pref("toolkit.telemetry.reportingpolicy.firstRun", false);',
    'user_pref("app.update.disabledForTesting", true);',
    'user_pref("browser.aboutwelcome.enabled", false);',
    // Light, so the theme flow starts from a known OS preference.
    'user_pref("layout.css.prefers-color-scheme.content-override", 1);',
    // Test host names that stand for loopback (scripts/npm-stack-test.sh: NPM refuses "localhost" as a proxy host).
    ...(localDomains.length ? [`user_pref("network.dns.localDomains", ${JSON.stringify(localDomains.join(','))});`] : []),
  ].join('\n') + '\n');
  // Port 0: Firefox picks a free port and prints it. -remote-allow-system-access lets browsingContext.setViewport work.
  const proc = spawn(binary, ['--headless', '--no-remote', '--profile', profileDir, '--remote-debugging-port', '0', '-remote-allow-system-access'],
    { stdio: ['ignore', 'pipe', 'pipe'], env: { ...process.env, TZ: timeZone, MOZ_CRASHREPORTER_DISABLE: '1' } });
  let output = '';
  const wsUrl = await new Promise((resolve, reject) => {
    const t = setTimeout(() => reject(new Error('Firefox did not start WebDriver BiDi within 60 s:\n' + output.slice(-2000))), 60_000);
    const on = d => {
      output += d;
      const m = /WebDriver BiDi listening on (ws:\/\/[^\s]+)/.exec(output);
      if (m) { clearTimeout(t); resolve(m[1]); }
    };
    proc.stdout.on('data', on);
    proc.stderr.on('data', on);
    proc.on('exit', code => { clearTimeout(t); reject(new Error(`Firefox exited (${code}):\n${output.slice(-2000)}`)); });
  });
  const ws = new WebSocket(wsUrl.replace(/\/$/, '') + '/session');
  await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = () => reject(new Error('BiDi WebSocket failed')); });

  let id = 0;
  const pending = new Map();
  const browser = {
    proc,
    /** Every console / JavaScript log entry, CSP report and reconnect state, in order. */
    events: [],
    /** Every request the browser sent: { context, method, url }. */
    requests: [],
    pages: new Set(),
  };
  browser.send = (method, params = {}) => new Promise((resolve, reject) => {
    const i = ++id;
    pending.set(i, { resolve, reject: e => reject(new Error(`${method}: ${e}`)) });
    ws.send(JSON.stringify({ id: i, method, params }));
  });
  ws.onmessage = e => {
    const m = JSON.parse(e.data);
    if (m.id && pending.has(m.id)) {
      const { resolve, reject } = pending.get(m.id);
      pending.delete(m.id);
      m.type === 'error' ? reject(`${m.error}: ${m.message}`) : resolve(m.result);
    } else if (m.method === 'log.entryAdded') {
      const p = m.params;
      const where = (p.stackTrace?.callFrames ?? []).slice(0, 2).map(f => `${f.url}:${f.lineNumber + 1}`).join(' ');
      browser.events.push({ kind: 'log', level: p.level, type: p.type, text: p.text, where, context: p.source?.context });
    } else if (m.method === 'script.message') {
      try { browser.events.push({ ...JSON.parse(m.params.data.value), context: m.params.source?.context }); } catch { }
    } else if (m.method === 'network.beforeRequestSent') {
      browser.requests.push({ context: m.params.context, method: m.params.request.method, url: m.params.request.url });
      if (m.params.isBlocked) {
        const fail = browser.failing?.(m.params.request.url);
        browser.send(fail ? 'network.failRequest' : 'network.continueRequest', { request: m.params.request.request }).catch(() => { });
      }
    }
  };

  const session = await browser.send('session.new', { capabilities: {} });
  browser.version = `${session.capabilities?.browserName ?? 'firefox'} ${session.capabilities?.browserVersion ?? ''}`.trim();
  await browser.send('session.subscribe', { events: ['log.entryAdded', 'script.message', 'network.beforeRequestSent'] });
  await browser.send('script.addPreloadScript', { functionDeclaration: PRELOAD, arguments: [{ type: 'channel', value: { channel: 'e2e' } }] });

  /** A new tab; `userContext` gives a separate cookie jar (another visitor). */
  browser.newPage = async ({ base, userContext, width = 1280, height = 900 } = {}) => {
    const { context } = await browser.send('browsingContext.create', { type: 'tab', ...(userContext ? { userContext } : {}) });
    const page = new Page(browser, context, base);
    await page.viewport(width, height);
    browser.pages.add(page);
    return page;
  };
  /** Makes every request whose URL passes `test` fail (a network error), uncached; returns the undo function. */
  browser.failRequests = async test => {
    const { intercept } = await browser.send('network.addIntercept', { phases: ['beforeRequestSent'] });
    browser.failing = test;
    await browser.send('network.setCacheBehavior', { cacheBehavior: 'bypass' });
    let undone = false;
    return async () => {
      if (undone) return;
      undone = true;
      browser.failing = null;
      await browser.send('network.removeIntercept', { intercept });
      await browser.send('network.setCacheBehavior', { cacheBehavior: 'default' });
    };
  };
  browser.newUserContext = async () => (await browser.send('browser.createUserContext', {})).userContext;
  browser.removeUserContext = userContext => browser.send('browser.removeUserContext', { userContext });
  browser.close = async () => {
    try { await browser.send('session.end'); } catch { }
    try { ws.close(); } catch { }
    proc.kill('SIGTERM');
    const t = Date.now();
    while (proc.exitCode === null && proc.signalCode === null && Date.now() - t < 5000) await sleep(100);
    if (proc.exitCode === null && proc.signalCode === null) proc.kill('SIGKILL');
  };
  return browser;
}

export class Page {
  constructor(browser, ctx, base) { this.browser = browser; this.ctx = ctx; this.base = base; }

  async viewport(width, height) {
    await this.browser.send('browsingContext.setViewport', { context: this.ctx, viewport: { width, height } });
  }

  url(p) { return p.startsWith('http') ? p : this.base + p; }

  /**
   * Lets the current page load to the end before anything that may leave it (goto, a click on a link or submit): Firefox
   * reports the module scripts of a page left mid-load as integrity failures (empty-body hash), over HTTPS with brotli
   * (scripts/npm-stack-test.sh) and, more rarely, on plain HTTP (a CI run, setup flow, "Continue" on the recovery codes).
   */
  async loaded() {
    // Lazy modules (vote.js marks <html data-enhancing> while they load) count too: leaving the page mid-import makes
    // Firefox report an integrity failure with the empty hash.
    await this.waitFor(`document.readyState === 'complete' && !document.documentElement.dataset.enhancing`,
      'the current page to finish loading', 10_000).catch(() => { });
  }

  async goto(p) {
    await this.loaded();
    await this.browser.send('browsingContext.navigate', { context: this.ctx, url: this.url(p), wait: 'complete' });
  }

  async reload() { await this.browser.send('browsingContext.reload', { context: this.ctx, wait: 'complete' }); }

  /** Brings the tab to the front (the others become hidden: visibilitychange). */
  async activate() { await this.browser.send('browsingContext.activate', { context: this.ctx }); }

  async close() { this.browser.pages.delete(this); await this.browser.send('browsingContext.close', { context: this.ctx }); }

  /** Evaluates an expression (promises awaited) and returns its JSON-able value. */
  async eval(expr, { userActivation = false } = {}) {
    const r = await this.browser.send('script.evaluate', { expression: expr, target: { context: this.ctx }, awaitPromise: true, userActivation });
    if (r.type === 'exception') throw new Error(`${r.exceptionDetails.text} in: ${expr.slice(0, 160)}`);
    return deserialize(r.result);
  }

  /** Waits until `expr` is truthy; throws `what` (or the expression) on timeout. Navigation in between is fine. */
  async waitFor(expr, what = expr, ms = 15_000) {
    const end = Date.now() + ms;
    let last = null;
    while (Date.now() < end) {
      try { const v = await this.eval(expr); if (v) return v; } catch (e) { last = e; }
      await sleep(100);
    }
    throw new Error(`timed out waiting for ${what}${last ? ` (last error: ${last.message})` : ''}`);
  }

  /**
   * Waits until the page's Blazor circuit is connected and has been quiet for a moment, so event handlers are
   * attached. Static pages (no circuit) return at once.
   */
  async interactive(ms = 20_000) {
    const hasCircuit = await this.eval(`!!document.querySelector('meta[name="av-static-routes"]')`);
    if (!hasCircuit) return;
    await this.waitFor(`(() => { const c = window.__e2e?.circuit; return c && c.opened && !c.closed && c.messages > 0 && Date.now() - c.last > 400; })()`,
      'the Blazor circuit to connect', ms);
  }

  /**
   * Waits until the page's ranking boards (vote page, home page example) are enhanced: their modules load after the load
   * event, and leaving the page while they load aborts the import, which Firefox reports as a console error.
   */
  async settled(ms = 5000) {
    await this.waitFor(`[...document.querySelectorAll('[data-rank-board]')].every(b => !b.hidden)`, 'the page modules', ms).catch(() => { });
  }

  async gotoInteractive(p) { await this.goto(p); await this.interactive(); }

  async node(selector) {
    const r = await this.browser.send('script.evaluate', { expression: `document.querySelector(${JSON.stringify(selector)})`, target: { context: this.ctx }, awaitPromise: false });
    if (r.result.type !== 'node') throw new Error(`no element ${selector}`);
    return { sharedId: r.result.sharedId };
  }

  /** Marks the first visible element matching `selector` whose trimmed text is `text`, returns a selector for it. */
  async byText(text, selector = 'button, a, label, summary') {
    const ok = await this.eval(`(() => {
      document.querySelectorAll('[data-e2e-target]').forEach(e => e.removeAttribute('data-e2e-target'));
      const el = [...document.querySelectorAll(${JSON.stringify(selector)})]
        .find(e => e.offsetParent !== null && e.textContent.trim() === ${JSON.stringify(text)} && !e.disabled);
      if (!el) return false;
      el.setAttribute('data-e2e-target', '');
      return true; })()`);
    if (!ok) throw new Error(`no visible ${selector} with text "${text}"`);
    return '[data-e2e-target]';
  }

  /** A real pointer click (trusted event, user activation) in the middle of the first visible match. */
  async click(selector) {
    await this.loaded();
    await this.eval(`(() => {
      const all = [...document.querySelectorAll(${JSON.stringify(selector)})];
      const e = all.find(x => x.offsetParent !== null || x.getClientRects().length > 0) ?? all[0];
      if (!e) throw new Error('no element ' + ${JSON.stringify(selector)});
      document.querySelectorAll('[data-e2e-click]').forEach(x => x.removeAttribute('data-e2e-click'));
      e.setAttribute('data-e2e-click', '');
      e.scrollIntoView({ block: 'center', behavior: 'instant' });
      return true; })()`);
    const el = await this.node('[data-e2e-click]');
    await this.eval(`document.querySelector('[data-e2e-click]').removeAttribute('data-e2e-click'), true`);
    await this.browser.send('input.performActions', {
      context: this.ctx, actions: [{
        type: 'pointer', id: 'mouse', parameters: { pointerType: 'mouse' }, actions: [
          { type: 'pointerMove', x: 0, y: 0, origin: { type: 'element', element: el } },
          { type: 'pointerDown', button: 0 }, { type: 'pointerUp', button: 0 }],
      }],
    });
    await this.browser.send('input.releaseActions', { context: this.ctx });
  }

  async clickText(text, selector) { await this.click(await this.byText(text, selector)); }

  /** Sets a field's value as typing would, then fires input and change (Blazor binds on either). */
  async fill(selector, value) {
    await this.eval(`(() => { const e = document.querySelector(${JSON.stringify(selector)}); if (!e) throw new Error('no element ' + ${JSON.stringify(selector)});
      e.value = ${JSON.stringify(value)}; e.dispatchEvent(new Event('input', { bubbles: true })); e.dispatchEvent(new Event('change', { bubbles: true })); return true; })()`);
  }

  async keys(...values) {
    const actions = values.flatMap(v => [{ type: 'keyDown', value: v }, { type: 'keyUp', value: v }]);
    await this.browser.send('input.performActions', { context: this.ctx, actions: [{ type: 'key', id: 'kb', actions }] });
    await this.browser.send('input.releaseActions', { context: this.ctx });
  }

  /**
   * Drags with the pointer. WebDriver's synthesized native drag never fires `drop` in Firefox, so the Sortable lists
   * under `root` are switched to their pointer-event fallback first; their drop handling (onEnd) is the same code.
   */
  async drag(sourceSelector, targetSelector, root = 'body') {
    await this.eval(`[...document.querySelectorAll(${JSON.stringify(root + ' [data-zone]')})].forEach(z => {
      const k = Object.keys(z).find(k => k.startsWith('Sortable')); if (k) { z[k].option('forceFallback', true); z[k].nativeDraggable = false; } }), true`);
    await this.eval(`document.querySelector(${JSON.stringify(targetSelector)}).scrollIntoView({ block: 'center', behavior: 'instant' }), true`);
    const src = await this.node(sourceSelector), dst = await this.node(targetSelector);
    await this.browser.send('input.performActions', {
      context: this.ctx, actions: [{
        type: 'pointer', id: 'mouse', parameters: { pointerType: 'mouse' }, actions: [
          { type: 'pointerMove', x: 0, y: 0, origin: { type: 'element', element: src } },
          { type: 'pointerDown', button: 0 }, { type: 'pause', duration: 100 },
          { type: 'pointerMove', x: 5, y: 5, duration: 100, origin: 'pointer' },
          { type: 'pointerMove', x: 0, y: 0, duration: 300, origin: { type: 'element', element: dst } },
          { type: 'pointerMove', x: 2, y: 2, duration: 100, origin: 'pointer' }, { type: 'pause', duration: 200 },
          { type: 'pointerMove', x: 3, y: 0, duration: 100, origin: 'pointer' }, { type: 'pause', duration: 200 },
          { type: 'pointerUp', button: 0 }, { type: 'pause', duration: 200 },
          { type: 'pointerMove', x: 1, y: 1, duration: 50, origin: 'pointer' }],
      }],
    });
    await this.browser.send('input.releaseActions', { context: this.ctx });
  }

  /**
   * Presses on `fromSelector` and moves to `toSelector` with a mouse or a touch pointer, holding `hold` ms first (a touch
   * long-press), then releases. No native drag and drop is involved (pointer events only).
   */
  async pointerDrag(fromSelector, toSelector, { pointerType = 'mouse', hold = 0, moveMs = 300 } = {}) {
    await this.eval(`document.querySelector(${JSON.stringify(fromSelector)}).scrollIntoView({ block: 'center', behavior: 'instant' }), true`);
    const from = await this.node(fromSelector), to = await this.node(toSelector);
    await this.browser.send('input.performActions', {
      context: this.ctx, actions: [{
        type: 'pointer', id: pointerType === 'mouse' ? 'mouse' : 'finger', parameters: { pointerType }, actions: [
          { type: 'pointerMove', x: 0, y: 0, origin: { type: 'element', element: from } },
          { type: 'pointerDown', button: 0 },
          ...(hold ? [{ type: 'pause', duration: hold }] : []),
          { type: 'pointerMove', x: 0, y: 0, duration: moveMs, origin: { type: 'element', element: to } },
          { type: 'pause', duration: 50 },
          { type: 'pointerUp', button: 0 }],
      }],
    });
    await this.browser.send('input.releaseActions', { context: this.ctx });
  }

  /** Raw key actions: [{ type: 'keyDown' | 'keyUp', value }] (WebDriver key values, e.g. '\uE008' Shift, '\uE014' ArrowRight). */
  async keyActions(actions) {
    await this.browser.send('input.performActions', { context: this.ctx, actions: [{ type: 'key', id: 'kb', actions }] });
    await this.browser.send('input.releaseActions', { context: this.ctx });
  }

  /** A screenshot of one element (its border box). */
  async screenshotElement(selector, file) {
    await this.eval(`document.querySelector(${JSON.stringify(selector)}).scrollIntoView({ block: 'center', behavior: 'instant' }), true`);
    const element = await this.node(selector);
    const r = await this.browser.send('browsingContext.captureScreenshot', { context: this.ctx, clip: { type: 'element', element } });
    fs.writeFileSync(file, Buffer.from(r.data, 'base64'));
    return file;
  }

  async screenshot(file) {
    const r = await this.browser.send('browsingContext.captureScreenshot', { context: this.ctx, origin: 'document' });
    fs.writeFileSync(file, Buffer.from(r.data, 'base64'));
    return file;
  }

  /** False when the reconnect dialog or #blazor-error-ui is showing. */
  async alive() {
    return this.eval(`!document.querySelector('#components-reconnect-modal[open]')
      && [...document.querySelectorAll('#blazor-error-ui')].every(e => getComputedStyle(e).display === 'none')`);
  }

  async path() { return this.eval('location.pathname + location.search'); }
}

function deserialize(v) {
  switch (v?.type) {
    case 'undefined': case 'null': return null;
    case 'string': case 'number': case 'boolean': return v.value;
    case 'array': return v.value.map(deserialize);
    case 'object': return Object.fromEntries(v.value.map(([k, x]) => [typeof k === 'string' ? k : deserialize(k), deserialize(x)]));
    default: return v?.value ?? null;
  }
}
