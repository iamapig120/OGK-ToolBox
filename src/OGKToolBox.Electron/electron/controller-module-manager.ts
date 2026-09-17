import { app } from "electron";
import { randomBytes, randomUUID } from "node:crypto";
import { execFile } from "node:child_process";
import { createServer } from "node:net";
import path from "node:path";
import { resolveControllerProvider } from "./controller-provider";
import type { ControllerBackend, ControllerCommand } from "./controller-backend";
import { emptyControllerSnapshot, withControllerModes } from "../src/controller-state";

export type ControllerProcessTarget = {
  directory: string; executable: string; prefixArgs: string[]; node: boolean; manifestPath?: string;
};
export type ControllerModuleOptions = {
  id?: string; label?: string;
  resolveTarget?: () => Promise<ControllerProcessTarget>;
};
import type {
  ControllerCommandResult, ControllerModuleStatus, ControllerSnapshot, HallRequest, LeverRequest,
  PicoLightingRequest
} from "../src/controller-models";

type ReadyMessage = { address: string; instanceId: string; moduleApiVersion: number };
type Listener = (snapshot: ControllerSnapshot) => void;
type StatusListener = (status: ControllerModuleStatus) => void;

const requestTimeouts = { command: 5000, health: 3000, shutdown: 750, release: 500 } as const;

export class ControllerModuleManager implements ControllerBackend {
  readonly id: string;
  readonly label: string;
  constructor(private readonly options: ControllerModuleOptions = {}) {
    this.id = options.id ?? "builtin";
    this.label = options.label ?? "NYAGEKI / LUXIS";
  }
  private child?: import("node:child_process").ChildProcessWithoutNullStreams;
  private address?: string;
  private readonly sessionToken = randomBytes(32).toString("base64url");
  private readonly instanceId = randomUUID();
  private starting?: Promise<void>;
  private stoppingTask?: Promise<void>;
  private startAbort?: AbortController;
  private restartTimer?: ReturnType<typeof setTimeout>;
  private restartAttempt = 0;
  private stopping = false;
  private streamAbort?: AbortController;
  private snapshot?: ControllerSnapshot;
  private readonly listeners = new Set<Listener>();
  private readonly statusListeners = new Set<StatusListener>();
  private status: ControllerModuleStatus = { state: "stopped" };

  getSnapshot(): ControllerSnapshot | null { return this.snapshot ? withControllerModes(this.snapshot) : null; }
  getStatus(): ControllerModuleStatus { return this.status; }
  onSnapshot(listener: Listener): () => void { this.listeners.add(listener); if (this.snapshot) listener(withControllerModes(this.snapshot)); return () => this.listeners.delete(listener); }
  onStatus(listener: StatusListener): () => void { this.statusListeners.add(listener); listener(this.status); return () => this.statusListeners.delete(listener); }

  start(): Promise<void> {
    if (this.stoppingTask) return this.stoppingTask.then(() => this.start());
    if (this.starting) return this.starting;
    if (!this.stopping && this.child && this.child.exitCode === null && this.address) return Promise.resolve();
    if (this.child && this.child.exitCode === null) return this.stop().then(() => this.start());
    this.stopping = false;
    const startupAbort = new AbortController();
    this.startAbort = startupAbort;
    let task!: Promise<void>;
    task = this.startCore(startupAbort.signal).then(() => {
      this.restartAttempt = 0;
    }).catch(error => {
      const message = error instanceof Error ? error.message : String(error);
      if (!this.stopping) {
        this.setStatus({ state: "fault", error: message });
        this.scheduleRestart(message);
      }
      throw error;
    }).finally(() => {
      if (this.starting === task) this.starting = undefined;
      if (this.startAbort === startupAbort) this.startAbort = undefined;
    });
    this.starting = task;
    return task;
  }

  async restart(): Promise<void> {
    this.clearRestartTimer();
    this.restartAttempt = 0;
    await this.stop();
    await this.start();
  }

