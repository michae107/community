const vscode = require("vscode");
const fs = require("fs");
const os = require("os");
const path = require("path");
const http = require("http");
const { spawn, execFileSync } = require("child_process");

const TMP = os.tmpdir();
const MCP_SERVER = path.join(__dirname, "mcp-server.mjs");
const PROMPT_FILE = path.join(__dirname, "prompt.md"); // editable system prompt
const CONFIG_FILE = path.join(__dirname, "config.json"); // backend + model + reasoning
const SOURCES = [__filename, MCP_SERVER, PROMPT_FILE, CONFIG_FILE]; // staleness keyed off these

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

// Full access: our editor bridge + Claude's own tools (so it can also create,
// rename, move files, etc.). The prompt tells it to prefer apply_edit.
const ALLOWED_TOOLS =
  "mcp__editor__get_editor_context mcp__editor__apply_edit mcp__editor__done Read Grep Glob Edit Write MultiEdit Bash";

const RUNNING_DECORATION = vscode.window.createTextEditorDecorationType({
  after: {
    contentText: "  ● agent…",
    color: new vscode.ThemeColor("editorCodeLens.foreground"),
    fontStyle: "italic",
  },
});

let lastContext;
let pendingEditor;
let decorationTimer;
let editPort;
const ownSessions = new Set(); // sessions this window owns; killed when it exits

// ---- per-folder session identity ----------------------------------------
function hashPath(s) {
  let h = 5381;
  for (let i = 0; i < s.length; i++) h = ((h << 5) + h + s.charCodeAt(i)) >>> 0;
  return h.toString(36);
}
function cwdFor(editor) {
  const f = vscode.workspace.getWorkspaceFolder(editor.document.uri);
  return f ? f.uri.fsPath : path.dirname(editor.document.uri.fsPath);
}
function sessionName(cwd) {
  const base = path.basename(cwd).replace(/[^a-zA-Z0-9_-]/g, "-");
  return `agent-${base}-${hashPath(cwd)}`;
}
const ctxFile = (s) => path.join(TMP, `voice-claude-${s}.context.json`);
const mcpFile = (s) => path.join(TMP, `voice-claude-${s}.mcp.json`);
const launchFile = (s) => path.join(TMP, `voice-claude-${s}.launch.sh`);

// ---- decoration ----------------------------------------------------------
function showRunning(editor) {
  pendingEditor = editor;
  const line = editor.selection.active.line;
  editor.setDecorations(RUNNING_DECORATION, [new vscode.Range(line, 0, line, 0)]);
  clearTimeout(decorationTimer);
  decorationTimer = setTimeout(clearRunning, 120000);
}
function clearRunning() {
  clearTimeout(decorationTimer);
  if (pendingEditor) {
    pendingEditor.setDecorations(RUNNING_DECORATION, []);
    pendingEditor = undefined;
  }
}

// ---- editor context ------------------------------------------------------
function writeContext() {
  const editor = vscode.window.activeTextEditor;
  if (!editor) return;
  const doc = editor.document;
  const sel = editor.selection;
  lastContext = {
    file: doc.uri.fsPath,
    languageId: doc.languageId,
    cursorLine: sel.active.line + 1,
    cursorColumn: sel.active.character + 1,
    selection: sel.isEmpty
      ? null
      : {
          startLine: sel.start.line + 1,
          startColumn: sel.start.character + 1,
          endLine: sel.end.line + 1,
          endColumn: sel.end.character + 1,
          text: doc.getText(sel),
        },
  };
  fs.writeFileSync(
    ctxFile(sessionName(cwdFor(editor))),
    JSON.stringify(lastContext, null, 2)
  );
}

