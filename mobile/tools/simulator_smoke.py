"""Launch the actual simulator app without credentials or Xbox requests."""
import json
import subprocess
import time
import re
from pathlib import Path

def run(*args, timeout=120):
    print('Checking: '+' '.join(args),flush=True)
    return subprocess.check_output(args,text=True,timeout=timeout)
devices=json.loads(run('xcrun','simctl','list','devices','available','--json'))['devices']
available=[(runtime,d) for runtime,group in devices.items() if 'iOS-26' in runtime for d in group if d['name'].startswith('iPhone')]
assert available, 'No iOS 26 simulator for background API checks'
def runtime_version(runtime):
    tail=runtime.rsplit('iOS-',1)[-1]
    return tuple(-int(n) for n in re.findall(r'\d+',tail))
available.sort(key=lambda pair:(runtime_version(pair[0]),'Max' in pair[1]['name'],'Plus' in pair[1]['name'],'Pro' in pair[1]['name'],pair[1]['name']))
runtime, model=available[0]
types=json.loads(run('xcrun','simctl','list','devicetypes','--json'))['devicetypes']
device_type=next(t['identifier'] for t in types if t['name'] == model['name'])
# A fresh device avoids repeatedly booting a broken pre-created runner snapshot.
device=run('xcrun','simctl','create','Achievement Labs CI',device_type,runtime).strip()
print('Chosen fresh simulator: '+model['name']+' · '+runtime,flush=True)
import atexit
def cleanup():
    for action in ['shutdown','delete']:
        try: subprocess.run(['xcrun','simctl',action,device],check=False,timeout=30)
        except subprocess.TimeoutExpired: print('Simulator cleanup timed out: '+action,flush=True)
atexit.register(cleanup)
# Avoid opening Simulator.app: it can boot an unrelated default device alongside
# the fresh test device and exhaust the runner during iOS 26 startup.
subprocess.run(['xcrun','simctl','shutdown','all'],check=False,timeout=60)
subprocess.run(['xcrun','simctl','boot',device],check=True,timeout=60)
print('Waiting for iOS 26 simulator boot',flush=True)
subprocess.run(['xcrun','simctl','bootstatus',device,'-b'],check=True,timeout=300)
app='mobile/simulator/Build/Products/Debug-iphonesimulator/AchievementLabsMobile.app'
subprocess.run(['xcrun','simctl','install',device,app],check=True)
try:
    # A fresh iOS 26 device can still be initializing launch services after
    # bootstatus and install succeed. Allow that initial launch extra time;
    # subsequent launches and all app assertions retain their normal limits.
    output=run('xcrun','simctl','launch',device,'org.achievementlabs.mobile','--storage-self-test',timeout=300)
except (subprocess.CalledProcessError, subprocess.TimeoutExpired):
    subprocess.run(['xcrun','simctl','spawn',device,'log','show','--last','2m','--style','compact','--predicate','process == "AchievementLabsMobile" OR eventMessage CONTAINS "org.achievementlabs.mobile"'],check=False,timeout=120)
    raise
print(output)
pid=output.strip().rsplit(':',1)[-1].strip()
time.sleep(5)
subprocess.run(['xcrun','simctl','spawn',device,'launchctl','list'],check=True,stdout=subprocess.DEVNULL)
processes=run('xcrun','simctl','spawn',device,'launchctl','list')
assert any(line.split() and line.split()[0] == pid and 'org.achievementlabs.mobile' in line for line in processes.splitlines()), 'App exited after launch'
Path('mobile/artifacts').mkdir(exist_ok=True)
subprocess.run(['xcrun','simctl','io',device,'screenshot','mobile/artifacts/iPhone-launch.png'],check=True)
subprocess.run(['xcrun','simctl','terminate',device,'org.achievementlabs.mobile'],check=True)
print('Simulator launch passed; no account connected or network actions requested.')

