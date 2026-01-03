app: vscode
# Looks for special string in window title.
# NOTE: This requires you to add a special setting to your VSCode settings.json
# See [our vscode docs](./README.md#terminal)
win.title: /focus:\[Terminal\]/
-
tag(): terminal

git ad:
    insert("git add .")
    key(enter)
    insert("git status")
    key(enter)
git status:
    insert("git status")
    key(enter)
less git status:
    insert("git status | less")
    key(enter)
git commit: 
    insert("git commit -m \"\"")
    key(left)
git push: 
    insert("git push")
    key(enter)
git pull: 
    insert("git pull")
    key(enter)

git auto push:
    insert("git add . && git commit -m \"update\" && git push")
    key(enter)

git if:
    insert("git diff")
    key(enter)

perforce reconcile:
    insert("p4 reconcile -m && p4 opened")
    key(enter)

perforce opened:
    insert("p4 opened")
    key(enter)

perforce opened file:
    insert("p4 opened > ~/Downloads/opened ; code ~/Downloads/opened")
    key(enter)

perforce difference:
    insert("p4 diff")
    key(enter)

perforce difference file:
    insert("p4 diff > ~/Downloads/diff ; code ~/Downloads/diff")
    key(enter)

perforce resolve:
    insert("p4 resolve")
    key(enter)

perforce submit:
    insert("p4 submit -d \"\"")
    key(left)

perforce sink:
    insert("p4 sync")
    key(enter)

# Plastic SCM (cm) commands - mirrors git commands
plastic status:
    insert("cm status")
    key(enter)

plastic ad:
    insert("cm add --all")
    key(enter)
    insert("cm status")
    key(enter)

plastic diff:
    insert("cm diff")
    key(enter)

plastic if:
    insert("cm diff")
    key(enter)

plastic commit:
    insert("cm checkin -m \"\"")
    key(left)

plastic check in:
    insert("cm checkin -m \"\"")
    key(left)

plastic push:
    insert("cm push")
    key(enter)

plastic pull:
    insert("cm pull")
    key(enter)

plastic update:
    insert("cm update")
    key(enter)

plastic log:
    insert("cm log")
    key(enter)

plastic history:
    insert("cm history")
    key(enter)

plastic branch:
    insert("cm branch")
    key(enter)

plastic branches:
    insert("cm find branch")
    key(enter)

plastic switch:
    insert("cm switch ")

plastic merge:
    insert("cm merge ")

plastic undo:
    insert("cm undo --all")
    key(enter)

plastic undo file:
    insert("cm undo ")

plastic remove:
    insert("cm remove ")

plastic shelve:
    insert("cm shelve")
    key(enter)

plastic unshelve:
    insert("cm unshelve")
    key(enter)

plastic auto push:
    insert("cm add --all && cm checkin -m \"update\" && cm push")
    key(enter)

plastic clone:
    insert("cm clone ")

plastic clone clipboard:
    insert("cm clone ")
    edit.paste()
    key(enter)

