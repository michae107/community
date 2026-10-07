#!/usr/bin/env bash
set -euo pipefail

source_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
project_dir="${1:-/home/michael/projects/UnityVoiceSandbox}"
godot_dir="${2:-/home/michael/projects/idle-rpg}"
if [[ ! -f "$project_dir/ProjectSettings/ProjectVersion.txt" ]]; then
    echo "Not a Unity project: $project_dir" >&2
    exit 1
fi
project_dir="$(realpath -- "$project_dir")"
if [[ -f "$project_dir/Library/ScriptAssemblies/Unity.RenderPipelines.Core.Runtime.dll" &&
      -f "$project_dir/Library/ScriptAssemblies/Unity.RenderPipelines.Universal.Runtime.dll" ]]; then
    dotnet build "$source_dir/MapEditor.csproj" --nologo "-p:UnityProjectPath=$project_dir"
else
    echo "First install: Unity will download URP and compile the linked package. Rerun this script afterward for the standalone build check."
fi
dotnet run --project "$source_dir/tools/ImportAssets" -- "$godot_dir" "$project_dir"
for package in com.idlerpg.map-editor com.talon.voice; do
    package_source="$source_dir/package"
    if [[ "$package" == com.talon.voice ]]; then
        package_source="$(realpath -- "$source_dir/../unity-voice/package")"
    fi
    target="$project_dir/Packages/$package"
    if [[ -e "$target" || -L "$target" ]]; then
        if [[ "$(readlink -f -- "$target")" != "$package_source" ]]; then
            echo "Refusing to replace an existing package: $target" >&2
            exit 1
        fi
    else
        ln -s -- "$package_source" "$target"
    fi
    echo "Linked $target -> $package_source"
done
python3 "$source_dir/../unity-voice/tools/refresh.py" "$project_dir"
