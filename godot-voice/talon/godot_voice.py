"""Talon client scoped to the active, connected Godot editor or game window."""

import getpass
import json
import re
import subprocess
import tempfile
import time
import uuid
from pathlib import Path

from talon import Context, Module, actions, cron, ui

mod = Module()
mod.tag("godot_voice_editor", desc="Connected Godot editor window")
mod.tag("godot_voice_ui", desc="Connected Godot editor or game UI")
mod.tag("godot_voice_scene_commands", desc="Focused game with registered scene commands")
mod.list("godot_voice_coordinate_commands", desc="Scene commands accepting a letter-number coordinate")
mod.list("godot_voice_text_commands", desc="Scene commands accepting text")
mod.list("godot_voice_plain_commands", desc="Scene commands without arguments")
connection_context = Context()
ctx = Context()
ctx.matches = """
tag: user.godot_voice_editor
"""
_window_id = None
_window_pid = None
_scene_lists = {}


def active_endpoint(force: bool = False):
    global _window_id, _window_pid
    window = ui.active_window()
    if window is None:
        raise RuntimeError("Focus a connected Godot editor or game first.")
    if force or window.id != _window_id or _window_pid is None:
        _window_id, _window_pid = window.id, None
        result = subprocess.run(
            ["xprop", "-id", hex(window.id), "_NET_WM_PID"],
            capture_output=True, text=True, timeout=1, check=True,
        )
        match = re.search(r"=\s*(\d+)\s*$", result.stdout)
        if match is None:
            raise RuntimeError("Cannot identify this window's process.")
        _window_pid = match.group(1)
    directory = Path(tempfile.gettempdir()) / f"talon-godot-{getpass.getuser()}" / _window_pid
    state = json.loads((directory / "state.json").read_text(encoding="utf-8"))
    if int(time.time() * 1000) - state.get("updated", 0) > 5000:
        raise RuntimeError("This Godot voice connection is stale or reloading.")
    return directory, state


def update_connection(*_):
    tags = []
    commands = {"coordinate": {}, "text": {}, "none": {}}
    try:
        _, state = active_endpoint()
        if state.get("kind", "editor") in ("editor", "game"):
            tags.append("user.godot_voice_ui")
            if state.get("kind", "editor") == "editor":
                tags.append("user.godot_voice_editor")
            elif state.get("commands"):
                tags.append("user.godot_voice_scene_commands")
                for entry in state["commands"]:
                    if entry.get("argument") in commands:
                        commands[entry["argument"]][entry["phrase"]] = entry["name"]
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError):
        pass
    if set(connection_context.tags) != set(tags):
        connection_context.tags = tags
    for argument, list_name in (
        ("coordinate", "godot_voice_coordinate_commands"),
        ("text", "godot_voice_text_commands"),
        ("none", "godot_voice_plain_commands"),
    ):
        key = "user." + list_name
        if _scene_lists.get(key) != commands[argument]:
            connection_context.lists[key] = commands[argument]
            _scene_lists[key] = commands[argument]


ui.register("win_focus", update_connection)
cron.interval("500ms", update_connection)


@mod.capture(rule="<user.number_signed_string> [(point | dot) <digit_string>]")
def godot_voice_number(m) -> float:
    """A signed Inspector number with optional decimal digits."""
    fraction = getattr(m, "digit_string", "")
    return float(m.number_signed_string + ("." + fraction if fraction else ""))


def command(method: str, arguments: dict | None = None):
    # Talon can group several Godot windows into one app whose cached PID belongs
    # to an older editor. X11's active window PID is the actual command target.
    directory, state = active_endpoint(force=True)
    if method == "game.command" and state.get("kind") != "game":
        raise RuntimeError("Scene commands require a focused game window.")
    if state.get("kind") == "game" and method not in ("status", "game.command") and not method.startswith("ui."):
        raise RuntimeError("Editor commands cannot target a game window.")
    request_id = uuid.uuid4().hex
    request = directory / f"{request_id}.request.json"
    response = directory / f"{request_id}.response.json"
    temporary = request.with_suffix(".tmp")
    temporary.write_text(json.dumps({
        "id": request_id,
        "method": method,
        "args": arguments or {},
        "created": int(time.time() * 1000),
    }), encoding="utf-8")
    temporary.replace(request)
    try:
        deadline = time.monotonic() + 3
        while time.monotonic() < deadline:
            if response.is_file():
                result = json.loads(response.read_text(encoding="utf-8"))
                if result.get("error"):
                    raise RuntimeError("Godot: " + result["error"])
                return result.get("result")
            actions.sleep("25ms")
        raise RuntimeError("Godot did not respond within three seconds. It may be busy or showing a dialog.")
    finally:
        request.unlink(missing_ok=True)
        response.unlink(missing_ok=True)
        temporary.unlink(missing_ok=True)


