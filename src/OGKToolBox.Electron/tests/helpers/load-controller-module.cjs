const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { createRequire } = require('node:module');
const ts = require('typescript');

module.exports = function load(relative, overrides = {}, globals = {}) {
  const cache = new Map();
  function read(filename) {
    if (cache.has(filename)) return cache.get(filename).exports;
    const module = { exports: {} }; cache.set(filename, module);
    const localRequire = createRequire(filename);
    const source = ts.transpileModule(fs.readFileSync(filename, 'utf8'), { compilerOptions: {
      target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS, esModuleInterop: true
    } }).outputText;
    vm.runInNewContext(source, { module, exports: module.exports, require: name => {
      if (Object.hasOwn(overrides, name)) return overrides[name];
      const dependency = path.resolve(path.dirname(filename), name + '.ts');
      return name.startsWith('.') && fs.existsSync(dependency) ? read(dependency) : localRequire(name);
    }, __dirname: path.dirname(filename), process, console, Buffer, URL, AbortController, TextDecoder,
    setTimeout, clearTimeout, setInterval, clearInterval, fetch, ...globals }, { filename });
    return module.exports;
  }
  return read(path.resolve(__dirname, '../..', relative));
};
