import {
  readDir,
  lstat,
  watch,
  type UnwatchFn,
} from "@tauri-apps/plugin-fs";
import { appDataDir, homeDir } from "@tauri-apps/api/path";
import { record, timed, timedSync } from "./perf";

export interface IndexEntry {
  path: string;
  lower: string; // lowercased path, precomputed for the fuzzy matcher
  isDir: boolean;
}

// Directory names never descended into. Artifact/dependency stores that
// dwarf the rest of the filesystem and are never what you're looking for.
const SKIP_NAMES = new Set([
  "node_modules",
  ".git",
  ".cache",
  ".npm",
  ".cargo",
  ".rustup",
  ".venv",
  "__pycache__",
  "target",
]);

let skipPaths: Set<string> | null = null;

async function getSkipPaths(): Promise<Set<string>> {
  if (!skipPaths) {
    const home = await getHome();
    skipPaths = new Set([
      `${home}/.local/share/Trash`,
      // Our own app data (index cache) and the Talon comms dir: indexing
      // them would make our own writes trigger watch events.
      await appDataDir(),
      `${home}/.voicefiles`,
    ]);
  }
  return skipPaths;
}

let home: string | null = null;

export async function getHome(): Promise<string> {
  if (!home) home = (await homeDir()).replace(/\/+$/, "");
  return home;
}

function shouldDescend(name: string, path: string, skip: Set<string>): boolean {
  return !SKIP_NAMES.has(name) && !skip.has(path);
}

/**
 * Recursively scan `root`, returning every entry found. Directories are
 * read with bounded concurrency so thousands of small IPC calls overlap.
 * Unreadable directories are skipped silently; symlinked directories are
 * recorded but not descended into (cycle safety).
 */
export function scanFileSystem(
  root: string,
  onProgress?: (count: number) => void,
): Promise<IndexEntry[]> {
  const CONCURRENCY = 32;
  const entries: IndexEntry[] = [];
  const queue: string[] = [root];
  let active = 0;
  let lastReported = 0;

  return new Promise((resolve) => {
    getSkipPaths().then((skip) => {
      const pump = () => {
        if (active === 0 && queue.length === 0) {
          resolve(entries);
          return;
        }
        while (active < CONCURRENCY && queue.length > 0) {
          const dir = queue.pop()!;
          active++;
          const t0 = performance.now();
          readDir(dir)
            .then((list) => {
              record("scan.readDir", performance.now() - t0);
              for (const e of list) {
                const p = dir === "/" ? `/${e.name}` : `${dir}/${e.name}`;
                entries.push({ path: p, lower: p.toLowerCase(), isDir: e.isDirectory });
                if (e.isDirectory && !e.isSymlink && shouldDescend(e.name, p, skip)) {
                  queue.push(p);
                }
              }
              if (onProgress && entries.length - lastReported >= 5000) {
                lastReported = entries.length;
                onProgress(entries.length);
              }
            })
            .catch(() => {})
            .finally(() => {
              active--;
              pump();
            });
        }
      };
      pump();
    });
  });
}

/**
 * In-memory filesystem index: a path-keyed map with a lazily rebuilt
 * array view for the fuzzy matcher. `version` bumps whenever contents
 * change so React can subscribe cheaply.
 */
export class FsIndex {
  private map = new Map<string, IndexEntry>();
  private cached: IndexEntry[] | null = null;
  version = 0;

  get entries(): IndexEntry[] {
    if (!this.cached)
      this.cached = timedSync("index.rebuild", () => Array.from(this.map.values()));
    return this.cached;
  }

  get size(): number {
    return this.map.size;
  }

  replaceAll(list: IndexEntry[]) {
    this.map = new Map(list.map((e) => [e.path, e]));
    this.touch();
  }

  private upsert(path: string, isDir: boolean): boolean {
    const existing = this.map.get(path);
    if (existing && existing.isDir === isDir) return false;
    this.map.set(path, { path, lower: path.toLowerCase(), isDir });
    this.touch();
    return true;
  }

  private removeTree(path: string): boolean {
    return timedSync("index.removeTree", () => {
      let changed = this.map.delete(path);
      const prefix = path + "/";
      for (const key of this.map.keys()) {
        if (key.startsWith(prefix)) {
          this.map.delete(key);
          changed = true;
        }
      }
      if (changed) this.touch();
      return changed;
    });
  }

  /**
   * Reconcile a batch of changed paths from the watcher against the
   * index: stat each one, upsert what exists, drop what doesn't. A path
   * that turns out to be a brand-new directory gets its subtree scanned.
   */
  async applyChangedPaths(paths: string[]): Promise<boolean> {
    return timed("watch.apply", async () => {
      record("watch.batchSize", paths.length);
      const skipList = [...(await getSkipPaths())];
      let changed = false;
      for (const p of new Set(paths)) {
        const segments = p.split("/");
        if (segments.some((s) => SKIP_NAMES.has(s))) continue;
        if (skipList.some((s) => p === s || p.startsWith(s + "/"))) continue;
        try {
          const info = await timed("watch.lstat", () => lstat(p));
          const isNewDir = info.isDirectory && !this.map.has(p);
          if (this.upsert(p, info.isDirectory)) changed = true;
          if (isNewDir) {
            // A directory appearing wholesale (mkdir -p, mv, unzip) brings
            // children the watcher never reported individually.
            const sub = await timed("watch.subscan", () => scanFileSystem(p));
            for (const e of sub) if (this.upsert(e.path, e.isDir)) changed = true;
          }
        } catch {
          if (this.removeTree(p)) changed = true;
        }
      }
      return changed;
    });
  }

