import subprocess

from talon import Module, actions, app, ui

mod = Module()


def _lower(value) -> str:
    return str(value or "").lower()


def _window_app_name(window: ui.Window) -> str:
    try:
        return _lower(window.app.name)
    except Exception:
        return ""


def _is_astrodash_window(window: ui.Window | None) -> bool:
    if not window:
        return False
    title = _lower(getattr(window, "title", ""))
    app_name = _window_app_name(window)
    return "astrodash" in title or "astrodash" in app_name


def _find_astrodash_window() -> ui.Window | None:
    try:
        active = ui.active_window()
    except Exception:
        active = None
    if _is_astrodash_window(active):
        return active

    for window in ui.windows():
        if _is_astrodash_window(window):
            return window
    return None


def _wmctrl_focus_window(window: ui.Window) -> bool:
    if app.platform != "linux":
        return False
    try:
        subprocess.run(
            ["wmctrl", "-ia", f"0x{window.id:08x}"],
            check=False,
            timeout=2,
        )
        actions.sleep(0.1)
        return ui.active_window() == window
    except Exception:
        return False


def _focus_astrodash_window() -> bool:
    window = _find_astrodash_window()
    if not window:
        actions.app.notify("Astrodash is not running")
        return False

    try:
        actions.user.switcher_focus_window(window)
        return True
    except Exception:
        if _wmctrl_focus_window(window):
            return True
        actions.app.notify("Could not focus Astrodash")
        return False


@mod.action_class
class Actions:
    def astrodash_focus() -> bool:
        """Focus a running Astrodash window."""
        return _focus_astrodash_window()

    def astrodash_focus_tab(number: int):
        """Focus Astrodash and switch to tab 1-9."""
        if number < 1 or number > 9:
            actions.app.notify("Astrodash tabs are one through nine")
            return
        if not actions.user.astrodash_focus():
            return
        actions.sleep(0.15)
        actions.key(f"ctrl-{number}")

    def astrodash_focus_next_tab():
        """Focus Astrodash and cycle to the next tab."""
        if not actions.user.astrodash_focus():
            return
        actions.sleep(0.15)
        actions.key("ctrl-tab")

    def astrodash_focus_previous_tab():
        """Focus Astrodash and cycle to the previous tab."""
        if not actions.user.astrodash_focus():
            return
        actions.sleep(0.15)
        actions.key("ctrl-shift-tab")

    def astrodash_tab_next():
        """Cycle Astrodash to the next tab."""
        actions.key("ctrl-tab")

    def astrodash_tab_previous():
        """Cycle Astrodash to the previous tab."""
        actions.key("ctrl-shift-tab")
