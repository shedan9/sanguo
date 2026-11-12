#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
"$root/scripts/check.sh"
mkdir -p build/windows build/evidence/g0
"$root/scripts/godot.sh" --headless --path client --export-release "Windows Desktop" "$root/build/windows/Sanguo.exe" 2>&1 | tee build/evidence/g0/windows-export.log
# Godot可能在.NET导出错误时仍返回0；额外核验日志和运行时结构。
python3 "$root/scripts/package-windows.py"
