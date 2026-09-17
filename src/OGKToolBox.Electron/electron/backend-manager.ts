import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { randomBytes, randomUUID } from "node:crypto";
import path from "node:path";
import { app } from "electron";

type ReadyMessage = { address: string; instanceId: string };
const requestTimeout = 5000;

export class BackendManager {
  private child?: ChildProcessWithoutNullStreams;
  private address?: string;
  private readonly sessionToken = randomBytes(32).toString("base64url");
  private readonly instanceId = randomUUID();
  private starting?: Promise<void>;

  start(): Promise<void> {
    this.starting ??= this.startCore().catch(error => {
      // A rejected promise must not poison every subsequent directory/retry request.
      this.starting = undefined;
      this.address = undefined;
      if (this.child?.exitCode === null) this.child.kill();
      throw error;
    });
    return this.starting;
  }

  async request<T>(endpoint: string, init: RequestInit = {}, timeout = requestTimeout): Promise<T> {
    await this.start();
    const response = await this.fetchWithTimeout(`${this.address}${endpoint}`, {
      ...init,
      headers: { "X-OGK-Session": this.sessionToken, ...(init.headers ?? {}) }
    }, timeout);
    if (!response.ok) throw new Error(await this.errorMessage(response));
    if (response.status === 204) return undefined as T;
    return await response.json() as T;
  }

  async bytes(endpoint: string, signal?: AbortSignal): Promise<Buffer> {
    await this.start();
    const response = await this.fetchWithTimeout(`${this.address}${endpoint}`, {
      headers: { "X-OGK-Session": this.sessionToken }, signal
    }, 10000);
    if (!response.ok) throw new Error(await this.errorMessage(response));
    return Buffer.from(await response.arrayBuffer());
  }

  async download(endpoint: string): Promise<{ bytes: Buffer; fileName: string }> {
    await this.start();
    const response = await this.fetchWithTimeout(`${this.address}${endpoint}`, {
      headers: { "X-OGK-Session": this.sessionToken }
    }, 30000);
    if (!response.ok) throw new Error(await this.errorMessage(response));
    const disposition = response.headers.get("content-disposition") ?? "";
    const encoded = /filename\*=UTF-8''([^;]+)/i.exec(disposition)?.[1];
    const quoted = /filename="([^"]+)"/i.exec(disposition)?.[1];
    return {
      bytes: Buffer.from(await response.arrayBuffer()),
      fileName: encoded ? decodeURIComponent(encoded) : (quoted ?? "export.bin")
    };
  }

  async stop(): Promise<void> {
    const child = this.child;
    if (!child || child.exitCode !== null) return;
    try {
      await this.request<void>("/api/session/shutdown", { method: "POST" }, 750);
    } catch { /* process termination below is the fallback */ }
    await new Promise<void>(resolve => {
      const timer = setTimeout(() => {
        if (child.exitCode === null) child.kill();
        resolve();
      }, 1500);
      child.once("exit", () => { clearTimeout(timer); resolve(); });
    });
  }

  private async startCore(): Promise<void> {
    const command = app.isPackaged
      ? { file: path.join(process.resourcesPath, "api", "OGKToolBox.Api.exe"), args: [] as string[] }
      : {
          file: "dotnet",
          args: ["run", "--project", path.join(__dirname, "../../../OGKToolBox.Api/OGKToolBox.Api.csproj"),
            "--no-launch-profile", "--no-build"]
        };
    const child = spawn(command.file, command.args, {
      windowsHide: true,
      env: {
        ...process.env,
        OGK_API_URL: "http://127.0.0.1:0",
        OGK_SESSION_TOKEN: this.sessionToken,
        OGK_INSTANCE_ID: this.instanceId,
        OGK_PARENT_PID: String(process.pid)
      }
    });
    this.child = child;
    let diagnosticOutput = "";
    const captureOutput = (value: Buffer) => {
      const text = value.toString().trimEnd();
      if (!text) return;
      console.error("[api]", text);
      // Keep the useful tail while preventing an unbounded startup failure message.
      diagnosticOutput = `${diagnosticOutput}${text}\n`.slice(-12_000);
    };
    child.stderr.on("data", captureOutput);

    const ready = await new Promise<ReadyMessage>((resolve, reject) => {
      let buffer = "";
      const timer = setTimeout(() => reject(new Error("本地 API 启动超时。")), 30_000);
      const fail = (error: Error) => { clearTimeout(timer); reject(error); };
      child.once("error", fail);
      child.once("exit", code => fail(new Error(`本地 API 在就绪前退出（${code ?? "unknown"}）。`)));
      child.stdout.on("data", value => {
        buffer += value.toString();
        const lines = buffer.split(/\r?\n/);
        buffer = lines.pop() ?? "";
        for (const line of lines) {
          if (!line.startsWith("OGK_READY ")) { if (line) console.log("[api]", line); continue; }
          try {
            const message = JSON.parse(line.slice(10)) as ReadyMessage;
            clearTimeout(timer);
            resolve(message);
          } catch { fail(new Error("本地 API 返回了无效的就绪消息。")); }
        }
      });
    }).catch(error => {
      if (child.exitCode === null) child.kill();
      const message = error instanceof Error ? error.message : String(error);
      const details = diagnosticOutput.trim();
      throw new Error(details
        ? `${message}\n\n本地 API 诊断输出：\n${details}`
        : message);
    });

    const parsed = new URL(ready.address);
    if (parsed.protocol !== "http:" || parsed.hostname !== "127.0.0.1" || ready.instanceId !== this.instanceId) {
      child.kill();
      throw new Error("本地 API 身份验证失败。");
    }
    this.address = parsed.origin;
    const health = await this.requestDirect<{ instanceId: string }>("/api/health");
    if (health.instanceId !== this.instanceId) {
      child.kill();
      throw new Error("本地 API 实例不匹配。");
    }
  }

  private async fetchWithTimeout(url: string, init: RequestInit, timeout: number): Promise<Response> {
    const controller = new AbortController();
    const upstream = init.signal;
    const onAbort = () => controller.abort(upstream?.reason);
    upstream?.addEventListener("abort", onAbort, { once: true });
    const timer = setTimeout(() => controller.abort(), timeout);
    try {
      return await fetch(url, { ...init, signal: controller.signal });
    } finally {
      clearTimeout(timer);
      upstream?.removeEventListener("abort", onAbort);
    }
  }

  private async requestDirect<T>(endpoint: string): Promise<T> {
    const response = await this.fetchWithTimeout(`${this.address}${endpoint}`, {
      headers: { "X-OGK-Session": this.sessionToken }
    }, requestTimeout);
    if (!response.ok) throw new Error(await this.errorMessage(response));
    return await response.json() as T;
  }

  private async errorMessage(response: Response): Promise<string> {
    const body = await response.json().catch(() => ({})) as { detail?: string; title?: string; error?: string };
    return body.detail ?? body.error ?? body.title ?? `本地 API 请求失败（${response.status}）。`;
  }
}