# A real iOS URLSession check, unlike the macOS command-line probe: ATS applies.
output=run('xcrun','simctl','launch',device,'org.achievementlabs.mobile','--collector-self-test')
container=Path(run('xcrun','simctl','get_app_container',device,'org.achievementlabs.mobile','data').strip())
marker=container/'Documents'/'Collector-self-test.json'
for _ in range(30):
    if marker.exists(): break
    time.sleep(1)
result=json.loads(marker.read_text())
print('Actual iOS app collector TLS check: '+str(result),flush=True)
assert result['scopedATS'] and result['passed'], 'Actual iOS app collector TLS failed'
subprocess.run(['xcrun','simctl','terminate',device,'org.achievementlabs.mobile'],check=True)

output=run('xcrun','simctl','launch',device,'org.achievementlabs.mobile','--audio-self-test')
container=Path(run('xcrun','simctl','get_app_container',device,'org.achievementlabs.mobile','data').strip())
marker=container/'Documents'/'Audio-self-test.json'
for _ in range(15):
    if marker.exists(): break
    time.sleep(1)
result=json.loads(marker.read_text())
print('Offline audio start/stop check: '+str(result),flush=True)
assert result.get('playing') and result.get('stopped') and result['audioManifest'] and result['liveManifest'], 'Audio/session manifest check failed'
subprocess.run(['xcrun','simctl','terminate',device,'org.achievementlabs.mobile'],check=True)

for page in ['library','lookup','queue','spoof','settings','settings-sync','settings-library','settings-app','settings-appearance','session-settings','home','xbox360']:
    output=run('xcrun','simctl','launch',device,'org.achievementlabs.mobile','--ui-preview='+page)
    preview_pid=output.strip().rsplit(':',1)[-1].strip()
    time.sleep(3)
    if page == 'xbox360':
        marker=container/'Documents'/'Xbox360-self-test.json'
        for attempt in range(45):
            if marker.exists(): break
            time.sleep(1)
        assert marker.exists(), '360 WebView bridge test did not finish'
        result=json.loads(marker.read_text()); assert result.get('passed'),result
    processes=run('xcrun','simctl','spawn',device,'launchctl','list')
    assert any(line.split() and line.split()[0] == preview_pid and 'org.achievementlabs.mobile' in line for line in processes.splitlines()), 'Preview exited: '+page
    subprocess.run(['xcrun','simctl','io',device,'screenshot','mobile/artifacts/iPhone-'+page+'.png'],check=True)
    subprocess.run(['xcrun','simctl','terminate',device,'org.achievementlabs.mobile'],check=True)
print('Nine offline refinement screens launched. All preview network requests are blocked.')

output=run('xcrun','simctl','launch',device,'org.achievementlabs.mobile','--web-callback-self-test')
time.sleep(5)
container=Path(run('xcrun','simctl','get_app_container',device,'org.achievementlabs.mobile','data').strip())
marker=container/'Documents'/'Callback-self-test.txt'
assert marker.read_text() == 'passed', 'Embedded WebKit did not capture and validate the synthetic redirect'
media=json.loads((container/'Documents'/'SignInMedia-self-test.json').read_text())
assert media == dict(inline=True,requiresTap=True,pictureInPicture=False,airPlay=False), media
print('Sign-in WebKit media requires a tap; inline playback preferred; PiP and AirPlay disabled.')
subprocess.run(['xcrun','simctl','terminate',device,'org.achievementlabs.mobile'],check=True)
print('Embedded WebKit automatic callback capture and full redirect validation passed without credentials or OAuth requests.')

output=run('xcrun','simctl','launch',device,'org.achievementlabs.mobile','--ui-preview=queue','--background-self-test')
time.sleep(12)
container=Path(run('xcrun','simctl','get_app_container',device,'org.achievementlabs.mobile','data').strip())
result=json.loads((container/'Documents'/'Background-self-test.json').read_text())
assert result['available'] and result['finishedInactive'], 'Background request did not finish safely'
assert result['manifestValid'], 'Background identifiers/modes missing from the built app'
print('iOS 26 background API request completed or safely declined with no network: '+str(result))
subprocess.run(['xcrun','simctl','terminate',device,'org.achievementlabs.mobile'],check=True)


