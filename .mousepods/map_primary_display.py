#!/usr/bin/env python3
"""Map MousePods absolute positioning to the primary X11 monitor."""

import os
import re
import subprocess
import time
import urllib.request


def main():
    if not os.environ.get("DISPLAY"):
        return

    monitors = subprocess.check_output(["xrandr", "--current"], text=True)
    primary = re.search(r"^(\S+) connected primary ", monitors, re.MULTILINE)
    if primary is None:
        return

    device = "AirPods Mouse (pointeur)"
    deadline = time.monotonic() + 15.0
    while time.monotonic() < deadline:
        result = subprocess.run(
            ["xinput", "map-to-output", device, primary.group(1)],
            capture_output=True,
            text=True,
        )
        if result.returncode == 0:
            print(f"Mapped {device} to primary monitor {primary.group(1)}.")
            break
        time.sleep(0.2)
    else:
        raise SystemExit(f"Could not map {device}: {result.stderr.strip()}")

    deadline = time.monotonic() + 15.0
    while time.monotonic() < deadline:
        try:
            with urllib.request.urlopen("http://127.0.0.1:8737/api/ping", timeout=1):
                return
        except OSError:
            time.sleep(0.2)
    raise SystemExit("MousePods web interface did not become ready.")


if __name__ == "__main__":
    main()
