import { app } from "electron";
import path from "node:path";
import type { ControllerBackend } from "./controller-backend";
import { ControllerModuleManager } from "./controller-module-manager";
import { resolveControllerProvider } from "./controller-provider";

/** Only built-in registrations or the explicitly selected external provider are launched. */
export function createControllerBackends(): ControllerBackend[] {
  const override = process.env.OGK_CONTROLLER_MODULE_DIR;
  const bundled = app.isPackaged ? path.join(process.resourcesPath, "controller") : path.resolve(__dirname, "../../resources/controller");
  if (override !== undefined) return [new ControllerModuleManager({ id: "external", label: "外部控制器",
    resolveTarget: () => resolveControllerProvider(bundled, app.getVersion(), override, process.execPath) })];
  const io4Entry = app.isPackaged
    ? path.join(process.resourcesPath, "app.asar.unpacked/dist-electron/electron/providers/io4-provider.js")
    : path.join(__dirname, "providers/io4-provider.js");
  return [new ControllerModuleManager(), new ControllerModuleManager({ id: "io4", label: "SimGEKI / IO4",
    resolveTarget: async () => ({ executable: process.execPath, prefixArgs: [io4Entry], node: true,
      directory: app.isPackaged ? process.resourcesPath : path.resolve(__dirname, "../..") }) })];
}
