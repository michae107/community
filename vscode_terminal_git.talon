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
