// Stdio MCP server bridging Claude Code to the user's live VS Code editor.
// - get_editor_context: reads the context file the extension keeps fresh.
// - apply_edit: posts an edit to the extension's local HTTP bridge, which
//   applies it to the OPEN editor buffer (never touches disk).
import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
  ListToolsRequestSchema,
  CallToolRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";
import { readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const CONTEXT_FILE =
  process.env.VOICE_CLAUDE_CONTEXT || join(tmpdir(), "voice-claude-context.json");
const PORT = process.env.VOICE_CLAUDE_PORT;

const TOOLS = [
  {
    name: "get_editor_context",
    description:
      "Returns the user's current VS Code editor state: active file path, " +
      "language, cursor line/column, and current selection. Use your own Read " +
      'tool to read file contents. Use this to resolve "this"/"here"/"this code".',
    inputSchema: { type: "object", properties: {}, additionalProperties: false },
  },
  {
    name: "done",
    description:
      "Call this at the END of every turn — after any edits, or if you made " +
      "none — to tell the editor you have finished.",
    inputSchema: { type: "object", properties: {}, additionalProperties: false },
  },
  {
    name: "apply_edit",
    description:
      "Edit the user's currently open file by replacing old_string with " +
      "new_string in the live editor buffer (same semantics as the native Edit " +
      "tool). old_string must match the buffer exactly and be unique unless " +
      "replace_all is true. If the file is empty, pass an empty old_string to " +
      "insert the initial content. This is the ONLY way to modify the file.",
    inputSchema: {
      type: "object",
      properties: {
        old_string: { type: "string", description: "Exact text to replace." },
        new_string: { type: "string", description: "Replacement text." },
        replace_all: {
          type: "boolean",
          description: "Replace all occurrences (default false).",
        },
      },
      required: ["old_string", "new_string"],
      additionalProperties: false,
    },
  },
];

const server = new Server(
  { name: "editor", version: "0.1.0" },
  { capabilities: { tools: {} } }
);

const err = (text) => ({ content: [{ type: "text", text }], isError: true });

server.setRequestHandler(ListToolsRequestSchema, async () => ({ tools: TOOLS }));

server.setRequestHandler(CallToolRequestSchema, async (req) => {
  const { name, arguments: args } = req.params;

  if (name === "get_editor_context") {
    let text;
    try {
      text = readFileSync(CONTEXT_FILE, "utf8");
    } catch {
      text = JSON.stringify({ error: "No editor context available yet." });
    }
    return { content: [{ type: "text", text }] };
  }

  if (name === "apply_edit" || name === "done") {
    if (!PORT) return err("Editor bridge unavailable (no port).");
    const route = name === "done" ? "/done" : "/edit";
    try {
      const res = await fetch(`http://127.0.0.1:${PORT}${route}`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify(args || {}),
      });
      const data = await res.json();
      return data.ok
        ? { content: [{ type: "text", text: data.message || "OK." }] }
        : err(data.error || "Failed.");
    } catch (e) {
      return err("Could not reach editor: " + e.message);
    }
  }

  return err(`Unknown tool: ${name}`);
});

await server.connect(new StdioServerTransport());
