more: edit.page_down()
less: edit.page_up()
look right: user.mouse_scroll_right(25)
look left: user.mouse_scroll_left(25)
semi: insert(";")
lend:
    key(end)
    insert(";")
bracer:
    key(end)
    key(space)
    insert("{")
    key(enter)
Boole:
    insert("bool")
not empty:
    insert(" != null")
is empty:
    insert(" == null")
paste:
    key(ctrl-v)