@mod.action_class
class Actions:
    def godot_voice_scene_command(name: str, argument: str = ""):
        """Invoke an explicitly registered command in the focused game's current scene."""
        command("game.command", {"name": name, "argument": argument})

    def godot_voice_coordinate_command(name: str, letters: str, number: int):
        """Pass spoken letters and a number to a registered coordinate command."""
        coordinate = "".join(letters.split()).upper() + str(number)
        command("game.command", {"name": name, "argument": coordinate})

    def godot_voice_command(method: str):
        """Run a command in the focused Godot editor or game."""
        result = command(method)
        if method == "status":
            actions.app.notify(f"Godot Voice connected: {len(result['hints'])} visible hints")

    def godot_voice_target(method: str, scope: str, letters: str, depth: int = 1):
        """Act on a letter-labelled scene, filesystem, or tab target."""
        if method == "tree.expand" and depth == 0:
            method = "tree.collapse"
        command(method, {"scope": scope, "letters": letters.lower(), "depth": depth})

    def godot_voice_focus(pane: str):
        """Reveal and focus an editor pane."""
        command("focus", {"pane": pane})

    def godot_voice_search(scope: str, text: str = ""):
        """Focus the scene or filesystem filter and set its search text."""
        command("search", {"scope": scope, "text": text})

    def godot_voice_tree(method: str, scope: str):
        """Act on the root of the scene or filesystem tree."""
        command(method, {"scope": scope})

    def godot_voice_tree_key(scope: str, direction: str):
        """Navigate the native tree using its keyboard controls."""
        command("tree.key", {"scope": scope, "direction": direction})

    def godot_voice_shortcut(name: str):
        """Use the editor's configured shortcut binding."""
        command("editor.shortcut", {"name": name})

    def godot_voice_action(scope: str, action: str):
        """Focus the relevant dock and run one of its native editing actions."""
        command(scope + ".action", {"action": action})

    def godot_voice_ui(method: str, letters: str):
        """Act on a green editor or game UI badge."""
        command("ui." + method, {"letters": letters.lower()})

    def godot_voice_ui_text(text: str, letters: str, submit: bool = False):
        """Replace a labelled input's contents, optionally submitting it."""
        result = command("ui.text", {"letters": letters.lower(), "text": text, "submit": submit})
        if result.get("clamped"):
            actions.app.notify(f"Godot constrained the value to {result['value']}")

    def godot_voice_ui_number(letters: str, value: float):
        """Set a labelled numeric field using its native property editor."""
        result = command("ui.number", {"letters": letters.lower(), "value": value})
        if result.get("clamped"):
            actions.app.notify(f"Godot constrained the value to {result['value']}")

    def godot_voice_ui_step(letters: str, direction: int, count: int = 1):
        """Adjust a labelled numeric field in its configured steps."""
        result = command("ui.step", {"letters": letters.lower(), "direction": direction, "count": count})
        if result.get("clamped"):
            actions.app.notify(f"Godot constrained the value to {result['value']}")

    def godot_voice_ui_scroll(letters: str, direction: int, count: int = 1):
        """Scroll the panel containing a labelled control."""
        command("ui.scroll", {"letters": letters.lower(), "direction": direction, "count": count})

    def godot_voice_ui_scope(scope: str):
        """Limit green hints to an editor dock, or show all editor controls."""
        command("ui.hints.scope", {"scope": scope})


@ctx.action_class("app")
class AppActions:
    def tab_open():
        command("editor.shortcut", {"name": "editor/new_scene"})

    def tab_close():
        command("editor.shortcut", {"name": "editor/close_scene"})

    def tab_next():
        command("tabs.next")

    def tab_previous():
        command("tabs.previous")

    def tab_reopen():
        command("editor.shortcut", {"name": "editor/reopen_closed_scene"})


@ctx.action_class("user")
class UserActions:
    def tab_jump(number: int):
        command("tabs.select", {"index": number})

    def tab_final():
        tabs = command("status")["tabs"]
        if not tabs:
            raise RuntimeError("Godot has no open scene tabs.")
        command("tabs.select", {"index": len(tabs)})

    def tab_duplicate():
        # Godot cannot show the same scene in two tabs. Never substitute file
        # duplication for the shared tab-cloning command.
        actions.app.notify("Godot does not support duplicate tabs for the same scene.")
