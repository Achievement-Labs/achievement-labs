"""Offline-only emulator verification: synthetic UI, no sign-in or live Xbox calls."""
import subprocess,time,pathlib,json
root=pathlib.Path('android/artifacts');root.mkdir(exist_ok=True)
def run(*args):return subprocess.check_output(args,text=True)
subprocess.run(['adb','install','-r','android/app/build/outputs/apk/debug/app-debug.apk'],check=True)
for page in ['home','library','find','queue','spoof','settings']:
 subprocess.run(['adb','shell','am','force-stop','org.achievementlabs.mobile'],check=True)
 subprocess.run(['adb','shell','am','start','-W','-n','org.achievementlabs.mobile/.MainActivity','--ez','preview','true','--es','page',page],check=True)
 time.sleep(6)
 assert run('adb','shell','pidof','org.achievementlabs.mobile').strip(),'App exited: '+page
 marker=json.loads(run('adb','shell','run-as','org.achievementlabs.mobile','cat','files/preview-rendered.json'))
 assert marker['page']==page and len(marker['text'])>100, 'Web UI did not render: '+page
 with open(root/f'Android-{page}.png','wb') as f:subprocess.run(['adb','exec-out','screencap','-p'],check=True,stdout=f)
# Android's real foreground service must survive Home and Activity replacement.
subprocess.run(['adb','shell','am','force-stop','org.achievementlabs.mobile'],check=True)
subprocess.run(['adb','shell','pm','grant','org.achievementlabs.mobile','android.permission.POST_NOTIFICATIONS'],check=True)
subprocess.run(['adb','shell','am','start','-W','-n','org.achievementlabs.mobile/.MainActivity','--ez','preview','true','--ez','backgroundTest','true'],check=True)
time.sleep(4)
subprocess.run(['adb','shell','input','keyevent','KEYCODE_HOME'],check=True)
time.sleep(3)
a=json.loads(run('adb','shell','run-as','org.achievementlabs.mobile','cat','files/preview-background.json'))
time.sleep(3)
b=json.loads(run('adb','shell','run-as','org.achievementlabs.mobile','cat','files/preview-background.json'))
assert b['ticks']>a['ticks'] and not b['foreground'] and b['active'], 'Background session did not continue'
services=run('adb','shell','dumpsys','activity','services','org.achievementlabs.mobile')
assert 'SessionService' in services and 'isForeground=true' in services, services
# CLEAR_TASK destroys/replaces the Activity while leaving the service/application alive.
subprocess.run(['adb','shell','am','start','-W','-f','0x10008000','-n','org.achievementlabs.mobile/.MainActivity','--ez','preview','true'],check=True)
time.sleep(3)
c=json.loads(run('adb','shell','run-as','org.achievementlabs.mobile','cat','files/preview-background.json'))
assert c['ticks']>b['ticks'] and c['active'], 'Activity replacement lost the running session'
subprocess.run(['adb','shell','am','start','-W','-f','0x10008000','-n','org.achievementlabs.mobile/.MainActivity','--ez','preview','true','--ez','timeoutTest','true'],check=True)
time.sleep(3)
assert 'isForeground=true' not in run('adb','shell','dumpsys','activity','services','org.achievementlabs.mobile'), 'Timeout did not stop foreground service'
print('Offline foreground session survived backgrounding and Activity replacement; timeout paused it.')
log=run('adb','logcat','-d','-s','AndroidRuntime:E')
assert 'FATAL EXCEPTION' not in log,log
print('Six offline Android screens launched without credentials or Xbox requests.')

subprocess.run(['adb','shell','am','force-stop','org.achievementlabs.mobile'],check=True)
subprocess.run(['adb','shell','am','start','-W','-n','org.achievementlabs.mobile/.MainActivity','--ez','preview','true','--ez','xbox360Test','true'],check=True)
for attempt in range(45):
 time.sleep(1)
 marker=subprocess.run(['adb','shell','run-as','org.achievementlabs.mobile','cat','files/xbox360-self-test.json'],capture_output=True,text=True)
 if marker.returncode==0: break
if marker.returncode!=0:
 print(run('adb','logcat','-d','-s','Labs360Test:I','chromium:E','AndroidRuntime:E'),flush=True)
 with open(root/'Android-xbox360-failure.png','wb') as out:subprocess.run(['adb','exec-out','screencap','-p'],stdout=out,check=True)
assert marker.returncode==0, '360 native bridge did not finish'
assert json.loads(marker.stdout).get('passed'),marker.stdout
with open(root/'Android-xbox360.png','wb') as out:subprocess.run(['adb','exec-out','screencap','-p'],stdout=out,check=True)
print('Xbox 360: actual WebView imported a synthetic profile, unlocked, signed, saved and verified native export/backup bytes. No account or network.')


# Inflate the shipped RemoteViews and exercise both native timer bases and next-item transitions.
subprocess.run(['adb','shell','am','force-stop','org.achievementlabs.mobile'],check=True)
subprocess.run(['adb','shell','am','start','-W','-n','org.achievementlabs.mobile/.MainActivity','--ez','preview','true','--ez','widgetTest','true'],check=True)
time.sleep(4)
widget=json.loads(run('adb','shell','run-as','org.achievementlabs.mobile','cat','files/widget-self-test.json'))
assert widget['passed'] and widget['timerTransitions'] and not widget['network'], widget
with open('android/artifacts/Android-widget.png','wb') as out: subprocess.run(['adb','exec-out','screencap','-p'],stdout=out,check=True)
print('Native session widget: elapsed/countdown directions, advancing next achievement and paused timer removal passed. No network.')
