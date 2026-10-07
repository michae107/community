import {
  watchImmediate,
  readTextFile,
  mkdir,
  type UnwatchFn,
} from "@tauri-apps/plugin-fs";
import { getHome } from "./fsIndex";
import { mark, record } from "./perf";

/**
 * File-based RPC, same pattern the VSCode command-server uses with Talon.
 * The Talon side writes {id, command, args} to ~/.voicefiles/request.json
 * (atomically, via tmp + rename); we watch the directory and dispatch.
 * The id dedupes: a request already present at startup is never replayed.
 */

export interface Request {
  id: string;
  command: string;
  args: string[];
}

export async function startCommandServer(
  onCommand: (command: string, args: string[]) => void,
): Promise<UnwatchFn> {
  const commsDir = `${await getHome()}/.voicefiles`;
  const requestFile = `${commsDir}/request.json`;
  await mkdir(commsDir, { recursive: true });

  let lastId = "";
  try {
    lastId = (JSON.parse(await readTextFile(requestFile)) as Request).id ?? "";
  } catch {
    // No pending request file — nothing to ignore.
  }

  const tryDispatch = async (): Promise<boolean> => {
    const t0 = performance.now();
    let req: Request;
    try {
      req = JSON.parse(await readTextFile(requestFile));
    } catch {
      return false; // partial write or deleted file
    }
    if (!req.id || req.id === lastId || typeof req.command !== "string") return false;
    lastId = req.id;
    record("cmd.readRequest", performance.now() - t0);
    mark(`cmd ${req.command} ${req.args?.join(",") ?? ""}`);
    onCommand(req.command, Array.isArray(req.args) ? req.args : []);
    return true;
  };

  let recheck: ReturnType<typeof setTimeout> | undefined;
  return watchImmediate(commsDir, async (event) => {
    if (!event.paths.some((p) => p.includes("request.json"))) return;
    if (await tryDispatch()) return;
    // The event may have fired for the tmp file before the atomic rename
    // landed, or we read mid-write: check once more shortly after.
    clearTimeout(recheck);
    recheck = setTimeout(tryDispatch, 150);
  });
}
