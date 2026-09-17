import type { ControllerSnapshot, ControllerCommandResult } from '../../src/OGKToolBox.Electron/src/controller-models';

export type ControllerCommand = 'rescan' | 'retry-sync' | 'virtual-key' | 'mode' | 'brightness'
  | 'custom-color' | 'pico-lighting' | 'hall' | 'hall-calibration' | 'hall-query' | 'device-query'
  | 'lever' | 'lever-calibration' | 'bootloader';

export interface ControllerAdapter {
  /** Return complete, cached state. Hardware polling must not block this method. */
  snapshot(): ControllerSnapshot;
  /** Validate request data and reject unsupported capabilities. Verified means actual readback succeeded. */
  command(name: ControllerCommand, body: Record<string, unknown>): Promise<Pick<ControllerCommandResult, 'status' | 'message'>>;
  /** Required and idempotent: release every synthetic key, including after device loss. */
  releaseAll(): void | Promise<void>;
  close?(): void | Promise<void>;
}
export function startProvider(adapter: ControllerAdapter, argv?: string[]): Promise<{ stop(): Promise<void> }>;
