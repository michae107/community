"""Send voice commands to the local MousePods service without shell paths."""

import os
import socket
from pathlib import Path

from talon import Module, app

mod = Module()


def send_commands(commands):
    runtime = Path(os.environ.get("XDG_RUNTIME_DIR", "/tmp"))
    with socket.socket(socket.AF_UNIX, socket.SOCK_DGRAM) as connection:
        connection.settimeout(0.5)
        connection.connect(str(runtime / "airpods-mouse.sock"))
        for command in commands:
            connection.send(command.encode("utf-8"))


@mod.action_class
class Actions:
    def mousepods_command(command: str):
        """Recenter, pause, or recenter and resume MousePods."""
        commands = {
            "recenter": ("recenter",),
            "pause": ("pause",),
            "start": ("recenter", "resume"),
        }
        if command not in commands:
            raise ValueError(f"Unknown MousePods voice command: {command}")
        try:
            send_commands(commands[command])
        except OSError:
            app.notify("MousePods", "The mouse controller is unavailable. Open MousePods to start it.")
