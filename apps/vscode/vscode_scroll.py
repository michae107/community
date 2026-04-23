from talon import Module, actions

mod = Module()


@mod.action_class
class Actions:
    def vscode_scroll_horizontal(direction: str, steps: int):
        """Scroll the focused VSCode editor horizontally by calling scrollLeft/scrollRight N times."""
        command = "scrollRight" if direction == "right" else "scrollLeft"
        for _ in range(steps):
            actions.user.vscode(command)
