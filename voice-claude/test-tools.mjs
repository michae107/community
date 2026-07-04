#!/usr/bin/env node
// Testbed: verify the editor MCP tools actually load at agent startup.
//
// Reuses the SAME paths, MCP server, prompt, and launch command as the plugin,
// but runs the agent in a detached (background) yet INTERACTIVE tmux session and
// loops until the agent successfully calls one of our bridge tools (`done`).
//
//   node test-tools.mjs            # test the backend in config.json
//   node test-tools.mjs codex      # force codex
//   node test-tools.mjs claude     # force claude
//   node test-tools.mjs both       # test both, one after the other
//
// A "pass" = the agent called the `done` (or `apply_edit`) tool, which can only
// happen if the editor MCP tools were really loaded. On each failed attempt the
// tmux pane is dumped so you can see what the agent said about its tools.

import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import http from "node:http";
import { fileURLToPath } from "node:url";
import { execFileSync, spawnSync } from "node:child_process";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const TMP = os.tmpdir();
const MCP_SERVER = path.join(__dirname, "mcp-server.mjs");
const PROMPT_FILE = path.join(__dirname, "prompt.md");
const CONFIG_FILE = path.join(__dirname, "config.json");

const ALLOWED_TOOLS =
  "mcp__editor__get_editor_context mcp__editor__apply_edit mcp__editor__done Read Grep Glob Edit Write MultiEdit Bash";

const ATTEMPTS = parseInt(process.env.ATTEMPTS || "20", 10);
const READY_MS = parseInt(process.env.READY_MS || "30000", 10); // max wait for composer
const GUIDE_GAP_MS = parseInt(process.env.GUIDE_GAP_MS || "1500", 10); // guide -> prompt
const WATCH_MS = parseInt(process.env.WATCH_MS || "45000", 10); // how long to wait for a hit

const DEFAULT_CONFIG = {
  backend: "claude",
  claude: { model: "haiku", effort: "low" },
  codex: { model: "gpt-5.3-codex-spark", reasoning: "low", fastMode: false },
};

function readConfig() {
  try {
    const c = JSON.parse(fs.readFileSync(CONFIG_FILE, "utf8"));
    return {
      backend: c.backend || DEFAULT_CONFIG.backend,
      claude: { ...DEFAULT_CONFIG.claude, ...(c.claude || {}) },
      codex: { ...DEFAULT_CONFIG.codex, ...(c.codex || {}) },
    };
  } catch {
    return DEFAULT_CONFIG;
  }
}

