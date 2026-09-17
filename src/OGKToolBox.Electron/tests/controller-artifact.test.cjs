const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createHash } = require('node:crypto');
const { verifyController } = require('../scripts/verify-controller.cjs');

function fixture(t) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'ogk-controller-artifact-'));
  t.after(() => {
    assert.equal(path.dirname(path.resolve(directory)), path.resolve(os.tmpdir()));
    assert.ok(path.basename(directory).startsWith('ogk-controller-artifact-'));
    fs.rmSync(directory, { recursive: true, force: true });
  });
  fs.writeFileSync(path.join(directory, 'OGKToolBox.ControllerHost.exe'), 'synthetic test binary');
  fs.writeFileSync(path.join(directory, 'module.json'), JSON.stringify({
    moduleId: 'ogk-controller', moduleVersion: '1.1.7', moduleApiVersion: 1,
    ogkToolBoxVersion: '1.1.7', platform: 'win-x64', protocolVersion: 1,
    mu3CoreCommit: 'a'.repeat(40), entryPoint: 'OGKToolBox.ControllerHost.exe'
  }));
  const lock = { schemaVersion: 1, moduleVersion: '1.1.7', platform: 'win-x64', files: {} };
  const saveLock = () => {
    for (const name of ['OGKToolBox.ControllerHost.exe', 'module.json'])
      lock.files[name] = createHash('sha256').update(fs.readFileSync(path.join(directory, name))).digest('hex');
    fs.writeFileSync(path.join(directory, 'artifact.json'), JSON.stringify(lock));
  };
  saveLock();
  return { directory, saveLock };
}

test('accepts a matching prebuilt module', t => {
  const { directory } = fixture(t);
  assert.equal(verifyController(directory, '1.1.7').moduleVersion, '1.1.7');
});
test('rejects a modified executable', t => {
  const { directory } = fixture(t);
  fs.appendFileSync(path.join(directory, 'OGKToolBox.ControllerHost.exe'), 'modified');
  assert.throws(() => verifyController(directory, '1.1.7'), /checksum mismatch/);
});
test('rejects an incompatible app version', t => {
  const { directory } = fixture(t);
  assert.throws(() => verifyController(directory, '9.0.0'), /version\/platform/);
});
test('rejects a missing module', t => {
  const { directory } = fixture(t);
  fs.unlinkSync(path.join(directory, 'OGKToolBox.ControllerHost.exe'));
  assert.throws(() => verifyController(directory, '1.1.7'), /ENOENT/);
});
test('rejects an invalid manifest even with matching checksums', t => {
  const { directory, saveLock } = fixture(t);
  const file = path.join(directory, 'module.json');
  const manifest = JSON.parse(fs.readFileSync(file));
  manifest.entryPoint = '../unexpected.exe';
  fs.writeFileSync(file, JSON.stringify(manifest));
  saveLock();
  assert.throws(() => verifyController(directory, '1.1.7'), /manifest/);
});
