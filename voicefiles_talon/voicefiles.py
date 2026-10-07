"""Talon integration for the voicefiles app.

Commands are sent over file-based RPC: a JSON request written atomically
to ~/.voicefiles/request.json, which the app watches.
"""

import json
import uuid
from pathlib import Path

from talon import Module

mod = Module()

mod.apps.voicefiles = """
os: linux
and app.name: voicefiles
os: linux
and app.name: Voicefiles
os: linux
and app.exe: voicefiles
"""

COMMS_DIR = Path.home() / ".voicefiles"
REQUEST_FILE = COMMS_DIR / "request.json"


@mod.action_class
class Actions:
    def voicefiles_command(command: str, arg: str = ""):
        """Send a command to the voicefiles app"""
        COMMS_DIR.mkdir(exist_ok=True)
        payload = {
            "id": uuid.uuid4().hex,
            "command": command,
            "args": [arg] if arg else [],
        }
        tmp = COMMS_DIR / "request.json.tmp"
        tmp.write_text(json.dumps(payload))
        tmp.rename(REQUEST_FILE)
