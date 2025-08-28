---
applyTo: '**'
---

# Talon Syntax Reference

This file contains important syntax patterns and best practices for Talon voice commands.

## Context Headers

### Correct Format
```talon
app: vscode
# Comments can go between context items
win.title: /focus:\[Terminal\]/
-
tag(): terminal

command_name:
    action1()
    action2()
```

### Key Points
- **Tags require parentheses**: Use `tag(): terminal` not `tag: terminal`
- **Separator line required**: The `-` line separates context header from commands
- **Comments allowed**: Comments can go between context items in the header
- **Window title matching**: Use regex patterns like `/focus:\[Terminal\]/` for precise targeting

### Common Context Matchers
- `app: vscode` - Target VS Code application
- `win.title: /pattern/` - Match window title with regex
- `tag(): terminal` - Apply terminal-specific commands
- `mode: command` - Target specific Talon modes

## Command Syntax

### Basic Commands
```talon
command_name: action()
multi_word_command: 
    action1()
    action2()
```

### Insert Text
```talon
semi: insert(";")
semi enter: insert(";\n")
semi enter alt: 
    insert(";")
    key(enter)
```

### Key Combinations
```talon
save: key(ctrl-s)
copy: key(ctrl-c)
paste: key(ctrl-v)
```

## File Organization

### User Commands (Global)
File: `user.talon`
```talon
# No context header = global commands
semi: insert(";")
```

### App-Specific Commands
File: `vscode_terminal_git.talon`
```talon
app: vscode
win.title: /focus:\[Terminal\]/
-
tag(): terminal

git_command: insert("git status")
```

## Common Mistakes to Avoid

1. **Wrong tag syntax**: `tag: terminal` ❌ → `tag(): terminal` ✅
2. **Missing separator**: Forgetting the `-` line between context and commands
3. **Comments in wrong place**: Comments between `app:` and `tag():` can cause parsing errors
4. **Incorrect app names**: Use exact app names (check Talon logs for correct names)

## VS Code Terminal Setup

To use window title matching for VS Code terminal, add this to VS Code settings.json:
```json
{
    "terminal.integrated.tabs.title": "focus:[Terminal]"
}
```

This allows targeting VS Code terminals specifically with `win.title: /focus:\[Terminal\]/`.
