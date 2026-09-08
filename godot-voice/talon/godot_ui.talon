app: godot_voice_editor
-
# Rango's established interaction vocabulary; no bare-letter/catch-all rule.
# Explicit Godot aliases; shared "go down/up" and "scroll down/up" still work.
up: key(up)
down: key(down)
click <user.letters>: user.godot_voice_ui("click", letters)
mouse click <user.letters>: user.godot_voice_ui("click", letters)
double click <user.letters>: user.godot_voice_ui("double_click", letters)
flick <user.letters>: user.godot_voice_ui("click", letters)
focus <user.letters>: user.godot_voice_ui("focus", letters)
menu <user.letters>: user.godot_voice_ui("menu", letters)
hover <user.letters>: user.godot_voice_ui("hover", letters)
change <user.letters>: user.godot_voice_ui("change", letters)
paste to <user.letters>: user.godot_voice_ui_text(clip.text(), letters)
insert <user.text> to <user.letters>:
    user.godot_voice_ui_text(text, letters)
enter <user.text> to <user.letters>:
    user.godot_voice_ui_text(text, letters, true)
set <user.letters> to <user.godot_voice_number>:
    user.godot_voice_ui_number(letters, godot_voice_number)
increase <user.letters> [<number_small>]: user.godot_voice_ui_step(letters, 1, number_small or 1)
decrease <user.letters> [<number_small>]: user.godot_voice_ui_step(letters, -1, number_small or 1)
upper <user.letters> [<number_small>]: user.godot_voice_ui_scroll(letters, -1, number_small or 1)
downer <user.letters> [<number_small>]: user.godot_voice_ui_scroll(letters, 1, number_small or 1)
hints on: user.godot_voice_command("ui.hints.show")
hints off: user.godot_voice_command("ui.hints.hide")
hints (toggle | switch): user.godot_voice_command("ui.hints.toggle")
hints refresh: user.godot_voice_command("ui.hints.reset")
hints all: user.godot_voice_ui_scope("all")
hints inspector: user.godot_voice_ui_scope("inspector")
hints signals: user.godot_voice_ui_scope("signals")
hints groups: user.godot_voice_ui_scope("groups")
