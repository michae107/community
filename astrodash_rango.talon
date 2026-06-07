title: /Astrodash/
-

# Treat the Astrodash Electron window like a browser page so rango-talon
# commands such as "labels" and "click <hint>" are active.
tag(): browser

tab next:
    user.astrodash_tab_next()

tab (last | previous):
    user.astrodash_tab_previous()

go tab <number>:
    user.astrodash_focus_tab(number)
