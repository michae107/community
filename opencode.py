from pathlib import Path
import subprocess

from talon import Module, ui


mod = Module()


@mod.action_class
class Actions:
    def opencode_focus_message_box():
        """Focus the active OpenCode window's editable message box."""
        helper = Path.home() / "junk/mcpscape-qwen-vm/focus-opencode.py"
        result = subprocess.run(
            ["/usr/bin/python3", str(helper), ui.active_window().title],
            capture_output=True,
            text=True,
            timeout=5,
        )
        if result.returncode != 0:
            raise RuntimeError(result.stderr.strip() or "Could not focus the OpenCode message box.")