  async rescan() { return this.command<ControllerCommandResult>("/api/v1/commands/rescan"); }
  async retrySync() { return this.command<ControllerCommandResult>("/api/v1/commands/retry-sync"); }
  async releaseAll() { return this.command<ControllerCommandResult>("/api/v1/commands/release-all"); }
  async releaseAllIfRunning(): Promise<void> {
    if (!this.address || this.stopping) return;
    const result = await this.request<ControllerCommandResult>("/api/v1/commands/release-all", { method: "POST" }, requestTimeouts.release);
    if (result?.status !== "Verified") throw new Error(result?.message || "控制器未能确认释放输入。");
  }
  async setVirtualKey(key: string, pressed: boolean) { return this.command<ControllerCommandResult>("/api/v1/commands/virtual-key", { key: controllerString(key, "key"), pressed: controllerBoolean(pressed, "pressed") }); }
  async setMode(keyboardMouse: boolean) { return this.command<ControllerCommandResult>("/api/v1/commands/mode", { keyboardMouse: controllerBoolean(keyboardMouse, "keyboardMouse") }); }
  async setBrightness(brightness: number) { return this.command<ControllerCommandResult>("/api/v1/commands/brightness", { brightness: controllerInteger(brightness, 0, 255, "brightness") }); }
  async setCustomColor(red: number, green: number, blue: number) {
    return this.command<ControllerCommandResult>("/api/v1/commands/custom-color", {
      red: controllerInteger(red, 0, 255, "red"), green: controllerInteger(green, 0, 255, "green"), blue: controllerInteger(blue, 0, 255, "blue")
    });
  }
  async setPicoLighting(request: PicoLightingRequest) {
    return this.command<ControllerCommandResult>("/api/v1/commands/pico-lighting", {
      brightness: controllerInteger(request?.brightness, 0, 255, "brightness"),
      groundR: controllerInteger(request?.groundR, 0, 255, "groundR"), groundG: controllerInteger(request?.groundG, 0, 255, "groundG"), groundB: controllerInteger(request?.groundB, 0, 255, "groundB"),
      sideR: controllerInteger(request?.sideR, 0, 255, "sideR"), sideG: controllerInteger(request?.sideG, 0, 255, "sideG"), sideB: controllerInteger(request?.sideB, 0, 255, "sideB"),
      cabPreset: controllerInteger(request?.cabPreset, 0, 255, "cabPreset"), cabGameMapping: controllerBoolean(request?.cabGameMapping, "cabGameMapping")
    });
  }
  async setHall(request: HallRequest) {
    return this.command<ControllerCommandResult>("/api/v1/commands/hall", {
      abcTravel: controllerInteger(request?.abcTravel, 0, 65535, "abcTravel"), abcRtTrigger: controllerInteger(request?.abcRtTrigger, 0, 65535, "abcRtTrigger"), abcRtRelease: controllerInteger(request?.abcRtRelease, 0, 65535, "abcRtRelease"), abcDead: controllerInteger(request?.abcDead, 0, 65535, "abcDead"),
      sideTravel: controllerInteger(request?.sideTravel, 0, 65535, "sideTravel"), sideRtTrigger: controllerInteger(request?.sideRtTrigger, 0, 65535, "sideRtTrigger"), sideRtRelease: controllerInteger(request?.sideRtRelease, 0, 65535, "sideRtRelease"), sideDead: controllerInteger(request?.sideDead, 0, 65535, "sideDead"),
      save: controllerBoolean(request?.save, "save"), rtEnabledAbc: controllerInteger(request?.rtEnabledAbc, 0, 255, "rtEnabledAbc"), rtEnabledSide: controllerInteger(request?.rtEnabledSide, 0, 255, "rtEnabledSide")
    });
  }
  async hallCalibration(action: string) { return this.command<ControllerCommandResult>("/api/v1/commands/hall-calibration", { action: controllerString(action, "action") }); }
  async hallQuery() { return this.command<ControllerCommandResult>("/api/v1/commands/hall-query"); }
  async deviceQuery() { return this.command<ControllerCommandResult>("/api/v1/commands/device-query"); }
  async setLever(request: LeverRequest) {
    return this.command<ControllerCommandResult>("/api/v1/commands/lever", {
      calibrationMin: controllerInteger(request?.calibrationMin, 0, 65535, "calibrationMin"), calibrationMax: controllerInteger(request?.calibrationMax, 0, 65535, "calibrationMax"),
      inverted: controllerBoolean(request?.inverted, "inverted"), sensitivity: controllerInteger(request?.sensitivity, 0, 10, "sensitivity"), save: controllerBoolean(request?.save, "save")
    });
  }
  async leverCalibration(action: string) { return this.command<ControllerCommandResult>("/api/v1/commands/lever-calibration", { action: controllerString(action, "action") }); }
  async bootloader() { return this.command<ControllerCommandResult>("/api/v1/commands/bootloader"); }

