"""Portable installation paths and the Talon-to-controller command protocol."""

import importlib.util
import os
import socket
import tempfile
import types
import unittest
from pathlib import Path
from unittest.mock import Mock, patch


def load_module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class TalonIntegrationTests(unittest.TestCase):
    def setUp(self):
        self.notifications = Mock()
        talon = types.ModuleType("talon")
        talon.Module = lambda: types.SimpleNamespace(action_class=lambda cls: cls)
        talon.app = types.SimpleNamespace(notify=self.notifications)
        adapter_path = Path(__file__).resolve().parents[2] / "mousepods.py"
        with patch.dict("sys.modules", {"talon": talon}):
            self.adapter = load_module("mousepods_adapter_test", adapter_path)

    def test_start_recenters_before_resuming_over_runtime_socket(self):
        with tempfile.TemporaryDirectory() as directory:
            with socket.socket(socket.AF_UNIX, socket.SOCK_DGRAM) as server:
                server.bind(str(Path(directory) / "airpods-mouse.sock"))
                server.settimeout(1)
                with patch.dict(os.environ, {"XDG_RUNTIME_DIR": directory}):
                    self.adapter.Actions.mousepods_command("start")
                    self.adapter.Actions.mousepods_command("pause")
                    self.adapter.Actions.mousepods_command("recenter")
                received = [server.recv(256) for _ in range(4)]
        self.assertEqual(received, [b"recenter", b"resume", b"pause", b"recenter"])
        self.notifications.assert_not_called()

    def test_missing_controller_reports_notification(self):
        with tempfile.TemporaryDirectory() as directory:
            with patch.dict(os.environ, {"XDG_RUNTIME_DIR": directory}):
                self.adapter.Actions.mousepods_command("start")
        self.notifications.assert_called_once()

    def test_unknown_command_is_rejected(self):
        with self.assertRaises(ValueError):
            self.adapter.Actions.mousepods_command("quit")

    def test_service_path_uses_home_specifier_without_username(self):
        installer = load_module(
            "mousepods_installer_test", Path(__file__).resolve().parents[1] / "install.py",
        )
        home = Path("/home/another-user")
        project = home / "Talon commands/.mousepods"
        self.assertEqual(
            installer.service_project_path(project, home), "%h/Talon commands/.mousepods",
        )
        self.assertEqual(
            installer.service_project_path(Path("/opt/mousepods"), home), "/opt/mousepods",
        )


if __name__ == "__main__":
    unittest.main()
