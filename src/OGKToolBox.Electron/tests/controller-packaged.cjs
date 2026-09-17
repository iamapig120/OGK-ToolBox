const assert = require('node:assert/strict');
const path = require('node:path');
const fs = require('node:fs');
const { createHash } = require('node:crypto');
const { spawn } = require('node:child_process');
const { once } = require('node:events');
const load = require('./helpers/load-controller-module.cjs');
const directory = process.env.OGK_CONTROLLER_PACKAGED_DIR;
if (!directory || !path.isAbsolute(directory)) throw new Error('Set OGK_CONTROLLER_PACKAGED_DIR to the absolute win-unpacked directory');
const executable = path.join(directory, 'OGK ToolBox.exe');
const entry = path.join(directory, 'resources/app.asar.unpacked/dist-electron/electron/providers/io4-provider.js');
const sdk = path.resolve(path.dirname(entry), '../../sdk/controller-provider/server.cjs');
assert.ok(fs.existsSync(entry)); assert.ok(fs.existsSync(sdk));

async function waitUntil(predicate, timeout = 6000) {
  const started = Date.now();
  while (!predicate()) {
    if (Date.now() - started > timeout) throw new Error('Timed out waiting for provider state');
    await new Promise(resolve => setTimeout(resolve, 25));
  }
}

(async () => {
  const code = `const path=require('node:path');const r=require('node:module').createRequire(process.argv[1]);const root=path.dirname(r.resolve('node-hid'));const binding=r('pkg-prebuilds/bindings')(root,r(path.join(root,'binding-options.js')));if(typeof binding.HID!=='function')throw new Error('Missing HID binding');console.log('PASS: packaged Node-API binding loads without developer dependencies');`;
  const binding = spawn(executable, ['-e', code, entry], { windowsHide: true,
    cwd: directory, env: { ...process.env, ELECTRON_RUN_AS_NODE: '1', NODE_PATH: '' } });
  binding.stdout.pipe(process.stdout); binding.stderr.pipe(process.stderr);
  assert.equal((await once(binding, 'exit'))[0], 0);

  const { ControllerModuleManager } = load('electron/controller-module-manager.ts', {
    electron: { app: { isPackaged: false, getVersion: () => '1.1.7' } }
  });
  const manager = new ControllerModuleManager({ id: 'io4', label: 'IO4 test',
    resolveTarget: async () => ({ directory, executable, node: true,
      prefixArgs: ['--require', path.join(__dirname, 'helpers/no-hid-devices.cjs'), entry] }) });
  try {
    await manager.start(); await waitUntil(() => manager.getStatus().state === 'ready');
    assert.equal(manager.getSnapshot().identity.kind, 'Unknown');
    assert.equal((await manager.rescan()).status, 'Verified');
    assert.equal((await manager.setInputMode('1')).status, 'Rejected');
    const originalChild = manager.child;
    const exited = once(originalChild, 'exit'); originalChild.kill(); await exited;
    await waitUntil(() => manager.child && manager.child !== originalChild && manager.getStatus().state === 'ready');
    assert.equal(manager.getSnapshot().identity.kind, 'Unknown');
    console.log('PASS: packaged provider handshake, snapshots, commands, isolated crash and restart (HID mocked)');
  } finally { await manager.stop(); }
  assert.equal(manager.getStatus().state, 'stopped'); assert.equal(manager.child, undefined);
  const originalHost = path.resolve(__dirname, '../resources/controller/OGKToolBox.ControllerHost.exe');
  const packagedHost = path.join(directory, 'resources/controller/OGKToolBox.ControllerHost.exe');
  const hash = file => createHash('sha256').update(fs.readFileSync(file)).digest('hex');
  assert.equal(hash(originalHost), hash(packagedHost));
  console.log('PASS: provider shutdown and unchanged bundled controller executable');
})().catch(error => { console.error(error); process.exitCode = 1; });