  async execute(command: ControllerCommand): Promise<ControllerCommandResult> {
    switch (command.name) {
      case "rescan": return this.rescan();
      case "retry-sync": return this.retrySync();
      case "release-all": return this.releaseAll();
      case "virtual-key": return this.setVirtualKey(command.key, command.pressed);
      case "mode": return this.setMode(command.keyboardMouse);
      case "input-mode": return this.setInputMode(command.modeId);
      case "brightness": return this.setBrightness(command.brightness);
      case "custom-color": return this.setCustomColor(command.red, command.green, command.blue);
      case "pico-lighting": return this.setPicoLighting(command.request);
      case "hall": return this.setHall(command.request);
      case "lever": return this.setLever(command.request);
      case "hall-calibration": return this.hallCalibration(command.action);
      case "lever-calibration": return this.leverCalibration(command.action);
      case "hall-query": return this.hallQuery();
      case "device-query": return this.deviceQuery();
      case "bootloader": return this.bootloader();
    }
  }

  async setInputMode(modeId: string): Promise<ControllerCommandResult> {
    const snapshot = this.getSnapshot();
    if (typeof modeId !== "string" || !snapshot?.canWrite || !snapshot.capabilities.mode ||
      !snapshot.inputModes?.options.some(option => option.id === modeId))
      return { commandId: randomUUID(), status: "Rejected", message: "当前控制器不支持此输入模式。", snapshot: snapshot ?? emptyControllerSnapshot() };
    // The old Host understands /mode with a boolean. Never send it a new endpoint.
    if (!this.snapshot?.inputModes) return this.setMode(modeId === "keyboard");
    return this.command<ControllerCommandResult>("/api/v1/commands/input-mode", { modeId });
  }

  stop(): Promise<void> {
    if (this.stoppingTask) return this.stoppingTask;
    const task = this.stopCore().finally(() => { if (this.stoppingTask === task) this.stoppingTask = undefined; });
    this.stoppingTask = task;
    return task;
  }

  private async stopCore(): Promise<void> {
    this.stopping = true;
    this.clearRestartTimer();
    this.streamAbort?.abort();
    this.streamAbort = undefined;
    this.startAbort?.abort();
    this.startAbort = undefined;
    const starting = this.starting;
    const child = this.child;
    const address = this.address;
    if (child && child.exitCode === null) {
      await this.terminateChild(child, address);
    }
    await starting?.catch(() => undefined);
    if (this.starting === starting) this.starting = undefined;
    this.child = undefined;
    this.address = undefined;
    this.snapshot = undefined;
    this.setStatus({ state: "stopped" });
  }

