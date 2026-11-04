// Runs the flows in order against one app and one browser, and fails a flow on anything the server or the browser
// reports as broken while it ran (DESIGN.md §24.7).
import fs from 'node:fs';
import path from 'node:path';

// Things that are reported but are not failures. Every entry needs a reason; prefer fixing the cause.
// { source: 'server' | 'browser', test: RegExp, reason: string }
export const ALLOWED = [];

const allowed = (source, text) => ALLOWED.some(a => a.source === source && a.test.test(text));

export function createRun({ app, browser, artifacts }) {
  const results = [];
  let current = null;
  let serverChecked = 0;
  const serverProblems = to => {
    const found = app.lines.slice(serverChecked, to).filter(l => /^\s*(fail|crit):/.test(l) && !allowed('server', l)).map(l => `server log: ${l}`);
    serverChecked = to;
    return found;
  };

  const run = {
    app, browser, artifacts,
    /** Shared between flows: the admin's page, the polls created, the TOTP secret. */
    state: {},

    /** A named assertion inside the current flow. A failure is recorded and the flow goes on. */
    check(name, ok, detail = '') {
      current.checks.push({ name, ok: !!ok, detail: ok ? '' : String(detail).slice(0, 1500) });
      console.log(`  ${ok ? 'ok  ' : 'FAIL'} ${name}${ok || !detail ? '' : `\n         ${String(detail).slice(0, 1500).replace(/\n/g, '\n         ')}`}`);
      return !!ok;
    },

    /** Failure screenshots of every open tab. */
    async screenshots(label) {
      const files = [];
      let n = 0;
      for (const page of browser.pages) {
        const file = path.join(artifacts, `${current.name}-${label}-${++n}.png`.replace(/[^A-Za-z0-9._-]+/g, '-'));
        try { files.push(await page.screenshot(file)); } catch { /* the tab may be gone */ }
      }
      return files;
    },

    /**
     * From now to the end of the current flow, a browser message matching `test` is expected (a fault the flow
     * injects on purpose) and is not a failure.
     */
    tolerate(test, reason) { current.tolerated.push({ test, reason, from: browser.events.length }); },

    /** Runs `fn` after the flow, once failure screenshots are taken (closing a voter's tab, say). */
    defer(fn) { current.deferred.push(fn); },

    async flow(name, fn) {
      current = { name, checks: [], problems: [], deferred: [], tolerated: [], started: Date.now() };
      results.push(current);
      console.log(`\n== ${name}`);
      // Server lines are checked from where the last check stopped, so startup and the gaps between flows count too.
      const eventMark = browser.events.length;
      try {
        await fn(run);
      } catch (e) {
        current.stopped = true;
        current.problems.push(`flow stopped: ${e.message}`);
        console.log(`  FAIL flow stopped: ${e.stack ?? e.message}`);
      }
      // Give late console errors and server log lines (a circuit failing on unload) a moment to arrive.
      await new Promise(r => setTimeout(r, 300));
      for (const page of browser.pages) {
        try { if (!(await page.alive())) current.problems.push(`circuit dropped or error UI shown on ${await page.path()}`); } catch { }
      }
      current.problems.push(...serverProblems(app.lines.length));
      if (app.exited !== null) current.problems.push(`the app exited (code ${app.exited})`);
      for (const [i, e] of browser.events.entries()) {
        if (i < eventMark) continue;
        const expected = current.tolerated.find(t => i >= t.from && t.test.test(e.text ?? `CSP ${e.directive} ${e.blocked}`));
        if (expected) {
          console.log(`  note: expected (${expected.reason}): ${e.text}`);
        } else if (e.kind === 'log' && e.level === 'error' && !allowed('browser', e.text)) {
          current.problems.push(`browser ${e.type === 'javascript' ? 'uncaught exception' : 'console error'}: ${e.text} ${e.where}`);
        } else if (e.kind === 'csp' && !allowed('browser', `CSP ${e.directive} ${e.blocked}`)) {
          current.problems.push(`CSP violation on ${e.url}: ${e.directive} blocked ${e.blocked} (${e.source})`);
        } else if (e.kind === 'reconnect' && e.state !== 'hide' && !e.leaving) {
          current.problems.push(`circuit reconnect state "${e.state}" on ${e.url}`);
        } else if (e.kind === 'log' && e.level === 'warn') {
          console.log(`  note: browser warning: ${e.text}`);
        }
      }
      for (const p of current.problems) console.log(`  FAIL ${p}`);
      current.ms = Date.now() - current.started;
      const failed = current.problems.length > 0 || current.checks.some(c => !c.ok);
      if (failed) {
        const shots = await run.screenshots('failure');
        for (const s of shots) console.log(`  screenshot ${s}`);
      }
      for (const fn of current.deferred.reverse()) {
        try { await fn(); } catch (e) { console.log(`  note: cleanup failed: ${e.message}`); }
      }
      delete current.deferred;
      delete current.tolerated;
      console.log(`  ${failed ? 'FAILED' : 'passed'} in ${(current.ms / 1000).toFixed(1)} s`);
      return { passed: !failed, stopped: !!current.stopped };
    },

    summary() {
      // Server errors logged after the last flow's checks (shutdown is not included: the app is still running).
      const late = serverProblems(app.lines.length);
      if (late.length) {
        results.push({ name: 'after the flows', checks: [], problems: late, ms: 0 });
        for (const p of late) console.log(`  FAIL ${p}`);
      }
      const failed = results.filter(r => r.problems.length > 0 || r.checks.some(c => !c.ok));
      console.log('\n== summary');
      for (const r of results) {
        const bad = r.problems.length + r.checks.filter(c => !c.ok).length;
        console.log(`  ${bad ? 'FAIL' : 'ok  '} ${r.name.padEnd(28)} ${String(r.checks.length).padStart(3)} checks  ${(r.ms / 1000).toFixed(1).padStart(5)} s${bad ? `  (${bad} problem${bad > 1 ? 's' : ''})` : ''}`);
      }
      const warnings = app.lines.filter(l => /^\s*warn:/.test(l));
      if (warnings.length) console.log(`  server warnings (not failures): ${warnings.length}, see the app log`);
      fs.writeFileSync(path.join(artifacts, 'results.json'), JSON.stringify(results, null, 2));
      return failed.length === 0;
    },
  };
  return run;
}
