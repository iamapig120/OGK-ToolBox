import fs from "node:fs/promises";
import path from "node:path";

export type ControllerProviderManifest = {
  moduleId: string; moduleVersion: string; moduleApiVersion: number;
  ogkToolBoxVersion: string; platform: string; entryPoint: string;
  runtime?: "native" | "node"; mu3CoreCommit?: string; protocolVersion?: number;
};

// Selection is made by the user launching the app, never by a game package or renderer.
export async function resolveControllerProvider(bundledDirectory: string, appVersion: string,
  override: string | undefined, nodeExecutable: string) {
  if (override !== undefined && (!override.trim() || !path.isAbsolute(override)))
    throw new Error("OGK_CONTROLLER_MODULE_DIR must be an absolute directory.");
  const directory = await fs.realpath(override ?? bundledDirectory);
  const manifestPath = path.join(directory, "module.json");
  const manifest = JSON.parse(await fs.readFile(manifestPath, "utf8")) as ControllerProviderManifest;
  if (!manifest || typeof manifest.moduleId !== "string" || !/^[a-z0-9][a-z0-9.-]{0,79}$/.test(manifest.moduleId)
    || typeof manifest.moduleVersion !== "string" || !manifest.moduleVersion.trim()
    || manifest.moduleApiVersion !== 1 || manifest.platform !== "win-x64" || manifest.ogkToolBoxVersion !== appVersion)
    throw new Error("Controller provider manifest is incompatible with this app.");
  const runtime = manifest.runtime ?? "native";
  if (runtime !== "native" && runtime !== "node") throw new Error("Unsupported controller provider runtime.");
  if (typeof manifest.entryPoint !== "string" || !/^[a-zA-Z0-9_-][a-zA-Z0-9._-]*\.(exe|cjs)$/.test(manifest.entryPoint)
    || !manifest.entryPoint.endsWith(runtime === "node" ? ".cjs" : ".exe"))
    throw new Error("Controller provider entryPoint must be a local .exe or .cjs filename.");
  if (override === undefined && (manifest.moduleId !== "ogk-controller" || manifest.moduleVersion !== appVersion
    || !/^[a-f0-9]{40}$/i.test(manifest.mu3CoreCommit ?? "") || manifest.protocolVersion !== 1
    || runtime !== "native" || manifest.entryPoint !== "OGKToolBox.ControllerHost.exe"))
    throw new Error("Bundled controller manifest is incompatible with this app.");
  const entryPoint = await fs.realpath(path.join(directory, manifest.entryPoint));
  if (path.dirname(entryPoint).toLowerCase() !== directory.toLowerCase())
    throw new Error("Controller provider entryPoint escapes its directory.");
  if (!(await fs.stat(entryPoint)).isFile()) throw new Error("Controller provider entryPoint is not a file.");
  return { directory, manifestPath, manifest,
    executable: runtime === "node" ? nodeExecutable : entryPoint,
    prefixArgs: runtime === "node" ? [entryPoint] : [],
    node: runtime === "node" };
}