function readGuideOneLine() {
  try {
    return fs.readFileSync(PROMPT_FILE, "utf8").replace(/\s+/g, " ").trim();
  } catch {
    return "";
  }
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function tmux(args) {
  execFileSync("tmux", args, { stdio: "ignore" });
}
function tmuxOk(args) {
  try {
    execFileSync("tmux", args, { stdio: "ignore" });
    return true;
  } catch {
    return false;
  }
}
function capturePane(session) {
  try {
    return execFileSync(
      "tmux",
      ["capture-pane", "-p", "-t", session, "-S", "-200"],
      { encoding: "utf8" }
    );
  } catch {
    return "(could not capture pane)";
  }
}

// Bridge: identical routes to the plugin. Records hits so we can detect tool use.
function startBridge() {
  const hits = [];
  const server = http.createServer((req, res) => {
    const reply = (obj) => {
      res.writeHead(200, { "content-type": "application/json" });
      res.end(JSON.stringify(obj));
    };
    if (req.method !== "POST") {
      res.writeHead(404);
      res.end();
      return;
    }
    if (req.url === "/done") {
      hits.push("done");
      reply({ ok: true, message: "done" });
      return;
    }
    if (req.url === "/edit") {
      let body = "";
      req.on("data", (c) => (body += c));
      req.on("end", () => {
        hits.push("edit");
        reply({ ok: true, message: "Applied test edit." });
      });
      return;
    }
    res.writeHead(404);
    res.end();
  });
  return new Promise((resolve) => {
    server.listen(0, "127.0.0.1", () =>
      resolve({ server, port: server.address().port, hits })
    );
  });
}

function buildCommand(backend, conf, port, ctxFile, mcpFilePath, errLog) {
  if (backend === "codex") {
    const codex = process.env.CODEX_PATH || "codex";
    const { model, reasoning, fastMode } = conf.codex;
    return (
      `${codex} -m ${model} ` +
      `-c model_reasoning_effort="${reasoning}" ` +
      `-c mcp_servers.editor.command="node" ` +
      `-c mcp_servers.editor.args='["${MCP_SERVER}"]' ` +
      `-c mcp_servers.editor.env.VOICE_CLAUDE_PORT='"${port}"' ` +
      `-c mcp_servers.editor.env.VOICE_CLAUDE_CONTEXT='"${ctxFile}"' ` +
      `-a never -s ${process.env.CODEX_SANDBOX || "danger-full-access"} ` +
      `${fastMode ? "--enable" : "--disable"} fast_mode ` +
      `${process.env.CODEX_EXTRA || ""}` +
      ` 2> >(tee ${errLog} >&2)`
    );
  }
  const claude = process.env.CLAUDE_PATH || "claude";
  const { model, effort } = conf.claude;
  fs.writeFileSync(
    mcpFilePath,
    JSON.stringify({
      mcpServers: {
        editor: {
          type: "stdio",
          command: "node",
          args: [MCP_SERVER],
          env: { VOICE_CLAUDE_PORT: String(port), VOICE_CLAUDE_CONTEXT: ctxFile },
        },
      },
    })
  );
  return (
    `${claude} --model ${model} --effort ${effort} --mcp-config ${mcpFilePath} ` +
    `--append-system-prompt "$(cat ${PROMPT_FILE})" ` +
    `--permission-mode acceptEdits ` +
    `--allowedTools ${ALLOWED_TOOLS}` +
    ` 2> >(tee ${errLog} >&2)`
  );
}

// Wait until the agent's input composer is actually ready to receive keystrokes,
// dismissing codex's startup "Update available!" menu if it appears (it eats the
// first keystrokes otherwise — the real cause of the "tools never load" bug).
// Returns true once ready, false on timeout.
async function waitForReady(backend, session) {
  const deadline = Date.now() + READY_MS;
  let dismissed = false;
  while (Date.now() < deadline) {
    const pane = capturePane(session);
    // Codex update menu: highlighted "1. Update now" + "Press enter to continue".
    if (!dismissed && /Update now/.test(pane) && /Press enter to continue/.test(pane)) {
      tmuxOk(["send-keys", "-t", session, "Down", "Down", "Enter"]); // -> Skip until next version
      dismissed = true;
      await sleep(800);
      continue;
    }
    if (backend === "codex") {
      // Composer/status bar is up once we see the usage line or the placeholder.
      if (/% used|\/model to change|Write tests for/.test(pane)) return true;
    } else {
      // Claude: composer prompt + footer hints appear when ready.
      if (/❯|accept edits on|shift\+tab|for agents|\? for shortcuts/.test(pane)) return true;
    }
    await sleep(700);
  }
  return false;
}

// True while codex is mid-turn (working). Used to gate message 2 until message 1
// has fully completed, so the two never fuse into a single submission.
function codexWorking(pane) {
  return /Esc to interrupt|esc to interrupt|Working|Thinking|Tool call|Running|Explored|Reading/.test(pane);
}

// After submitting a message, wait for codex to start working then return to idle.
// Returns true if it settled, false on timeout.
async function waitForTurnIdle(session, { startMs = 9000, idleMs = 90000 } = {}) {
  // Phase 1: confirm the turn actually started (so an empty/idle pane right after
  // the keystroke isn't mistaken for "done").
  const startDeadline = Date.now() + startMs;
  let started = false;
  while (Date.now() < startDeadline) {
    if (codexWorking(capturePane(session))) {
      started = true;
      break;
    }
    await sleep(250);
  }
  if (!started) return true; // trivial/instant turn — nothing to wait for
  // Phase 2: wait for it to go idle (two consecutive idle reads to debounce).
  const idleDeadline = Date.now() + idleMs;
  let idleStreak = 0;
  while (Date.now() < idleDeadline) {
    if (codexWorking(capturePane(session))) idleStreak = 0;
    else if (++idleStreak >= 2) return true;
    await sleep(600);
  }
  return false;
}

// Honest probe: a REAL edit request, phrased exactly like the plugin sends it,
// with NO mention of which tool to use. Success = an actual apply_edit (/edit)
// bridge hit, which only happens if the editor MCP tools loaded AND were used.
const SEND_GUIDE = process.env.SEND_GUIDE !== "0"; // message-1 loader on by default
// Message 1: force codex to actually CALL an editor tool, which makes it discover
// and load the deferred MCP tools (a passive "load them" instruction is ignored).
const LOAD_MSG =
  "Before any other work: call the get_editor_context editor tool right now to " +
  "confirm your editor MCP tools (get_editor_context, apply_edit, done) are " +
  "loaded. If they are deferred, load them first, then call get_editor_context.";
function probePrompt(testFile) {
  // Message 2: the real edit, with an explicit instruction to route it through
  // apply_edit instead of shell/native edits.
  return (
    `[file: ${testFile}; cursor line 1, col 1] On line 1, change the word ` +
    `"hello" to "goodbye". Make this change using the apply_edit editor tool.`
  );
}

async function attempt(backend, conf, n) {
  const session = `vc-test-${backend}`;
  const ctxFile = path.join(TMP, `vc-test-${backend}.context.json`);
  const mcpFilePath = path.join(TMP, `vc-test-${backend}.mcp.json`);
  const launchFile = path.join(TMP, `vc-test-${backend}.launch.sh`);
  const errLog = path.join(TMP, `vc-test-${backend}.err`);

  // Fresh editor context pointing at a real temp file (so get_editor_context and
  // any accidental apply_edit have something sane to resolve).
  const testFile = path.join(TMP, `vc-test-${backend}.txt`);
  fs.writeFileSync(testFile, "hello world\n");
  fs.writeFileSync(
    ctxFile,
    JSON.stringify(
      { file: testFile, languageId: "plaintext", cursorLine: 1, cursorColumn: 1, selection: null },
      null,
      2
    )
  );

  const { server, port, hits } = await startBridge();

  const cmd = buildCommand(backend, conf, port, ctxFile, mcpFilePath, errLog);
  fs.writeFileSync(
    launchFile,
    "#!/usr/bin/env bash\n" +
      `${cmd}\n` +
      `echo "[vc-test] agent exited $?"\n` +
      "exec bash\n"
  );

  tmuxOk(["kill-session", "-t", session]);
  tmux(["new-session", "-d", "-s", session, "-x", "200", "-y", "50", "-c", TMP, `bash ${launchFile}`]);

  // Async submit: type the text, let codex's paste-burst detection settle, then
  // press Enter and CONFIRM the turn actually started — codex collapses long input
  // into a "[Pasted Content]" chip that a single Enter won't submit, so retry
  // Enter until it's working (or we run out of tries). Returns true if submitted.
  const submit = async (text) => {
    tmux(["send-keys", "-t", session, "-l", text]);
    await sleep(800); // let paste-burst detection finish so Enter submits, not buffers
    for (let i = 0; i < 5; i++) {
      tmuxOk(["send-keys", "-t", session, "Enter"]);
      await sleep(700);
      if (codexWorking(capturePane(session))) return true;
    }
    return false;
  };

  console.log(`\n=== [${backend}] attempt ${n}/${ATTEMPTS} (port ${port}) ===`);
  const ready = await waitForReady(backend, session);
  if (!ready) {
    console.log(`⚠️  [${backend}] composer never became ready in ${READY_MS / 1000}s.`);
    console.log("---- tmux pane ----\n" + capturePane(session).trimEnd() + "\n-------------------");
    server.close();
    tmuxOk(["kill-session", "-t", session]);
    return false;
  }
  if (SEND_GUIDE) {
    // Message 1: force-load the editor tools. Wait for this turn to FULLY
    // complete before message 2, otherwise the two fuse and tools never load.
    await submit(LOAD_MSG);
    const settled = await waitForTurnIdle(session);
    console.log(`   message-1 turn ${settled ? "completed" : "TIMED OUT"}; pane after load:`);
    console.log("   ┌─ post-load pane ─");
    console.log(capturePane(session).trimEnd().split("\n").map((l) => "   │ " + l).join("\n"));
    console.log("   └──────────────────");
  }
  // Message 2: the real edit request.
  await submit(probePrompt(testFile));

  // Poll for the meaningful hit: apply_edit (/edit) means tools loaded AND used.
  const deadline = Date.now() + WATCH_MS;
  while (Date.now() < deadline) {
    if (hits.includes("edit")) break;
    await sleep(1000);
  }

  const pane = capturePane(session);
  server.close();

  if (hits.includes("edit")) {
    console.log(`✅ [${backend}] PASS — apply_edit used (hits: ${hits.join(", ") || "none"})`);
    tmuxOk(["kill-session", "-t", session]);
    return true;
  }

  console.log(`❌ [${backend}] FAIL — no bridge hit in ${WATCH_MS / 1000}s.`);
  console.log("---- tmux pane ----");
  console.log(pane.trimEnd());
  console.log("-------------------");
  try {
    const e = fs.readFileSync(errLog, "utf8").trim();
    if (e) console.log("---- stderr ----\n" + e + "\n----------------");
  } catch {}
  tmuxOk(["kill-session", "-t", session]);
  return false;
}

async function runBackend(backend, conf) {
  for (let n = 1; n <= ATTEMPTS; n++) {
    if (await attempt(backend, conf, n)) return true;
    await sleep(1000);
  }
  console.log(`\n🛑 [${backend}] gave up after ${ATTEMPTS} attempts.`);
  return false;
}

async function main() {
  // Ensure tmux exists.
  if (spawnSync("tmux", ["-V"], { stdio: "ignore" }).status !== 0) {
    console.error("tmux not found.");
    process.exit(1);
  }
  const conf = readConfig();
  const arg = (process.argv[2] || conf.backend).toLowerCase();
  const backends = arg === "both" ? ["claude", "codex"] : [arg];

  for (const b of backends) {
    if (b !== "claude" && b !== "codex") {
      console.error(`Unknown backend: ${b}`);
      continue;
    }
    await runBackend(b, conf);
  }
}

main();
