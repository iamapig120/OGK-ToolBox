import type { ControllerCommandResult, ControllerModuleStatus, ControllerSnapshot, HallRequest, LeverRequest,
  PicoLightingRequest } from "../src/controller-models";

export type ControllerCommand =
  | { name: "rescan" | "retry-sync" | "release-all" | "hall-query" | "device-query" | "bootloader" }
  | { name: "virtual-key"; key: string; pressed: boolean }
  | { name: "mode"; keyboardMouse: boolean }
  | { name: "input-mode"; modeId: string }
  | { name: "brightness"; brightness: number }
  | { name: "custom-color"; red: number; green: number; blue: number }
  | { name: "pico-lighting"; request: PicoLightingRequest }
  | { name: "hall"; request: HallRequest }
  | { name: "lever"; request: LeverRequest }
  | { name: "hall-calibration" | "lever-calibration"; action: string };

/** Application contract, independent of the hardware wire protocol and provider language. */
export interface ControllerBackend {
  readonly id: string;
  readonly label: string;
  getSnapshot(): ControllerSnapshot | null;
  getStatus(): ControllerModuleStatus;
  onSnapshot(listener: (snapshot: ControllerSnapshot) => void): () => void;
  onStatus(listener: (status: ControllerModuleStatus) => void): () => void;
  start(): Promise<void>;
  restart(): Promise<void>;
  stop(): Promise<void>;
  execute(command: ControllerCommand): Promise<ControllerCommandResult>;
  releaseAllIfRunning(): Promise<void>;
}
