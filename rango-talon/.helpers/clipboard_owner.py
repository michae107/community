"""
Owns the X11 clipboard with the formats given on stdin, until another program copies.

Browsers only write the clipboard on a click they count as the user's (voice control
clicks don't count) and can't put files on it at all, so the asset server copies here.

Input: JSON {"formats": {"<target>": "<base64 data>", ...}} on stdin, or in the file
named by the first argument (read, then deleted, so the caller never waits on a pipe).
Output (stdout): "ok" once the clipboard is ours, or "error: ..." and a non-zero exit.

Large data (images) goes out in INCR chunks, as the ICCCM asks.
"""

import base64
import json
import os
import sys

from Xlib import X, Xatom, display
from Xlib.protocol import event

CHUNK = 64 * 1024


def main() -> int:
    try:
        if len(sys.argv) > 1:
            with open(sys.argv[1], encoding="utf-8") as source:
                request = json.load(source)
            os.unlink(sys.argv[1])
        else:
            request = json.load(sys.stdin)
        formats = {name: base64.b64decode(data) for name, data in request["formats"].items()}
        if not formats:
            raise ValueError("no formats")
    except Exception as error:
        print(f"error: bad input ({error})", flush=True)
        return 2

    try:
        x = display.Display()
    except Exception as error:
        print(f"error: no X display ({error})", flush=True)
        return 1

    atom = lambda name: x.intern_atom(name)  # noqa: E731
    clipboard = atom("CLIPBOARD")
    targets_atom = atom("TARGETS")
    timestamp_atom = atom("TIMESTAMP")
    incr_atom = atom("INCR")
    data = {atom(name): payload for name, payload in formats.items()}

    window = x.screen().root.create_window(0, 0, 1, 1, 0, X.CopyFromParent)
    # A real timestamp for the ownership (from a property change on our own window).
    window.change_attributes(event_mask=X.PropertyChangeMask)
    window.change_property(atom("GG_ASSET_CLIPBOARD"), Xatom.STRING, 8, b"")
    while True:
        stamp_event = x.next_event()
        if stamp_event.type == X.PropertyNotify:
            owned_since = stamp_event.time
            break
    window.change_attributes(event_mask=0)

    window.set_selection_owner(clipboard, owned_since)
    if x.get_selection_owner(clipboard) != window:
        print("error: could not take the clipboard", flush=True)
        return 1
    print("ok", flush=True)
    sys.stdout.close()

    # Requestor window id -> (property, type, remaining bytes) for INCR transfers.
    transfers: dict[int, tuple[int, int, bytes]] = {}

    def reply(request_event, property_atom: int) -> None:
        notify = event.SelectionNotify(
            time=request_event.time,
            requestor=request_event.requestor,
            selection=request_event.selection,
            target=request_event.target,
            property=property_atom,
        )
        request_event.requestor.send_event(notify)

    while True:
        current = x.next_event()

        if current.type == X.SelectionClear and current.atom == clipboard:
            return 0

        if current.type == X.SelectionRequest:
            requestor = current.requestor
            target = current.target
            # Obsolete clients leave the property as None: use the target.
            property_atom = current.property or target
            if target == targets_atom:
                offered = [targets_atom, timestamp_atom, *data.keys()]
                requestor.change_property(property_atom, Xatom.ATOM, 32, offered)
            elif target == timestamp_atom:
                requestor.change_property(property_atom, Xatom.INTEGER, 32, [owned_since])
            elif target in data:
                payload = data[target]
                if len(payload) <= CHUNK:
                    requestor.change_property(property_atom, target, 8, payload)
                else:
                    requestor.change_attributes(event_mask=X.PropertyChangeMask)
                    requestor.change_property(property_atom, incr_atom, 32, [len(payload)])
                    transfers[requestor.id] = (property_atom, target, payload)
            else:
                property_atom = X.NONE
            reply(current, property_atom)
            x.flush()
            continue

        # INCR: each time the requestor deletes the property, the next chunk (empty when done).
        if (
            current.type == X.PropertyNotify
            and current.state == X.PropertyDelete
            and current.window.id in transfers
        ):
            property_atom, target, remaining = transfers[current.window.id]
            if current.atom != property_atom:
                continue
            chunk, rest = remaining[:CHUNK], remaining[CHUNK:]
            current.window.change_property(property_atom, target, 8, chunk)
            if chunk:
                transfers[current.window.id] = (property_atom, target, rest)
            else:
                del transfers[current.window.id]
                current.window.change_attributes(event_mask=0)
            x.flush()


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:  # The display went away, or a requestor vanished mid-transfer.
        try:
            print(f"error: {error}", flush=True)
        except Exception:
            pass
        sys.exit(1)
