const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const vm = require('node:vm');
const ts = require('typescript');
const { spawn } = require('node:child_process');
const { once } = require('node:events');
const { createServer } = require('node:net');
const { randomBytes } = require('node:crypto');
const root = path.resolve(__dirname, '../../..');
const example = path.join(root, 'examples/controller-provider');

function load(name, overrides = {}, globals = {}) {
  const code = ts.transpileModule(fs.readFileSync(path.join(__dirname, '../electron', name + '.ts'), 'utf8'), {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS, esModuleInterop: true }
  }).outputText;
  const exports = {};
  vm.runInNewContext(code, { exports, require: key => overrides[key] ?? require(key), __dirname,
    process, Buffer, URL, AbortController, TextDecoder, setTimeout, clearTimeout, console, fetch, ...globals });
  return exports;
}
const resolver = load('controller-provider');

test('provider selection defaults to unchanged bundled binary and accepts independent example', async () => {
  const bundled = path.resolve(__dirname, '../resources/controller');
  const builtin = await resolver.resolveControllerProvider(bundled, '1.1.7', undefined, process.execPath);
  assert.equal(builtin.manifest.moduleId, 'ogk-controller');
  assert.equal(builtin.node, false);
  const other = await resolver.resolveControllerProvider(bundled, '1.1.7', example, process.execPath);
  assert.equal(other.manifest.moduleId, 'example-controller');
  assert.equal(other.executable, process.execPath);
  assert.equal(other.prefixArgs[0], path.join(example, 'provider.cjs'));
  await assert.rejects(resolver.resolveControllerProvider(bundled, '1.1.7', '../relative', process.execPath), /absolute/);
  await assert.rejects(resolver.resolveControllerProvider(bundled, '9.0.0', example, process.execPath), /incompatible/);
});

test('rejects path traversal and unknown runtimes before launch', async t => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'ogk-provider-test-'));
  t.after(() => {
    assert.equal(path.dirname(directory), path.resolve(os.tmpdir()));
    assert.ok(path.basename(directory).startsWith('ogk-provider-test-'));
    fs.rmSync(directory, { recursive: true, force: true });
  });
  const manifest = JSON.parse(fs.readFileSync(path.join(example, 'module.json')));
  for (const entryPoint of ['../provider.cjs', 'C:\\provider.cjs', 'provider.cjs --flag']) {
    fs.writeFileSync(path.join(directory, 'module.json'), JSON.stringify({ ...manifest, entryPoint }));
    await assert.rejects(resolver.resolveControllerProvider('', '1.1.7', directory, process.execPath), /entryPoint/);
  }
  fs.writeFileSync(path.join(directory, 'module.json'), JSON.stringify({ ...manifest, runtime: 'shell' }));
  await assert.rejects(resolver.resolveControllerProvider('', '1.1.7', directory, process.execPath), /runtime/);
});

test('actual manager starts third-party provider, receives input, rejects unsupported writes and stops', { timeout: 15000 }, async t => {
  const { ControllerModuleManager } = load('controller-module-manager', {
    electron: { app: { isPackaged: false, getVersion: () => '1.1.7' } }, './controller-provider': resolver,
    '../src/controller-state': load('../src/controller-state')
  }, { process: { ...process, execPath: require('electron'), env: { ...process.env, OGK_CONTROLLER_MODULE_DIR: example } } });
  const manager = new ControllerModuleManager();
  t.after(() => manager.stop());
  await manager.start();
  assert.equal(manager.getSnapshot().identity.kind, 'ExampleController');
  assert.equal(manager.getSnapshot().capabilities.inputMonitor, true);
  assert.equal((await manager.setBrightness(100)).status, 'Rejected');
  assert.equal((await manager.rescan()).status, 'Verified');
  assert.equal((await manager.releaseAll()).status, 'Verified');
  await manager.stop();
  assert.equal(manager.getStatus().state, 'stopped');
});

test('provider rejects missing tokens and browser origins, streams snapshots and shuts down', { timeout: 15000 }, async t => {
  const reservation = createServer(); reservation.listen(0, '127.0.0.1'); await once(reservation, 'listening');
  const port = reservation.address().port; await new Promise(resolve => reservation.close(resolve));
  const token = randomBytes(32).toString('hex');
  const child = spawn(process.execPath, [path.join(example, 'provider.cjs'), '--port', String(port),
    '--session-token', token, '--instance-id', 'test-instance', '--parent-pid', String(process.pid), '--software-version', '1.1.7'], { windowsHide: true });
  t.after(() => { if (child.exitCode === null) child.kill(); });
  await new Promise((resolve, reject) => {
    let output = ''; const timer = setTimeout(() => reject(new Error('Provider startup timeout')), 5000);
    child.stdout.on('data', chunk => { output += chunk; if (output.includes('OGK_CONTROLLER_READY ')) { clearTimeout(timer); resolve(); } });
    child.once('error', error => { clearTimeout(timer); reject(error); });
    child.once('exit', () => { clearTimeout(timer); reject(new Error('Provider exited before ready')); });
  });
  const base = `http://127.0.0.1:${port}`, headers = { 'X-OGK-Controller-Session': token };
  assert.equal((await fetch(base + '/health')).status, 401);
  assert.equal((await fetch(base + '/health', { headers: { ...headers, Origin: 'https://example.com' } })).status, 401);
  assert.equal((await (await fetch(base + '/health', { headers })).json()).instanceId, 'test-instance');
  assert.equal((await fetch(base + '/api/v1/commands/brightness', { method: 'POST', headers, body: 'invalid' })).status, 400);
  const abort = new AbortController();
  const stream = await fetch(base + '/api/v1/stream', { headers, signal: abort.signal });
  assert.match(new TextDecoder().decode((await stream.body.getReader().read()).value), /ExampleController/);
  abort.abort();
  const exited = once(child, 'exit');
  assert.equal((await fetch(base + '/api/v1/commands/shutdown', { method: 'POST', headers })).status, 200);
  await exited;
});
