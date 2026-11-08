# SPDX-License-Identifier: Apache-2.0
"""Remember head movement beyond the visible edges of an X11 desktop."""

import ctypes
import ctypes.util
import re
import subprocess
import time
from dataclasses import dataclass


@dataclass(frozen=True)
class Monitor:
    left: int
    top: int
    width: int
    height: int

    @property
    def right(self):
        return self.left + self.width - 1

    @property
    def bottom(self):
        return self.top + self.height - 1


def project_to_monitors(x, y, monitors):
    """Preserve the horizontal screen choice, including shorter side monitors."""
    visible_x = min(
        (min(max(x, monitor.left), monitor.right) for monitor in monitors),
        key=lambda candidate: abs(candidate - x),
    )
    column = [monitor for monitor in monitors if monitor.left <= visible_x <= monitor.right]
    visible_y = min(
        (min(max(y, monitor.top), monitor.bottom) for monitor in column),
        key=lambda candidate: abs(candidate - y),
    )
    return visible_x, visible_y


class EdgeMemory:
    def __init__(self):
        self.reset()

    def reset(self):
        self.virtual = None
        self.visible = None

    def move(self, dx, dy, actual, monitors):
        # Picking up the physical mouse deliberately establishes a new anchor.
        if self.virtual is None or actual != self.visible:
            self.virtual = tuple(float(value) for value in actual)
        self.virtual = (self.virtual[0] + dx, self.virtual[1] + dy)
        self.visible = project_to_monitors(
            round(self.virtual[0]), round(self.virtual[1]), monitors,
        )
        return self.visible

    @property
    def overflow(self):
        if self.virtual is None:
            return (0.0, 0.0)
        return tuple(virtual - visible for virtual, visible in zip(self.virtual, self.visible))


def read_monitors():
    output = subprocess.check_output(
        ["xrandr", "--listmonitors"], text=True, timeout=2,
    )
    geometries = re.findall(r"(\d+)/\d+x(\d+)/\d+([+-]\d+)([+-]\d+)", output)
    monitors = [Monitor(int(x), int(y), int(width), int(height))
                for width, height, x, y in geometries]
    if not monitors:
        raise OSError("No active monitor geometry reported by xrandr")
    return monitors


class X11EdgePointer:
    def __init__(self):
        library = ctypes.util.find_library("X11")
        if not library:
            raise OSError("libX11 is unavailable")
        self.x11 = ctypes.CDLL(library)
        self.x11.XOpenDisplay.argtypes = [ctypes.c_char_p]
        self.x11.XOpenDisplay.restype = ctypes.c_void_p
        self.x11.XDefaultRootWindow.argtypes = [ctypes.c_void_p]
        self.x11.XDefaultRootWindow.restype = ctypes.c_ulong
        self.x11.XQueryPointer.argtypes = [
            ctypes.c_void_p, ctypes.c_ulong,
            ctypes.POINTER(ctypes.c_ulong), ctypes.POINTER(ctypes.c_ulong),
            ctypes.POINTER(ctypes.c_int), ctypes.POINTER(ctypes.c_int),
            ctypes.POINTER(ctypes.c_int), ctypes.POINTER(ctypes.c_int),
            ctypes.POINTER(ctypes.c_uint),
        ]
        self.x11.XWarpPointer.argtypes = [
            ctypes.c_void_p, ctypes.c_ulong, ctypes.c_ulong,
            ctypes.c_int, ctypes.c_int, ctypes.c_uint, ctypes.c_uint,
            ctypes.c_int, ctypes.c_int,
        ]
        self.x11.XSync.argtypes = [ctypes.c_void_p, ctypes.c_int]
        self.x11.XCloseDisplay.argtypes = [ctypes.c_void_p]
        self.monitors = read_monitors()
        self.display = self.x11.XOpenDisplay(None)
        if not self.display:
            raise OSError("Cannot open the X11 display")
        self.root = self.x11.XDefaultRootWindow(self.display)
        self.memory = EdgeMemory()
        self.next_geometry_check = time.monotonic() + 5

    def position(self):
        windows = [ctypes.c_ulong(), ctypes.c_ulong()]
        coordinates = [ctypes.c_int() for _ in range(4)]
        mask = ctypes.c_uint()
        success = self.x11.XQueryPointer(
            self.display, self.root,
            *(ctypes.byref(value) for value in windows + coordinates),
            ctypes.byref(mask),
        )
        if not success:
            raise OSError("Cannot read the X11 pointer position")
        return coordinates[0].value, coordinates[1].value

    def move(self, dx, dy):
        if time.monotonic() >= self.next_geometry_check:
            self.next_geometry_check = time.monotonic() + 5
            try:
                monitors = read_monitors()
            except (OSError, subprocess.SubprocessError):
                monitors = self.monitors
            if monitors != self.monitors:
                self.monitors = monitors
                self.memory.reset()
        actual = self.position()
        target = self.memory.move(dx, dy, actual, self.monitors)
        if target != actual:
            self.x11.XWarpPointer(self.display, 0, self.root, 0, 0, 0, 0, *target)
            self.x11.XSync(self.display, False)

    def close(self):
        self.x11.XCloseDisplay(self.display)
