const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { createServer } = require('node:net');
const { once } = require('node:events');
const { randomBytes } = require('node:crypto');
const sdkPath = path.resolve(__dirname, '../../../sdk/controller-provider/server.cjs');
const example = require(path.resolve(__dirname, '../../../examples/controller-provider/provider.cjs')).adapter;

function sdk(fastDeadline = false) {
  const module = { exports: {} };
  vm.runInNewContext(fs.readFileSync(sdkPath, 'utf8'), { module, require, process, Buffer,
    setTimeout: (fn, ms) => setTimeout(fn, fastDeadline && ms === 5000 ? 40 : ms), clearTimeout,
    setInterval, clearInterval });
  return module.exports;
}
async function session(adapter, fastDeadline = false) {
  const reservation = createServer(); reservation.listen(0, '127.0.0.1'); await once(reservation, 'listening');
  const port = reservation.address().port;
  await new Promise(resolve => reservation.close(resolve));
  const token = randomBytes(32).toString('hex');
  const argv = ['--port', String(port), '--session-token', token, '--instance-id', 'sdk-regression',
    '--parent-pid', String(process.pid), '--software-version', require('../package.json').version];
  const provider = await sdk(fastDeadline).startProvider(adapter, argv);
  const call = (command, value = {}) => fetch(`http://127.0.0.1:${port}/api/v1/commands/${command}`, {
    method: 'POST', headers: { 'X-OGK-Controller-Session': token }, body: JSON.stringify(value)
  });
  const get = endpoint => fetch(`http://127.0.0.1:${port}${endpoint}`, { headers: { 'X-OGK-Controller-Session': token } });
  return { ...provider, call, get };
}
function gate() {
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  return { promise, resolve };
}

test('SDK accepts named input modes and keeps legacy snapshots valid', async t => {
  const calls = [];
  let mode = '1';
  const provider = await session({ ...example,
    snapshot: () => ({ ...example.snapshot(), inputModes: { current: mode,
      options: [{ id: '1', label: 'IO4' }, { id: '2', label: 'DLL' }, { id: '3', label: 'Keyboard' }] } }),
    command: async (name, body) => { calls.push([name, body.modeId]); mode = body.modeId; return { status: 'Verified' }; }
  });
  t.after(() => provider.stop());
  assert.equal((await provider.call('input-mode', { modeId: 2 })).status, 400);
  const response = await (await provider.call('input-mode', { modeId: '2' })).json();
  assert.equal(response.status, 'Verified');
  assert.equal(response.snapshot.inputModes.current, '2');
  assert.deepEqual(calls, [['input-mode', '2']]);
  const legacy = await session(example);
  t.after(() => legacy.stop());
  const snapshot = await (await legacy.get('/api/v1/snapshot')).json();
  assert.equal(snapshot.identity.kind, 'ExampleController');
  assert.equal(snapshot.inputModes, undefined);
});

test('shutdown discards queued writes and closes despite a command that never returns', { timeout: 4000 }, async t => {
  const entered = gate(), unfinished = gate();
  let writes = 0, closed = 0, released = 0;
  const provider = await session({ ...example,
    command: async () => { writes++; entered.resolve(); await unfinished.promise; return { status: 'Verified' }; },
    close: () => { closed++; }, releaseAll: () => { released++; }
  });
  t.after(() => { unfinished.resolve(); return provider.stop(); });
  const first = provider.call('brightness').catch(() => undefined);
  await entered.promise;
  const second = provider.call('brightness').catch(() => undefined);
  const response = await provider.call('shutdown');
  assert.equal(response.status, 200);
  const stopping = provider.stop();
  assert.equal(provider.stop(), stopping);
  await stopping;
  await Promise.all([first, second]);
  assert.equal(writes, 1);
  assert.equal(closed, 1);
  assert.equal(released, 1);
});

test('adapter command deadline stops the provider before another queued command can execute', { timeout: 3000 }, async t => {
  const entered = gate(), unfinished = gate(), closed = gate();
  let writes = 0;
  const provider = await session({ ...example,
    command: async () => { writes++; entered.resolve(); await unfinished.promise; return { status: 'Verified' }; },
    close: () => { closed.resolve(); }
  }, true);
  t.after(() => { unfinished.resolve(); return provider.stop(); });
  const first = provider.call('brightness').catch(() => undefined);
  await entered.promise;
  const second = provider.call('brightness').catch(() => undefined);
  await closed.promise;
  await provider.stop();
  await Promise.all([first, second]);
  assert.equal(writes, 1);
});

test('shutdown remains bounded when a driver cleanup method hangs', { timeout: 3000 }, async t => {
  let released = false;
  const provider = await session({ ...example, close: () => new Promise(() => {}), releaseAll: () => { released = true; } });
  t.after(() => provider.stop());
  const started = Date.now();
  await provider.stop();
  assert.ok(Date.now() - started < 1500);
  assert.equal(released, true);
});

test('shutdown releases held keys while the driver connection is still open', async t => {
  const events = [];
  let connected = true, held = true;
  const provider = await session({ ...example,
    releaseAll: () => {
      if (!connected) throw new Error('Cannot release keys through a closed connection');
      held = false; events.push('release');
    },
    close: () => { events.push('close'); connected = false; }
  });
  t.after(() => provider.stop());
  await provider.stop();
  assert.deepEqual(events, ['release', 'close']);
  assert.equal(held, false);
  assert.equal(connected, false);
});

test('a release timeout still closes the driver after the bounded release attempt', { timeout: 3000 }, async t => {
  const events = [];
  const provider = await session({ ...example,
    releaseAll: () => { events.push('release'); return new Promise(() => {}); },
    close: () => { events.push('close'); }
  });
  t.after(() => provider.stop());
  await provider.stop();
  assert.deepEqual(events, ['release', 'close']);
});

test('a failed HTTP bind closes an already started adapter', async () => {
  const occupied = createServer(); occupied.listen(0, '127.0.0.1'); await once(occupied, 'listening');
  let closed = 0, released = 0;
  try {
    await assert.rejects(sdk().startProvider({ ...example, close: () => { closed++; }, releaseAll: () => { released++; } },
      ['--port', String(occupied.address().port), '--session-token', 't'.repeat(32), '--instance-id', 'occupied',
        '--parent-pid', String(process.pid), '--software-version', require('../package.json').version]));
    assert.equal(closed, 1);
    assert.equal(released, 1);
  } finally { await new Promise(resolve => occupied.close(resolve)); }
});
