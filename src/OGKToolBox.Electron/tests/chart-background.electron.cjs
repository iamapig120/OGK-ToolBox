// Run with: node tests/chart-background.electron.cjs
// Real Electron + the actual ChartPlayer, without the application backend or devices.
// The test briefly shows/overlays its own unfocused windows before hiding/minimizing them.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

if (!process.versions.electron) {
  const { spawn } = require("node:child_process");
  const temporaryRoot = path.resolve(os.tmpdir());
  const profile = fs.mkdtempSync(path.join(temporaryRoot, "ogk-chart-background-"));
  const env = { ...process.env, OGK_CHART_TEST_PROFILE: profile, ELECTRON_DISABLE_SECURITY_WARNINGS: "true" };
  delete env.ELECTRON_RUN_AS_NODE;
  const child = spawn(require("electron"), [__filename], { env, stdio: "inherit", windowsHide: true });
  const cleanup = () => {
    const target = path.resolve(profile);
    if (path.dirname(target) !== temporaryRoot || !path.basename(target).startsWith("ogk-chart-background-"))
      throw new Error(`Unexpected test profile location: ${target}`);
    fs.rmSync(target, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  };
  child.on("error", error => { console.error(error); cleanup(); process.exitCode = 1; });
  child.on("exit", code => { cleanup(); process.exitCode = code ?? 1; });
} else {
  const { app, BrowserWindow } = require("electron");
  const profile = process.env.OGK_CHART_TEST_PROFILE;
  if (!profile) throw new Error("Run this test through Node to isolate and clean up its profile.");
  app.setPath("userData", profile);
  app.setPath("sessionData", path.join(profile, "session"));
  app.on("window-all-closed", () => {}); // The baseline and fixed cases use separate windows.
  // Match the application's current software rendering setup; no occlusion-related flags.
  app.commandLine.appendSwitch("disable-gpu");
  app.commandLine.appendSwitch("disable-gpu-compositing");
  app.commandLine.appendSwitch("in-process-gpu");
  app.commandLine.appendSwitch("use-angle", "swiftshader");

  const sleep = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
  function productionThrottling() {
    // Read the real BrowserWindow setting without starting main.ts (which starts providers).
    const ts = require("typescript");
    const file = path.join(__dirname, "../electron/main.ts");
    const source = ts.createSourceFile(file, fs.readFileSync(file, "utf8"), ts.ScriptTarget.Latest, true);
    const windows = [];
    function visit(node) {
      if (ts.isNewExpression(node) && node.expression.getText(source) === "BrowserWindow") windows.push(node);
      ts.forEachChild(node, visit);
    }
    visit(source);
    assert.equal(windows.length, 1, "Identify the application's main BrowserWindow explicitly if more windows are introduced.");
    const property = (object, name) => object.properties.find(item => item.name?.getText(source) === name)?.initializer;
    const preferences = property(windows[0].arguments[0], "webPreferences");
    const value = property(preferences, "backgroundThrottling");
    if (!value) return true; // Electron's documented default.
    assert.ok([ts.SyntaxKind.TrueKeyword, ts.SyntaxKind.FalseKeyword].includes(value.kind));
    return value.kind === ts.SyntaxKind.TrueKeyword;
  }

  async function fixture() {
    const preview = {
      durationSeconds: 60, maxTick: 23040, resolution: 192, measureCount: 30,
      unknownCommandCount: 0, tempos: [{ tick: 0, seconds: 0, bpm: 120 }], scrollRanges: [], lanes: [],
      notes: Array.from({ length: 100 }, (_, index) => ({ kind: "Tap", tick: index * 192, x: 0, laneKind: "Center" }))
    };
    fs.writeFileSync(path.join(profile, "preload.cjs"), `require("electron").contextBridge.exposeInMainWorld("ogk", { chartPreview: async () => (${JSON.stringify(preview)}) });`);
    await require("esbuild").build({
      stdin: {
        contents: `
          import React, { useState } from "react";
          import { createRoot } from "react-dom/client";
          import { ChartPlayer } from "./src/chart-player";
          const stats = { callbacks: 0, paints: 0, closed: false, active: new Set() };
          const request = window.requestAnimationFrame.bind(window);
          const cancel = window.cancelAnimationFrame.bind(window);
          window.requestAnimationFrame = callback => {
            const id = request(time => { stats.active.delete(id); stats.callbacks++; callback(time); });
            stats.active.add(id); return id;
          };
          window.cancelAnimationFrame = id => { stats.active.delete(id); cancel(id); };
          const fillRect = CanvasRenderingContext2D.prototype.fillRect;
          CanvasRenderingContext2D.prototype.fillRect = function(...args) {
            if (this.canvas.classList.contains("chart-canvas")) stats.paints++;
            return fillRect.apply(this, args);
          };
          window.chartTest = () => ({
            callbacks: stats.callbacks, paints: stats.paints, active: stats.active.size, closed: stats.closed,
            position: Number(document.querySelector('[aria-label="谱面播放位置"]')?.value || 0),
            playing: !!document.querySelector('button[aria-label="暂停"]'), visibility: document.visibilityState
          });
          function Fixture() {
            const [closed, setClosed] = useState(false);
            return closed ? <p>Closed</p> : <ChartPlayer chart={{filePath:"fixture",difficultyName:"Test"}}
              music={{title:"Background regression"}} root="" onClose={() => { stats.closed = true; setClosed(true); }}/>
          }
          createRoot(document.getElementById("root")).render(<Fixture/>);
        `,
        resolveDir: path.resolve(__dirname, ".."), sourcefile: "chart-background-fixture.tsx", loader: "tsx"
      },
      outfile: path.join(profile, "fixture.js"), bundle: true, platform: "browser", jsx: "automatic",
      define: { "process.env.NODE_ENV": '"production"' }, logLevel: "silent"
    });
    fs.writeFileSync(path.join(profile, "index.html"), `<!doctype html><meta charset="utf-8">
      <meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'self'; style-src 'unsafe-inline'">
      <style>body{margin:0}.player-info,.player-options{display:none}.chart-board{width:600px;height:420px}.chart-canvas{width:100%;height:100%}</style>
      <div id="root"></div><script src="fixture.js"></script>`);
  }

  async function exercise(backgroundThrottling, baseline = false) {
    const win = new BrowserWindow({
      show: false, width: 640, height: 540, frame: false, transparent: true, skipTaskbar: true,
      webPreferences: { preload: path.join(profile, "preload.cjs"), contextIsolation: true, nodeIntegration: false, sandbox: false, backgroundThrottling }
    });
    const errors = [];
    win.webContents.on("console-message", details => { if (details.level === "error") errors.push(details.message); });
    win.webContents.session.webRequest.onBeforeRequest({ urls: ["http://*/*", "https://*/*"] }, (_details, done) => done({ cancel: true }));
    const evaluate = source => win.webContents.executeJavaScript(source);
    const state = () => evaluate("window.chartTest?.()");
    const click = selector => evaluate(`document.querySelector(${JSON.stringify(selector)}).click()`);
    const waitFor = async (predicate, description) => {
      const deadline = Date.now() + 5000;
      while (Date.now() < deadline) {
        if (predicate(await state())) return;
        await sleep(40);
      }
      throw new Error(`Timed out waiting for ${description}: ${JSON.stringify(await state())}; ${errors.join("\n")}`);
    };
    async function advancing(label, minimumCallbacks = 8) {
      // No renderer calls/timers during the observation interval: sampling cannot wake its rAF loops.
      const before = await state();
      await sleep(1200);
      const after = await state();
      const result = { state: label, seconds: +(after.position - before.position).toFixed(3), callbacks: after.callbacks - before.callbacks, paints: after.paints - before.paints };
      assert.ok(result.seconds >= 0.75, `The chart clock must advance while ${label}: ${JSON.stringify(result)}`);
      assert.ok(result.callbacks >= minimumCallbacks && result.paints >= minimumCallbacks, `The real animation loops/canvas must keep drawing while ${label}: ${JSON.stringify(result)}`);
      console.log(JSON.stringify(result));
    }
    try {
      await win.loadFile(path.join(profile, "index.html"));
      win.showInactive();
      await waitFor(value => value?.playing && value.paints > 0 && value.position > 0.1, "actual ChartPlayer autoplay and canvas rendering");
      win.hide();
      assert.equal(win.isVisible(), false);
      await sleep(250); // Let the native visibility change reach Chromium.
      if (baseline) {
        const before = await state();
        await sleep(1200);
        const after = await state();
        assert.equal(after.visibility, "hidden");
        assert.equal(after.callbacks, before.callbacks, "The default setting must reproduce stopped rAF in this environment.");
        assert.equal(after.paints, before.paints);
        console.log("BASELINE: Electron's default background throttling stops the actual player's animation callbacks and canvas draws when hidden.");
        return;
      }
      await advancing("hidden");
      win.showInactive();
      // An opaque, unfocusable test window covers only our own player window.
      const cover = new BrowserWindow({
        ...win.getBounds(), show: false, frame: false, focusable: false, skipTaskbar: true,
        alwaysOnTop: true, backgroundColor: "#202020", webPreferences: { sandbox: false, contextIsolation: true, nodeIntegration: false }
      });
      try {
        await cover.loadURL(`data:text/html,${encodeURIComponent("<body style='background:#202020;color:white'>Chart background regression</body>")}`);
        cover.showInactive();
        assert.equal(cover.isVisible(), true);
        assert.deepEqual(cover.getBounds(), win.getBounds());
        await sleep(250);
        await advancing("covered by the test window");
      } finally {
        cover.destroy();
      }
      win.minimize();
      await sleep(250);
      assert.equal(win.isMinimized(), true);
      // Windows may lower native minimized presentation to 1 fps even with throttling disabled.
      // Require actual progress, not a foreground frame rate from an invisible native surface.
      await advancing("minimized", 1);
      await click('button[aria-label="暂停"]');
      await waitFor(value => !value.playing, "pause while minimized");
      await sleep(1200); // Let the minimized window present the explicit pause position.
      const paused = await state();
      await sleep(1200);
      assert.equal((await state()).position, paused.position, "Explicit pause must keep the chart clock still.");
      await click('button[aria-label="播放"]');
      await waitFor(value => value.playing && value.position > paused.position + 0.1, "resume while minimized");
      await advancing("minimized after resume", 1);
      await click(".player-close");
      await waitFor(value => value.closed && value.active === 0, "close and cancellation of both animation loops");
      const closed = await state();
      await sleep(400);
      assert.equal((await state()).callbacks, closed.callbacks, "Closing the player must stop its animation callbacks.");
      assert.deepEqual(errors, [], "The actual renderer must remain free of errors.");
      console.log("PASS: production window setting keeps the actual ChartPlayer clock/canvas active when hidden, covered, or minimized; pause/resume/close still work.");
    } finally {
      win.destroy();
    }
  }

  const timeout = setTimeout(() => { console.error("Chart background regression timed out."); app.exit(1); }, 30000);
  async function run() {
    await fixture();
    await app.whenReady();
    console.log(`Electron ${process.versions.electron}; ${process.platform}`);
    await exercise(true, true);
    await exercise(productionThrottling());
  }
  run().then(() => { clearTimeout(timeout); app.exit(0); }).catch(error => { clearTimeout(timeout); console.error(error); app.exit(1); });
}
