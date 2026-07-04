app: vscode
-
# Speak: "agent refactor this into a helper function"
agent <user.prose>$: user.run_rpc_command("voiceClaude.run", prose)

# Open the input box instead of speaking the instruction
agent prompt: user.run_rpc_command("voiceClaude.run")
