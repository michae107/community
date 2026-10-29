"""Voice control for the local MCPScape500 and MCPScape727 clients.

The game client's VoiceControl plugin (mcpscape500 repo:
rt4-client/plugin-playground/src/main/java/VoiceControl) listens on 127.0.0.1:43620.
MCPScape727 uses the same protocol on port 43720. The active window selects the client.
One command per line, one reply line: "ok ..." or "error ...". Commands:
  tab NAME   open a side tab (inventory, stats, settings, ..., logout)
  tabs       list tab names
  run [on|off|toggle]   switch run mode (no argument: toggle)
  look DIRECTION   turn the camera to face north, northeast, ... (orbits round, half tilted)
  zoom in|out   move the camera closer or farther (MCPScape727)
  ping       "ok pong"
"""

import socket

from talon import Module, actions, ui

HOST = "127.0.0.1"
PORT = 43620
# longer than the plugin's 5 s wait for the game thread, so the plugin always answers first
# (otherwise a slow frame reads as "not running" and the command still runs afterwards)
TIMEOUT = 6.0

mod = Module()
mod.apps.mcpscape500 = "app.name: MCPScape500"
mod.apps.mcpscape500 = "win.title: /^MCPScape500( |$)/"

mod.apps.mcpscape727 = "app.name: MCPScape727"
mod.apps.mcpscape727 = "win.title: /^MCPScape727( |$)/"

mod.list("mcpscape_tab", desc="MCPScape side tabs")
mod.list("mcpscape_direction", desc="MCPScape camera directions")


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
    window = ui.active_window()
    is_727 = window.title.startswith("MCPScape727") or window.app.name == "MCPScape727"
    game = "MCPScape727" if is_727 else "MCPScape500"
    port = 43720 if is_727 else PORT
    try:
        reply = send_command(line, port=port, timeout=12.0)
    except TimeoutError:
        actions.app.notify(game + " didn't answer")
        return None
    except OSError:
        actions.app.notify(game + " isn't running (no voice control on port %d)" % port)
        return None
    if not reply.startswith("ok"):
        actions.app.notify(game + ": " + (reply.removeprefix("error ").strip() or "no reply"))
        return None
    return reply


@mod.action_class
class Actions:
    def mcpscape_tab(name: str):
        """Open an MCPScape side tab (inventory, stats, settings, ...)."""
        run("tab " + name)

    def mcpscape_look(direction: str):
        """Turn the MCPScape camera to face a direction (north, northeast, ...)."""
        run("look " + direction)

    def mcpscape_command(line: str):
        """Send a raw voice-control command line to the active MCPScape client."""
        run(line)
