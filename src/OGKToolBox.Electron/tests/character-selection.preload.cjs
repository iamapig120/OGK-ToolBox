// Test bridge only: every resource request stays in memory and resolves on demand.
const gameRoot = "C:\\OGK-character-selection-fixture";
const characters = [1000, 1001, 1002].map((modelId, index) => ({
  id: index + 1, modelId, name: `角色 ${String.fromCharCode(65 + index)}`
}));
const summary = {
  gameRoot, musicCount: 0, cardCount: 0, characterCount: characters.length,
  resourceCount: 0, diagnosticCount: 0, gameVersion: "1.50.0",
  lastScanAt: "2026-01-01T00:00:00Z"
};
const snapshot = () => ({
  summary: { ...summary }, characters: characters.map(item => ({ ...item })),
  music: [], cards: [], resources: [], diagnostics: []
});
const requests = [];
const launches = [];
let windowStateListener;
let scanCount = 0;
let sectionCount = 0;
let nextRequest = 0;
const unsubscribe = () => () => {};
function deferred(kind, payload) {
  return new Promise(resolve => requests.push({
    id: ++nextRequest, kind, payload, resolve, settled: false
  }));
}
function resolvePending(kind, predicate, value) {
  for (const request of requests.filter(item =>
    item.kind === kind && !item.settled && predicate(item.payload))) {
    request.settled = true;
    request.resolve(value);
  }
}
function expressionList(modelId, count) {
  return Array.from({ length: count }, (_, index) => ({
    bundlePath: `fixture-${modelId}`, spritePathId: `${index + 1}`,
    bundleKey: `anm_chara_${String(modelId).padStart(6, "0")}01`,
    name: `Chara_${String(modelId).padStart(6, "0")}01_Face_A_${String(index).padStart(2, "0")}`
  }));
}

localStorage.setItem("ogk-toolbox.game-root.v1", gameRoot);
window.ogk = {
  cachedScan: async () => snapshot(),
  scan: async () => { scanCount++; return snapshot(); },
  librarySummary: async () => ({ ...summary }),
  librarySection: async (_root, kind) => {
    sectionCount++;
    return kind === "characters" ? characters.map(item => ({ ...item })) : [];
  },
  cancelLibrarySection() {},
  prepareGameLauncher: async () => ({ fileName: "fixture.bat" }),
  launchGame: async (root, options) => {
    launches.push({ root, options: { ...options } });
    return { fileName: "fixture.bat" };
  },
  packageManifest: async () => ({
    schemaVersion: 1, repository: "test", release: "test",
    optionPackages: [], mods: [], sourceUrl: "", checkedAt: ""
  }),
  optionPackages: async () => ({ directoryPath: "", exists: true, packages: [] }),
  inspectConfiguration: async () => ({ files: [], mods: [], diagnostics: [] }),
  controllerSnapshot: async () => null,
  controllerStatus: async () => ({ state: "ready" }),
  onControllerSnapshot: unsubscribe,
  onControllerStatus: unsubscribe,
  onPackageProgress: unsubscribe,
  onScanProgress: unsubscribe,
  setUiScale: async scale => scale,
  isWindowMaximized: async () => false,
  onWindowStateChange(callback) {
    windowStateListener = callback;
    return () => { windowStateListener = undefined; };
  },
  thumbnail: async () => null,
  cancelThumbnail() {},
  characterExpressions: request => deferred("list", { ...request }),
  characterExpressionPreview: request => deferred("preview", { ...request })
};

window.__characterSelectionTest = {
  state: () => ({
    scanCount, sectionCount, launches,
    requests: requests.map(({ id, kind, payload, settled }) => ({ id, kind, payload, settled }))
  }),
  rerender(maximized) {
    if (!windowStateListener) throw new Error("Window state listener is not registered.");
    windowStateListener(maximized);
  },
  resolveList(modelId, count) {
    resolvePending("list", payload => payload.modelId === modelId, expressionList(modelId, count));
  },
  resolvePreviews(modelId) {
    // A valid image with an identifiable source lets the test reject stale previews.
    const image = `data:image/svg+xml,${encodeURIComponent(`<svg xmlns="http://www.w3.org/2000/svg" width="2" height="2"><title>${modelId}</title><rect width="2" height="2" fill="green"/></svg>`)}`;
    resolvePending("preview", payload => payload.bundlePath === `fixture-${modelId}`, image);
  }
};
