// Used only by the packaged-process smoke test; never opens attached hardware.
const Module = require('node:module');
const original = Module._load;
Module._load = function (name, ...args) {
  if (name === 'node-hid') return { devicesAsync: async () => [], HIDAsync: {
    open: async () => { throw new Error('Hardware access is disabled in this test'); }
  } };
  return original.call(this, name, ...args);
};
