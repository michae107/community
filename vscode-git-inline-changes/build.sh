#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
EXT_NAME="local.git-inline-changes"
VSCODE_EXT_DIR="$HOME/.vscode/extensions/$EXT_NAME"

cd "$SCRIPT_DIR"

echo "==> Installing dependencies..."
npm install

echo "==> Compiling TypeScript..."
npx tsc -p ./

echo "==> Symlinking to VS Code extensions directory..."
if [ -L "$VSCODE_EXT_DIR" ]; then
    echo "    Removing existing symlink..."
    rm "$VSCODE_EXT_DIR"
elif [ -d "$VSCODE_EXT_DIR" ]; then
    echo "    Removing existing directory..."
    rm -rf "$VSCODE_EXT_DIR"
fi

ln -s "$SCRIPT_DIR" "$VSCODE_EXT_DIR"
echo "    Linked: $VSCODE_EXT_DIR -> $SCRIPT_DIR"

echo ""
echo "==> Done! Restart VS Code (or run 'Developer: Reload Window') to load the extension."
