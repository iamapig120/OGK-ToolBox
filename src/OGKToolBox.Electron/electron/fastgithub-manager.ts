import { app, session } from "electron";
import { spawn, type ChildProcess } from "node:child_process";
import { existsSync } from "node:fs";
import { readFile } from "node:fs/promises";
import { X509Certificate } from "node:crypto";
import net from "node:net";
import path from "node:path";

const proxyHost = "127.0.0.1";
const proxyPort = 38457;
const proxyUrl = `http://${proxyHost}:${proxyPort}`;
const proxyPartition = "electron-updater";

/**
 * Runs the embedded FastGithub build for GitHub traffic in an isolated
 * Electron session. It never changes the Windows system proxy or certificate
 * store.
 */
export class FastGithubManager {
  private process?: ChildProcess;
  private startPromise?: Promise<boolean>;
  private proxyEnabled = false;

  async enable(): Promise<boolean> {
    if (process.platform !== "win32") return false;

    if (this.startPromise) return this.startPromise;
    this.startPromise = this.startAndConfigure();
    try {
      return await this.startPromise;
    } finally {
      this.startPromise = undefined;
    }
  }

  async disable(): Promise<void> {
    this.startPromise = undefined;
    const updaterSession = session.fromPartition(proxyPartition, { cache: false });
    if (this.proxyEnabled) {
      this.proxyEnabled = false;
      try {
        await updaterSession.closeAllConnections();
        updaterSession.setCertificateVerifyProc(null);
        await updaterSession.setProxy({ mode: "direct" });
      } catch {
        // The updater session may already be closing during application exit.
      }
    }
    await this.stopProcess();
  }

  async fetch(input: string, init?: RequestInit): Promise<Response> {
    if (!await this.enable()) return globalThis.fetch(input, init);
    const githubSession = session.fromPartition(proxyPartition, { cache: false });
    return githubSession.fetch(input, init);
  }

  private async startAndConfigure(): Promise<boolean> {
    const executable = path.join(process.resourcesPath, "fastgithub", "fastgithub.exe");
    if (!existsSync(executable)) return false;

    if (!this.process || this.process.killed || this.process.exitCode !== null) {
      const dataRoot = path.join(app.getPath("userData"), "fastgithub");
      const child = spawn(executable, [
        "FastGithub:Embedded=true",
        `ParentProcessId=${process.pid}`,
        `DataRoot=${dataRoot}`
      ], {
        cwd: path.dirname(executable),
        detached: false,
        stdio: "ignore",
        windowsHide: true
      });
      this.process = child;
    }

    if (!await this.waitUntilReady(this.process)) {
      await this.stopProcess();
      return false;
    }

    const dataRoot = path.join(app.getPath("userData"), "fastgithub");
    let caFingerprint: string;
    try {
      caFingerprint = new X509Certificate(await readFile(path.join(dataRoot, "cacert", "fastgithub.cer"))).fingerprint;
    } catch {
      await this.stopProcess();
      return false;
    }

    const updaterSession = session.fromPartition(proxyPartition, { cache: false });
    try {
      await updaterSession.setProxy({
        proxyRules: `http=${proxyUrl};https=${proxyUrl}`,
        proxyBypassRules: "<local>"
      });
      updaterSession.setCertificateVerifyProc((request, callback) => {
        const embeddedCertificate =
          isGithubHost(request.hostname) &&
          request.certificate.issuerName === "FastGithub" &&
          request.certificate.issuerCert?.fingerprint === caFingerprint;
        callback(embeddedCertificate || request.verificationResult === "OK" ? 0 : -2);
      });
      this.proxyEnabled = true;
      return true;
    } catch {
      await this.stopProcess();
      return false;
    }
  }

  private async waitUntilReady(child: ChildProcess): Promise<boolean> {
    let exited = false;
    const onExit = () => { exited = true; };
    child.once("exit", onExit);
    child.once("error", onExit);

    try {
      for (let attempt = 0; attempt < 100; attempt += 1) {
        if (exited) return false;
        if (await canConnect(proxyHost, proxyPort)) return true;
        await delay(100);
      }
      return false;
    } finally {
      child.removeListener("exit", onExit);
      child.removeListener("error", onExit);
    }
  }

  private async stopProcess(): Promise<void> {
    const child = this.process;
    this.process = undefined;
    if (!child || child.exitCode !== null) return;

    child.kill();
    await Promise.race([onceExit(child), delay(1500)]);
  }
}

export const fastGithubManager = new FastGithubManager();

function isGithubHost(hostname: string): boolean {
  const host = hostname.toLowerCase();
  return host === "github.com" || host.endsWith(".github.com") ||
    host.endsWith(".githubusercontent.com") || host.endsWith(".githubassets.com");
}

function canConnect(host: string, port: number): Promise<boolean> {
  return new Promise(resolve => {
    const socket = net.createConnection({ host, port });
    const finish = (connected: boolean) => {
      socket.destroy();
      resolve(connected);
    };
    socket.once("connect", () => finish(true));
    socket.once("error", () => finish(false));
    socket.setTimeout(250, () => finish(false));
  });
}

function onceExit(child: ChildProcess): Promise<void> {
  if (child.exitCode !== null) return Promise.resolve();
  return new Promise(resolve => child.once("exit", () => resolve()));
}

function delay(milliseconds: number): Promise<void> {
  return new Promise(resolve => setTimeout(resolve, milliseconds));
}
