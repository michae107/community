import { writeTextFile } from "@tauri-apps/plugin-fs";

/**
 * Temporary profiling harness. Accumulates named timings and dumps them
 * to /tmp/voicefiles-perf.json every 2s. /tmp is outside the watched
 * home tree, so dumping can't feed back into the fs watcher.
 */

interface Stat {
  n: number;
  total: number;
  max: number;
}

const stats = new Map<string, Stat>();
const marks: string[] = [];

export function record(name: string, ms: number): void {
  let s = stats.get(name);
  if (!s) stats.set(name, (s = { n: 0, total: 0, max: 0 }));
  s.n++;
  s.total += ms;
  if (ms > s.max) s.max = ms;
}

export function mark(msg: string): void {
  marks.push(`${(performance.now() / 1000).toFixed(1)}s ${msg}`);
  if (marks.length > 200) marks.shift();
}

export async function timed<T>(name: string, fn: () => Promise<T>): Promise<T> {
  const t0 = performance.now();
  try {
    return await fn();
  } finally {
    record(name, performance.now() - t0);
  }
}

export function timedSync<T>(name: string, fn: () => T): T {
  const t0 = performance.now();
  try {
    return fn();
  } finally {
    record(name, performance.now() - t0);
  }
}

// Main-thread stall detector: a 25ms heartbeat; any gap beyond 50ms
// means the main thread was blocked that long.
let last = performance.now();
setInterval(() => {
  const now = performance.now();
  const gap = now - last - 25;
  if (gap > 50) record("mainthread.stall", gap);
  last = now;
}, 25);

setInterval(() => {
  const out: Record<string, unknown> = { uptimeSec: Math.round(performance.now() / 1000) };
  const table: Record<string, string> = {};
  for (const [name, s] of [...stats.entries()].sort()) {
    table[name] = `n=${s.n} avg=${(s.total / s.n).toFixed(1)}ms max=${s.max.toFixed(0)}ms total=${Math.round(s.total)}ms`;
  }
  out.stats = table;
  out.marks = marks.slice(-40);
  writeTextFile("/tmp/voicefiles-perf.json", JSON.stringify(out, null, 2)).catch(() => {});
}, 2000);
