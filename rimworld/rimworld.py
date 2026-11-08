"""Voice commands for RimWorld.

The commands are C# methods in Voice.cs, next to this file. rimworld-exec, from
~/projects/rimworld-agent, keeps that file loaded inside the game through the Agent Bridge mod
and calls its methods, so nothing is compiled per command. It reloads the file by itself when
the game restarts or the file changes.

Each command's time, from the call to the game's answer, is printed to the Talon log.
"""

import subprocess
import time
from pathlib import Path

from talon import Module, app, cron

mod = Module()
mod.apps.rimworld = "app.name: RimWorldLinux"

DOTNET = Path.home() / ".dotnet/dotnet"
RIMWORLD_EXEC = Path.home() / "projects/rimworld-agent/cli/bin/Release/net9.0/rimworld-exec.dll"
VOICE_FILE = Path(__file__).with_name("Voice.cs")

TIMEOUT_SECONDS = 60
POLL_MILLISECONDS = 10

# rimworld-exec exit status when the game is not running.
GAME_NOT_REACHABLE = 2


def rimworld_exec(args: list[str], label: str, quiet_if_no_game: bool = False):
    """Runs rimworld-exec without blocking Talon, and shows any message it prints."""
    process = subprocess.Popen(
        [str(DOTNET), str(RIMWORLD_EXEC), *args],
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
    )
    started = time.perf_counter()
    job = None

    def check():
        if process.poll() is None:
            if time.perf_counter() - started > TIMEOUT_SECONDS:
                cron.cancel(job)
                process.kill()
                process.communicate()
                app.notify("RimWorld", "The game did not answer.")
            return
        cron.cancel(job)
        milliseconds = (time.perf_counter() - started) * 1000
        print(f"rimworld: {label} took {milliseconds:.0f} ms")
        # communicate() also closes the pipe.
        message = process.communicate()[0].strip()
        if quiet_if_no_game and process.returncode == GAME_NOT_REACHABLE:
            return
        # The bridge reports "(done, no output)" when the code returns nothing to say.
        if message and message != "(done, no output)":
            app.notify("RimWorld", message)

    job = cron.interval(f"{POLL_MILLISECONDS}ms", check)


def call(method: str, *args):
    rimworld_exec(
        ["--call", str(VOICE_FILE), f"Voice.{method}", *[str(a) for a in args]],
        f"{method}({', '.join(str(a) for a in args)})",
    )


def preload():
    """Loads Voice.cs into the game ahead of the first command, if the game is running."""
    rimworld_exec(["--load", str(VOICE_FILE)], "load Voice.cs", quiet_if_no_game=True)


app.register("ready", preload)


@mod.action_class
class Actions:
    def rimworld_look(dx: int, dz: int, count: int = 1):
        """Pans the RimWorld camera by a third of the screen per count, in direction (dx, dz)."""
        call("Look", dx * count, dz * count)

    def rimworld_colonist(n: int):
        """Selects the nth RimWorld colonist in colonist bar order and moves the camera to them."""
        call("Colonist", n)
