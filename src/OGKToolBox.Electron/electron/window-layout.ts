type WorkArea = { x: number; y: number; width: number; height: number };

/** Electron work areas are in logical pixels, including Windows display scaling. */
export function windowLayout(area: WorkArea) {
  const portrait = area.height > area.width;
  const width = Math.min(1440, area.width);
  const height = Math.min(portrait ? 1920 : 900, area.height);
  return {
    x: area.x + Math.floor((area.width - width) / 2),
    y: area.y + Math.floor((area.height - height) / 2),
    width, height,
    minWidth: Math.min(portrait ? 640 : 1080, area.width),
    minHeight: Math.min(720, area.height),
  };
}
