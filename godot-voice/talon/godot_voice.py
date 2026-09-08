"""Talon client for the C# Godot editor addon; commands are scoped to the editor."""

import getpass
import json
import re
import subprocess
import tempfile
import time
import uuid
from pathlib import Path

from talon import Context, Module, actions, ui

mod = Module()
mod.apps.godot_voice_editor = """
os: linux
app.name: Godot
"""
ctx = Context()
ctx.matches = """
app: godot_voice_editor
"""


@mod.capture(rule="<user.number_signed_string> [(point | dot) <digit_string>]")
def godot_voice_number(m) -> float:
    """A signed Inspector number with optional decimal digits."""
    fraction = getattr(m, "digit_string", "")
    return float(m.number_signed_string + ("." + fraction if fraction else ""))


def command(method: str, arguments: dict | None = None):
    # Talon can group several Godot windows into one app whose cached PID belongs
    # to an older editor. X11's active window PID is the actual command target.
    window = ui.active_window()
    if window is None:
        raise RuntimeError("Focus the Godot editor first.")
    pid_property = subprocess.run(
        ["xprop", "-id", hex(window.id), "_NET_WM_PID"],
        capture_output=True, text=True, timeout=1, check=True,
    ).stdout
    match = re.search(r"=\s*(\d+)\s*$", pid_property)
    if match is None:
        raise RuntimeError("Cannot identify this Godot window's process.")
    directory = (
        Path(tempfile.gettempdir())
        / f"talon-godot-{getpass.getuser()}"
        / match.group(1)
    )
    if not (directory / "state.json").is_file():
        raise RuntimeError("Talon Voice is not connected to this editor. Run godot-voice/install.sh.")
    state = json.loads((directory / "state.json").read_text(encoding="utf-8"))
    if int(time.time() * 1000) - state.get("updated", 0) > 5000:
        raise RuntimeError("This editor's voice endpoint is stale or busy reloading. Wait for Godot and try again.")
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
    def godot_voice_command(method: str):
        """Run a Godot editor command."""
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
        """Act on a green native editor UI badge."""
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
