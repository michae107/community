# Voice Claude

Speak an instruction in VSCode; **real Claude Code** runs in a visible terminal and
makes surgical edits to the file at your cursor.

## How it works

```
Talon ("agent <instruction>")
   → VSCode command (voiceClaude.run, via the command-server you already have)
   → extension launches `claude` in a visible terminal, wired to an MCP server
   → Claude Code calls the MCP `get_editor_context` tool to learn your file/cursor/
     selection, then edits with its own Edit tool (surgical, streaming, in the TUI)
```

Two pieces live in this folder:

- **`extension.js`** — keeps a context file (`$TMPDIR/voice-claude-context.json`)
  in sync with your active editor (file, cursor, selection), and launches Claude
  Code in a terminal. The first "agent …" starts the session; later ones continue
  the same conversation.
- **`mcp-server.mjs`** — a stdio MCP server exposing one tool, `get_editor_context`,
  which returns whatever is in the context file. Claude Code is launched with this
  server via `--mcp-config`.

Edits are made by Claude Code itself, so they're real, surgical, and stream live in
the terminal — no whole-file replacement.

## Install

```bash
bash ~/.talon/user/voice-claude/build.sh
```

This runs `npm install` (for the MCP SDK) and symlinks the extension into
`~/.vscode/extensions/local.voice-claude`. It's also picked up automatically by
`~/.talon/user/install.sh`. Reload VSCode afterward
(`Developer: Reload Window`).

The Talon grammar (`talon/voice_claude.talon`) is already under `~/.talon/user`, so
Talon loads it automatically.

## Use

With VSCode focused, put your cursor in code (or select a block) and say:

> "agent rename this variable to total"

Claude Code opens in the bottom terminal, resolves "this" via the MCP tool, and
edits. Keep talking to continue the same session.

There's also **"agent prompt"** to type the instruction in a box instead of speaking.

## Settings

- `voiceClaude.claudePath` — path to the `claude` CLI.
- `voiceClaude.permissionMode` — `acceptEdits` (default) auto-applies edits for
  hands-free use; `default` makes Claude ask first.
- `voiceClaude.extraArgs` — extra args appended to the `claude` command.

## Test without voice

Command palette → **"Voice Claude: Run prompt at cursor"** → type an instruction.
Fastest way to verify before wiring up voice.
