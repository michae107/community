"""Request a native asset refresh after installing/updating the linked package."""

import getpass
import json
import sys
import tempfile
import time
import uuid
from pathlib import Path


def main():
    project = Path(sys.argv[1]).resolve()
    root = Path(tempfile.gettempdir()) / f"talon-unity-{getpass.getuser()}"
    connections = []
    for state_path in root.glob("*/state.json"):
        try:
            state = json.loads(state_path.read_text())
            if Path(state.get("project", "")).resolve() == project and time.time() * 1000 - state["updated"] < 5000:
                connections.append((state_path.parent, state))
        except (OSError, ValueError, KeyError):
            continue
    if not connections:
        print("First install: focus or reopen Unity to import the package. Later installs request an asset refresh automatically.")
        return
    if len(connections) != 1:
        raise RuntimeError("Multiple editors match this project; close the extra instance before reinstalling.")
    directory, state = connections[0]
    identifier = uuid.uuid4().hex
    request = directory / f"{identifier}.request.json"
    response = directory / f"{identifier}.response.json"
    temporary = directory / f"{identifier}.tmp"
    try:
        temporary.write_text(json.dumps({
            "id": identifier,
            "session": state["session"],
            "method": "editor",
            "args": {"action": "refresh"},
            "created": int(time.time() * 1000),
        }))
        temporary.replace(request)
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            if response.exists():
                result = json.loads(response.read_text())
                if result.get("error"):
                    raise RuntimeError(result["error"] + " Focus Unity and rerun the installer.")
                print("Unity accepted the asset refresh. Source changes remain linked.")
                return
            try:
                current = json.loads((directory / "state.json").read_text())
                if current["session"] != state["session"] and time.time() * 1000 - current["updated"] < 5000:
                    print("Unity reloaded the plugin after the asset refresh.")
                    return
            except (OSError, ValueError, KeyError):
                pass
            time.sleep(0.1)
        raise RuntimeError("Unity has not acknowledged the refresh. Check its Console for compilation errors.")
    finally:
        request.unlink(missing_ok=True)
        response.unlink(missing_ok=True)
        temporary.unlink(missing_ok=True)


if __name__ == "__main__":
    main()
