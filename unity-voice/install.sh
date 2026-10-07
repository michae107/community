#!/usr/bin/env bash
set -euo pipefail

source_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
project_dir="${1:-/home/michael/projects/UnityVoiceSandbox}"
if [[ ! -f "$project_dir/ProjectSettings/ProjectVersion.txt" ]]; then
    echo "Not a Unity project: $project_dir" >&2
    exit 1
fi
project_dir="$(realpath -- "$project_dir")"
dotnet build "$source_dir/UnityVoice.csproj" --nologo
target="$project_dir/Packages/com.talon.voice"
if [[ -e "$target" || -L "$target" ]]; then
    if [[ "$(readlink -f -- "$target")" != "$source_dir/package" ]]; then
        echo "Refusing to replace an existing package: $target" >&2
        exit 1
    fi
else
    ln -s -- "$source_dir/package" "$target"
fi
echo "Linked $target -> $source_dir/package"
python3 "$source_dir/tools/refresh.py" "$project_dir"
