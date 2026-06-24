app: vscode
-

look right: user.vscode_scroll_horizontal("right", 40)
look left: user.vscode_scroll_horizontal("left", 40)

# Toggle read-only (session-only) for the focused file
make read only: user.vscode("workbench.action.files.toggleActiveEditorReadonlyInSession")

# Claude dangerous permissions toggle
claude danger:
    user.vscode("workbench.action.openSettings")
    sleep(200ms)
    insert("claudeCode.allowDangerouslySkipPermissions")
    sleep(200ms)
    key(enter)

build:
    user.vscode("workbench.action.tasks.runTask")
    sleep(200ms)
    insert("build")
    key(enter)
