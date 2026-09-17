const assert = require('node:assert/strict');
const test = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const ts = require('typescript');
const { renderToStaticMarkup } = require('react-dom/server');

const source = fs.readFileSync(path.join(__dirname, '../src/hdd-setup-wizard.tsx'), 'utf8');
const code = ts.transpileModule(source, { compilerOptions: {
  target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX
}}).outputText;

function wizard({ kind = 'Pico', state = 'SyncingDevice', moduleState = 'ready' } = {}) {
  const values = ['controller', {}, false];
  let cursor = 0;
  const exports = {};
  vm.runInNewContext(code, {
    exports, document: { body: {} },
    require(name) {
      if (name.endsWith('.css')) return {};
      if (name === 'react-dom') return { createPortal: content => content };
      if (name === 'react') return {
        useEffect() {}, useMemo: factory => factory(),
        useState(initial) {
          const index = cursor++;
          if (index >= values.length) values[index] = initial;
          return [values[index], value => { values[index] = value; }];
        }
      };
      return require(name);
    }
  });
  return renderToStaticMarkup(exports.HddSetupWizard({
    root: 'C:\\game', moduleStatus: { state: moduleState },
    snapshot: { state, identity: { kind }, capabilities: { inputMonitor: false }, readbackComplete: false, canWrite: false },
    // This is deliberately false: Home's input readiness must not gate IO installation.
    controllerOnline: false,
    onClose() {}, async onChanged() {}
  }));
}

for (const [kind, name] of [['Leonardo', 'NYAGEKI'], ['Pico', 'LUXIS']]) {
  for (const state of ['ConnectedWaitingForData', 'SyncingDevice', 'SyncingHall', 'SyncFailed', 'Ready', 'CalibratingHall', 'CalibratingLever']) {
    test(`${name} can configure game IO during ${state}, without monitor/readback readiness`, () => {
      const html = wizard({ kind, state });
      assert.match(html, new RegExp(`已检测到 ${name}`));
      assert.match(html, /自动配置控制器/);
      assert.doesNotMatch(html, /未检测到支持的控制器/);
      assert.equal(html.includes('LUXIS 的 Aime IO'), kind === 'Pico');
    });
  }
}

for (const state of ['Disabled', 'Searching', 'BootloaderPending', 'Faulted']) {
  test(`a stale known identity during ${state} does not offer automatic configuration`, () => {
    const html = wizard({ state });
    assert.doesNotMatch(html, /自动配置控制器/);
    assert.match(html, /选择 MU3IO DLL/);
    assert.match(html, /使用键盘鼠标/);
    assert.match(html, /使用 IO4 \/ 暂不配置/);
  });
}

for (const moduleState of ['starting', 'restarting', 'fault', 'stopped']) {
  test(`a ${moduleState} module does not reuse the previous connected snapshot`, () => {
    assert.doesNotMatch(wizard({ state: 'Ready', moduleState }), /自动配置控制器/);
  });
}

test('an unknown device keeps manual controller options', () => {
  assert.match(wizard({ kind: 'Unknown', state: 'Ready' }), /未检测到支持的控制器/);
});

test('a recognized device with an unsupported protocol explains the limitation', () => {
  const html = wizard({ state: 'Unsupported' });
  assert.match(html, /已检测到 LUXIS，当前固件协议不受支持/);
  assert.doesNotMatch(html, /自动配置控制器/);
  assert.match(html, /选择 MU3IO DLL/);
});
