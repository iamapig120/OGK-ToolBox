export type ControllerState =
  | "Disabled" | "Searching" | "ConnectedWaitingForData" | "SyncingDevice" | "SyncingHall"
  | "Ready" | "CalibratingHall" | "CalibratingLever" | "SyncFailed" | "BootloaderPending"
  | "Unsupported" | "Faulted";

export type ControllerKind = "Unknown" | "Leonardo" | "Pico" | (string & {});
export type CommandResultStatus = "Rejected" | "Accepted" | "Verified" | "Failed";

export type ControllerSnapshot = {
  sequence: number;
  sampledAt: string;
  state: ControllerState;
  error?: string | null;
  identity: {
    kind: ControllerKind;
    displayName: string;
    vendorId: number;
    productId: number;
    firmware: string;
    hardwareVersion: number;
    protocolVersion: number;
  };
  capabilities: {
    inputMonitor: boolean;
    virtualKeys: boolean;
    mode: boolean;
    basicLighting: boolean;
    picoLighting: boolean;
    hallConfiguration: boolean;
    hallCalibration: boolean;
    leverConfiguration: boolean;
    leverCalibration: boolean;
    cardReader: boolean;
    bootloader: boolean;
  };
  input: {
    leftA: boolean; leftB: boolean; leftC: boolean; leftSide: boolean; leftMenu: boolean;
    rightA: boolean; rightB: boolean; rightC: boolean; rightSide: boolean; rightMenu: boolean;
    test: boolean; service: boolean; lever: number; rawLever: number; mappedLever: number;
  };
  card: { present: boolean; cardType: number; type: string; identifier: string };
  hall: {
    configurationValid: boolean; abcTravel: number; abcRtTrigger: number; abcRtRelease: number; abcDead: number;
    sideTravel: number; sideRtTrigger: number; sideRtRelease: number; sideDead: number;
    rtEnabledAbc: number; rtEnabledSide: number; delta: number[]; maxDelta: number[]; baseline: number[];
    calibrationState: number; calibrationSamples: number;
  };
  lever: {
    calibrationMin: number; calibrationMax: number; inverted: boolean; sensitivity: number; outputDeadband: number;
    calibrationState: number; pendingCalibrationMin: number; pendingCalibrationMax: number;
    leftNoise: number; rightNoise: number;
  };
  deviceConfig: {
    valid: boolean; brightness: number; groundColor: number[]; sideColor: number[]; cabPreset: number;
    cabGameMapping: boolean; inputMode: number; isKmMode: boolean; capabilities: number; protocolSupported: boolean;
  };
  operation?: { id: string; name: string; status: CommandResultStatus; startedAt: string; message: string } | null;
  canWrite: boolean;
  readbackComplete: boolean;
  deviceConfigRevision: number;
  hallConfigRevision: number;
};

export type ControllerCommandResult = {
  commandId: string;
  status: CommandResultStatus;
  message: string;
  snapshot: ControllerSnapshot;
};

export type ControllerModuleStatus = {
  state: "starting" | "ready" | "fault" | "restarting" | "stopped";
  error?: string;
};

export type PicoLightingRequest = {
  brightness: number; groundR: number; groundG: number; groundB: number;
  sideR: number; sideG: number; sideB: number; cabPreset: number; cabGameMapping: boolean;
};

export type HallRequest = {
  abcTravel: number; abcRtTrigger: number; abcRtRelease: number; abcDead: number;
  sideTravel: number; sideRtTrigger: number; sideRtRelease: number; sideDead: number;
  save: boolean; rtEnabledAbc: number; rtEnabledSide: number;
};

export type LeverRequest = {
  calibrationMin: number; calibrationMax: number; inverted: boolean; sensitivity: number; save: boolean;
};