  private async startCore(signal: AbortSignal): Promise<void> {
    this.setStatus({ state: "starting" });
    let child: import("node:child_process").ChildProcessWithoutNullStreams | undefined;
    let address: string | undefined;
    let diagnostic = "";
    try {
      const moduleDirectory = app.isPackaged
        ? path.join(process.resourcesPath, "controller")
        : path.resolve(__dirname, "../../resources/controller");
      const provider = this.options.resolveTarget ? await this.options.resolveTarget()
        : await resolveControllerProvider(moduleDirectory, app.getVersion(), process.env.OGK_CONTROLLER_MODULE_DIR, process.execPath);
      if (signal.aborted || this.stopping) throw new Error("控制器模块启动已取消。");
      const port = await reservePort();
      if (signal.aborted || this.stopping) throw new Error("控制器模块启动已取消。");
      const { spawn } = await import("node:child_process");
      if (signal.aborted || this.stopping) throw new Error("控制器模块启动已取消。");
      const spawned = spawn(provider.executable, [...provider.prefixArgs, "--port", String(port), "--session-token", this.sessionToken,
        "--instance-id", this.instanceId, "--parent-pid", String(process.pid), "--software-version", app.getVersion(), ...(provider.manifestPath ? ["--manifest", provider.manifestPath] : [])], {
          cwd: provider.directory, windowsHide: true, env: { ...process.env, ...(provider.node ? { ELECTRON_RUN_AS_NODE: "1" } : {}) }
        });
      child = spawned;
      this.child = spawned;
      const capture = (value: Buffer) => { const line = value.toString().replaceAll(this.sessionToken, "[redacted]").trimEnd(); if (!line) return; console.error("[controller]", line); diagnostic = `${diagnostic}${line}\n`.slice(-12000); };
      spawned.stderr.on("data", capture);
      let failReady: ((error: Error) => void) | undefined;
      const childFailure = (message: string) => {
        failReady?.(new Error(message));
        this.handleChildFailure(spawned, message);
      };
      spawned.once("error", error => childFailure(`控制器模块进程错误：${error.message}`));
      spawned.once("exit", (code, signal) => childFailure(`控制器模块进程退出（${code ?? signal ?? "unknown"}）。`));
      const ready = await new Promise<ReadyMessage>((resolve, reject) => {
        let buffer = "";
        const timer = setTimeout(() => reject(new Error("控制器模块启动超时。")), 30000);
        failReady = error => { clearTimeout(timer); reject(error); };
        spawned.stdout.on("data", value => {
          buffer += value.toString(); const lines = buffer.split(/\r?\n/); buffer = lines.pop() ?? "";
          for (const line of lines) {
            if (!line.startsWith("OGK_CONTROLLER_READY ")) { if (line) console.log("[controller]", line.replaceAll(this.sessionToken, "[redacted]")); continue; }
            try { clearTimeout(timer); resolve(JSON.parse(line.slice("OGK_CONTROLLER_READY ".length)) as ReadyMessage); }
            catch { failReady?.(new Error("控制器模块返回了无效的就绪消息。")); }
          }
        });
      });
      const parsed = new URL(ready.address);
      if (parsed.protocol !== "http:" || parsed.hostname !== "127.0.0.1" || parsed.port !== String(port) || parsed.username !== "" || parsed.password !== "" || parsed.pathname !== "/" || parsed.search !== "" || parsed.hash !== "" || ready.instanceId !== this.instanceId || ready.moduleApiVersion !== 1)
        throw new Error("控制器模块身份验证失败。");
      address = parsed.origin;
      this.address = address;
      const health = await this.requestDirect<{ instanceId: string; moduleApiVersion: number; ogkToolBoxVersion: string }>("/health", requestTimeouts.health, signal);
      if (health.instanceId !== this.instanceId || health.moduleApiVersion !== 1 || health.ogkToolBoxVersion !== app.getVersion())
        throw new Error("控制器模块版本或实例不匹配。");
      // A replacement Host may inherit no reliable knowledge of keys held by the
      // previous renderer. Release before exposing its first snapshot/stream.
      await this.request<ControllerCommandResult>("/api/v1/commands/release-all", { method: "POST", signal }, requestTimeouts.release);
      this.snapshot = await this.requestDirect<ControllerSnapshot>("/api/v1/snapshot", requestTimeouts.command, signal);
      this.startStream();
    } catch (error) {
      if (child) {
        if (this.child === child) {
          this.child = undefined;
          if (!address || this.address === address) this.address = undefined;
          this.snapshot = undefined;
        }
        await this.terminateChild(child, address);
      }
      const message = error instanceof Error ? error.message : String(error);
      throw new Error(diagnostic.trim() ? `${message}\n\n控制器模块诊断输出：\n${diagnostic.trim()}` : message);
    }
  }

  private async terminateChild(child: import("node:child_process").ChildProcessWithoutNullStreams,
    address?: string): Promise<void> {
    if (child.exitCode !== null) return;
    if (address) {
      await this.requestTo(address, "/api/v1/commands/release-all", { method: "POST" }, requestTimeouts.release).catch(() => undefined);
      await this.requestTo(address, "/api/v1/commands/shutdown", { method: "POST" }, requestTimeouts.shutdown).catch(() => undefined);
    }
    await waitForExit(child, 1000);
    if (child.exitCode === null) {
      if (process.platform === "win32" && child.pid) {
        await new Promise<void>(resolve => {
          execFile("taskkill", ["/PID", String(child.pid), "/T", "/F"], { windowsHide: true }, () => resolve());
        });
      } else {
        try { child.kill(); } catch { /* process may have exited between the check and kill */ }
      }
      await waitForExit(child, 1000);
    }
  }

