#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "$0")/env.sh"
if [[ -n "${SANGUO_GODOT:-}" ]]; then
    executable="$SANGUO_GODOT"
elif [[ -x "$sanguo_root/.tools/Godot_mono.app/Contents/MacOS/Godot" ]]; then
    executable="$sanguo_root/.tools/Godot_mono.app/Contents/MacOS/Godot"
elif command -v godot >/dev/null 2>&1; then
    executable="$(command -v godot)"
else
    echo "未找到Godot .NET。请设置SANGUO_GODOT为4.7.2 .NET版可执行文件路径。" >&2
    exit 127
fi
exec "$executable" "$@"