// ---- apply edits into the live buffer (called by the MCP bridge) ----------
async function applyEdit({ old_string, new_string, replace_all }) {
  if (typeof old_string !== "string" || typeof new_string !== "string") {
    return { ok: false, error: "old_string and new_string are required." };
  }
  const target = lastContext && lastContext.file;
  const doc =
    vscode.workspace.textDocuments.find((d) => d.uri.fsPath === target) ||
    (vscode.window.activeTextEditor &&
      vscode.window.activeTextEditor.document);
  if (!doc) return { ok: false, error: "Target file is not open in the editor." };

  // Empty old_string = create/replace content (e.g. an empty buffer): set the
  // whole document to new_string. Matches the native Edit tool's new-file case.
  if (old_string === "") {
    const full = new vscode.Range(
      doc.positionAt(0),
      doc.positionAt(doc.getText().length)
    );
    const edit = new vscode.WorkspaceEdit();
    edit.replace(doc.uri, full, new_string);
    const ok = await vscode.workspace.applyEdit(edit);
    return ok
      ? { ok: true, message: `Wrote content to ${path.basename(doc.uri.fsPath)}.` }
      : { ok: false, error: "applyEdit was rejected." };
  }

  const text = doc.getText();
  const idxs = [];
  for (let i = text.indexOf(old_string); i !== -1; ) {
    idxs.push(i);
    i = text.indexOf(old_string, i + old_string.length);
  }
  if (idxs.length === 0) {
    return { ok: false, error: "old_string not found in the open buffer." };
  }
  if (idxs.length > 1 && !replace_all) {
    return {
      ok: false,
      error: `old_string is not unique (${idxs.length} matches). Add more context or set replace_all.`,
    };
  }

  const targets = replace_all ? idxs : [idxs[0]];
  const edit = new vscode.WorkspaceEdit();
  for (const start of targets) {
    const range = new vscode.Range(
      doc.positionAt(start),
      doc.positionAt(start + old_string.length)
    );
    edit.replace(doc.uri, range, new_string);
  }
  const ok = await vscode.workspace.applyEdit(edit);
  return ok
    ? { ok: true, message: `Applied ${targets.length} edit(s) to ${path.basename(doc.uri.fsPath)}.` }
    : { ok: false, error: "applyEdit was rejected." };
}

// ---- local HTTP bridge: the MCP server posts here ------------------------
function startBridge() {
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
      clearRunning();
      reply({ ok: true, message: "done" });
      return;
    }
    if (req.url === "/edit") {
      let body = "";
      req.on("data", (c) => (body += c));
      req.on("end", async () => {
        try {
          reply(await applyEdit(JSON.parse(body)));
        } catch (e) {
          reply({ ok: false, error: e.message });
        }
      });
      return;
    }
    res.writeHead(404);
    res.end();
  });
  server.listen(0, "127.0.0.1", () => {
    editPort = server.address().port;
  });
  return server;
}

// ---- tmux helpers --------------------------------------------------------
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
function clientsAttached(session) {
  try {
    return (
      execFileSync("tmux", ["list-clients", "-t", session], {
        encoding: "utf8",
      }).trim().length > 0
    );
  } catch {
    return false;
  }
}
function capturePane(session) {
  try {
    return execFileSync("tmux", ["capture-pane", "-p", "-t", session, "-S", "-80"], {
      encoding: "utf8",
    });
  } catch {
    return "";
  }
}
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// Wait until the agent's input composer is ready, dismissing codex's startup
// "Update available!" menu first (it otherwise eats the first keystrokes — i.e.
// the guide that tells the agent to load the editor MCP tools).
async function waitForReady(backend, session, timeoutMs = 30000) {
  const deadline = Date.now() + timeoutMs;
  let dismissed = false;
  while (Date.now() < deadline) {
    const pane = capturePane(session);
    if (!dismissed && /Update now/.test(pane) && /Press enter to continue/.test(pane)) {
      tmuxOk(["send-keys", "-t", session, "Down", "Down", "Enter"]); // Skip until next version
      dismissed = true;
      await sleep(800);
      continue;
    }
    if (backend === "codex") {
      if (/% used|\/model to change|Write tests for/.test(pane)) return true;
    } else {
      if (/❯|accept edits on|shift\+tab|for agents|\? for shortcuts/.test(pane)) return true;
    }
    await sleep(700);
  }
  return false;
}

// True while codex is mid-turn. Used to gate the second message until the first
// (the tool-loader) has fully completed, so they never fuse into one submission.
function codexWorking(pane) {
  return /Esc to interrupt|esc to interrupt|Working|Thinking|Tool call|Running|Explored|Reading|Called/.test(
    pane
  );
}

// Submit a message: type it, let codex's paste-burst detection settle, then press
// Enter and confirm the turn started — codex collapses long input into a
// "[Pasted Content]" chip that a single Enter won't submit, so retry until it's
// working. Returns true if the turn started.
async function submitMessage(session, text) {
  tmux(["send-keys", "-t", session, "-l", text]);
  await sleep(800);
  for (let i = 0; i < 5; i++) {
    tmuxOk(["send-keys", "-t", session, "Enter"]);
    await sleep(700);
    if (codexWorking(capturePane(session))) return true;
  }
  return false;
}

