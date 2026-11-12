#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s tests/scripts -p 'test_*.py'
python3 scripts/collect-ol-sources.py content/evidence/ol-public-20261112
"$root/scripts/dotnet.sh" restore Sanguo.slnx --locked-mode
"$root/scripts/dotnet.sh" test Sanguo.slnx --no-restore
"$root/scripts/dotnet.sh" run --project tools/Sanguo.ContentValidator --no-restore -- content
"$root/scripts/dotnet.sh" run --project tools/Sanguo.Simulator --no-restore -- content 42
"$root/scripts/dotnet.sh" run --project tools/Sanguo.Simulator --no-restore -- --battle-basic 42 build/basic-battle
"$root/scripts/dotnet.sh" run --project tools/Sanguo.Simulator --no-restore -- --battle-resume build/basic-battle/checkpoint.json build/basic-battle/resumed
cmp build/basic-battle/final.json build/basic-battle/resumed/final.json
