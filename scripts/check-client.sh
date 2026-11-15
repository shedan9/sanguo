#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
evidence="$root/build/client-battle"
mkdir -p "$evidence"
"$root/scripts/dotnet.sh" restore client/Sanguo.Client.csproj --locked-mode
"$root/scripts/dotnet.sh" build client/Sanguo.Client.csproj --no-restore
"$root/scripts/godot.sh" --headless --path client --log-file "$evidence/write.log" -- --battle-smoke write "$evidence"
"$root/scripts/godot.sh" --headless --path client --log-file "$evidence/resume.log" -- --battle-smoke resume "$evidence"
cmp "$evidence/final.json" "$evidence/resumed-final.json"
"$root/scripts/godot.sh" --headless --path client --log-file "$evidence/layout.log" -- --battle-layout-smoke
"$root/scripts/godot.sh" --headless --path client --log-file "$evidence/artwork.log" -- --battle-artwork-smoke
"$root/scripts/godot.sh" --headless --path client --log-file "$evidence/g0.log" -- --smoke
python3 - "$evidence" <<'PY'
import pathlib
import sys

root = pathlib.Path(sys.argv[1])
for name, marker in [('write', 'BATTLE_CLIENT_SMOKE_OK'), ('resume', 'BATTLE_CLIENT_RESUME_OK'), ('layout', 'BATTLE_LAYOUT_SMOKE_OK layouts=32'), ('artwork', 'BATTLE_ARTWORK_SMOKE_OK cards=8 portraits=2'), ('g0', 'G0_SMOKE_OK')]:
    log = (root / (name + '.log')).read_text(encoding='utf-8')
    if marker not in log or any(line.startswith('ERROR:') for line in log.splitlines()):
        raise SystemExit('Client check failed; inspect ' + str(root / (name + '.log')))
print('Client button flow, AI, cross-process save/resume and G0 routing passed.')
PY