// Wait for the current codex turn to start then go idle (debounced).
async function waitForTurnIdle(session, { startMs = 9000, idleMs = 90000 } = {}) {
  const startDeadline = Date.now() + startMs;
  let started = false;
  while (Date.now() < startDeadline) {
    if (codexWorking(capturePane(session))) {
      started = true;
      break;
    }
    await sleep(250);
  }
  if (!started) return true; // trivial/instant turn
  const idleDeadline = Date.now() + idleMs;
  let idleStreak = 0;
  while (Date.now() < idleDeadline) {
    if (codexWorking(capturePane(session))) idleStreak = 0;
    else if (++idleStreak >= 2) return true;
    await sleep(600);
  }
  return false;
}

// Message 1 for a fresh session: force codex to actually CALL an editor tool,
// which makes it discover and load the deferred MCP tools (a passive "load them"
// instruction is ignored by the model).
const LOAD_MSG =
  "Before any other work: call the get_editor_context editor tool right now to " +
  "confirm your editor MCP tools (get_editor_context, apply_edit, done) are " +
  "loaded. If they are deferred, load them first, then call get_editor_context.";

function sessionCreated(session) {
  try {
    return parseInt(
      execFileSync(
        "tmux",
        ["display-message", "-p", "-t", session, "#{session_created}"],
        { encoding: "utf8" }
      ).trim(),
      10
    );
  } catch {
    return 0;
  }
}
function sourcesMtime() {
  return Math.max(
    ...SOURCES.map((f) => {
      try {
        return fs.statSync(f).mtimeMs / 1000;
      } catch {
        return 0;
      }
    })
  );
}

function writeLaunch(session, conf) {
  const cfg = vscode.workspace.getConfiguration("voiceClaude");

  // MCP config file (used by the Claude backend).
  fs.writeFileSync(
    mcpFile(session),
    JSON.stringify({
      mcpServers: {
        editor: {
          type: "stdio",
          command: "node",
          args: [MCP_SERVER],
          env: {
            VOICE_CLAUDE_PORT: String(editPort),
            VOICE_CLAUDE_CONTEXT: ctxFile(session),
          },
        },
      },
    })
  );

  let cmd;
  if (conf.backend === "codex") {
    const codex = cfg.get("codexPath") || "codex";
    const { model, reasoning, fastMode } = conf.codex;
    // Codex loads MCP servers via -c overrides; read-only sandbox forces it to
    // edit through our apply_edit tool instead of writing files itself.
    // -a never -s danger-full-access lets Codex's MCP calls reach our bridge
    // and gives full file access for rename/move.
    cmd =
      `${codex} -m ${model} ` +
      `-c model_reasoning_effort="${reasoning}" ` +
      `-c mcp_servers.editor.command="node" ` +
      `-c mcp_servers.editor.args='["${MCP_SERVER}"]' ` +
      `-c mcp_servers.editor.env.VOICE_CLAUDE_PORT='"${editPort}"' ` +
      `-c mcp_servers.editor.env.VOICE_CLAUDE_CONTEXT='"${ctxFile(session)}"' ` +
      `-a never -s danger-full-access ` +
      `${fastMode ? "--enable" : "--disable"} fast_mode`;
  } else {
    const claude = cfg.get("claudePath") || "claude";
    const permission = cfg.get("permissionMode") || "acceptEdits";
    const extra = cfg.get("extraArgs") || "";
    const { model, effort } = conf.claude;
    cmd =
      `${claude} --model ${model} --effort ${effort} --mcp-config ${mcpFile(session)} ` +
      `--append-system-prompt "$(cat ${PROMPT_FILE})" ` +
      `--permission-mode ${permission} ` +
      `--allowedTools ${ALLOWED_TOOLS} ${extra}`;
  }
  // Keep the window open on exit (and log stderr) so failures are visible
  // instead of the terminal vanishing.
  const errLog = path.join(TMP, `voice-claude-${session}.err`);
  fs.writeFileSync(
    launchFile(session),
    "#!/usr/bin/env bash\n" +
      `${cmd} 2> >(tee ${errLog} >&2)\n` +
      `echo "[voice-claude] agent exited $? — log: ${errLog}"\n` +
      "exec bash\n"
  );
}

