tag: browser
browser.host: discord.com
and not tag: user.rango_disabled
-

# Send counted scrolling as one request so rapid repeats do not overlap.
more [<number_small> times]:
    steps = number_small or 1
    user.rango_scroll("main", "down", 0.66 * steps)
    sleep(500ms)
less [<number_small> times]:
    steps = number_small or 1
    user.rango_scroll("main", "up", 0.66 * steps)
    sleep(500ms)
