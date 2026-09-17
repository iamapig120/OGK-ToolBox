import { randomUUID } from "node:crypto";
import type { ControllerBackend, ControllerCommand } from "./controller-backend";
import { emptyControllerSnapshot, hasControllerDevice, withControllerModes } from "../src/controller-state";
import type { ControllerCommandResult, ControllerModuleStatus, ControllerSnapshot, HallRequest, LeverRequest,
  PicoLightingRequest } from "../src/controller-models";

/** Selects one device for UI/commands while keeping backend health independent. */
export class ControllerHub {
  private readonly backends: ControllerBackend[];
  private activeId: string;
  private manualSelection = false;
  private stopping = false;
  private startPromise?: Promise<void>;
  private stopPromise?: Promise<void>;
  private queue: Promise<unknown> = Promise.resolve();
  private reconcilePending = false;
  private sequence = 0;
  private readonly connections = new Map<string, { online: boolean; version: number }>();
  private readonly heldKeys = new Map<string, { backend: ControllerBackend; version: number }>();
  private readonly snapshotListeners = new Set<(snapshot: ControllerSnapshot) => void>();
  private readonly statusListeners = new Set<(status: ControllerModuleStatus) => void>();

  constructor(backends: readonly ControllerBackend[]) {
    if (!backends.length || new Set(backends.map(backend => backend.id)).size !== backends.length)
      throw new Error("Controller backends require unique IDs and at least one registration.");
    this.backends = [...backends];
    this.activeId = backends[0].id;
    for (const backend of backends) {
      backend.onSnapshot(() => this.changed(backend));
      backend.onStatus(() => this.changed(backend));
    }
  }

  getSnapshot(): ControllerSnapshot {
    const backend = this.active();
    const status = backend.getStatus();
    const raw = status.state === "ready" && !this.stopping ? backend.getSnapshot() : null;
    const snapshot = raw ? withControllerModes(raw) : { ...emptyControllerSnapshot(),
      state: status.state === "fault" ? "Faulted" as const : this.stopping || status.state === "stopped" ? "Disabled" as const : "Searching" as const,
      error: status.error ?? null };
    return { ...snapshot, sequence: this.sequence,
      source: { backendId: backend.id, connectionId: `${backend.id}:${this.connections.get(backend.id)?.version ?? 0}` } };
  }

  getStatus(): ControllerModuleStatus {
    const current = this.stopping ? { state: "stopped" as const } : this.active().getStatus();
    return { ...current, selectedBackendId: this.activeId,
      backends: this.backends.map(backend => ({ id: backend.id,
        label: this.connected(backend) ? backend.getSnapshot()!.identity.displayName : backend.label,
        kind: backend.getSnapshot()?.identity.kind,
        connected: this.connected(backend), selected: backend.id === this.activeId,
        state: backend.getStatus().state, error: backend.getStatus().error })) };
  }
  onSnapshot(listener: (snapshot: ControllerSnapshot) => void): () => void {
    this.snapshotListeners.add(listener); listener(this.getSnapshot()); return () => this.snapshotListeners.delete(listener);
  }
  onStatus(listener: (status: ControllerModuleStatus) => void): () => void {
    this.statusListeners.add(listener); listener(this.getStatus()); return () => this.statusListeners.delete(listener);
  }

  start(): Promise<void> {
    if (this.stopPromise) return this.stopPromise.then(() => this.start());
    if (this.startPromise) return this.startPromise;
    this.stopping = false;
    const task = Promise.allSettled(this.backends.map(backend => backend.start())).then(async results => {
      if (this.stopping) return;
      await this.enqueue(() => this.reconcile());
      if (results.every(result => result.status === "rejected")) throw (results[0] as PromiseRejectedResult).reason;
    }).finally(() => { if (this.startPromise === task) this.startPromise = undefined; });
    this.startPromise = task;
    return task;
  }
  async restart(): Promise<void> { await this.stop(); await this.start(); }
  stop(): Promise<void> {
    if (this.stopPromise) return this.stopPromise;
    this.stopping = true;
    const task = Promise.allSettled(this.backends.map(backend => backend.stop())).then(async () => {
      await this.queue.catch(() => undefined);
      await this.startPromise?.catch(() => undefined);
      this.heldKeys.clear(); this.publish();
    }).finally(() => { if (this.stopPromise === task) this.stopPromise = undefined; });
    this.stopPromise = task;
    return task;
  }

  selectBackend(id: string): Promise<ControllerCommandResult> {
    return this.enqueue(async () => {
      const selected = this.backends.find(backend => backend.id === id);
      if (!selected || this.stopping) return this.result("Rejected", "无法选择此控制器。");
      await this.switchTo(selected);
      if (this.stopping) return this.result("Rejected", "控制器服务已停止。");
      this.manualSelection = true;
      this.publish();
      return this.result("Verified", "已选择控制器。");
    });
  }

  rescan(): Promise<ControllerCommandResult> { return this.scan("rescan"); }
  retrySync(): Promise<ControllerCommandResult> { return this.scan("retry-sync"); }
  releaseAll(): Promise<ControllerCommandResult> {
    return this.enqueue(async () => {
      const results = await Promise.allSettled(this.backends.map(backend => backend.releaseAllIfRunning()));
      for (const [key, owner] of this.heldKeys)
        if (results[this.backends.indexOf(owner.backend)].status === "fulfilled") this.heldKeys.delete(key);
      return this.result(results.some(result => result.status === "rejected") ? "Failed" : "Verified",
        results.some(result => result.status === "rejected") ? "部分控制器未能确认释放输入。" : "虚拟按键已全部释放。");
    });
  }
  async releaseAllIfRunning(): Promise<void> { await this.releaseAll(); }

