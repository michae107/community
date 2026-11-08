app: rimworld
-
# Plain "look left" and "look right" must match user.talon's global commands exactly, so that
# this more specific context wins while RimWorld is focused.
look left: user.rimworld_look(-1, 0)
look right: user.rimworld_look(1, 0)
look up: user.rimworld_look(0, 1)
look down: user.rimworld_look(0, -1)
look left <number_small>: user.rimworld_look(-1, 0, number_small)
look right <number_small>: user.rimworld_look(1, 0, number_small)
look up <number_small>: user.rimworld_look(0, 1, number_small)
look down <number_small>: user.rimworld_look(0, -1, number_small)

colonist <number_small>: user.rimworld_colonist(number_small)