  private handleChildFailure(child: import("node:child_process").ChildProcessWithoutNullStreams, message: string): void {
    if (this.child !== child) return;
    this.child = undefined;
    this.address = undefined;
    this.streamAbort?.abort();
    this.streamAbort = undefined;
    this.snapshot = undefined;
    if (this.stopping) return;
    this.setStatus({ state: "fault", error: message });
    this.scheduleRestart(message);
  }

  private scheduleRestart(error: string): void {
    if (this.stopping || this.restartTimer) return;
    const delay = Math.min(10000, 500 * 2 ** Math.min(this.restartAttempt, 4));
    this.restartAttempt = Math.min(5, this.restartAttempt + 1);
    this.restartTimer = setTimeout(() => {
      this.restartTimer = undefined;
      if (this.stopping) return;
      this.setStatus({ state: "restarting", error });
      void this.start().catch(startError => console.error("[controller] restart failed", startError));
    }, delay);
  }

  private clearRestartTimer(): void {
    if (this.restartTimer) clearTimeout(this.restartTimer);
    this.restartTimer = undefined;
  }

  private async command<T>(endpoint: string, body?: unknown): Promise<T> {
    await this.start();
    const init: RequestInit = { method: "POST" };
    if (body !== undefined) { init.body = JSON.stringify(body); init.headers = { "Content-Type": "application/json" }; }
    const child = this.child;
    const result = await this.request<T>(endpoint, init, requestTimeouts.command);
    if (!this.stopping && child === this.child && result && typeof result === "object" && "snapshot" in result) {
      const snapshot = (result as { snapshot: ControllerSnapshot }).snapshot;
      if (snapshot && (!this.snapshot || snapshot.sequence >= this.snapshot.sequence)) this.publish(snapshot);
    }
    return result;
  }

  private async request<T>(endpoint: string, init: RequestInit = {}, timeout: number = requestTimeouts.command): Promise<T> {
    if (!this.address) throw new Error("控制器服务尚未就绪。");
    return this.withResponse(this.address, endpoint, init, timeout, async response => {
      if (!response.ok) throw new Error(await this.errorMessage(response, endpoint));
      if (response.status === 204) return undefined as T;
      return await response.json() as T;
    });
  }

  private async requestDirect<T>(endpoint: string, timeout: number, signal?: AbortSignal): Promise<T> { return this.request<T>(endpoint, signal ? { signal } : {}, timeout); }

  private requestTo(address: string, endpoint: string, init: RequestInit, timeout: number): Promise<Response> {
    // SSE keeps a deadline for headers only; ordinary JSON requests include body consumption.
    return this.withResponse(address, endpoint, init, timeout, async response => response);
  }

  private async withResponse<T>(address: string, endpoint: string, init: RequestInit, timeout: number,
    consume: (response: Response) => Promise<T>): Promise<T> {
    const abort = new AbortController();
    const upstream = init.signal;
    const onAbort = () => abort.abort(upstream?.reason);
    upstream?.addEventListener("abort", onAbort, { once: true });
    if (upstream?.aborted) onAbort();
    const timer = setTimeout(() => abort.abort(), timeout);
    try {
      const response = await fetch(`${address}${endpoint}`, { ...init, redirect: "error", signal: abort.signal,
        headers: { "X-OGK-Controller-Session": this.sessionToken, ...(init.headers ?? {}) } });
      return await consume(response);
    } catch (error) {
      if (abort.signal.aborted && !upstream?.aborted) throw new Error(`控制器请求超时：${endpoint}`);
      throw error;
    } finally {
      clearTimeout(timer);
      upstream?.removeEventListener("abort", onAbort);
    }
  }

  private startStream(): void {
    this.streamAbort?.abort();
    const abort = new AbortController();
    this.streamAbort = abort;
    void this.streamLoop(abort.signal);
  }

