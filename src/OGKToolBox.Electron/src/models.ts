export type Summary = {
  gameRoot: string;
  musicCount: number;
  cardCount: number;
  characterCount: number;
  resourceCount: number;
  diagnosticCount: number;
  gameVersion: string;
  lastScanAt?: string;
};

export type Scan = {
  summary: Summary;
  music: any[];
  cards: any[];
  characters: any[];
  resources: any[];
  diagnostics: any[];
};

export type LibrarySection = Exclude<keyof Scan, "summary">;

export type Config = { hookVersion: string; files: any[]; mods: any[]; diagnostics: any[] };
export type ThumbnailCache = { gameRoot: string; kind: "card" | "music" | "resource" };
export type ResourcePage = { items: any[]; total: number; offset: number; limit: number };
