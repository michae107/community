import {
  useDeferredValue,
  useEffect,
  useMemo,
  useRef,
  useState,
} from "react";
import { readDir, type UnwatchFn } from "@tauri-apps/plugin-fs";
import { openPath } from "@tauri-apps/plugin-opener";
import { getCurrentWindow } from "@tauri-apps/api/window";
import {
  FsIndex,
  getHome,
  loadCachedIndex,
  saveCachedIndex,
  scanFileSystem,
  watchRoot,
} from "./lib/fsIndex";
import { fuzzySearch } from "./lib/fuzzy";
import { startCommandServer } from "./lib/commandServer";
import { mark, timed, timedSync } from "./lib/perf";
import { VirtualList } from "./VirtualList";

interface Item {
  path: string;
  name: string;
  isDir: boolean;
}

type ScanPhase = "starting" | "cached" | "scanning" | "ready";

const ROW_HEIGHT = 30;
const MIN_QUERY = 2;

function basename(path: string): string {
  return path.slice(path.lastIndexOf("/") + 1);
}

function dirname(path: string): string {
  const i = path.lastIndexOf("/");
  return i <= 0 ? "/" : path.slice(0, i);
}

export default function App() {
  const indexRef = useRef(new FsIndex());
  const backRef = useRef<string[]>([]);
  const fwdRef = useRef<string[]>([]);

  const [home, setHome] = useState("");
  const [cwd, setCwd] = useState("");
  const [dirItems, setDirItems] = useState<Item[]>([]);
  const [query, setQuery] = useState("");
  const [indexVersion, setIndexVersion] = useState(0);
  const [scanPhase, setScanPhase] = useState<ScanPhase>("starting");
  const [scanCount, setScanCount] = useState(0);
  const [watching, setWatching] = useState(false);

  const cwdRef = useRef(cwd);
  cwdRef.current = cwd;

  const pretty = (path: string) =>
    home && path.startsWith(home) ? "~" + path.slice(home.length) : path;

  const readDirSorted = async (path: string): Promise<Item[]> => {
    const list = await timed("ui.readDir", () => readDir(path));
    return list
      .map((e) => ({
        path: path === "/" ? `/${e.name}` : `${path}/${e.name}`,
        name: e.name,
        isDir: e.isDirectory,
      }))
      .sort(
        (a, b) =>
          Number(b.isDir) - Number(a.isDir) ||
          a.name.localeCompare(b.name, undefined, { sensitivity: "base" }),
      );
  };

  const loadDir = async (path: string) => {
    let items: Item[] = [];
    try {
      items = await readDirSorted(path);
    } catch {
      // Unreadable directory: still navigate, show it empty.
    }
    setCwd(path);
    setDirItems(items);
    setQuery("");
  };

  const refreshDir = async (path: string) => {
    if (path !== cwdRef.current) return;
    try {
      setDirItems(await readDirSorted(path));
    } catch {
      setDirItems([]);
    }
  };

  const navigate = (path: string) => {
    backRef.current.push(cwdRef.current);
    fwdRef.current = [];
    loadDir(path);
  };

  const goBack = () => {
    const prev = backRef.current.pop();
    if (prev === undefined) return;
    fwdRef.current.push(cwdRef.current);
    loadDir(prev);
  };

  const goForward = () => {
    const next = fwdRef.current.pop();
    if (next === undefined) return;
    backRef.current.push(cwdRef.current);
    loadDir(next);
  };

  const deferredQuery = useDeferredValue(query);
  const searchMode = deferredQuery.trim().length >= MIN_QUERY;

  const results = useMemo<Item[]>(() => {
    if (!searchMode) return [];
    return timedSync("search", () => fuzzySearch(indexRef.current.entries, deferredQuery)).map((e) => ({
      path: e.path,
      name: basename(e.path),
      isDir: e.isDir,
    }));
    // indexVersion invalidates when the filesystem changes underneath us
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [deferredQuery, searchMode, indexVersion]);

  const items = searchMode ? results : dirItems;
  const itemsRef = useRef(items);
  itemsRef.current = items;

  const openItem = (n: number) => {
    const item = itemsRef.current[n - 1];
    if (!item) return;
    mark(`openItem ${n} ${item.isDir ? "dir" : "file"} ${item.name}`);
    if (item.isDir) navigate(item.path);
    else timed("ui.openPath", () => openPath(item.path)).catch(() => {});
  };

  const commandRef = useRef<(cmd: string, args: string[]) => void>(() => {});
  commandRef.current = (cmd, args) => {
    switch (cmd) {
      case "open": {
        const n = parseInt(args[0] ?? "", 10);
        if (n >= 1) openItem(n);
        break;
      }
      case "back":
        goBack();
        break;
      case "forward":
        goForward();
        break;
    }
  };

  useEffect(() => {
    const unwatchers: UnwatchFn[] = [];
    let disposed = false;
    (async () => {
      const h = await getHome();
      setHome(h);
      await loadDir(h);

      unwatchers.push(
        await startCommandServer((cmd, args) => commandRef.current(cmd, args)),
      );

      const cached = await loadCachedIndex(h);
      if (cached && !disposed) {
        indexRef.current.replaceAll(cached);
        setIndexVersion(indexRef.current.version);
        setScanPhase("cached");
        setScanCount(cached.length);
      } else {
        setScanPhase("scanning");
      }

      mark("scan start");
      const fresh = await scanFileSystem(h, (count) => setScanCount(count));
      mark(`scan done ${fresh.length}`);
      if (disposed) return;
      indexRef.current.replaceAll(fresh);
      setIndexVersion(indexRef.current.version);
      setScanCount(fresh.length);
      setScanPhase("ready");
      await saveCachedIndex(h, fresh);

      // Coalesce watch callbacks: the watcher fires once per changed
      // path, so accumulate paths and reconcile at most every 300ms,
      // never overlapping two reconcile passes.
      const pending = new Set<string>();
      let flushScheduled = false;
      let applying = false;
      const flush = async () => {
        flushScheduled = false;
        if (applying) {
          scheduleFlush();
          return;
        }
        applying = true;
        const batch = [...pending];
        pending.clear();
        try {
          const changed = await indexRef.current.applyChangedPaths(batch);
          if (changed) {
            setIndexVersion(indexRef.current.version);
            setScanCount(indexRef.current.size);
          }
          if (batch.some((p) => p === cwdRef.current || dirname(p) === cwdRef.current)) {
            refreshDir(cwdRef.current);
          }
        } finally {
          applying = false;
        }
      };
      const scheduleFlush = () => {
        if (flushScheduled) return;
        flushScheduled = true;
        window.setTimeout(flush, 300);
      };

      try {
        unwatchers.push(
          await watchRoot(h, (paths) => {
            for (const p of paths) pending.add(p);
            scheduleFlush();
          }),
        );
        setWatching(true);
      } catch {
        setWatching(false);
      }
    })().catch(() => {});
    return () => {
      disposed = true;
      unwatchers.forEach((u) => u());
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    getCurrentWindow()
      .setTitle(cwd ? `voicefiles — ${pretty(cwd)}` : "voicefiles")
      .catch(() => {});
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [cwd, home]);

  const digits = String(Math.max(items.length, 1)).length;
  const crumb = pretty(cwd);
  const crumbDir = crumb.slice(0, crumb.lastIndexOf("/") + 1);
  const crumbBase = crumb.slice(crumb.lastIndexOf("/") + 1);

  const renderRow = (item: Item, i: number) => (
    <button
      type="button"
      onClick={() => openItem(i + 1)}
      className="group flex h-full w-full items-center gap-4 px-7 text-left text-[15px] hover:bg-panel"
    >
      <span
        className="shrink-0 text-right text-amber/60 tabular-nums group-hover:text-amber"
        style={{ width: `${digits}ch` }}
      >
        {i + 1}
      </span>
      <span className="min-w-0 flex-1 truncate">
        {searchMode && (
          <span className="text-faint">{pretty(dirname(item.path))}/</span>
        )}
        <span className={item.isDir ? "font-medium text-gold" : "text-ink"}>
          {item.name}
        </span>
        {item.isDir && <span className="text-amber/50">/</span>}
      </span>
    </button>
  );

  return (
    <div className="atmosphere flex h-full flex-col bg-bg font-mono text-ink">
      <header className="px-7 pt-6">
        <div className="flex items-baseline gap-3">
          <span className="text-lg text-amber select-none">»</span>
          <input
            autoFocus
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="search everywhere"
            spellCheck={false}
            className="w-full bg-transparent pb-2 text-lg text-ink caret-amber outline-none placeholder:text-faint"
          />
        </div>
        <div className="h-px bg-gradient-to-r from-amber/60 via-amber/20 to-transparent" />
        <div className="flex items-center justify-between pt-2.5 pb-1 text-[13px]">
          <div className="truncate">
            <span className="text-dim">{crumbDir}</span>
            <span className="text-ink">{crumbBase || crumb}</span>
          </div>
          <div className="shrink-0 pl-4 tracking-wider">
            <span className={backRef.current.length ? "text-amber" : "text-faint"}>‹</span>
            <span className="px-1 text-faint">·</span>
            <span className={fwdRef.current.length ? "text-amber" : "text-faint"}>›</span>
          </div>
        </div>
      </header>

      <main className="min-h-0 flex-1">
        {items.length === 0 ? (
          <div className="flex h-full items-center justify-center text-sm text-faint">
            {searchMode
              ? scanPhase === "starting" || scanPhase === "scanning"
                ? "still indexing…"
                : "no matches"
              : "empty directory"}
          </div>
        ) : (
          <div key={searchMode ? "search" : cwd} className="list-in h-full">
            <VirtualList
              items={items}
              rowHeight={ROW_HEIGHT}
              renderRow={renderRow}
              scrollKey={searchMode ? deferredQuery : cwd}
            />
          </div>
        )}
      </main>

      <footer className="flex items-center justify-between border-t border-line px-7 py-2 text-xs text-dim">
        <div className="flex items-center gap-2">
          {scanPhase === "ready" ? (
            <span className={watching ? "text-amber" : "text-faint"}>●</span>
          ) : (
            <span className="pulse-soft text-amber">◐</span>
          )}
          <span>
            {scanCount.toLocaleString()} indexed
            {scanPhase === "starting" && " — starting"}
            {scanPhase === "cached" && " (cache) — rescanning"}
            {scanPhase === "scanning" && " — scanning"}
            {scanPhase === "ready" && (watching ? " — live" : " — watch failed")}
          </span>
        </div>
        <span className="text-faint">
          {searchMode ? `${items.length} matches` : `${items.length} items`}
        </span>
      </footer>
    </div>
  );
}