async function run(promptArg) {
  const editor = vscode.window.activeTextEditor;
  if (!editor) return; // cursor not in an editor → do nothing

  const instruction =
    promptArg ||
    (await vscode.window.showInputBox({ prompt: "Voice Claude instruction" }));
  if (!instruction) return;

  // Save so Claude's own Read tool sees current content (apply_edit writes back
  // into the live buffer, so no reload is triggered).
  await editor.document.save();
  writeContext();

  const conf = readConfig();
  const cwd = cwdFor(editor);
  const session = sessionName(cwd);

  // Recreate the session if it's missing or older than the extension code/config.
  let exists = tmuxOk(["has-session", "-t", session]);
  if (exists && sourcesMtime() > sessionCreated(session)) {
    tmuxOk(["kill-session", "-t", session]);
    exists = false;
  }
  const created = !exists;
  if (created) {
    writeLaunch(session, conf);
    tmux([
      "new-session", "-d", "-s", session, "-c", cwd,
      `bash ${launchFile(session)}`,
    ]);
  }

  // Ensure a visible terminal is attached.
  if (!clientsAttached(session)) {
    const t = spawn(
      "konsole",
      ["-e", "bash", "-lc", `tmux attach -t ${session}`],
      { detached: true, stdio: "ignore" }
    );
    t.on("error", (e) =>
      vscode.window.showErrorMessage("Voice Claude: " + e.message)
    );
    t.unref();
  }

  // Send the instruction with the cursor/selection baked into the prompt, plus an
  // explicit directive to route the change through apply_edit (codex otherwise
  // prefers shell/native edits, which bypass the live buffer).
  const sel = lastContext.selection;
  const loc = sel
    ? `selection from line ${sel.startLine} col ${sel.startColumn} to line ${sel.endLine} col ${sel.endColumn}`
    : `cursor line ${lastContext.cursorLine}, col ${lastContext.cursorColumn}`;
  const userPrompt =
    `[file: ${lastContext.file}; ${loc}] ${instruction} ` +
    `Make the change using the apply_edit editor tool (not shell or native file edits).`;

  ownSessions.add(session);
  showRunning(editor);

  try {
    if (created) {
      // Wait for the composer (dismissing codex's update menu, which eats the
      // first keystrokes). Then force-load the editor tools (message 1), wait for
      // that turn to finish, then send the real request (message 2). Loading
      // requires an actual tool CALL — a passive "load them" instruction is
      // ignored — so message 1 tells codex to call get_editor_context.
      const ready = await waitForReady(conf.backend, session);
      if (!ready) {
        vscode.window.showWarningMessage("Voice Claude: agent didn't become ready in time.");
      }
      await submitMessage(session, LOAD_MSG);
      await waitForTurnIdle(session);
      await submitMessage(session, userPrompt);
    } else {
      // Existing session — tools already loaded on first launch; just send it.
      await submitMessage(session, userPrompt);
    }
  } catch (e) {
    vscode.window.showErrorMessage("Voice Claude: " + e.message);
  }
}

function kill() {
  const editor = vscode.window.activeTextEditor;
  if (!editor) return;
  const session = sessionName(cwdFor(editor));
  tmuxOk(["kill-session", "-t", session]);
  ownSessions.delete(session);
  clearRunning();
  vscode.window.showInformationMessage(`Voice Claude: killed ${session}.`);
}

function activate(context) {
  writeContext();
  const bridge = startBridge();
  context.subscriptions.push(
    { dispose: () => bridge.close() },
    vscode.commands.registerCommand("voiceClaude.run", run),
    vscode.commands.registerCommand("voiceClaude.kill", kill),
    vscode.window.onDidChangeActiveTextEditor(writeContext),
    vscode.window.onDidChangeTextEditorSelection(writeContext),
    // Clear the running decoration once the edit lands. Covers Codex (edits disk
    // → buffer reloads) and Claude (apply_edit changes the buffer); the `done`
    // tool also clears it for Claude.
    vscode.workspace.onDidChangeTextDocument((e) => {
      if (pendingEditor && e.document === pendingEditor.document) clearRunning();
    })
  );
}

// VSCode exiting (or reloading) deactivates the extension → kill our sessions.
function deactivate() {
  for (const s of ownSessions) {
    try {
      execFileSync("tmux", ["kill-session", "-t", s], { stdio: "ignore" });
    } catch {}
  }
}

module.exports = { activate, deactivate };
