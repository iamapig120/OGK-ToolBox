const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const ts = require('typescript');
const React = require('react');
const { renderToStaticMarkup } = require('react-dom/server');

function load(bridge = {}, hooks = {}) {
  const source = fs.readFileSync(path.join(__dirname, '../src/controller-page.tsx'), 'utf8')
    + '\nexport { InputMonitor };\n';
  const code = ts.transpileModule(source, { compilerOptions: {
    target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX
  } }).outputText;
  const snapshotListeners = [], statusListeners = [], effects = [], stateChanges = [];
  const ogk = {
    controllerSnapshot: () => new Promise(() => {}), controllerStatus: () => new Promise(() => {}),
    onControllerSnapshot(listener) { snapshotListeners.push(listener); return () => {}; },
    onControllerStatus(listener) { statusListeners.push(listener); return () => {}; },
    ...bridge
  };
  const exports = {};
  vm.runInNewContext(code, {
    exports, window: { ogk }, console,
    require(name) {
      if (name.endsWith('.css')) return {};
      if (name === './pencil-icons') return new Proxy({}, { get: () => () => null });
      if (name === 'react') return { ...React,
        useState(initial) { return [typeof initial === 'function' ? initial() : initial, value => stateChanges.push(value)]; },
        useRef: initial => ({ current: initial }), useCallback: callback => callback,
        useEffect: callback => effects.push(callback), useMemo: callback => callback(), ...hooks
      };
      return require(name);
    }
  });
  return { exports, snapshotListeners, statusListeners, effects, stateChanges };
}

const snapshot = (connectionId = 'first') => ({
  source: { backendId: 'example', connectionId },
  identity: { kind: 'CustomController', displayName: 'Custom Controller' },
  state: 'Ready', sequence: 1, canWrite: true, readbackComplete: true,
  capabilities: { mode: true, virtualKeys: false },
  inputModes: { current: 'usb', options: [{ id: 'usb', label: 'USB 输入' }, { id: 'keyboard', label: '键盘输入' }, { id: 'custom-mode', label: '自定义模式' }] },
  input: { leftA: true, mappedLever: 512 }, deviceConfig: { isKmMode: false },
  card: { present: false }, hall: {}, lever: {}
});

function elements(node, predicate) {
  if (Array.isArray(node)) return node.flatMap(child => elements(child, predicate));
  if (!node || typeof node !== 'object') return [];
  return [...(predicate(node) ? [node] : []), ...elements(node.props?.children, predicate)];
}
const buttons = tree => elements(tree, node => node.type === 'button');
const render = element => renderToStaticMarkup(element);
const result = value => ({ commandId: 'test', status: 'Verified', message: '', snapshot: value });

test('mode UI follows declared string modes for third-party and original devices', async () => {
  const sent = [];
  const { exports } = load({ controllerSetInputMode: async id => { sent.push(id); return result(snapshot()); } });
  const command = task => task();
  const generic = exports.ControllerModeControl({ snapshot: snapshot(), command });
  assert.equal(buttons(generic).length, 3);
  assert.match(render(generic), /USB 输入/);
  await buttons(generic)[2].props.onClick();
  await Promise.resolve();
  assert.deepEqual(sent, ['custom-mode']);
  const original = { ...snapshot(), identity: { kind: 'Pico', displayName: 'LUXIS' },
    inputModes: { current: 'km', options: [{ id: 'mu3', label: 'MU3IO' }, { id: 'km', label: '模拟键鼠' }] } };
  const tree = exports.ControllerModeControl({ snapshot: original, command });
  assert.equal(buttons(tree).length, 2);
  assert.equal(buttons(tree)[1].props['aria-pressed'], true);
});

test('missing mode capability or write permission produces a read-only label', () => {
  const { exports } = load();
  for (const value of [{ ...snapshot(), canWrite: false },
    { ...snapshot(), capabilities: { ...snapshot().capabilities, mode: false } }]) {
    const tree = exports.ControllerModeControl({ snapshot: value, command: () => { throw new Error('must not send'); } });
    assert.equal(buttons(tree).length, 0);
    assert.match(render(tree), /USB 输入/);
  }
});

test('failed, rejected and missing mode results show an error without changing the selected mode', async () => {
  for (const status of ['Failed', 'Rejected', undefined]) {
    const states = [];
    let cursor = 0;
    const { exports } = load({}, { useState(initial) {
      const index = cursor++;
      if (!(index in states)) states[index] = initial;
      return [states[index], value => { states[index] = value; }];
    } });
    const value = snapshot();
    const command = async () => status ? { ...result(value), status, message: '设备未确认输入模式' } : undefined;
    const draw = () => { cursor = 0; return exports.ControllerModeControl({ snapshot: value, command }); };
    buttons(draw())[1].props.onClick();
    assert.equal(buttons(draw())[1].props.disabled, true);
    await new Promise(setImmediate);
    const tree = draw();
    assert.equal(buttons(tree)[0].props['aria-pressed'], true);
    assert.equal(buttons(tree)[1].props['aria-pressed'], false);
    assert.equal(buttons(tree)[1].props.disabled, false);
    const alerts = elements(tree, element => element.props?.role === 'alert');
    assert.equal(alerts.length, 1);
    assert.match(render(alerts[0]), status ? /设备未确认输入模式/ : /输入模式切换未完成/);
  }
});

