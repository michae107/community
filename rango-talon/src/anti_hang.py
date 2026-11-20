"""Hard-deadline helpers for Rango's clipboard command transport.

This module deliberately has no Talon imports so its timeout behavior can be
regression-tested without starting Talon or a desktop application.

Why everything here goes through killable xclip processes on Linux:
an X11 clipboard read has to be answered by whichever process owns the
clipboard. If that owner is wedged (for example an `xclip -i` serving a large
PNG whose INCR transfer was abandoned by a requestor that died), every reader
blocks forever. Talon's native clipboard calls run on Talon's main thread with
no timeout, so one wedged owner froze Talon completely. A subprocess can be
killed when its deadline passes; Talon's main thread cannot.
"""

import json
import subprocess
import time
from collections.abc import Callable, Sequence
from typing import Any

DEFAULT_RESPONSE_TIMEOUT_SECONDS = 2.0
CLIPBOARD_READ_SLICE_SECONDS = 0.1
MAXIMUM_POLL_SLEEP_SECONDS = 0.05

# Budgets for the clipboard save/set/restore steps around a request. Each is a
# hard cap enforced by killing the helper process.
TARGETS_TIMEOUT_SECONDS = 0.3
SAVE_READ_TIMEOUT_SECONDS = 1.0
WRITE_TIMEOUT_SECONDS = 0.5
WRITE_CONFIRM_TIMEOUT_SECONDS = 0.5

XCLIP_BASE = ("xclip", "-selection", "clipboard")
XCLIP_READ_COMMAND = (*XCLIP_BASE, "-out")

# Only text is preserved. Restoring an image means leaving an `xclip -i`
# daemon serving megabytes over INCR, which is the exact process that wedged
# and froze both Talon and Firefox; in testing it also only served the image
# once before returning empty data.
TEXT_TARGETS = ("UTF8_STRING", "text/plain;charset=utf-8", "text/plain", "STRING")


class ClipboardOwnerNotResponding(RuntimeError):
    """The current clipboard owner did not answer within its deadline."""


def _run_bounded(
    command: Sequence[str],
    timeout: float,
    *,
    input_bytes: bytes | None = None,
    capture: bool = True,
    runner: Callable[..., subprocess.CompletedProcess] = subprocess.run,
) -> subprocess.CompletedProcess | None:
    """Run a helper process; return None if it had to be killed."""
    if timeout <= 0:
        return None
    try:
        return runner(
            list(command),
            input=input_bytes,
            stdout=subprocess.PIPE if capture else subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            stdin=None if input_bytes is not None else subprocess.DEVNULL,
            timeout=timeout,
            check=False,
            close_fds=True,
            start_new_session=True,
        )
    except subprocess.TimeoutExpired:
        return None


def read_process_text(
    deadline: float,
    command: Sequence[str] = XCLIP_READ_COMMAND,
    *,
    clock: Callable[[], float] = time.perf_counter,
    runner: Callable[..., subprocess.CompletedProcess] = subprocess.run,
) -> str | None:
    """Read text through a process that can be killed if its owner wedges."""
    remaining = deadline - clock()
    if remaining <= 0:
        raise TimeoutError("Rango clipboard deadline expired before the read.")

    result = _run_bounded(
        command,
        min(CLIPBOARD_READ_SLICE_SECONDS, remaining),
        runner=runner,
    )
    if result is None or result.returncode != 0:
        return None
    return result.stdout.decode("utf-8", errors="replace")


def read_targets(
    timeout: float = TARGETS_TIMEOUT_SECONDS,
    *,
    runner: Callable[..., subprocess.CompletedProcess] = subprocess.run,
) -> list[str] | None:
    """Return the clipboard's offered targets, [] if empty, None if wedged."""
    result = _run_bounded((*XCLIP_BASE, "-t", "TARGETS", "-out"), timeout, runner=runner)
    if result is None:
        return None
    if result.returncode != 0:
        # xclip exits non-zero when nothing owns the clipboard.
        return []
    return result.stdout.decode("utf-8", errors="replace").split()


def save_clipboard(
    *,
    runner: Callable[..., subprocess.CompletedProcess] = subprocess.run,
) -> tuple[str, bytes] | None:
    """Snapshot the clipboard as (target, data), bounded in time.

    Returns None when there is nothing to save. Raises
    ClipboardOwnerNotResponding when the owner does not answer, so the caller
    can log it; the request can still proceed because taking ownership of the
    clipboard does not require the old owner's cooperation.
    """
    targets = read_targets(runner=runner)
    if targets is None:
        raise ClipboardOwnerNotResponding("Clipboard owner did not answer TARGETS.")
    if not targets:
        return None

    for target in TEXT_TARGETS:
        if target not in targets:
            continue
        result = _run_bounded(
            (*XCLIP_BASE, "-t", target, "-out"),
            SAVE_READ_TIMEOUT_SECONDS,
            runner=runner,
        )
        if result is None:
            raise ClipboardOwnerNotResponding(f"Clipboard owner did not send {target}.")
        if result.returncode == 0:
            return target, result.stdout
    return None


def write_clipboard(
    data: bytes,
    target: str = "UTF8_STRING",
    *,
    runner: Callable[..., subprocess.CompletedProcess] = subprocess.run,
) -> bool:
    """Take clipboard ownership with xclip, which then serves it in the background."""
    result = _run_bounded(
        (*XCLIP_BASE, "-t", target, "-in"),
        WRITE_TIMEOUT_SECONDS,
        input_bytes=data,
        capture=False,
        runner=runner,
    )
    return result is not None and result.returncode == 0


def write_clipboard_text_confirmed(
    text: str,
    sleep: Callable[[float], None],
    *,
    clock: Callable[[], float] = time.perf_counter,
    read_text: Callable[[float], str | None] = read_process_text,
) -> None:
    """Write text and wait until the clipboard really serves it.

    xclip forks before it claims the selection, so its parent can exit before
    the new content is visible. Pressing Rango's hotkey too early would make
    the extension read the previous clipboard instead of the request.
    """
    if not write_clipboard(text.encode("utf-8")):
        raise TimeoutError("Could not write the Rango request to the clipboard.")

    deadline = clock() + WRITE_CONFIRM_TIMEOUT_SECONDS
    while clock() < deadline:
        if read_text(deadline) == text:
            return
        sleep(0.005)
    raise TimeoutError("The Rango request never appeared on the clipboard.")


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
