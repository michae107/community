"""Voice commands enabled only for the focused, connected Unity editor."""

import getpass
import json
import re
import subprocess
import tempfile
import time
import uuid
from pathlib import Path

from talon import Context, Module, actions, clip, cron, ctrl, ui

mod = Module()
mod.tag("unity_voice", desc="Focused Unity editor with Talon Voice installed")
connection = Context()
ctx = Context()
ctx.matches = "tag: user.unity_voice"
_window_id = None
_window_pid = None


def endpoint(force=False):
    global _window_id, _window_pid
    window = ui.active_window()
    if window is None:
        raise RuntimeError("Focus the connected Unity editor first.")
    if force or window.id != _window_id or _window_pid is None:
        _window_id, _window_pid = window.id, None
        result = subprocess.run(
            ["xprop", "-id", hex(window.id), "_NET_WM_PID"],
            capture_output=True, text=True, check=True, timeout=1,
        )
        match = re.search(r"=\s*(\d+)\s*$", result.stdout)
        if match is None:
            raise RuntimeError("Cannot identify the focused window's process.")
        _window_pid = match.group(1)
    directory = Path(tempfile.gettempdir()) / f"talon-unity-{getpass.getuser()}" / _window_pid
    state = json.loads((directory / "state.json").read_text(encoding="utf-8"))
    if state.get("kind") != "editor" or state.get("version") != 1:
        raise RuntimeError("Unsupported Unity voice connection.")
    if not 0 <= int(time.time() * 1000) - state.get("updated", 0) < 5000:
        raise RuntimeError("Unity is busy, disconnected, or reloading scripts.")
    return directory, state


def update_connection(*_):
    tags = []
    try:
        endpoint()
        tags = ["user.unity_voice"]
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError):
        pass
    if set(connection.tags) != set(tags):
        connection.tags = tags


ui.register("win_focus", update_connection)
cron.interval("500ms", update_connection)


def command(method, arguments=None):
    directory, state = endpoint(force=True)
    request_id = uuid.uuid4().hex
    request = directory / f"{request_id}.request.json"
    response = directory / f"{request_id}.response.json"
    temporary = directory / f"{request_id}.tmp"
    try:
        temporary.write_text(json.dumps({
            "id": request_id, "session": state["session"],
            "method": method, "args": arguments or {},
            "created": int(time.time() * 1000),
        }), encoding="utf-8")
        temporary.replace(request)
        deadline = time.monotonic() + 3
        while time.monotonic() < deadline:
            if response.is_file():
                result = json.loads(response.read_text(encoding="utf-8"))
                if result.get("error"):
                    raise RuntimeError("Unity: " + result["error"])
                if result.get("clipboard") is not None:
                    clip.set_text(result["clipboard"])
                if result.get("movePointer"):
                    current_directory, current = endpoint(force=True)
                    if current_directory != directory or current["session"] != state["session"]:
                        raise RuntimeError("Focus changed; pointer action cancelled.")
                    ctrl.mouse_move(result["x"], result["y"])
                return result
            actions.sleep("25ms")
        raise RuntimeError("Unity did not respond. It may be compiling or displaying a dialog.")
    finally:
        request.unlink(missing_ok=True)
        response.unlink(missing_ok=True)
        temporary.unlink(missing_ok=True)


def letters(value):
    result = "".join(value.split()).lower()
    if not re.fullmatch(r"[a-z]+", result):
        raise ValueError("A letter hint is required.")
    return result


@mod.capture(rule="<user.number_signed_string> [(point | dot) <digit_string>]")
def unity_voice_number(m) -> float:
    """A signed decimal for Unity numeric fields."""
    fraction = getattr(m, "digit_string", "")
    return float(m.number_signed_string + ("." + fraction if fraction else ""))


@mod.action_class
class Actions:
    def unity_voice_editor(action: str):
        """Invoke a supported native editor action."""
        command("editor", {"action": action})

    def unity_voice_focus(pane: str):
        """Focus a built-in pane; center the pointer for the Scene viewport."""
        command("focus", {"pane": pane})

    def unity_voice_hints(action: str):
        """Show, hide, scope, or refresh native UI hints."""
        command("hints", {"action": action})

    def unity_voice_status():
        """Report the editor connection and visible hint counts."""
        actions.app.notify(command("status")["message"])

    def unity_voice_tree(action: str, scope: str, target: str = "", depth: int = 1):
        """Target a native Hierarchy or Project row by letters."""
        if not 0 <= depth <= 8:
            raise ValueError("Expansion depth must be between zero and eight.")
        if depth == 0 and action == "expand":
            action = "collapse"
        command("tree." + action, {
            "scope": scope, "letters": letters(target) if target else "", "depth": depth,
        })

    def unity_voice_tree_key(scope: str, key: str):
        """Focus a native tree and use its keyboard navigation."""
        command("focus", {"pane": scope})
        actions.key(key)

    def unity_voice_ui(action: str, target: str):
        """Focus or click the currently labelled native control."""
        command("ui", {"action": action, "letters": letters(target)})
        if action in ("click", "double_click", "menu"):
            ctrl.mouse_click(button=1 if action == "menu" else 0)
            if action == "double_click":
                actions.sleep("50ms")
                ctrl.mouse_click(button=0)
        elif action == "change":
            actions.key("ctrl-a")

    def unity_voice_text(target: str, text: str):
        """Set an exposed native text field."""
        command("ui", {"action": "text", "letters": letters(target), "text": text})

    def unity_voice_number_set(target: str, value: float):
        """Set an exposed native numeric field or slider."""
        command("ui", {"action": "set", "letters": letters(target), "value": value})

    def unity_voice_scroll(target: str, direction: int, amount: int = 1):
        """Scroll the native container surrounding a labelled control."""
        command("ui", {"action": "scroll", "letters": letters(target),
                       "value": direction * min(max(amount, 1), 20)})


@ctx.action_class("app")
class AppActions:
    def tab_open():
        command("tabs", {"action": "open"})

    def tab_close():
        command("tabs", {"action": "close"})

    def tab_next():
        command("tabs", {"action": "next"})

    def tab_previous():
        command("tabs", {"action": "previous"})

    def tab_reopen():
        command("tabs", {"action": "reopen"})


@ctx.action_class("user")
class UserActions:
    def tab_jump(number: int):
        command("tabs", {"action": "select", "index": number})

    def tab_final():
        command("tabs", {"action": "final"})

    def tab_duplicate():
        command("tabs", {"action": "duplicate"})
