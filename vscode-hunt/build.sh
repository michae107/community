#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
EXT_NAME="local.hunt"
VSCODE_EXT_DIR="$HOME/.vscode/extensions/$EXT_NAME"

cd "$SCRIPT_DIR"
npm install
npm test

if [ -L "$VSCODE_EXT_DIR" ]; then
    rm "$VSCODE_EXT_DIR"
elif [ -e "$VSCODE_EXT_DIR" ]; then
    echo "Refusing to replace non-symlink path: $VSCODE_EXT_DIR" >&2
    exit 1
fi

ln -s "$SCRIPT_DIR" "$VSCODE_EXT_DIR"
echo "Linked $VSCODE_EXT_DIR -> $SCRIPT_DIR"
echo "Reload VS Code to activate Hunt."
