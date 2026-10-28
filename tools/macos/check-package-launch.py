#!/usr/bin/env python3
"""Exercise the downloaded bundle through LaunchServices, outside the build directory."""
from pathlib import Path
import json,os,signal,subprocess,sys,tempfile,time
root=Path(__file__).resolve().parents[2]
out=root/'artifacts/macos-preview/checks';out.mkdir(exist_ok=True)
package_root=Path(os.environ.get('LABS_MAC_OUTPUT',str(root/'artifacts/macos-preview'))).resolve()
arch=os.environ.get('LABS_MAC_RUNTIME','osx-arm64').removeprefix('osx-')
archives=list(package_root.glob('AchievementLabs-macOS-*-'+arch+'.zip'))
assert len(archives)==1, 'Expected one package for the checked architecture'
archive=archives[0]
window_query=out/'windows.swift'
window_query.write_text('''import CoreGraphics
import Foundation
let windows = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String:Any]] ?? []
let matches = windows.filter { window in
    let name = window[kCGWindowOwnerName as String] as? String ?? ""
    let bounds = window[kCGWindowBounds as String] as? [String:Any] ?? [:]
    return ["AchievementLabs","Achievement Labs"].contains(name) && (window[kCGWindowLayer as String] as? Int ?? -1) == 0 && (bounds["Width"] as? Double ?? 0) > 100
}.map { ["pid": $0[kCGWindowOwnerPID as String] ?? 0, "id": $0[kCGWindowNumber as String] ?? 0] }
let bytes = try JSONSerialization.data(withJSONObject:matches)
print(String(data:bytes,encoding:.utf8)!)
''')
query=out/'window-query'
subprocess.run(['swiftc',str(window_query),'-o',str(query)],check=True)
# Hosted CI only: refuse to run against an existing account/configuration.
assert os.environ.get('GITHUB_ACTIONS')=='true', 'This check is restricted to a clean hosted CI runner'
for folder in ['Library/Application Support/AchievementLabs','Library/Application Support/UltimateAchievementLabs','Library/Caches/AchievementLabs','Documents/AchievementLabs','.local/share/AchievementLabs']:
    assert not (Path.home()/folder).exists(), 'Refusing to launch with pre-existing app user data'
with tempfile.TemporaryDirectory(prefix='Achievement Labs downloaded app ') as folder:
    app=Path(folder)/'Achievement Labs.app'
    subprocess.run(['ditto','-x','-k',str(archive),folder],check=True)
    subprocess.run(['codesign','--verify','--strict','--verbose=2',str(app)],check=True)
    missing=[str(p.relative_to(app)) for p in app.rglob('*') if p.is_symlink() and not p.exists()]
    assert not missing,('Broken packaged links',missing)
    launch=subprocess.Popen(['open','-W','-n',str(app)],cwd='/',stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
    windows=[]
    try:
        for attempt in range(30):
            windows=json.loads(subprocess.check_output([str(query)],text=True))
            if windows:break
            if launch.poll() not in (None,0):break
            time.sleep(1)
        assert windows, 'Downloaded .app did not create a visible window through LaunchServices'
        time.sleep(5)
        assert json.loads(subprocess.check_output([str(query)],text=True)), 'Normal startup window disappeared'
    finally:
        for window in windows:
            try:os.kill(window['pid'],signal.SIGTERM)
            except ProcessLookupError:pass
        try:launch.wait(timeout=20)
        except subprocess.TimeoutExpired:launch.terminate()
    subprocess.run(['codesign','--verify','--strict',str(app)],check=True)
    (out/'finder-launch.json').write_text(json.dumps(dict(passed=True,archiveExtracted=True,launchServices=True,normalStartup=True,workingDirectory='/',visibleWindows=len(windows),signatureValidAfterLaunch=True,checkedArchitecture=arch),indent=2))
    print('PASS: downloaded ZIP extraction, intact links/signature and normal Finder-style startup from / with a visible native window.')