  private async streamLoop(signal: AbortSignal): Promise<void> {
    while (!signal.aborted && this.address) {
      try {
        const health = await this.requestDirect<{ instanceId: string; moduleApiVersion: number; ogkToolBoxVersion: string }>("/health", requestTimeouts.health);
        if (health.instanceId !== this.instanceId || health.moduleApiVersion !== 1 || health.ogkToolBoxVersion !== app.getVersion()) throw new Error("控制器模块健康检查不匹配。");
        if (signal.aborted) return;
        await this.consumeStream(signal);
        if (!signal.aborted) { this.snapshot = undefined; this.setStatus({ state: "fault", error: "控制器实时数据流已断开。" }); }
      } catch (error) {
        if (signal.aborted) return;
        const message = error instanceof Error ? error.message : String(error);
        this.snapshot = undefined;
        this.setStatus({ state: "fault", error: message });
      }
      if (!signal.aborted) {
        try { await new Promise<void>((resolve, reject) => { const timer = setTimeout(resolve, 1000); signal.addEventListener("abort", () => { clearTimeout(timer); reject(new Error("aborted")); }, { once: true }); }); }
        catch { return; }
      }
    }
  }

  private async consumeStream(signal: AbortSignal): Promise<void> {
    const response = await this.requestTo(this.address!, "/api/v1/stream", { headers: {}, signal }, requestTimeouts.health);
    if (!response.ok || !response.body) throw new Error("控制器实时数据流连接失败。");
    this.setStatus({ state: "ready" });
    const reader = response.body.getReader(); const decoder = new TextDecoder(); let buffer = "";
    const cancel = () => { void reader.cancel().catch(() => undefined); };
    signal.addEventListener("abort", cancel, { once: true });
    if (signal.aborted) cancel();
    try {
      while (!signal.aborted) {
        const next = await reader.read(); if (next.done) break;
        buffer += decoder.decode(next.value, { stream: true });
        const blocks = buffer.split("\n\n"); buffer = blocks.pop() ?? "";
        for (const block of blocks) {
          const data = block.split(/\r?\n/).find(line => line.startsWith("data: "))?.slice(6);
          if (!data) continue;
          try { this.publish(JSON.parse(data) as ControllerSnapshot); } catch { /* ignore malformed event */ }
        }
      }
    } finally {
      signal.removeEventListener("abort", cancel);
      await reader.cancel().catch(() => undefined);
    }
    if (!signal.aborted) throw new Error("控制器实时数据流已断开。");
  }

  private publish(snapshot: ControllerSnapshot): void { this.snapshot = snapshot; for (const listener of this.listeners) listener(withControllerModes(snapshot)); }
  private setStatus(status: ControllerModuleStatus): void { this.status = status; for (const listener of this.statusListeners) listener(status); }
  private async errorMessage(response: Response, endpoint: string): Promise<string> {
    const raw = await response.text().catch(() => "");
    let body: { error?: string; detail?: string } = {};
    try { body = JSON.parse(raw) as typeof body; } catch { /* Kestrel model-binding failures may have an empty body. */ }
    return body.detail ?? body.error ?? `${endpoint} 控制器请求失败（${response.status}）${raw.trim() ? `：${raw.trim().slice(0, 240)}` : "。"}`;
  }
}

function controllerInteger(value: unknown, minimum: number, maximum: number, name: string): number {
  if (typeof value !== "number" || !Number.isFinite(value)) throw new Error(`控制器命令参数 ${name} 不是有效数字。`);
  return Math.max(minimum, Math.min(maximum, Math.round(value)));
}
function controllerBoolean(value: unknown, name: string): boolean {
  if (typeof value !== "boolean") throw new Error(`控制器命令参数 ${name} 不是有效布尔值。`);
  return value;
}
function controllerString(value: unknown, name: string): string {
  if (typeof value !== "string" || value.length === 0) throw new Error(`控制器命令参数 ${name} 不是有效文本。`);
  return value;
}
async function waitForExit(child: import("node:child_process").ChildProcessWithoutNullStreams, timeout: number): Promise<void> {
  if (child.exitCode !== null) return;
  await new Promise<void>(resolve => {
    const timer = setTimeout(() => { child.removeListener("exit", onExit); resolve(); }, timeout);
    const onExit = () => { clearTimeout(timer); resolve(); };
    child.once("exit", onExit);
  });
}
async function reservePort(): Promise<number> {
  const server = createServer();
  await new Promise<void>((resolve, reject) => { server.once("error", reject); server.listen(0, "127.0.0.1", () => resolve()); });
  const port = (server.address() as import("node:net").AddressInfo).port;
  await new Promise<void>(resolve => server.close(() => resolve()));
  return port;
}
