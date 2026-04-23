import subprocess

from talon import Module, actions

mod = Module()


def _wmctrl_ordered_ids():
    result = subprocess.run(
        ["wmctrl", "-l"], check=True, capture_output=True, text=True, timeout=2
    )
    ids = []
    for line in result.stdout.splitlines():
        parts = line.split(None, 1)
        if not parts:
            continue
        try:
            ids.append(int(parts[0], 16))
        except ValueError:
            pass
    return ids


@mod.action_class
class Actions:
    def focus_app_index(name: str, index: int):
        """Focus the Nth window of an app (by spoken name) in desktop order (1-based)."""
        app = actions.user.get_running_app(name)
        app_window_ids = {w.id for w in app.windows()}
        if not app_window_ids:
            actions.app.notify(f"No {name} windows open")
            return
        ordered = [wid for wid in _wmctrl_ordered_ids() if wid in app_window_ids]
        if not ordered:
            ordered = sorted(app_window_ids)
        target = ordered[min(index, len(ordered)) - 1]
        subprocess.run(["wmctrl", "-ia", f"0x{target:08x}"], check=False, timeout=2)
