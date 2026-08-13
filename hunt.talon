app: vscode
-
# Focus whichever Hunt screen is showing: search box or results.
bar hunt: user.vscode("hunt.focus")

hunt <user.letters>: user.run_rpc_command("hunt.openResult", letters)
hunt side <user.letters>: user.run_rpc_command("hunt.openResultToSide", letters)
hunt copy <user.letters>: user.run_rpc_command("hunt.copyResult", letters)
hunt next: user.run_rpc_command("hunt.nextResult")
hunt last: user.run_rpc_command("hunt.previousResult")
hunt refresh: user.run_rpc_command("hunt.refresh")
hunt clear: user.run_rpc_command("hunt.clear")
hunt case: user.run_rpc_command("hunt.toggleCase")
hunt word: user.run_rpc_command("hunt.toggleWholeWord")
hunt expression: user.run_rpc_command("hunt.toggleRegex")
hunt include: user.run_rpc_command("hunt.setInclude")
hunt exclude: user.run_rpc_command("hunt.setExclude")
hunt focus: user.run_rpc_command("hunt.focus")
