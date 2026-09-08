#!/usr/bin/env bash
set -euo pipefail
voice_source="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
voice_project="${1:-$HOME/projects/idle-rpg}"
dotnet run --project "$voice_source/tools/VoiceTools" -- install "$voice_project" "$voice_source"
