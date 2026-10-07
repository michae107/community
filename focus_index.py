import subprocess

from talon import Module, actions

mod = Module()


def _wmctrl_ordered_windows():
    result = subprocess.run(
        ["wmctrl", "-lx"], check=True, capture_output=True, text=True, timeout=2
    )
    windows = []
    for line in result.stdout.splitlines():
        parts = line.split(None, 3)
        if len(parts) < 3:
            continue
        try:
            windows.append((int(parts[0], 16), parts[2].split(".", 1)[0].lower()))
        except ValueError:
            pass
    return windows


@mod.action_class
class Actions:
    def focus_app_index(name: str, index: int):
        """Focus the Nth window of an app (by spoken name) in desktop order (1-based)."""
        app = actions.user.get_running_app(name)
        app_window_ids = {w.id for w in app.windows()}
        if not app_window_ids:
            actions.app.notify(f"No {name} windows open")
            return
        windows = _wmctrl_ordered_windows()
        # Godot games register under the project name, separately from the editor.
        # Group them by WM_CLASS instance, without depending on window titles.
        is_godot_editor = any(
            wid in app_window_ids and instance == "godot_editor"
            for wid, instance in windows
        )
        ordered = [
            wid
            for wid, instance in windows
            if wid in app_window_ids
            or (
                is_godot_editor
                and instance in {"godot_editor", "godot_engine"}
            )
        ]
        if not ordered:
            ordered = sorted(app_window_ids)
        target = ordered[min(index, len(ordered)) - 1]
        subprocess.run(["wmctrl", "-ia", f"0x{target:08x}"], check=False, timeout=2)
