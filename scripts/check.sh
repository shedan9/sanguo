#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
"$root/scripts/dotnet.sh" restore Sanguo.slnx --locked-mode
"$root/scripts/dotnet.sh" test Sanguo.slnx --no-restore
"$root/scripts/dotnet.sh" run --project tools/Sanguo.ContentValidator --no-restore -- content
"$root/scripts/dotnet.sh" run --project tools/Sanguo.Simulator --no-restore -- content 42
