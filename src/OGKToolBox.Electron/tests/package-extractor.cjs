const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const assert = require('node:assert/strict');
const ts = require('typescript');
const vm = require('node:vm');

const source = require('node:fs').readFileSync(path.join(__dirname, '../electron/package-extractor.ts'), 'utf8');
const code = ts.transpileModule(source, {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, esModuleInterop: true }
}).outputText;
const context = { exports: {}, require, console, process, Buffer, setTimeout, clearTimeout };
vm.runInNewContext(code, context, { filename: 'package-extractor.ts' });
const { ensurePackageDataConfig, packageExtractRoot } = context.exports;

(async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'ogk-package-extractor-'));
  try {
    const contentOnly = path.join(root, 'content-only');
    await fs.mkdir(path.join(contentOnly, 'assets'), { recursive: true });
    await fs.writeFile(path.join(contentOnly, 'assets', 'bundle'), 'data');
    await fs.mkdir(path.join(contentOnly, 'chapter'), { recursive: true });
    await fs.writeFile(path.join(contentOnly, 'chapter', 'Chapter.xml'), '<chapter/>');
    assert.equal(await packageExtractRoot(contentOnly, 'A020'), contentOnly);
    assert.equal(await ensurePackageDataConfig(contentOnly, { major: 1, minor: 50, release: 3 }), true);
    assert.match(await fs.readFile(path.join(contentOnly, 'DataConfig.xml'), 'utf8'), /<release>3<\/release>/);
    assert.equal(await ensurePackageDataConfig(contentOnly, { major: 1, minor: 50, release: 3 }), false);

    const wrapped = path.join(root, 'wrapped');
    const packageRoot = path.join(wrapped, 'A020');
    await fs.mkdir(packageRoot, { recursive: true });
    await fs.writeFile(path.join(packageRoot, 'DataConfig.xml'), '<DataConfig/>');
    assert.equal(await packageExtractRoot(wrapped, 'A020'), packageRoot);
    console.log('PASS: content-only and wrapped Option archives are recognized; DataConfig is synthesized once');
  } finally {
    await fs.rm(root, { recursive: true, force: true });
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
