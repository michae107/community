app: vscode
-

# Claude dangerous permissions toggle
claude danger:
    user.vscode("workbench.action.openSettings")
    sleep(200ms)
    insert("claudeCode.allowDangerouslySkipPermissions")
    sleep(200ms)
    key(enter)
