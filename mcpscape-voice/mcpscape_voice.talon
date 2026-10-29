app: mcpscape500
app: mcpscape727
-
# MCPScape side tabs, e.g. "show inventory", "show stats", "show settings", "show logout"
show {user.mcpscape_tab}: user.mcpscape_tab(mcpscape_tab)

# run mode (the run orb): "toggle run", "run on", "run off"
toggle run: user.mcpscape_command("run toggle")
run on: user.mcpscape_command("run on")
run off: user.mcpscape_command("run off")

# camera: "look north", "look north east", "look southwest"
look {user.mcpscape_direction}: user.mcpscape_look(mcpscape_direction)
