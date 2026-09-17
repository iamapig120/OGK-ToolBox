const test = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const { once } = require('node:events');
const load = require('./helpers/load-controller-module.cjs');
const { emptyControllerSnapshot } = load('src/controller-state.ts');
const { ControllerModuleManager } = load('electron/controller-module-manager.ts', {
  electron: { app: { isPackaged: false, getVersion: () => require('../package.json').version } }
});

test('legacy mode metadata is adapted without sending a new endpoint to the old Host', async () => {
  const manager = new ControllerModuleManager();
  manager.snapshot = { ...emptyControllerSnapshot(), canWrite: true,
    capabilities: { ...emptyControllerSnapshot().capabilities, mode: true } };
  const calls = [];
  manager.setMode = async value => { calls.push(value); return { status: 'Verified', snapshot: manager.getSnapshot() }; };
  assert.equal(manager.getSnapshot().inputModes.current, 'native');
  await manager.execute({ name: 'input-mode', modeId: 'keyboard' });
  await manager.execute({ name: 'input-mode', modeId: 'native' });
  assert.deepEqual(calls, [true, false]);
  assert.equal((await manager.setInputMode('3')).status, 'Rejected');
});

test('declared mode IDs use the extension and reject undeclared or read-only modes', async () => {
  const manager = new ControllerModuleManager();
  manager.snapshot = { ...emptyControllerSnapshot(), canWrite: true,
    capabilities: { ...emptyControllerSnapshot().capabilities, mode: true },
    inputModes: { current: '1', options: [{ id: '1', label: 'IO4' }, { id: '2', label: 'DLL' }] } };
  let request;
  manager.command = async (endpoint, body) => { request = { endpoint, modeId: body.modeId }; return { status: 'Verified' }; };
  await manager.setInputMode('2');
  assert.deepEqual(request, { endpoint: '/api/v1/commands/input-mode', modeId: '2' });
  assert.equal((await manager.setInputMode('3')).status, 'Rejected');
  manager.snapshot.canWrite = false;
  assert.equal((await manager.setInputMode('1')).status, 'Rejected');
});

test('JSON command timeout includes a stalled response body', { timeout: 3000 }, async t => {
  const server = http.createServer((_req, res) => { res.writeHead(200, { 'Content-Type': 'application/json' }); res.write('{'); });
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  t.after(() => { server.closeAllConnections(); server.close(); });
  const manager = new ControllerModuleManager();
  manager.address = `http://127.0.0.1:${server.address().port}`;
  await assert.rejects(manager.request('/stalled', {}, 100), /请求超时/);
});

test('explicit external selection registers only that provider', () => {
  class FakeManager { constructor(options = {}) { Object.assign(this, { id: 'builtin' }, options); } }
  const { createControllerBackends } = load('electron/controller-registry.ts', {
    electron: { app: { isPackaged: false } }, './controller-module-manager': { ControllerModuleManager: FakeManager }
  }, { process: { ...process, env: { ...process.env, OGK_CONTROLLER_MODULE_DIR: 'E:/test-provider' } } });
  const backends = createControllerBackends();
  assert.equal(backends.length, 1); assert.equal(backends[0].id, 'external');
});

test('release requires confirmation before the hub may switch devices', async () => {
  const manager = new ControllerModuleManager();
  let calls = 0;
  manager.request = async () => { calls++; return { status: 'Failed', message: 'Release failed' }; };
  await manager.releaseAllIfRunning();
  assert.equal(calls, 0);
  manager.address = 'http://127.0.0.1:12345';
  await assert.rejects(manager.releaseAllIfRunning(), /Release failed/);
  manager.request = async () => ({ status: 'Verified' });
  await manager.releaseAllIfRunning();
});

test('process manager shares teardown and delays concurrent start until old state is cleared', async () => {
  const manager = new ControllerModuleManager();
  let release;
  const gate = new Promise(resolve => { release = resolve; });
  manager.child = { exitCode: null };
  manager.terminateChild = async () => gate;
  let starts = 0;
  manager.startCore = async () => { starts++; manager.status = { state: 'ready' }; };
  const first = manager.stop(), second = manager.stop(), starting = manager.start();
  assert.equal(first, second);
  await new Promise(resolve => setImmediate(resolve)); assert.equal(starts, 0);
  release(); await Promise.all([first, starting]);
  assert.equal(starts, 1); assert.equal(manager.getStatus().state, 'ready');
  await manager.stop();
});
