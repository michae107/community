title: /Changer/
-

# Changer implements Rango's clipboard/hotkey protocol directly, so the
# standard rango-talon commands work inside its Tauri webview.
tag(): browser

# Changer exposes diff-line navigation through its local Rango command bridge.
^go <number>$: user.rango_go_to_line(number)
