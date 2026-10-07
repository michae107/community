"""Hard-deadline helpers for Rango's clipboard command transport.

This module deliberately has no Talon imports so its timeout behavior can be
regression-tested without starting Talon or a desktop application.
"""

import json
import subprocess
import time
from collections.abc import Callable, Sequence
from typing import Any

DEFAULT_RESPONSE_TIMEOUT_SECONDS = 1.0
CLIPBOARD_READ_SLICE_SECONDS = 0.1
MAXIMUM_POLL_SLEEP_SECONDS = 0.05
XCLIP_READ_COMMAND = ("xclip", "-selection", "clipboard", "-out")


def read_process_text(
    deadline: float,
    command: Sequence[str] = XCLIP_READ_COMMAND,
    *,
    clock: Callable[[], float] = time.perf_counter,
    runner: Callable[..., subprocess.CompletedProcess[str]] = subprocess.run,
) -> str | None:
    """Read text through a process that can be killed if its owner wedges."""
    remaining = deadline - clock()
    if remaining <= 0:
        raise TimeoutError("Rango clipboard deadline expired before the read.")

    try:
        result = runner(
            list(command),
            capture_output=True,
            text=True,
            timeout=min(CLIPBOARD_READ_SLICE_SECONDS, remaining),
            check=False,
        )
    except subprocess.TimeoutExpired:
        return None

    return result.stdout if result.returncode == 0 else None


def wait_for_json_response(
    read_text: Callable[[float], str | None],
    sleep: Callable[[float], None],
    *,
    deadline: float,
    clock: Callable[[], float] = time.perf_counter,
) -> dict[str, Any]:
    """Poll until a Rango response arrives, enforcing the deadline on all paths."""
    sleep_time = 0.0005
    initial_raw_text: str | None = None
    captured_initial_value = False

    while True:
        if clock() >= deadline:
            raise TimeoutError("Timed out waiting for Rango response.")

        raw_text = read_text(deadline)
        if not captured_initial_value:
            initial_raw_text = raw_text
            captured_initial_value = True

        if raw_text is not None:
            try:
                message = json.loads(raw_text)
            except (TypeError, ValueError):
                if raw_text == initial_raw_text:
                    raise ValueError("The Rango request message was not valid JSON.")
            else:
                if isinstance(message, dict) and message.get("type") == "response":
                    return message

        remaining = deadline - clock()
        if remaining <= 0:
            raise TimeoutError("Timed out waiting for Rango response.")

        sleep(min(sleep_time, remaining))
        sleep_time = min(sleep_time * 2, MAXIMUM_POLL_SLEEP_SECONDS)
