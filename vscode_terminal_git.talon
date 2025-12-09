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

