const assert = require('node:assert/strict');
const test = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { EventEmitter } = require('node:events');
const ts = require('typescript');

function load(relative, overrides = {}, globals = {}) {
  const source = fs.readFileSync(path.join(__dirname, '..', relative), 'utf8');
  const code = ts.transpileModule(source, { compilerOptions: {
    target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS, esModuleInterop: true
  }}).outputText;
  const exports = {};
  vm.runInNewContext(code, { exports, require: name => overrides[name] ?? require(name),
    __dirname, process, console, Buffer, URL, AbortController, setTimeout, clearTimeout, ...globals });
  return exports;
}

const { loadInitialLibrary } = load('src/library-startup.ts');

test('valid cache enters the app without a second summary request or scan', async () => {
  const cached = { summary: { resourceCount: 26807 } };
  const result = await loadInitialLibrary(async () => cached,
    async () => { throw new Error('Unexpected scan'); }, () => true);
  assert.equal(result.value, cached);
  assert.equal(result.cached, true);
});

for (const failure of [false, true]) {
  test(`missing or failed cache is rebuilt (failure=${failure})`, async () => {
    let scans = 0;
    const rebuilt = {};
    const result = await loadInitialLibrary(async () => {
      if (failure) throw new Error('Cache request timed out');
      return null;
    }, async () => { scans++; return rebuilt; }, () => true);
    assert.equal(result.value, rebuilt);
    assert.equal(result.cached, false);
    assert.equal(scans, 1);
  });
}

test('rebuild errors preserve the actual reason for the error screen', async () => {
  await assert.rejects(loadInitialLibrary(async () => null,
    async () => { throw new Error('Directory unavailable'); }, () => true), /Directory unavailable/);
});

test('an abandoned startup does not launch a scan or publish a result', async () => {
  assert.equal(await loadInitialLibrary(async () => null,
    async () => { throw new Error('Unexpected scan'); }, () => false), null);
  let active = true;
  assert.equal(await loadInitialLibrary(async () => null,
    async () => { active = false; return {}; }, () => active), null);
});

test('failed API startup can be retried, and concurrent callers share the retry', async () => {
  let starts = 0;
  let instanceId;
  const spawn = (_file, _args, options) => {
    const child = new EventEmitter();
    child.stdout = new EventEmitter();
    child.stderr = new EventEmitter();
    child.exitCode = null;
    child.kill = () => { child.exitCode = 1; child.emit('exit', 1); };
    starts++;
    instanceId = options.env.OGK_INSTANCE_ID;
    const fails = starts === 1;
    setImmediate(() => {
      if (fails) { child.stderr.emit('data', Buffer.from('startup failed')); child.kill(); }
      else child.stdout.emit('data', Buffer.from('OGK_READY ' + JSON.stringify({
        address: 'http://127.0.0.1:12345', instanceId
      }) + '\n'));
    });
    return child;
  };
  const { BackendManager } = load('electron/backend-manager.ts', {
    electron: { app: { isPackaged: false } }, 'node:child_process': { spawn }
  }, { fetch: async () => ({ ok: true, json: async () => ({ instanceId }) }) });
  const backend = new BackendManager();
  await assert.rejects(backend.start(), /startup failed/);
  await Promise.all([backend.start(), backend.start()]);
  assert.equal(starts, 2);
});
