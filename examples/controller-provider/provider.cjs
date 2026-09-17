'use strict';
const { startProvider } = require('../../sdk/controller-provider/server.cjs');

// Synthetic, read-only device. No HID access, keyboard injection or physical settings changes.
const state = {
  sequence: 0, sampledAt: '', state: 'Ready', error: null,
  identity: { kind: 'ExampleController', displayName: 'Example Controller (simulation)',
    vendorId: 0, productId: 0, firmware: 'simulation', hardwareVersion: 0, protocolVersion: 0 },
  capabilities: { inputMonitor: true, virtualKeys: false, mode: false, basicLighting: false,
    picoLighting: false, hallConfiguration: false, hallCalibration: false, leverConfiguration: false,
    leverCalibration: false, cardReader: false, bootloader: false },
  input: { leftA: false, leftB: false, leftC: false, leftSide: false, leftMenu: false,
    rightA: false, rightB: false, rightC: false, rightSide: false, rightMenu: false,
    test: false, service: false, lever: 0, rawLever: 0, mappedLever: 0 },
  card: { present: false, cardType: 0, type: '', identifier: '' },
  hall: { configurationValid: false, abcTravel: 0, abcRtTrigger: 0, abcRtRelease: 0, abcDead: 0,
    sideTravel: 0, sideRtTrigger: 0, sideRtRelease: 0, sideDead: 0, rtEnabledAbc: 0, rtEnabledSide: 0,
    delta: [], maxDelta: [], baseline: [], calibrationState: 0, calibrationSamples: 0 },
  lever: { calibrationMin: 0, calibrationMax: 0, inverted: false, sensitivity: 0, outputDeadband: 0,
    calibrationState: 0, pendingCalibrationMin: 0, pendingCalibrationMax: 0, leftNoise: 0, rightNoise: 0 },
  deviceConfig: { valid: false, brightness: 0, groundColor: [0, 0, 0], sideColor: [0, 0, 0],
    cabPreset: 0, cabGameMapping: false, inputMode: 0, isKmMode: false, capabilities: 0, protocolSupported: true },
  operation: null, canWrite: false, readbackComplete: true, deviceConfigRevision: 0, hallConfigRevision: 0
};

const adapter = {
  snapshot() { return structuredClone(state); },
  async command(name) {
    return name === 'rescan' || name === 'retry-sync'
      ? { status: 'Verified', message: 'Simulation ready' }
      : { status: 'Rejected', message: 'This read-only example does not support this command' };
  },
  releaseAll() { /* No synthetic keys are held by this read-only provider. */ }
};

if (require.main === module) startProvider(adapter).catch(() => { console.error('Example provider failed to start'); process.exitCode = 1; });
module.exports = { adapter };
