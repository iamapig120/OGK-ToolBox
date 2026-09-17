import { app, BrowserWindow, dialog, ipcMain, safeStorage } from "electron";
import { autoUpdater } from "electron-updater";
import fs from "node:fs/promises";
import { existsSync, readFileSync } from "node:fs";
import path from "node:path";
import { fastGithubManager } from "./fastgithub-manager";

export type UpdateState =
  | "idle"
  | "unsupported"
  | "checking"
  | "available"
  | "not-available"
  | "downloading"
  | "ready"
  | "error";

export type UpdateStatus = {
  packaged: boolean;
  currentVersion: string;
  availableVersion?: string;
  state: UpdateState;
  progress?: number;
  error?: string;
  hasToken: boolean;
};

const tokenHelp = "需要更新令牌。请在设置「关于」中填写对私有仓库具有 Contents: Read 权限的 GitHub fine-grained token。";

type Hooks = {
  stopSidecar(): Promise<void>;
  stopController(): Promise<void>;
  beginInstallQuit(): void;
  getWindow(): BrowserWindow | undefined;
};

type UpdateFeed = { owner: string; repo: string; privateFeed: boolean };

export class UpdateManager {
  private readonly listeners = new Set<(status: UpdateStatus) => void>();
  private status: UpdateStatus = {
    packaged: app.isPackaged,
    currentVersion: app.getVersion(),
    state: app.isPackaged ? "idle" : "unsupported",
    hasToken: false
  };
  private installing = false;
  private promptedVersion?: string;
  private configured = false;
  private readonly fastGithub = fastGithubManager;

  constructor(private readonly hooks: Hooks) {}

  get isInstalling(): boolean {
    return this.installing;
  }

  async start(): Promise<void> {
    this.status.hasToken = Boolean(await this.readToken());
    this.emit();
    this.registerIpc();
    if (!app.isPackaged) return;

    autoUpdater.autoDownload = true;
    autoUpdater.autoInstallOnAppQuit = false;
    autoUpdater.allowPrerelease = false;
    autoUpdater.autoRunAppAfterInstall = true;

    autoUpdater.on("checking-for-update", () => this.patch({ state: "checking", error: undefined }));
    autoUpdater.on("update-available", info => {
      this.patch({ state: "available", availableVersion: info.version, error: undefined });
    });
    autoUpdater.on("update-not-available", () => {
      this.patch({ state: "not-available", availableVersion: undefined, progress: undefined, error: undefined });
      void this.fastGithub.disable();
    });
    autoUpdater.on("download-progress", progress => {
      this.patch({ state: "downloading", progress: progress.percent, error: undefined });
    });
    autoUpdater.on("update-downloaded", info => {
      this.patch({
        state: "ready",
        availableVersion: info.version,
        progress: 100,
        error: undefined
      });
      void this.fastGithub.disable();
      void this.promptInstall(info.version);
    });
    autoUpdater.on("error", error => {
      this.patch({ state: "error", error: mapUpdateError(error) });
      void this.fastGithub.disable();
    });

    await this.check(false);
  }

  private registerIpc(): void {
    ipcMain.handle("app:get-version", () => app.getVersion());
    ipcMain.handle("update:status", () => this.status);
    ipcMain.handle("update:set-token", async (_event, token: unknown) => {
      if (typeof token !== "string") throw new Error("更新令牌无效。");
      await this.writeToken(token.trim());
      this.status.hasToken = Boolean(token.trim());
      this.configured = false;
      this.emit();
      return this.status;
    });
    ipcMain.handle("update:check", () => this.check(true));
    ipcMain.handle("update:install", () => this.install());
    ipcMain.on("update:subscribe", event => {
      const send = (status: UpdateStatus) => {
        if (!event.sender.isDestroyed()) event.sender.send("update:status", status);
      };
      this.listeners.add(send);
      send(this.status);
      event.sender.once("destroyed", () => this.listeners.delete(send));
    });
  }

  private async check(manual: boolean): Promise<UpdateStatus> {
    if (!app.isPackaged) {
      this.patch({ state: "unsupported", error: "开发模式不检查更新。" });
      return this.status;
    }

    const feed = readUpdateFeed();
    if (!feed) {
      this.patch({
        state: "error",
        error: "未配置更新源。安装包缺少 app-update.yml。"
      });
      return this.status;
    }

    const token = await this.readToken();
    this.status.hasToken = Boolean(token);
    if (feed.privateFeed && !token) {
      this.patch({ state: "error", error: tokenHelp });
      return this.status;
    }

    await this.fastGithub.enable();
    this.configureFeed(feed, token);
    try {
      await autoUpdater.checkForUpdates();
    } catch (error) {
      this.patch({ state: "error", error: mapUpdateError(error) });
      await this.fastGithub.disable();
    }
    if (manual && this.status.state === "ready") void this.promptInstall(this.status.availableVersion);
    return this.status;
  }