  private touch() {
    this.cached = null;
    this.version++;
  }
}

/*
 * Index cache, persisted in IndexedDB. Deliberately NOT a file on disk:
 * a full index is ~140MB of JSON, and shipping that through a single
 * plugin-fs IPC call reliably corrupted the webview heap (SIGABRT in
 * glibc malloc). IndexedDB lives inside the webview — no IPC, and the
 * entries are stored in chunks of structured-cloneable tuples.
 */

const DB_NAME = "voicefiles";
const STORE = "index-cache";
// Small chunks + a breather between puts: IDB put() structured-clones
// synchronously on the main thread, and saving is a background task —
// smoothness beats save speed.
const CHUNK_SIZE = 25_000;

type CacheChunk = [string, 0 | 1][];

function openDb(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const req = indexedDB.open(DB_NAME, 1);
    req.onupgradeneeded = () => req.result.createObjectStore(STORE);
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
}

function idbGet<T>(db: IDBDatabase, key: string): Promise<T | undefined> {
  return new Promise((resolve, reject) => {
    const req = db.transaction(STORE).objectStore(STORE).get(key);
    req.onsuccess = () => resolve(req.result as T | undefined);
    req.onerror = () => reject(req.error);
  });
}

export async function loadCachedIndex(root: string): Promise<IndexEntry[] | null> {
  try {
    const db = await openDb();
    const meta = await idbGet<{ root: string; chunks: number }>(db, "meta");
    if (!meta || meta.root !== root) return null;
    const entries: IndexEntry[] = [];
    for (let i = 0; i < meta.chunks; i++) {
      const chunk = await idbGet<CacheChunk>(db, `chunk${i}`);
      if (!chunk) return null;
      for (const [path, d] of chunk) {
        entries.push({ path, lower: path.toLowerCase(), isDir: d === 1 });
      }
    }
    return entries;
  } catch {
    return null;
  }
}

export async function saveCachedIndex(root: string, entries: IndexEntry[]): Promise<void> {
  try {
    const db = await openDb();
    const put = (value: unknown, key: string) =>
      new Promise<void>((resolve, reject) => {
        const tx = db.transaction(STORE, "readwrite");
        tx.objectStore(STORE).put(value, key);
        tx.oncomplete = () => resolve();
        tx.onerror = () => reject(tx.error);
      });
    // One transaction per chunk, yielding in between: a single big
    // transaction structured-clones ~140MB in one go and stalls the main
    // thread for seconds. Meta goes last — a torn save is just a miss.
    await put(null, "meta");
    const chunks = Math.ceil(entries.length / CHUNK_SIZE);
    for (let i = 0; i < chunks; i++) {
      const chunk: CacheChunk = entries
        .slice(i * CHUNK_SIZE, (i + 1) * CHUNK_SIZE)
        .map((e) => [e.path, e.isDir ? 1 : 0]);
      await put(chunk, `chunk${i}`);
      await new Promise((r) => setTimeout(r, 25));
    }
    await put({ root, chunks }, "meta");
  } catch {
    // Cache is a startup optimization; failing to write it is not fatal.
  }
}

/**
 * Watch `root` for changes, reporting batches of changed paths. Events
 * are debounced so bulk operations (npm install, rm -rf) arrive as a few
 * large batches instead of thousands of callbacks.
 *
 * Each top-level directory gets its own recursive watch rather than one
 * watch over `root`: the watcher backend follows symlinks, so a single
 * poisoned subtree (e.g. a wine prefix symlinking `/`) would otherwise
 * kill watching entirely. Unwatchable subtrees are skipped.
 */
export async function watchRoot(
  root: string,
  onChanged: (paths: string[]) => void,
): Promise<UnwatchFn> {
  const skip = await getSkipPaths();
  const cb = (event: { type: unknown; paths: string[] }) => {
    // Drop pure read notifications (open/close-read): our own scans and
    // any process reading files generate hundreds of thousands of these,
    // and they can never change the index.
    const t = event.type;
    if (typeof t === "object" && t !== null && "access" in t) return;
    if (event.paths.length > 0) onChanged(event.paths);
  };

  // Each watch() call costs one inotify instance (default limit: 128),
  // so watch as many subtrees as possible per call. On failure, split
  // the set in halves to isolate the poisoned subtree(s) — typically a
  // handful of instances total, and only unwatchable subtrees are lost.
  const watchGroup = async (paths: string[]): Promise<UnwatchFn[]> => {
    if (paths.length === 0) return [];
    try {
      return [await watch(paths, cb, { recursive: true, delayMs: 500 })];
    } catch {
      // Unwatchable subtree (permissions, symlinks out of home, e.g. a
      // wine prefix's dosdevices/z: → /). Split to isolate it; a lone
      // unwatchable path is dropped.
      if (paths.length === 1) return [];
      const mid = Math.ceil(paths.length / 2);
      return [
        ...(await watchGroup(paths.slice(0, mid))),
        ...(await watchGroup(paths.slice(mid))),
      ];
    }
  };

  const unwatchers: UnwatchFn[] = [];
  // Non-recursive watch on root itself: entries created/removed directly in it.
  unwatchers.push(await watch(root, cb, { delayMs: 500 }));
  const tops = (await readDir(root))
    .filter(
      (e) =>
        e.isDirectory &&
        !e.isSymlink &&
        shouldDescend(e.name, root === "/" ? `/${e.name}` : `${root}/${e.name}`, skip),
    )
    .map((e) => (root === "/" ? `/${e.name}` : `${root}/${e.name}`));
  unwatchers.push(...(await watchGroup(tops)));
  return () => unwatchers.forEach((u) => u());
}
