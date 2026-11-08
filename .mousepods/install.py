#!/usr/bin/env python3
"""Install MousePods from this folder; run with system Python on Linux."""

import argparse
import os
import shlex
import shutil
import subprocess
from pathlib import Path

PROJECT = Path(__file__).resolve().parent


def service_project_path(project, home):
    """Use systemd's home specifier whenever the project lives below home."""
    if project.is_relative_to(home):
        return "%h/" + project.relative_to(home).as_posix().replace("%", "%%")
    return str(project).replace("%", "%%")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--python", default="/usr/bin/python3", help="System Python with Linux Bluetooth support")
    args = parser.parse_args()
    python = PROJECT / ".venv/bin/python"
    uv = shutil.which("uv")
    if not python.exists():
        if uv:
            subprocess.run([uv, "venv", "--python", args.python, str(PROJECT / ".venv")], check=True)
        else:
            subprocess.run([args.python, "-m", "venv", str(PROJECT / ".venv")], check=True)
    requirements = str(PROJECT / "requirements.txt")
    if uv:
        subprocess.run([uv, "pip", "install", "--python", str(python), "-r", requirements], check=True)
    else:
        subprocess.run([str(python), "-m", "pip", "install", "-r", requirements], check=True)

    if not (PROJECT / "config.json").exists():
        shutil.copyfile(PROJECT / "config.example.json", PROJECT / "config.json")

    home = Path.home()
    config_home = Path(os.environ.get("XDG_CONFIG_HOME", home / ".config"))
    data_home = Path(os.environ.get("XDG_DATA_HOME", home / ".local/share"))
    service = config_home / "systemd/user/airpods-mouse.service"
    service.parent.mkdir(parents=True, exist_ok=True)
    project_path = service_project_path(PROJECT, home)
    template = (PROJECT / "airpods-mouse.service").read_text()
    executable_path = project_path.replace("\\", "\\\\").replace('"', '\\"')
    service.write_text(
        template.replace("WorkingDirectory=@PROJECT@", f"WorkingDirectory={project_path}")
        .replace("@PROJECT@", executable_path)
    )

    launcher = home / ".local/bin/mousepods"
    launcher.parent.mkdir(parents=True, exist_ok=True)
    launcher.write_text(
        "#!/bin/sh\nset -eu\nexec "
        + shlex.quote(str(python)) + " "
        + shlex.quote(str(PROJECT / "launch.py")) + ' "$@"\n'
    )
    launcher.chmod(0o755)
    desktop = data_home / "applications/mousepods.desktop"
    desktop.parent.mkdir(parents=True, exist_ok=True)
    desktop_launcher = str(launcher).replace("\\", "\\\\").replace('"', '\\"').replace("%", "%%")
    desktop.write_text(
        "[Desktop Entry]\nType=Application\nName=MousePods\n"
        "Comment=Control the mouse with AirPods motion\n"
        f'Exec="{desktop_launcher}"\n'
        "Icon=input-mouse\nTerminal=false\nCategories=Utility;Accessibility;\n"
    )
    subprocess.run(["systemd-analyze", "--user", "verify", str(service)], check=True)
    subprocess.run(["systemctl", "--user", "daemon-reload"], check=True)
    subprocess.run(["systemctl", "--user", "enable", "airpods-mouse.service"], check=True)
    subprocess.run(["systemctl", "--user", "start", "airpods-mouse.service"], check=True)
    print(f"MousePods installed from {PROJECT}")


if __name__ == "__main__":
    main()
