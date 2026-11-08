#!/usr/bin/env python3
"""Open the MousePods interface, starting its user service when needed."""

import subprocess
import webbrowser


def main():
    subprocess.run(["systemctl", "--user", "start", "airpods-mouse.service"], check=True)
    webbrowser.open("http://127.0.0.1:8737/#overview")


if __name__ == "__main__":
    main()