  private async install(): Promise<void> {
    if (this.installing) return;
    if (this.status.state !== "ready") throw new Error("还没有下载完成的更新。");
    this.installing = true;
    try {
      await this.fastGithub.disable();
      await this.hooks.stopController();
      await this.hooks.stopSidecar();
      this.hooks.beginInstallQuit();
      autoUpdater.quitAndInstall(false, true);
    } catch (error) {
      this.installing = false;
      this.patch({ state: "error", error: mapUpdateError(error) });
      throw error;
    }
  }

  async stop(): Promise<void> {
    await this.fastGithub.disable();
  }

  private async promptInstall(version?: string): Promise<void> {
    if (!version || this.promptedVersion === version) return;
    const window = this.hooks.getWindow();
    if (!window || window.isDestroyed()) return;
    this.promptedVersion = version;
    const result = await dialog.showMessageBox(window, {
      type: "info",
      buttons: ["立即重启", "稍后"],
      defaultId: 0,
      cancelId: 1,
      title: "更新已就绪",
      message: `新版本 ${version} 已就绪，重启后完成更新。`
    });
    if (result.response === 0) await this.install();
  }

  private configureFeed(feed: UpdateFeed, token?: string): void {
    if (token) process.env.GH_TOKEN = token;
    else delete process.env.GH_TOKEN;
    if (this.configured) return;
    autoUpdater.setFeedURL({
      provider: "github",
      owner: feed.owner,
      repo: feed.repo,
      private: feed.privateFeed,
      token: token || undefined,
      releaseType: "release"
    });
    this.configured = true;
  }

  private tokenPath(): string {
    return path.join(app.getPath("userData"), "github-update-token");
  }

  private async readToken(): Promise<string | undefined> {
    const file = this.tokenPath();
    if (!existsSync(file)) return undefined;
    const raw = (await fs.readFile(file, "utf8")).trim();
    if (!raw) return undefined;
    if (raw.startsWith("enc:")) {
      if (!safeStorage.isEncryptionAvailable()) return undefined;
      try {
        return safeStorage.decryptString(Buffer.from(raw.slice(4), "base64")).trim() || undefined;
      } catch {
        return undefined;
      }
    }
    return raw;
  }

  private async writeToken(token: string): Promise<void> {
    const file = this.tokenPath();
    if (!token) {
      await fs.rm(file, { force: true });
      delete process.env.GH_TOKEN;
      return;
    }
    const payload = safeStorage.isEncryptionAvailable()
      ? `enc:${safeStorage.encryptString(token).toString("base64")}`
      : token;
    await fs.writeFile(file, payload, "utf8");
  }

  private patch(update: Partial<UpdateStatus>): void {
    this.status = { ...this.status, ...update, packaged: app.isPackaged, currentVersion: app.getVersion() };
    this.emit();
  }

  private emit(): void {
    for (const listener of this.listeners) listener(this.status);
  }
}

function readUpdateFeed(): UpdateFeed | undefined {
  const file = path.join(process.resourcesPath, "app-update.yml");
  if (!existsSync(file)) return undefined;
  const text = readFileSync(file, "utf8");
  const owner = /^owner:\s*(\S+)/m.exec(text)?.[1];
  const repo = /^repo:\s*(\S+)/m.exec(text)?.[1];
  if (!owner || !repo) return undefined;
  const privateLine = /^private:\s*(true|false)/m.exec(text)?.[1];
  return { owner, repo, privateFeed: privateLine === "true" };
}

function mapUpdateError(error: unknown): string {
  const message = error instanceof Error ? error.message : String(error);
  const lower = message.toLowerCase();
  if (lower.includes("401") || lower.includes("unauthorized") || lower.includes("bad credentials")) {
    return "GitHub 拒绝了更新请求。公开 Release 仓不需要令牌；若仍失败，请稍后重试。";
  }
  if (lower.includes("404") || lower.includes("not found") || lower.includes("rate limit")) {
    return "无法读取更新源。请确认 lynshp/OGKToolBox-releases 已发布包含 latest.yml 的正式版本。";
  }
  if (lower.includes("latest.yml") || lower.includes("cannot find channel") || lower.includes("no published versions")) {
    return "未找到可用的 GitHub Release。请确认已发布包含 latest.yml 的正式版本。";
  }
  return message || "检查更新失败。";
}
