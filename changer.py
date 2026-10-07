import subprocess
from pathlib import Path

from talon import Module, actions, app, ui

mod = Module()

CHANGER_BINARY = Path("/home/michael/projects/changer/src-tauri/target/debug/changer")


def _lower(value) -> str:
    return str(value or "").lower()


def _is_changer_window(window: ui.Window | None) -> bool:
    if not window:
        return False
    title = _lower(getattr(window, "title", ""))
    try:
        app_name = _lower(window.app.name)
    except Exception:
        app_name = ""
    return "changer" in title or app_name == "changer"


def _find_changer_window() -> ui.Window | None:
    try:
        active = ui.active_window()
    except Exception:
        active = None
    if _is_changer_window(active):
        return active
    return next((window for window in ui.windows() if _is_changer_window(window)), None)


def _wmctrl_focus_window(window: ui.Window) -> bool:
    if app.platform != "linux":
        return False
    try:
        subprocess.run(
            ["wmctrl", "-ia", f"0x{window.id:08x}"],
            check=False,
            timeout=2,
        )
        actions.sleep(0.15)
        return ui.active_window() == window
    except Exception:
        return False


def _focus_changer() -> bool:
    window = _find_changer_window()
    if window:
        try:
            actions.user.switcher_focus_window(window)
            return True
        except Exception:
            if _wmctrl_focus_window(window):
                return True
            actions.app.notify("Could not focus Changer")
            return False

    if not CHANGER_BINARY.exists():
        actions.app.notify("Changer has not been built yet")
        return False

    try:
        subprocess.Popen([str(CHANGER_BINARY)], cwd=str(CHANGER_BINARY.parents[3]))
        actions.sleep(0.8)
        window = _find_changer_window()
        if window:
            actions.user.switcher_focus_window(window)
        return True
    except Exception as error:
        actions.app.notify(f"Could not launch Changer: {error}")
        return False


@mod.action_class
class Actions:
    def changer_focus() -> bool:
        """Focus Changer, launching its local desktop build when needed."""
        return _focus_changer()
