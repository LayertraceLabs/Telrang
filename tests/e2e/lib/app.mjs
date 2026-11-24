// Starts the published app (Release, Production environment) on a free port with a scratch data directory.
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import http from 'node:http';
import https from 'node:https';
import net from 'node:net';
import path from 'node:path';

export async function freePort() {
  return new Promise((resolve, reject) => {
    const srv = net.createServer();
    srv.unref();
    srv.on('error', reject);
    srv.listen(0, '127.0.0.1', () => { const { port } = srv.address(); srv.close(() => resolve(port)); });
  });
}

/**
 * Runs the app: `dotnet <dll>` from its publish folder, a self-contained build's executable (`app` not ending in .dll,
 * DESIGN.md §29), or the Docker image `image` (`docker run`, the container's own /data). Every stdout/stderr line goes to
 * `logFile`; `lines` keeps them in memory so a flow can look at what the server logged while it ran.
 */
export async function startApp({ dll, image, dataDir, logFile }) {
  const port = await freePort();
  // http://localhost, not 127.0.0.1: WebAuthn refuses an IP address as relying party id, and localhost is a secure
  // context, so the Secure auth cookie of the Production environment works over plain HTTP.
  const base = `http://localhost:${port}`;
  const log = fs.createWriteStream(logFile);
  const lines = [];
  const settings = {
    ASPNETCORE_ENVIRONMENT: 'Production',
    // The default console formatter with one line per entry keeps "fail:" / "crit:" at the start of the line.
    Logging__Console__FormatterName: 'simple',
    Logging__Console__FormatterOptions__SingleLine: 'true',
  };
  const container = image ? `telrang-e2e-${port}` : null;
  let proc;
  if (image) {
    const env = Object.entries(settings).flatMap(([k, v]) => ['-e', `${k}=${v}`]);
    proc = spawn('docker', ['run', '--rm', '--name', container, '-p', `127.0.0.1:${port}:8080`, ...env, image],
      { stdio: ['ignore', 'pipe', 'pipe'] });
  } else {
    const env = { ...process.env, ...settings, ASPNETCORE_URLS: base, App__DataDir: dataDir, TELRANG_CONFIG: 'none', DOTNET_NOLOGO: '1' };
    const [command, args] = dll.endsWith('.dll') ? ['dotnet', [dll]] : [dll, []];
    proc = spawn(command, args, { cwd: path.dirname(dll), stdio: ['ignore', 'pipe', 'pipe'], env });
  }
  let partial = '';
  const onData = chunk => {
    log.write(chunk);
    partial += chunk.toString();
    const parts = partial.split('\n');
    partial = parts.pop();
    lines.push(...parts);
  };
  proc.stdout.on('data', onData);
  proc.stderr.on('data', onData);
  let exited = null;
  proc.on('exit', code => { exited = code ?? -1; });

  const deadline = Date.now() + 60_000;
  while (Date.now() < deadline) {
    if (exited !== null) throw new Error(`app exited with code ${exited} before it was ready; see ${logFile}`);
    try { if ((await fetch(base + '/healthz')).ok) break; } catch { /* not listening yet */ }
    await new Promise(r => setTimeout(r, 250));
  }
  if (Date.now() >= deadline) throw new Error(`app did not answer /healthz within 60 s; see ${logFile}`);

  return {
    base,
    lines,
    get exited() { return exited; },
    /** The one-time admin setup key the app logs on its first start (DESIGN.md §14.1). */
    setupKey() {
      for (const l of lines) { const m = /Admin setup key: (\S+)/.exec(l); if (m) return m[1]; }
      return null;
    },
    async stop() {
      if (container) {
        await new Promise(r => spawn('docker', ['stop', '-t', '10', container], { stdio: 'ignore' }).on('exit', r));
      }
      if (exited === null) {
        proc.kill('SIGTERM');
        const t = Date.now();
        while (exited === null && Date.now() - t < 10_000) await new Promise(r => setTimeout(r, 100));
        if (exited === null) proc.kill('SIGKILL');
      }
      if (container) await new Promise(r => spawn('docker', ['rm', '-f', container], { stdio: 'ignore' }).on('exit', r));
      await new Promise(r => log.end(r));
    },
  };
}

/** GET `url` (status code or error code); `resolveTo` connects to that address whatever the host name resolves to. */
function probe(url, resolveTo) {
  const u = new URL(url);
  const lookup = resolveTo ? (_host, options, cb) => options?.all ? cb(null, [{ address: resolveTo, family: 4 }]) : cb(null, resolveTo, 4) : undefined;
  return new Promise(resolve => {
    const req = (u.protocol === 'https:' ? https : http).get(u, { lookup }, res => { res.resume(); resolve(res.statusCode); });
    req.on('error', e => resolve(e.code ?? e.message));
    req.setTimeout(5000, () => req.destroy(new Error('timeout')));
  });
}

/**
 * An app that already runs in the Docker container `container` behind a proxy at `base` (scripts/npm-stack-test.sh):
 * follows its log from the start (the setup key, fail/crit lines) and waits for /healthz through the proxy. `resolveTo`:
 * the address the base URL's host name stands for (a test name such as vote.telrang.test). Stopping the app is the
 * caller's job; `stop` only ends the log follower.
 */
export async function attachApp({ base, container, logFile, resolveTo }) {
  const log = fs.createWriteStream(logFile);
  const lines = [];
  const proc = spawn('docker', ['logs', '--follow', container], { stdio: ['ignore', 'pipe', 'pipe'] });
  let partial = '';
  const onData = chunk => {
    log.write(chunk);
    partial += chunk.toString();
    const parts = partial.split('\n');
    partial = parts.pop();
    lines.push(...parts);
  };
  proc.stdout.on('data', onData);
  proc.stderr.on('data', onData);
  let exited = null;
  proc.on('exit', code => { exited = code ?? -1; });

  const deadline = Date.now() + 60_000;
  let last = '';
  while (Date.now() < deadline) {
    if (exited !== null) throw new Error(`docker logs ${container} ended (${exited}): is the container running?`);
    last = await probe(base + '/healthz', resolveTo);
    if (last === 200) break;
    await new Promise(r => setTimeout(r, 500));
  }
  if (Date.now() >= deadline) throw new Error(`${base}/healthz did not answer within 60 s (${last})`);

  return {
    base,
    lines,
    // The app's own exit is not watched here: the flows' server checks see its log.
    get exited() { return null; },
    /** The newest key: the container's log keeps the lines of earlier starts. */
    setupKey() {
      for (const l of [...lines].reverse()) { const m = /Admin setup key: (\S+)/.exec(l); if (m) return m[1]; }
      return null;
    },
    async stop() {
      if (exited === null) proc.kill('SIGTERM');
      await new Promise(r => log.end(r));
    },
  };
}
