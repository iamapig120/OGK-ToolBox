const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { createSimGekiAdapter } = require('../../../examples/simgeki-provider/provider.cjs');

function fixture(t) {
  let time = 0, callback, cleared = 0;
  const timer = {};
  const adapter = createSimGekiAdapter({ now: () => time,
    schedule(fn, interval) { assert.equal(interval, 50); callback = fn; return timer; },
    cancel(handle) { assert.equal(handle, timer); cleared++; }
  });
  t.after(() => adapter.close());
  return { adapter, advance(ms) { time = ms; callback(); }, cleared: () => cleared };
}

test('SimGEKI simulation has complete safe defaults and cannot mutate through snapshots', t => {
  const { adapter } = fixture(t), snapshot = adapter.snapshot();
  assert.equal(snapshot.identity.displayName, 'SimGEKI（模拟）');
  assert.equal(snapshot.identity.firmware, 'simulation');
  assert.equal(snapshot.identity.vendorId, 0);
  assert.equal(snapshot.state, 'Ready');
  assert.equal(snapshot.card.identifier, '');
  assert.equal(snapshot.capabilities.virtualKeys, false);
  assert.equal(snapshot.capabilities.basicLighting, false);
  assert.equal(snapshot.capabilities.mode, true);
  assert.deepEqual(snapshot.inputModes.options.map(option => option.id), ['1', '2', '3']);
  assert.equal(snapshot.hall.configurationValid, false);
  assert.equal(snapshot.readbackComplete, true);
  snapshot.inputModes.current = '3'; snapshot.inputModes.options[0].label = 'changed'; snapshot.input.leftA = false;
  assert.equal(adapter.snapshot().inputModes.current, '1');
  assert.equal(adapter.snapshot().inputModes.options[0].label, 'IO4');
  assert.equal(adapter.snapshot().input.leftA, true);
});

test('SimGEKI simulation cycles button gaps and sweeps both lever endpoints without wall-clock waits', t => {
  const { adapter, advance } = fixture(t);
  assert.equal(adapter.snapshot().input.leftA, true);
  advance(500);
  for (const [name, value] of Object.entries(adapter.snapshot().input)) {
    if (typeof value === 'boolean') assert.equal(value, false, name);
  }
  advance(850); assert.equal(adapter.snapshot().input.leftB, true); assert.equal(adapter.snapshot().input.leftA, false);
  advance(2000); assert.equal(adapter.snapshot().input.mappedLever, 1023); assert.equal(adapter.snapshot().input.rawLever, 0);
  advance(6000); assert.equal(adapter.snapshot().input.mappedLever, 0); assert.equal(adapter.snapshot().input.rawLever, 65535);
  advance(9600); assert.equal(adapter.snapshot().input.leftA, true);
});

test('SimGEKI simulation changes three modes only in memory with compatible boolean commands', async t => {
  const { adapter } = fixture(t);
  for (const modeId of ['2', '3', '1']) {
    const before = adapter.snapshot().deviceConfigRevision;
    assert.equal((await adapter.command('input-mode', { modeId })).status, 'Verified');
    const snapshot = adapter.snapshot();
    assert.equal(snapshot.inputModes.current, modeId);
    assert.equal(snapshot.deviceConfig.inputMode, Number(modeId));
    assert.equal(snapshot.deviceConfig.isKmMode, modeId === '3');
    assert.equal(snapshot.deviceConfigRevision, before + 1);
  }
  assert.equal((await adapter.command('mode', { keyboardMouse: true })).status, 'Verified');
  assert.equal(adapter.snapshot().inputModes.current, '3');
  assert.equal((await adapter.command('mode', { keyboardMouse: false })).status, 'Verified');
  assert.equal(adapter.snapshot().inputModes.current, '1');
  const fresh = fixture(t).adapter;
  assert.equal(fresh.snapshot().deviceConfigRevision, 0);
  assert.equal(fresh.snapshot().inputModes.current, '1');
});

test('SimGEKI simulation rejects malformed modes and unsupported hardware commands without changing configuration', async t => {
  const { adapter } = fixture(t), original = adapter.snapshot();
  for (const body of [{}, { modeId: 2 }, { modeId: '4' }, { modeId: '' }, null])
    assert.equal((await adapter.command('input-mode', body)).status, 'Rejected');
  assert.equal((await adapter.command('mode', { keyboardMouse: 1 })).status, 'Rejected');
  for (const name of ['virtual-key', 'brightness', 'bootloader', 'hall-calibration', 'unknown'])
    assert.equal((await adapter.command(name, {})).status, 'Rejected');
  assert.deepEqual(adapter.snapshot(), original);
});

test('SimGEKI simulation refreshes normally and closes its timer once without late updates', async t => {
  const { adapter, advance, cleared } = fixture(t);
  for (const name of ['rescan', 'retry-sync']) assert.equal((await adapter.command(name)).status, 'Verified');
  adapter.releaseAll(); adapter.releaseAll();
  adapter.close(); adapter.close();
  assert.equal(cleared(), 1);
  const closed = adapter.snapshot();
  assert.equal(closed.state, 'Disabled'); assert.equal(closed.canWrite, false);
  assert.equal(closed.input.leftA, false);
  advance(1600);
  assert.deepEqual(adapter.snapshot(), closed);
  assert.equal((await adapter.command('input-mode', { modeId: '2' })).status, 'Rejected');
  assert.equal((await adapter.command('rescan')).status, 'Rejected');
});

test('SimGEKI simulation manifest uses the standard Node entry and current app version', () => {
  const manifest = JSON.parse(fs.readFileSync(path.join(__dirname, '../../../examples/simgeki-provider/module.json'), 'utf8'));
  const app = JSON.parse(fs.readFileSync(path.join(__dirname, '../package.json'), 'utf8'));
  assert.equal(manifest.moduleId, 'simgeki-simulation'); assert.equal(manifest.moduleApiVersion, 1);
  assert.equal(manifest.runtime, 'node'); assert.equal(manifest.entryPoint, 'provider.cjs');
  assert.equal(manifest.ogkToolBoxVersion, app.version);
});
