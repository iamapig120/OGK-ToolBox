const test = require('node:test');
const assert = require('node:assert/strict');
const load = require('./helpers/load-controller-module.cjs');
const { ControllerHub } = load('electron/controller-hub.ts');
const { emptyControllerSnapshot } = load('src/controller-state.ts');
const tick = () => new Promise(resolve => setImmediate(resolve));
function deferred() { let resolve; const promise = new Promise(done => { resolve = done; }); return { promise, resolve }; }
class Backend {
  constructor(id, kind = 'Unknown') {
    this.id = id; this.label = id; this.status = { state: 'ready' };
    this.snapshot = { ...emptyControllerSnapshot(), state: kind === 'Unknown' ? 'Searching' : 'Ready',
      identity: { ...emptyControllerSnapshot().identity, kind, displayName: kind }, canWrite: true,
      capabilities: { ...emptyControllerSnapshot().capabilities, inputMonitor: true, virtualKeys: true, mode: true } };
    this.snapshots = new Set(); this.statuses = new Set(); this.commands = []; this.keys = new Set(); this.releases = 0;
  }
  getSnapshot() { return this.snapshot; }
  getStatus() { return this.status; }
  onSnapshot(cb) { this.snapshots.add(cb); cb(this.snapshot); return () => this.snapshots.delete(cb); }
  onStatus(cb) { this.statuses.add(cb); cb(this.status); return () => this.statuses.delete(cb); }
  publish(kind) { this.snapshot = { ...this.snapshot, state: kind === 'Unknown' ? 'Searching' : 'Ready', identity: { ...this.snapshot.identity, kind, displayName: kind } }; for (const cb of this.snapshots) cb(this.snapshot); }
  statusTo(state, error) { this.status = { state, error }; for (const cb of this.statuses) cb(this.status); }
  async start() { this.statusTo('ready'); }
  async stop() { this.statusTo('stopped'); }
  async restart() { await this.stop(); await this.start(); }
  async releaseAllIfRunning() { this.releases++; this.keys.clear(); }
  async execute(command) {
    this.commands.push(command);
    if (command.name === 'virtual-key') { if (command.pressed) this.keys.add(command.key); else this.keys.delete(command.key); }
    return { commandId: 'test', status: 'Verified', message: '', snapshot: this.snapshot };
  }
}

test('an idle IO4 service does not hide the selected Host failure or expose stale input', async () => {
  const host = new Backend('builtin', 'Leonardo'), io4 = new Backend('io4');
  const hub = new ControllerHub([host, io4]);
  host.statusTo('fault', 'Host failed'); await tick();
  assert.equal(hub.getStatus().state, 'fault');
  assert.equal(hub.getStatus().error, 'Host failed');
  assert.equal(hub.getSnapshot().identity.kind, 'Unknown');
  assert.equal(hub.getSnapshot().canWrite, false);
});

test('hotplug keeps the online device; explicit switching releases its held keys first', async () => {
  const host = new Backend('builtin', 'Leonardo'), io4 = new Backend('io4');
  io4.snapshot.capabilities.virtualKeys = false;
  const hub = new ControllerHub([host, io4]);
  await hub.setVirtualKey('L_A', true); io4.publish('SimGEKI'); await tick();
  assert.equal(hub.getStatus().selectedBackendId, 'builtin');
  assert.equal(host.keys.has('L_A'), true);
  await hub.selectBackend('io4');
  assert.equal(host.keys.size, 0);
  assert.equal(host.releases, 1);
  assert.equal((await hub.setVirtualKey('L_A', false)).status, 'Rejected');
  assert.equal(io4.commands.length, 0);
});

test('any registered connected backend can be discovered; a manual choice remains selected', async () => {
  const host = new Backend('builtin'), other = new Backend('third-party', 'FutureDevice');
  const hub = new ControllerHub([host, other]); await tick();
  assert.equal(hub.getStatus().selectedBackendId, 'third-party');
  await hub.selectBackend('builtin'); other.publish('FutureDevice'); await tick();
  assert.equal(hub.getStatus().selectedBackendId, 'builtin');
  assert.equal((await hub.selectBackend('missing')).status, 'Rejected');
});

test('queued configuration and key presses cannot cross a reconnect of the same backend', async () => {
  const host = new Backend('builtin', 'Leonardo'), hub = new ControllerHub([host]);
  await tick();
  const gate = deferred(), entered = deferred(), original = host.execute.bind(host);
  host.execute = async command => { if (command.name === 'brightness' && command.brightness === 1) { entered.resolve(); await gate.promise; } return original(command); };
  const first = hub.setBrightness(1); await entered.promise;
  const oldConnection = hub.getSnapshot().source.connectionId;
  const stale = hub.setBrightness(2), staleKey = hub.setVirtualKey('L_A', true);
  host.publish('Unknown'); host.publish('Leonardo'); gate.resolve(); await first;
  assert.notEqual(hub.getSnapshot().source.connectionId, oldConnection);
  assert.equal((await stale).status, 'Rejected'); assert.equal((await staleKey).status, 'Rejected');
  assert.equal(host.commands.filter(command => command.brightness === 2 || command.name === 'virtual-key').length, 0);
});

test('a release failure retains the old device instead of silently abandoning held input', async () => {
  const host = new Backend('builtin', 'Leonardo'), io4 = new Backend('io4', 'SimGEKI');
  const hub = new ControllerHub([host, io4]);
  await hub.setVirtualKey('L_A', true);
  host.releaseAllIfRunning = async () => { throw new Error('release failed'); };
  assert.equal((await hub.releaseAll()).status, 'Failed');
  assert.equal(host.keys.has('L_A'), true);
  assert.equal(hub.heldKeys.get('L_A').backend, host);
  await assert.rejects(hub.selectBackend('io4'), /release failed/);
  assert.equal(hub.getStatus().selectedBackendId, 'builtin');
  await hub.setVirtualKey('L_A', false);
  assert.equal(host.keys.size, 0);
});

test('a selected rescan error is not replaced by an unrelated backend success', async () => {
  const host = new Backend('builtin'), io4 = new Backend('io4'), hub = new ControllerHub([host, io4]);
  host.execute = async () => { throw new Error('Host unavailable'); };
  await assert.rejects(hub.rescan(), /Host unavailable/);
});

test('start during stop waits for teardown, and stop is shared by concurrent callers', async () => {
  const host = new Backend('builtin'), hub = new ControllerHub([host]); await tick();
  const gate = deferred(); let starts = 0, stops = 0;
  host.stop = async () => { stops++; await gate.promise; host.statusTo('stopped'); };
  host.start = async () => { starts++; host.statusTo('ready'); };
  const stopping = hub.stop(), second = hub.stop(), starting = hub.start();
  assert.equal(stopping, second); await tick(); assert.equal(starts, 0);
  gate.resolve(); await Promise.all([stopping, starting]);
  assert.equal(stops, 1); assert.equal(starts, 1); assert.equal(hub.getStatus().state, 'ready');
});

test('release cleanup does not send ordinary commands or restart stopped providers', async () => {
  const host = new Backend('builtin'), hub = new ControllerHub([host]);
  await hub.stop(); await hub.releaseAllIfRunning();
  assert.equal(host.commands.length, 0); assert.equal(host.getStatus().state, 'stopped');
});

test('duplicate backend identifiers are rejected', () => {
  assert.throws(() => new ControllerHub([new Backend('same'), new Backend('same')]), /unique IDs/);
});
