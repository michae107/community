import faulthandler
import json
import logging
import time
from logging.handlers import RotatingFileHandler
from pathlib import Path
from typing import Any

from talon import Module, actions, app, clip

from .anti_hang import (
    DEFAULT_RESPONSE_TIMEOUT_SECONDS,
    ClipboardOwnerNotResponding,
    read_process_text,
    save_clipboard,
    wait_for_json_response,
    write_clipboard,
    write_clipboard_text_confirmed,
)

mod = Module()
_request_in_flight = False

logger = logging.getLogger("rango.command_server")
_log_path = Path.home() / ".talon" / "rango-command.log"
if not any(
    isinstance(handler, RotatingFileHandler)
    and Path(handler.baseFilename) == _log_path
    for handler in logger.handlers
):
    handler = RotatingFileHandler(_log_path, maxBytes=1_000_000, backupCount=2)
    handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(message)s"))
    logger.addHandler(handler)
logger.setLevel(logging.INFO)
logger.propagate = False

# If a request is still running this long after it started, something below
# the Python deadlines is stuck (native code). faulthandler's watchdog thread
# dumps every thread's stack to this file without needing the main thread.
_HANG_TRACE_SECONDS = 6.0
_hang_trace_file = open(Path.home() / ".talon" / "rango-hang-traces.log", "a")


def send_request_and_wait(command: dict) -> Any:
    """Send a command with a hard deadline and no re-entrant request queue."""
    global _request_in_flight
    if _request_in_flight:
        logger.warning("request_rejected reason=already_in_flight")
        raise RuntimeError("A Rango command is already waiting for a response.")

    _request_in_flight = True
    started = time.perf_counter()
    deadline = started + DEFAULT_RESPONSE_TIMEOUT_SECONDS
    action_name = command.get("action", {}).get("name", "unknown")
    logger.info("request_started action=%s", action_name)
    try:
        json_message = json.dumps(command)
        _hang_trace_file.write(f"\n--- watchdog armed for {action_name} at {time.ctime()} ---\n")
        _hang_trace_file.flush()
        faulthandler.dump_traceback_later(
            _HANG_TRACE_SECONDS, repeat=False, file=_hang_trace_file
        )
        if app.platform == "linux":
            response = _linux_round_trip(json_message, deadline)
        else:
            # Talon's native context preserves every clipboard format.
            with clip.revert():
                _set_transient_clipboard_text(json_message)
                actions.user.rango_type_hotkey()
                response = _read_json_response_until(deadline)
        logger.info(
            "request_finished action=%s elapsed_ms=%d",
            action_name,
            round((time.perf_counter() - started) * 1000),
        )
        return response
    except TimeoutError:
        logger.error(
            "request_timed_out action=%s elapsed_ms=%d",
            action_name,
            round((time.perf_counter() - started) * 1000),
        )
        raise
    except Exception:
        logger.exception(
            "request_failed action=%s elapsed_ms=%d",
            action_name,
            round((time.perf_counter() - started) * 1000),
        )
        raise
    finally:
        faulthandler.cancel_dump_traceback_later()
        _request_in_flight = False


def _linux_round_trip(json_message: str, deadline: float) -> dict[str, Any]:
    """Clipboard round trip in which every step is a killable xclip process.

    Talon's native clipboard calls block the main thread forever when the
    clipboard owner stops answering, which froze all of Talon. Here each step
    has its own hard timeout instead.
    """
    try:
        saved = save_clipboard()
    except ClipboardOwnerNotResponding as error:
        # Claiming the clipboard below evicts the wedged owner, so the request
        # can still work; only the old clipboard contents are lost.
        logger.warning("clipboard_owner_not_responding detail=%s", error)
        saved = None

    try:
        write_clipboard_text_confirmed(json_message, actions.sleep)
        actions.user.rango_type_hotkey()
        return _read_json_response_until(deadline)
    finally:
        _restore_clipboard(saved)


def _restore_clipboard(saved: tuple[str, bytes] | None):
    if saved is None:
        restored = write_clipboard(b"")
    else:
        target, data = saved
        restored = write_clipboard(data, target)
    if not restored:
        logger.warning("clipboard_restore_failed")


def set_clipboard_text(text: str):
    """Set clipboard text without risking a main-thread hang on Linux."""
    if app.platform == "linux":
        if not write_clipboard(text.encode("utf-8")):
            logger.warning("clipboard_copy_failed")
        return
    clip.set_text(text)


def _read_json_response_until(deadline: float) -> dict[str, Any]:
    return wait_for_json_response(
        _read_clipboard_text,
        actions.sleep,
        deadline=deadline,
    )


def _read_clipboard_text(deadline: float) -> str | None:
    if app.platform == "linux":
        return read_process_text(deadline)
    return clip.text()


def _set_transient_clipboard_text(text: str):
    """Sets the clipboard to the given text, making it transient"""
    # The try except is only necessary because mime type assignment is only
    # supported in beta. To be removed once it's in public.
    try:
        mime = clip.MimeData()
        mime.text = text
        mime["ExcludeClipboardContentFromMonitorProcessing"] = b"true"
        mime["org.nspasteboard.AutoGeneratedType"] = b"true"
        mime["org.nspasteboard.TransientType"] = b"true"
        clip.set_mime(mime)
    except TypeError:
        clip.set_text(text)
