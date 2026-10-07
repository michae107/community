"""Voice control for MCPScape500 (2009scape, the local RuneScape 2009 game).

The game client's VoiceControl plugin (mcpscape500 repo:
rt4-client/plugin-playground/src/main/java/VoiceControl) listens on 127.0.0.1:43620.
One command per line, one reply line: "ok ..." or "error ...". Commands:
  tab NAME   open a side tab (inventory, stats, settings, ..., logout)
  tabs       list tab names
  ping       "ok pong"
"""

import socket

from talon import Module, actions

HOST = "127.0.0.1"
PORT = 43620
TIMEOUT = 2.0

mod = Module()
mod.apps.mcpscape500 = "app.name: MCPScape500"
mod.apps.mcpscape500 = "win.title: /^MCPScape500( |$)/"

mod.list("mcpscape_tab", desc="MCPScape500 side tabs")


def send_command(line: str, port: int | None = None, timeout: float = TIMEOUT) -> str:
    """Send one command line to the game and return its reply (raises OSError if the game isn't listening)."""
    with socket.create_connection((HOST, port or PORT), timeout=timeout) as sock:
        sock.settimeout(timeout)
        sock.sendall((line.strip() + "\n").encode("utf-8"))
        reply = b""
        while not reply.endswith(b"\n"):
            chunk = sock.recv(4096)
            if not chunk:
                break
            reply += chunk
    return reply.decode("utf-8", "replace").strip()


def run(line: str) -> str | None:
    """Send a command; notify (never raise) when the game isn't running or rejects it."""
    try:
        reply = send_command(line)
    except OSError:
        actions.app.notify("MCPScape500 isn't running (no voice control on port %d)" % PORT)
        return None
    if not reply.startswith("ok"):
        actions.app.notify("MCPScape500: " + (reply.removeprefix("error ").strip() or "no reply"))
        return None
    return reply


@mod.action_class
class Actions:
    def mcpscape_tab(name: str):
        """Open an MCPScape500 side tab (inventory, stats, settings, ...)."""
        run("tab " + name)

    def mcpscape_command(line: str):
        """Send a raw voice-control command line to MCPScape500."""
        run(line)
