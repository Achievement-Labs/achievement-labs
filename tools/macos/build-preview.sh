#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$ROOT"
OUT="${LABS_MAC_OUTPUT:-$ROOT/artifacts/macos-preview}"
APP="$OUT/Achievement Labs.app"
RUNTIME="${LABS_MAC_RUNTIME:-osx-arm64}"
case "$RUNTIME" in osx-arm64|osx-x64) ;; *) echo 'Unsupported Mac runtime' >&2; exit 1;; esac
ARCH="${RUNTIME#osx-}"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
python3 tools/xbox360/prepare.py

dotnet publish src/AchievementLabs.Desktop/AchievementLabs.Desktop.csproj -c Release -r "$RUNTIME" --self-contained true -p:LabsMacOS=true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -p:NuGetAudit=false -p:LabsXbox360SigningFile="$ROOT/tools/xbox360/shared/signing.dat" -o "$OUT/publish"
# Keep real runtime files in Resources, where managed DLLs are bundle resources.
# The native bootstrap loader is also a real file beside the executable.
python3 - "$OUT/publish" "$APP" <<'PY'
from pathlib import Path
import shutil,sys
published,app=map(Path,sys.argv[1:])
target=app/'Contents/Resources/App';target.mkdir(parents=True,exist_ok=True)
for entry in published.iterdir():
    destination=target/entry.name
    if entry.is_dir():shutil.copytree(entry,destination,dirs_exist_ok=True,symlinks=False)
    else:shutil.copy2(entry,destination,follow_symlinks=True)
host=bytearray((target/'AchievementLabs').read_bytes())
old=b'AchievementLabs.dll\0';new=b'../Resources/App/AchievementLabs.dll\0'
# .NET apphost reserves 1024 bytes for its relative managed-entrypoint path.
assert host.count(old)==1, 'Expected a unique apphost entrypoint'
offset=host.index(old)
assert host[offset+len(old):offset+1024]==bytes(1024-len(old)), 'Unexpected apphost path reservation'
host[offset:offset+len(new)]=new
(app/'Contents/MacOS/AchievementLabs').write_bytes(host)
shutil.copy2(target/'libhostfxr.dylib',app/'Contents/MacOS/libhostfxr.dylib')
(target/'AchievementLabs').unlink()
assert not any(p.is_symlink() for p in app.rglob('*'))
PY
chmod +x "$APP/Contents/MacOS/AchievementLabs"
mkdir -p "$APP/Contents/Helpers"
SWIFT_ARCH="$ARCH"
if [ "$ARCH" = x64 ]; then SWIFT_ARCH=x86_64; fi
xcrun swiftc -parse-as-library -swift-version 5 -O -target "${SWIFT_ARCH}-apple-macos14.0" tools/macos/MicrosoftSignIn.swift -o "$APP/Contents/Helpers/MicrosoftSignIn"
xcrun swiftc -emit-library -parse-as-library -swift-version 5 -O -target "${SWIFT_ARCH}-apple-macos14.0" tools/macos/MacNotifications.swift -o "$APP/Contents/Resources/App/libLabsMacNotifications.dylib"
python3 - "$APP" <<'PY'
from pathlib import Path
import plistlib,sys
app=Path(sys.argv[1]);info={'CFBundleName':'Achievement Labs','CFBundleDisplayName':'Achievement Labs','CFBundleIdentifier':'org.achievementlabs.desktop.macos.preview','CFBundleExecutable':'AchievementLabs','CFBundlePackageType':'APPL','CFBundleShortVersionString':'0.1.1','CFBundleVersion':'1','LSMinimumSystemVersion':'14.0','NSHighResolutionCapable':True,'NSLocalNetworkUsageDescription':'Find your Windows Achievement Labs app and pair securely on your local network.','CFBundleIconFile':'AchievementLabs.icns','NSHumanReadableCopyright':'Achievement Labs; project: Achievement-Labs/achievement-labs'}
(app/'Contents/Info.plist').write_bytes(plistlib.dumps(info))
PY
ICONSET="$OUT/AchievementLabs.iconset"
mkdir -p "$ICONSET"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" src/AchievementLabs.App/Assets/achievement-labs-icon.png --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
  double=$((size*2));sips -z "$double" "$double" src/AchievementLabs.App/Assets/achievement-labs-icon.png --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AchievementLabs.icns"
cat > "$OUT/entitlements.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?><!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd"><plist version="1.0"><dict><key>com.apple.security.cs.allow-jit</key><true/></dict></plist>
PLIST
# Ad-hoc signing is for local testing only; Developer ID/notarization is a later release step.
while IFS= read -r -d '' binary; do
  if file -b "$binary" | grep -q 'Mach-O'; then codesign --force --sign - --options runtime --entitlements "$OUT/entitlements.plist" "$binary"; fi
done < <(find "$APP/Contents/Helpers" "$APP/Contents/Resources/App" "$APP/Contents/MacOS" -type f -print0)
codesign --force --sign - --options runtime --entitlements "$OUT/entitlements.plist" "$APP"
codesign --verify --strict --verbose=2 "$APP"
python3 - "$APP" <<'PY'
from pathlib import Path
import sys
app=Path(sys.argv[1]);bad=[p.name for p in app.rglob('*') if p.is_file() and (p.suffix.lower() in {'.con','.profile','.keystore','.p12','.labslink','.cs','.axaml','.pdb','.exe'} or 'keyvault' in p.name.lower())]
assert not bad,bad
print('Verified Mac bundle excludes Windows EXEs, source/debug files and personal-profile/credential stores.')
PY
cp docs/macos-preview.md "$OUT/README.md"
ditto -c -k --sequesterRsrc --keepParent "$APP" "$OUT/AchievementLabs-macOS-0.1.1.1-${ARCH}.zip"
(cd "$OUT" && shasum -a 256 AchievementLabs-macOS-0.1.1.1-${ARCH}.zip > SHA256.txt)
echo "Built self-contained $RUNTIME Mac preview: $OUT"
