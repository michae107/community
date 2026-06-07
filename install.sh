#!/usr/bin/env bash
# Bootstrap script for fresh OS installs.
# Runs every per-extension build.sh found under ~/.talon/user/.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

shopt -s nullglob
builds=("$SCRIPT_DIR"/*/build.sh)
shopt -u nullglob

if [ ${#builds[@]} -eq 0 ]; then
    echo "No build.sh scripts found under $SCRIPT_DIR/*/"
    exit 0
fi

for build in "${builds[@]}"; do
    echo ""
    echo "############################################################"
    echo "# Running $(dirname "$build" | sed "s|$SCRIPT_DIR/||")/build.sh"
    echo "############################################################"
    bash "$build"
done

echo ""
echo "==> All extensions installed."
