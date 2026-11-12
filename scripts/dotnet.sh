#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "$0")/env.sh"
if ! command -v dotnet >/dev/null 2>&1; then
    echo "未找到.NET SDK。请安装global.json指定版本，或解压到.tools/dotnet。" >&2
    exit 127
fi
exec dotnet "$@"
