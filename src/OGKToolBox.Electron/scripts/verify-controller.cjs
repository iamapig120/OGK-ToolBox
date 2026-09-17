const fs = require('node:fs');
const path = require('node:path');
const { createHash } = require('node:crypto');

const requiredFiles = ['OGKToolBox.ControllerHost.exe', 'module.json'];

function verifyController(directory, appVersion) {
  const lock = JSON.parse(fs.readFileSync(path.join(directory, 'artifact.json'), 'utf8'));
  if (lock.schemaVersion !== 1 || lock.platform !== 'win-x64' || lock.moduleVersion !== appVersion)
    throw new Error('Controller artifact version/platform does not match the app. Obtain a compatible prebuilt module.');
  for (const name of requiredFiles) {
    const expected = lock.files?.[name];
    if (typeof expected !== 'string' || !/^[a-f0-9]{64}$/.test(expected))
      throw new Error(`Missing or invalid controller checksum: ${name}`);
    const bytes = fs.readFileSync(path.join(directory, name));
    if (createHash('sha256').update(bytes).digest('hex') !== expected)
      throw new Error(`Controller artifact checksum mismatch: ${name}`);
  }
  const manifest = JSON.parse(fs.readFileSync(path.join(directory, 'module.json'), 'utf8'));
  if (manifest.moduleId !== 'ogk-controller' || manifest.moduleApiVersion !== 1
    || manifest.platform !== 'win-x64' || manifest.entryPoint !== requiredFiles[0]
    || manifest.moduleVersion !== appVersion || manifest.ogkToolBoxVersion !== appVersion
    || manifest.protocolVersion !== 1 || !/^[a-f0-9]{40}$/i.test(manifest.mu3CoreCommit))
    throw new Error('Controller manifest does not match the supported module contract.');
  return manifest;
}

module.exports = { verifyController };
if (require.main === module) {
  try {
    const root = path.resolve(__dirname, '..');
    const { version } = JSON.parse(fs.readFileSync(path.join(root, 'package.json'), 'utf8'));
    verifyController(path.join(root, 'resources/controller'), version);
    console.log(`Verified prebuilt controller ${version} (win-x64); no private source is required.`);
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
