import subprocess

from talon import Module, actions, app, ui

mod = Module()

# The editor and a game launched from it (-game, or Play in New Window) are both the UnrealEditor process, so the
# app switcher cannot tell them apart; their window titles can.
EDITOR_TITLE = "unreal editor"


def _is_unreal_window(window: ui.Window) -> bool:
    try:
        return window.app.name == "UnrealEditor" and bool(window.title.strip())
    except Exception:
        return False


def _find_window(game: bool) -> ui.Window | None:
    for window in ui.windows():
        if not _is_unreal_window(window):
            continue
        is_editor = EDITOR_TITLE in window.title.lower()
        if is_editor != game:
            return window
    return None


def _focus(window: ui.Window) -> None:
    try:
        actions.user.switcher_focus_window(window)
    except Exception:
        if app.platform == "linux":
            subprocess.run(["wmctrl", "-ia", f"0x{window.id:08x}"], check=False, timeout=2)


@mod.action_class
class Actions:
    def unreal_focus_game() -> None:
        """Focus the Unreal game window (not the editor)."""
        window = _find_window(game=True)
        if window:
            _focus(window)
        else:
            actions.app.notify("No Unreal game window open")

    def unreal_focus_editor() -> None:
        """Focus the Unreal Editor window."""
        window = _find_window(game=False)
        if window:
            _focus(window)
        else:
            actions.app.notify("No Unreal Editor window open")
