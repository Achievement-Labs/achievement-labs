#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$ROOT"
OUT="${LABS_LINUX_OUTPUT:-$ROOT/artifacts/linux-preview}"
APP="$OUT/AchievementLabs-linux-x64"
mkdir -p "$APP"
python3 tools/xbox360/prepare.py
dotnet publish src/AchievementLabs.Desktop/AchievementLabs.Desktop.csproj -c Release -r linux-x64 --self-contained true -p:LabsLinux=true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -p:NuGetAudit=false -p:LabsXbox360SigningFile="$ROOT/tools/xbox360/shared/signing.dat" -o "$APP"
test -f "$APP/Helpers/microsoft-signin.py"
chmod +x "$APP/AchievementLabs"
cp docs/linux-preview.md "$APP/README.md"
python3 - "$APP" <<'PY'
from pathlib import Path
import sys
root=Path(sys.argv[1])
bad=[str(p.relative_to(root)) for p in root.rglob('*') if p.is_file() and (p.suffix.lower() in {'.exe','.pdb','.con','.profile','.labslink','.keystore','.p12'} or p.name in {'auth.json','events_token.txt','signing.dat','synthetic.dat'})]
assert not bad,bad
assert (root/'AchievementLabs').read_bytes()[:4]==b'\x7fELF'
print('PASS: Linux ELF package excludes Windows helpers, personal profiles and loose credential/signing files.')
PY
tar -czf "$OUT/AchievementLabs-linux-0.1.0.1-x64.tar.gz" -C "$OUT" AchievementLabs-linux-x64
(cd "$OUT" && sha256sum AchievementLabs-linux-0.1.0.1-x64.tar.gz > SHA256.txt)