  setVirtualKey(key: string, pressed: boolean): Promise<ControllerCommandResult> {
    const intended = this.active();
    const version = this.connections.get(intended.id)?.version;
    return this.enqueue(async () => {
      if (typeof key !== "string" || typeof pressed !== "boolean") return this.result("Rejected", "虚拟按键参数无效。");
      const owner = !pressed ? this.heldKeys.get(key) : undefined;
      const target = owner?.backend ?? intended;
      const targetVersion = owner?.version ?? version;
      if (this.stopping || !this.connected(target) || targetVersion !== this.connections.get(target.id)?.version ||
        (!owner && target !== this.active()) || !target.getSnapshot()?.capabilities.virtualKeys)
        return this.result("Rejected", "当前控制器不支持虚拟按键。");
      const result = await target.execute({ name: "virtual-key", key, pressed });
      if (result.status === "Verified" || result.status === "Accepted") {
        if (pressed) this.heldKeys.set(key, { backend: target, version: targetVersion! }); else this.heldKeys.delete(key);
      }
      return { ...result, snapshot: this.getSnapshot() };
    });
  }
  setMode(keyboardMouse: boolean) { return this.execute({ name: "mode", keyboardMouse }); }
  setInputMode(modeId: string) { return this.execute({ name: "input-mode", modeId }); }
  setBrightness(brightness: number) { return this.execute({ name: "brightness", brightness }); }
  setCustomColor(red: number, green: number, blue: number) { return this.execute({ name: "custom-color", red, green, blue }); }
  setPicoLighting(request: PicoLightingRequest) { return this.execute({ name: "pico-lighting", request }); }
  setHall(request: HallRequest) { return this.execute({ name: "hall", request }); }
  hallCalibration(action: string) { return this.execute({ name: "hall-calibration", action }); }
  hallQuery() { return this.execute({ name: "hall-query" }); }
  deviceQuery() { return this.execute({ name: "device-query" }); }
  setLever(request: LeverRequest) { return this.execute({ name: "lever", request }); }
  leverCalibration(action: string) { return this.execute({ name: "lever-calibration", action }); }
  bootloader() { return this.execute({ name: "bootloader" }); }

  private execute(command: ControllerCommand): Promise<ControllerCommandResult> {
    const intended = this.active();
    const version = this.connections.get(intended.id)?.version;
    return this.enqueue(async () => {
      if (this.stopping || intended !== this.active() || !this.connected(intended) ||
        version !== this.connections.get(intended.id)?.version) return this.result("Rejected", "控制器连接已变化，请重试。");
      const result = await intended.execute(command);
      return { ...result, snapshot: this.getSnapshot() };
    });
  }
  private scan(name: "rescan" | "retry-sync"): Promise<ControllerCommandResult> {
    return this.enqueue(async () => {
      if (this.stopping) return this.result("Rejected", "控制器服务已停止。");
      const results = await Promise.allSettled(this.backends.map(backend => backend.execute({ name })));
      await this.reconcile();
      const result = results[this.backends.indexOf(this.active())];
      if (result.status === "rejected") throw result.reason;
      return { ...result.value, snapshot: this.getSnapshot() };
    });
  }
  private active(): ControllerBackend { return this.backends.find(backend => backend.id === this.activeId)!; }
  private connected(backend: ControllerBackend): boolean {
    return !this.stopping && backend.getStatus().state === "ready" && hasControllerDevice(backend.getSnapshot());
  }
  private async switchTo(backend: ControllerBackend): Promise<void> {
    if (backend.id === this.activeId) return;
    const previous = this.active();
    await previous.releaseAllIfRunning();
    if (this.stopping) return;
    for (const [key, owner] of this.heldKeys) if (owner.backend === previous) this.heldKeys.delete(key);
    this.activeId = backend.id;
    this.publish();
  }
  private async reconcile(): Promise<void> {
    if (this.stopping || this.manualSelection || this.connected(this.active())) return;
    const next = this.backends.find(backend => this.connected(backend));
    if (next) await this.switchTo(next);
  }
  private changed(backend: ControllerBackend): void {
    const previous = this.connections.get(backend.id) ?? { online: false, version: 0 };
    const online = this.connected(backend);
    // Keep a manual choice while it is connected, but let a lost selection fall back to an available device.
    if (!this.stopping && backend.id === this.activeId && previous.online && !online) this.manualSelection = false;
    this.connections.set(backend.id, { online, version: previous.version + (online && !previous.online ? 1 : 0) });
    this.publish();
    if (this.reconcilePending || this.stopping) return;
    this.reconcilePending = true;
    void this.enqueue(() => this.reconcile()).catch(() => {
      // A failed release keeps the previous device selected. Its status remains visible.
    }).finally(() => { this.reconcilePending = false; });
  }
  private enqueue<T>(operation: () => Promise<T>): Promise<T> {
    const next = this.queue.then(operation); this.queue = next.catch(() => undefined); return next;
  }
  private result(status: ControllerCommandResult["status"], message: string): ControllerCommandResult {
    return { commandId: randomUUID(), status, message, snapshot: this.getSnapshot() };
  }
  private publish(): void {
    ++this.sequence;
    const snapshot = this.getSnapshot(), status = this.getStatus();
    for (const listener of this.snapshotListeners) listener(snapshot);
    for (const listener of this.statusListeners) listener(status);
  }
}
