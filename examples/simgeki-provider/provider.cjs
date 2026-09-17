'use strict';
const { startProvider } = require('../../sdk/controller-provider/server.cjs');

const modeOptions = [
  { id: '1', label: 'IO4' }, { id: '2', label: 'DLL' }, { id: '3', label: '模拟键盘' }
];
const inputKeys = ['leftA', 'leftB', 'leftC', 'leftSide', 'leftMenu',
  'rightA', 'rightB', 'rightC', 'rightSide', 'rightMenu', 'test', 'service'];
const neutralInput = () => ({ ...Object.fromEntries(inputKeys.map(key => [key, false])),
  lever: 0x8000, rawLever: 0x8000, mappedLever: 512 });

/** Simulated device data only. No HID imports, OS input injection or persisted settings. */
function createSimGekiAdapter({ now = Date.now, schedule = setInterval, cancel = clearInterval } = {}) {
  const startedAt = now();
  let closed = false;
  const state = {
    sequence: 0, sampledAt: new Date(startedAt).toISOString(), state: 'Ready', error: null,
    identity: { kind: 'SimGEKI', displayName: 'SimGEKI（模拟）', vendorId: 0, productId: 0,
      firmware: 'simulation', hardwareVersion: 0, protocolVersion: 1 },
    capabilities: { inputMonitor: true, virtualKeys: false, mode: true, basicLighting: false,
      picoLighting: false, hallConfiguration: false, hallCalibration: false, leverConfiguration: false,
      leverCalibration: false, cardReader: false, bootloader: false },
    input: neutralInput(),
    inputModes: { current: '1', options: modeOptions.map(option => ({ ...option })) },
    card: { present: false, cardType: 0, type: '', identifier: '' },
    hall: { configurationValid: false, abcTravel: 0, abcRtTrigger: 0, abcRtRelease: 0, abcDead: 0,
      sideTravel: 0, sideRtTrigger: 0, sideRtRelease: 0, sideDead: 0, rtEnabledAbc: 0,
      rtEnabledSide: 0, delta: [], maxDelta: [], baseline: [], calibrationState: 0, calibrationSamples: 0 },
    lever: { calibrationMin: 0, calibrationMax: 65535, inverted: true, sensitivity: 0, outputDeadband: 0,
      calibrationState: 0, pendingCalibrationMin: 0, pendingCalibrationMax: 0, leftNoise: 0, rightNoise: 0 },
    deviceConfig: { valid: true, brightness: 0, groundColor: [0, 0, 0], sideColor: [0, 0, 0],
      cabPreset: 0, cabGameMapping: false, inputMode: 1, isKmMode: false, capabilities: 0,
      protocolSupported: true },
    operation: null, canWrite: true, readbackComplete: true, deviceConfigRevision: 0, hallConfigRevision: 0
  };
  const touch = () => { state.sequence++; state.sampledAt = new Date(now()).toISOString(); };
  const tick = () => {
    if (closed) return;
    const elapsed = Math.max(0, now() - startedAt);
    const input = neutralInput();
    // Each button is lit for 450ms, followed by a visible 350ms gap; the lever sweeps every 8s.
    if (elapsed % 800 < 450) input[inputKeys[Math.floor(elapsed / 800) % inputKeys.length]] = true;
    input.mappedLever = Math.round((Math.sin(elapsed * 2 * Math.PI / 8000) + 1) * 1023 / 2);
    input.rawLever = input.lever = 0xffff - Math.round(input.mappedLever * 0xffff / 1023);
    state.input = input;
    touch();
  };
  tick();
  const timer = schedule(tick, 50);
  timer?.unref?.();

  return {
    snapshot() { return structuredClone(state); },
    async command(name, body = {}) {
      if (closed) return { status: 'Rejected', message: '模拟器已关闭。' };
      if (name === 'rescan' || name === 'retry-sync') {
        tick();
        return { status: 'Verified', message: 'SimGEKI 模拟输入运行中；未访问硬件。' };
      }
      const modeId = name === 'input-mode' ? body?.modeId
        : name === 'mode' && typeof body?.keyboardMouse === 'boolean' ? (body.keyboardMouse ? '3' : '1') : undefined;
      if ((name === 'input-mode' || name === 'mode') && typeof modeId === 'string'
        && modeOptions.some(option => option.id === modeId)) {
        state.inputModes.current = modeId;
        state.deviceConfig.inputMode = Number(modeId);
        state.deviceConfig.isKmMode = modeId === '3';
        state.deviceConfigRevision++;
        touch();
        return { status: 'Verified', message: '模拟模式已在内存中更新并回读；没有写入设备或系统输入。' };
      }
      return { status: 'Rejected', message: '模拟器仅支持状态刷新和 1 / 2 / 3 三种输入模式。' };
    },
    releaseAll() { /* The animation represents device readings and never holds OS input. */ },
    close() {
      if (closed) return;
      closed = true;
      cancel(timer);
      state.input = neutralInput();
      state.state = 'Disabled';
      state.canWrite = false;
      state.readbackComplete = false;
      touch();
    }
  };
}

async function startSimulation(argv = process.argv.slice(2)) {
  const adapter = createSimGekiAdapter();
  try { return await startProvider(adapter, argv); }
  catch (error) { adapter.close(); throw error; }
}

if (require.main === module) {
  void startSimulation().catch(() => {
    console.error('SimGEKI simulation provider failed to start. Launch it through OGKToolBox.');
    process.exitCode = 1;
  });
}
module.exports = { createSimGekiAdapter, startSimulation };