test('device selector preserves explicit choice and sends the selected backend id', async () => {
  const sent = [], options = [];
  const { exports } = load({ controllerSelectBackend: async id => { sent.push(id); return result(snapshot()); } });
  const status = { state: 'ready', backends: [
    { id: 'original', label: 'LUXIS', connected: true, selected: true, state: 'ready' },
    { id: 'third-party', label: '第三方控制器', connected: false, selected: false, state: 'fault' }
  ] };
  const tree = exports.ControllerDeviceSelector({ status, command: (task, option) => { options.push(option); return task(); } });
  assert.equal(buttons(tree)[0].props['aria-pressed'], true);
  assert.match(render(tree), /第三方控制器/);
  assert.match(render(tree), /不可用/);
  await buttons(tree)[1].props.onClick();
  await Promise.resolve();
  assert.deepEqual(sent, ['third-party']);
  assert.equal(options[0].allowConnectionChange, true);
});

test('physical keys remain visible without virtual-input handlers', () => {
  const { exports } = load();
  const noop = () => {};
  const tree = exports.InputMonitor({ snapshot: snapshot(),
    virtualButtons: [['L_A', 'A', true, 'left-a', 'a']], keyboardHeldKeys: new Set(), keyboardBindings: {},
    onPress: noop, onRelease: noop, onPressKeyboard: noop, onReleaseKeyboard: noop, onReleaseAll: noop,
    inspectorTab: 'info', onInspectorTab: noop, command: noop });
  const key = buttons(tree).find(button => button.props.className.includes('controller-key'));
  assert.equal(key.props.disabled, true);
  assert.equal(key.props['aria-pressed'], true);
  assert.equal(key.props.onPointerDown, undefined);
  assert.equal(key.props.onKeyDown, undefined);
});

test('late command responses cannot replace a newer connection of the same hardware kind', async () => {
  const harness = load();
  const controller = harness.exports.useController();
  harness.effects.forEach(effect => effect());
  harness.snapshotListeners[0](snapshot('old-connection'));
  let finish;
  const pending = controller.invoke(() => new Promise(resolve => { finish = resolve; }));
  harness.snapshotListeners[0](snapshot('new-connection'));
  finish(result(snapshot('old-connection')));
  assert.equal(await pending, undefined);
  assert.equal(harness.exports.useController().snapshot.source.connectionId, 'new-connection');
});

test('a late failure from a previous connection cannot fault the selected controller', async () => {
  const harness = load();
  const controller = harness.exports.useController();
  harness.effects.forEach(effect => effect());
  harness.snapshotListeners[0](snapshot('old-connection'));
  let fail;
  const pending = controller.invoke(() => new Promise((_resolve, reject) => { fail = reject; }));
  harness.snapshotListeners[0](snapshot('new-connection'));
  harness.statusListeners[0]({ state: 'ready' });
  fail(new Error('old connection failed'));
  assert.equal(await pending, undefined);
  assert.equal(harness.exports.useController().moduleStatus.state, 'ready');
});

test('a command failure preserves backend choices and the selected backend', async () => {
  const harness = load();
  const controller = harness.exports.useController();
  harness.effects.forEach(effect => effect());
  harness.snapshotListeners[0](snapshot());
  const status = { state: 'ready', selectedBackendId: 'example', backends: [
    { id: 'original', label: 'LUXIS', connected: true, selected: false, state: 'ready' },
    { id: 'example', label: '第三方控制器', connected: true, selected: true, state: 'ready' }
  ] };
  harness.statusListeners[0](status);
  assert.equal(await controller.invoke(async () => { throw new Error('command transport failed'); }), undefined);
  const updated = harness.exports.useController().moduleStatus;
  assert.equal(updated.state, 'fault');
  assert.equal(updated.selectedBackendId, 'example');
  assert.equal(updated.backends, status.backends);
  assert.equal(buttons(harness.exports.ControllerDeviceSelector({ status: updated, command: controller.invoke })).length, 2);
});

test('a detected third-party controller keeps manual game IO choices without installing the original DLL', () => {
  const source = fs.readFileSync(path.join(__dirname, '../src/hdd-setup-wizard.tsx'), 'utf8');
  const code = ts.transpileModule(source, { compilerOptions: {
    target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX
  } }).outputText;
  const exports = {}, states = ['controller', {}, false];
  let cursor = 0;
  vm.runInNewContext(code, { exports, document: { body: {} }, require(name) {
    if (name.endsWith('.css')) return {};
    if (name === 'react-dom') return { createPortal: content => content };
    if (name === 'react') return { ...React, useEffect() {}, useMemo: fn => fn(),
      useState(initial) { return [cursor < states.length ? states[cursor++] : initial, () => {}]; } };
    return require(name);
  } });
  const html = render(React.createElement(exports.HddSetupWizard, { root: 'C:\\game',
    snapshot: snapshot(), moduleStatus: { state: 'ready' }, onClose() {}, async onChanged() {} }));
  assert.match(html, /已检测到 Custom Controller，请选择游戏输入方式/);
  assert.match(html, /选择 MU3IO DLL/);
  assert.doesNotMatch(html, /自动配置控制器|NYAGEKI_IO\.dll/);
});
