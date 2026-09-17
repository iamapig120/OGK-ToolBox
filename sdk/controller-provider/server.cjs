'use strict';
const http = require('node:http');
const { randomUUID, timingSafeEqual } = require('node:crypto');

const COMMANDS = new Set(['rescan', 'retry-sync', 'release-all', 'virtual-key', 'mode', 'input-mode', 'brightness',
  'custom-color', 'pico-lighting', 'hall', 'hall-calibration', 'hall-query', 'device-query',
  'lever', 'lever-calibration', 'bootloader']);

// Hardware implementations supply snapshots and commands; this helper contains no device protocol.
async function startProvider(adapter, argv = process.argv.slice(2)) {
  const args = Object.fromEntries(Array.from({ length: argv.length / 2 }, (_, i) => [argv[i * 2], argv[i * 2 + 1]]));
  const port = Number(args['--port']), parentPid = Number(args['--parent-pid']);
  const token = args['--session-token'], instanceId = args['--instance-id'], version = args['--software-version'];
  if (!Number.isInteger(port) || port < 1 || port > 65535 || !Number.isInteger(parentPid) || parentPid < 1
    || typeof token !== 'string' || token.length < 32 || !instanceId || !version)
    throw new Error('Provider must be launched by OGKToolBox with valid session arguments.');
  const clients = new Set();
  let sequence = 0, closing = false, queue = Promise.resolve(), stopping, cancelRunning, activeOperation;
  const snapshot = () => ({ ...adapter.snapshot(), sequence: ++sequence, sampledAt: new Date().toISOString() });
  const limited = async (task, milliseconds) => {
    let timeout;
    try {
      return await Promise.race([task, new Promise((_, reject) => {
        timeout = setTimeout(() => { const error = new Error('Adapter operation timed out'); error.code = 'ADAPTER_TIMEOUT'; reject(error); }, milliseconds);
      })]);
    } finally { clearTimeout(timeout); }
  };
  const serialize = operation => {
    const next = queue.then(async () => {
      if (closing) throw new Error('Provider is stopping');
      let cancel;
      const interrupted = new Promise((_, reject) => { cancel = reject; });
      cancelRunning = cancel;
      const operationTask = Promise.resolve().then(() => {
        if (closing) throw new Error('Provider is stopping');
        return operation();
      });
      activeOperation = operationTask;
      const finished = () => { if (activeOperation === operationTask) activeOperation = undefined; };
      operationTask.then(finished, finished);
      try { return await limited(Promise.race([operationTask, interrupted]), 5000); }
      catch (error) {
        // A timeout cannot cancel arbitrary driver code. Stop the provider before another write can run.
        if (error?.code === 'ADAPTER_TIMEOUT') void stop();
        throw error;
      } finally { if (cancelRunning === cancel) cancelRunning = undefined; }
    });
    queue = next.catch(() => {});
    return next;
  };
  const send = (res, code, data) => {
    if (res.destroyed || res.writableEnded) return;
    res.writeHead(code, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' }); res.end(JSON.stringify(data));
  };
  const broadcast = () => {
    if (closing || !clients.size) return;
    const event = `data: ${JSON.stringify(snapshot())}\n\n`;
    for (const client of clients) if (!client.write(event)) client.destroy();
  };
  let timer, watchdog;
  const server = http.createServer(async (req, res) => {
    try {
      const received = req.headers['x-ogk-controller-session'];
      if (req.headers.origin || typeof received !== 'string' || Buffer.byteLength(received) !== Buffer.byteLength(token)
        || !timingSafeEqual(Buffer.from(received), Buffer.from(token))) return send(res, 401, { error: 'Unauthorized' });
      if (closing) return send(res, 503, { error: 'Provider is stopping' });
      if (req.method === 'GET' && req.url === '/health')
        return send(res, 200, { instanceId, moduleApiVersion: 1, ogkToolBoxVersion: version });
      if (req.method === 'GET' && req.url === '/api/v1/snapshot') return send(res, 200, snapshot());
      if (req.method === 'GET' && req.url === '/api/v1/stream') {
        if (clients.size >= 4) return send(res, 429, { error: 'Too many streams' });
        res.writeHead(200, { 'Content-Type': 'text/event-stream', 'Cache-Control': 'no-store' });
        res.write(`data: ${JSON.stringify(snapshot())}\n\n`);
        clients.add(res); res.on('close', () => clients.delete(res)); return;
      }
      if (req.method !== 'POST' || !req.url?.startsWith('/api/v1/commands/')) return send(res, 404, { error: 'Unknown endpoint' });
      const command = req.url.slice('/api/v1/commands/'.length);
      if (!COMMANDS.has(command) && command !== 'shutdown') return send(res, 404, { error: 'Unknown command' });
      let body = '', size = 0;
      for await (const chunk of req) {
        size += chunk.length;
        if (size > 8192) { send(res, 413, { error: 'Request too large' }); req.destroy(); return; }
        body += chunk.toString('utf8');
      }
      let value;
      try { value = body ? JSON.parse(body) : {}; }
      catch { return send(res, 400, { error: 'Invalid JSON' }); }
      if (!value || typeof value !== 'object' || Array.isArray(value)) return send(res, 400, { error: 'Expected an object' });
      if (command === 'input-mode' && (typeof value.modeId !== 'string' || !value.modeId || value.modeId.length > 80))
        return send(res, 400, { error: 'Expected a modeId string' });
      if (command === 'shutdown') {
        send(res, 200, { commandId: randomUUID(), status: 'Accepted', message: 'Stopping', snapshot: snapshot() });
        void stop(); return;
      }
      const result = await serialize(() => command === 'release-all'
        ? Promise.resolve(adapter.releaseAll()).then(() => ({ status: 'Verified', message: 'Inputs released' }))
        : adapter.command(command, value));
      send(res, 200, { commandId: randomUUID(), ...result, snapshot: snapshot() });
      broadcast();
    } catch {
      // Never echo arbitrary driver errors: they can contain device/card data or session arguments.
      if (!res.headersSent) send(res, closing ? 503 : 500, { error: closing ? 'Provider is stopping' : 'Controller adapter failed' });
      else res.destroy();
    }
  });
  server.requestTimeout = 5000;
  server.headersTimeout = 5000;
  const stop = () => {
    if (stopping) return stopping;
    closing = true; clearInterval(timer); clearInterval(watchdog);
    cancelRunning?.(new Error('Provider is stopping'));
    process.removeListener('SIGTERM', stop); process.removeListener('SIGINT', stop);
    stopping = (async () => {
      // Cancellation ends our wait, not arbitrary driver code. Give in-flight work a bounded grace period.
      await limited(Promise.allSettled([queue, activeOperation]), 250).catch(() => undefined);
      // Some drivers need the open connection to release held keys. close() must then halt any remaining work.
      await limited(Promise.resolve().then(() => adapter.releaseAll()), 250).catch(() => undefined);
      await limited(Promise.resolve().then(() => adapter.close?.()), 250).catch(() => undefined);
      for (const client of clients) client.end();
      clients.clear();
      await new Promise(resolve => { server.close(resolve); server.closeAllConnections(); });
    })();
    return stopping;
  };
  try {
    await new Promise((resolve, reject) => {
      const failed = error => reject(error);
      server.once('error', failed);
      server.listen(port, '127.0.0.1', () => { server.removeListener('error', failed); resolve(); });
    });
  } catch (error) { await stop(); throw error; }
  server.on('error', () => { void stop(); });
  timer = setInterval(() => { try { broadcast(); } catch { void stop(); } }, 50);
  watchdog = setInterval(() => { try { process.kill(parentPid, 0); } catch { void stop(); } }, 1000);
  process.on('SIGTERM', stop); process.on('SIGINT', stop);
  process.stdout.write(`OGK_CONTROLLER_READY ${JSON.stringify({ address: `http://127.0.0.1:${port}`, instanceId, moduleApiVersion: 1 })}\n`);
  return { stop };
}

module.exports = { startProvider };
