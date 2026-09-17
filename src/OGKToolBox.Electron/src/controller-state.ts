import type { ControllerSnapshot } from "./controller-models";

export function emptyControllerSnapshot(): ControllerSnapshot {
  return {
    sequence: 0, sampledAt: new Date().toISOString(), state: "Searching", error: null,
    identity: { kind: "Unknown", displayName: "未连接", vendorId: 0, productId: 0, firmware: "—", hardwareVersion: 0, protocolVersion: 0 },
    capabilities: { inputMonitor: false, virtualKeys: false, mode: false, basicLighting: false, picoLighting: false,
      hallConfiguration: false, hallCalibration: false, leverConfiguration: false, leverCalibration: false, cardReader: false, bootloader: false },
    input: { leftA: false, leftB: false, leftC: false, leftSide: false, leftMenu: false,
      rightA: false, rightB: false, rightC: false, rightSide: false, rightMenu: false,
      test: false, service: false, lever: 0, rawLever: 0, mappedLever: 512 },
    card: { present: false, cardType: 0, type: "", identifier: "" },
    hall: { configurationValid: false, abcTravel: 0, abcRtTrigger: 0, abcRtRelease: 0, abcDead: 0,
      sideTravel: 0, sideRtTrigger: 0, sideRtRelease: 0, sideDead: 0, rtEnabledAbc: 0, rtEnabledSide: 0,
      delta: [], maxDelta: [], baseline: [], calibrationState: 0, calibrationSamples: 0 },
    lever: { calibrationMin: 0, calibrationMax: 0, inverted: false, sensitivity: 0, outputDeadband: 0,
      calibrationState: 0, pendingCalibrationMin: 0, pendingCalibrationMax: 0, leftNoise: 0, rightNoise: 0 },
    deviceConfig: { valid: false, brightness: 0, groundColor: [0, 0, 0], sideColor: [0, 0, 0],
      cabPreset: 0, cabGameMapping: false, inputMode: 0, isKmMode: false, capabilities: 0, protocolSupported: false },
    operation: null, canWrite: false, readbackComplete: false, deviceConfigRevision: 0, hallConfigRevision: 0
  };
}

/** v1 providers without mode metadata keep their original boolean command semantics. */
export function withControllerModes(snapshot: ControllerSnapshot): ControllerSnapshot {
  if (snapshot.inputModes || !snapshot.capabilities.mode) return snapshot;
  return { ...snapshot, inputModes: { current: snapshot.deviceConfig.isKmMode ? "keyboard" : "native",
    options: [{ id: "native", label: "MU3IO" }, { id: "keyboard", label: "模拟键鼠" }] } };
}

export function hasControllerDevice(snapshot: ControllerSnapshot | null): boolean {
  return Boolean(snapshot && snapshot.identity.kind !== "Unknown" &&
    !["Disabled", "Searching", "BootloaderPending", "Faulted"].includes(snapshot.state));
}
