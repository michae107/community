import type { IndexEntry } from "./fsIndex";

/**
 * Tiered fuzzy matcher over full paths. Query is split on whitespace;
 * every token must match. Per token, cheapest test first:
 *
 *   1. contiguous substring of the basename  (best — you named the file)
 *   2. contiguous substring of the full path
 *   3. in-order subsequence of the full path (classic fuzzy)
 *
 * Scores favor basename hits, prefix hits, and short paths, so
 * "screenparse" ranks ~/projects/screenparse above deep incidental hits.
 */

const NO_MATCH = -1;

function scoreToken(entry: IndexEntry, token: string): number {
  const path = entry.lower;
  const baseStart = path.lastIndexOf("/") + 1;
  const base = path.slice(baseStart);

  const inBase = base.indexOf(token);
  if (inBase !== -1) {
    let score = 100 - base.length * 0.5;
    if (inBase === 0) score += 25;
    if (base === token) score += 40;
    return score;
  }

  if (path.indexOf(token) !== -1) {
    return 50 - path.length * 0.05;
  }

  // Short tokens only match contiguously: a 2-3 char subsequence matches
  // most of a million-entry index, which is all noise and real jank.
  if (token.length < 4) return NO_MATCH;

  // Subsequence scan with a small bonus for consecutive runs and for
  // matches that start a path segment or word.
  let score = 20 - path.length * 0.05;
  let ti = 0;
  let prevHit = -2;
  for (let pi = 0; pi < path.length && ti < token.length; pi++) {
    if (path[pi] === token[ti]) {
      if (pi === prevHit + 1) score += 2;
      const before = path[pi - 1];
      if (before === "/" || before === "." || before === "_" || before === "-" || before === " ") {
        score += 3;
      }
      prevHit = pi;
      ti++;
    }
  }
  return ti === token.length ? score : NO_MATCH;
}

export function fuzzySearch(
  entries: IndexEntry[],
  query: string,
  limit = 500,
): IndexEntry[] {
  const tokens = query.toLowerCase().split(/\s+/).filter(Boolean);
  if (tokens.length === 0) return [];

  const scored: { entry: IndexEntry; score: number }[] = [];
  outer: for (const entry of entries) {
    let total = 0;
    for (const token of tokens) {
      const s = scoreToken(entry, token);
      if (s === NO_MATCH) continue outer;
      total += s;
    }
    scored.push({ entry, score: total });
  }

  scored.sort((a, b) => b.score - a.score || a.entry.path.length - b.entry.path.length);
  return scored.slice(0, limit).map((s) => s.entry);
}
