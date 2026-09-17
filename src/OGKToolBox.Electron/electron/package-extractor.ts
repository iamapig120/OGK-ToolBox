import fs from "node:fs/promises";
import path from "node:path";

export type PackageVersion = { major: number; minor: number; release: number };

async function hasDataConfig(directory: string): Promise<boolean> {
  const entries = await fs.readdir(directory, { withFileTypes: true });
  return entries.some(entry => entry.isFile() && entry.name.toLowerCase() === "dataconfig.xml");
}

/**
 * Option archives are published in two layouts: either their contents are at
 * the archive root, or everything is wrapped in an Axxx directory. Do not use
 * DataConfig.xml as the archive discriminator because content-only packages
 * (such as A020) intentionally omit it.
 */
export async function packageExtractRoot(staging: string, packageId: string): Promise<string> {
  const entries = await fs.readdir(staging, { withFileTypes: true });
  if (entries.length === 0) throw new Error("ZIP 内容为空，已停止安装。");
  if (entries.some(entry => entry.isFile() && entry.name.toLowerCase() === "dataconfig.xml")) return staging;

  const directories = entries.filter(entry => entry.isDirectory() && !entry.isSymbolicLink());
  const namedRoot = directories.find(entry => entry.name.toLowerCase() === packageId.toLowerCase());
  if (namedRoot) return path.join(staging, namedRoot.name);

  // Preserve the legacy wrapper layout when it is unambiguous. A single
  // content directory (for example `assets`) is not unwrapped, because it is
  // a real package directory that belongs below Axxx.
  if (directories.length === 1 && entries.length === 1) {
    const child = path.join(staging, directories[0].name);
    if (await hasDataConfig(child)) return child;
  }

  return staging;
}

export async function ensurePackageDataConfig(directory: string, version: PackageVersion): Promise<boolean> {
  if (await hasDataConfig(directory)) return false;
  const content = [
    "<DataConfig>",
    "  <version>",
    `    <major>${version.major}</major>`,
    `    <minor>${version.minor}</minor>`,
    `    <release>${version.release}</release>`,
    "  </version>",
    "</DataConfig>",
    ""
  ].join("\r\n");
  await fs.writeFile(path.join(directory, "DataConfig.xml"), content, "utf8");
  return true;
}
