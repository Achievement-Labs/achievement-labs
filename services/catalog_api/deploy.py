"""Deploy a versioned metadata snapshot beside the existing site. Run on the host."""
import argparse,datetime,hashlib,json,pathlib,re,shutil,subprocess

def run(*args):
    return subprocess.run(args,check=True,text=True,capture_output=True).stdout.strip()

def main():
    p=argparse.ArgumentParser();p.add_argument('--stage',required=True,type=pathlib.Path);a=p.parse_args()
    stage=a.stage.resolve(); root=pathlib.Path('/opt/achievement-labs/catalog-api');root.mkdir(exist_ok=True)
    version=root/('versions/'+stage.name);version.parent.mkdir(exist_ok=True)
    if version.exists():raise RuntimeError('Version already exists')
    shutil.copytree(stage,version)
    for path in version.rglob('*'):
        path.chmod(0o755 if path.is_dir() else 0o644)
    config=pathlib.Path('/opt/ual-site/nginx.conf');previous=config.read_bytes()
    before=hashlib.sha256(previous).hexdigest()
    backup=root/('nginx-'+stage.name+'.conf');backup.write_bytes(previous)
    name='achievement-labs-catalog-'+stage.name
    image=run('docker','inspect','licensing','--format','{{.Image}}')
    run('docker','run','-d','--name',name,'--restart','unless-stopped','--network','ual-site_default',
        '--read-only','--user','65534:65534','--cap-drop','ALL','--security-opt','no-new-privileges',
        '--memory','192m','--cpus','1','--pids-limit','64','--workdir','/catalog',
        '-v',str(version)+':/catalog:ro','-e','CATALOG_DATABASE=/catalog/catalog.sqlite',
        '-e','PYTHONDONTWRITEBYTECODE=1',image,'python','-m','services.catalog_api.serve')
    try:
        # Execute the same read-only WSGI application in the container before routing it publicly.
        run('docker','exec',name,'python','-c',"from services.catalog_api.app import create_app; s=[]; r=b''.join(create_app()({'REQUEST_METHOD':'GET','PATH_INFO':'/api/v2/catalog','QUERY_STRING':''},lambda v,h:s.append(v))); assert s==['200 OK'],r; print(r.decode())")
        route='''
    # BEGIN PUBLIC CATALOG API (separate read-only metadata service)
    location ~ ^/api/v2/catalog(?:/|$) {
        limit_req zone=labs_catalog burst=40 nodelay;
        proxy_pass http://'''+name+''':8092;
        proxy_connect_timeout 3s;
        proxy_read_timeout 10s;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $remote_addr;
        limit_req_status 429;
    }
    # END PUBLIC CATALOG API
'''
        text=previous.decode()
        if '# BEGIN PUBLIC CATALOG API' in text:
            text=re.sub(r'    # BEGIN PUBLIC CATALOG API.*?    # END PUBLIC CATALOG API\n', '', text, flags=re.S)
            text=re.sub(r'^limit_req_zone \$binary_remote_addr zone=labs_catalog:10m rate=10r/s;\n', '', text)
            text=text.replace('    \"~^/api/v2/catalog(?:/|$)\" \"\";\n', '')
        text=text.replace('map $uri $ual_cache_control {', 'map $uri $ual_cache_control {\n    \"~^/api/v2/catalog(?:/|$)\" \"\";',1)
        text='limit_req_zone $binary_remote_addr zone=labs_catalog:10m rate=10r/s;\n'+text
        text=text.replace('    location / {',route+'\n    location / {',1)
        if text.count('# BEGIN PUBLIC CATALOG API')!=1:raise RuntimeError('Site route not found')
        if config.read_bytes()!=previous:raise RuntimeError('Proxy changed during staging')
        config.write_bytes(text.encode());run('docker','exec','ual-site','nginx','-t');run('docker','exec','ual-site','nginx','-s','reload')
        report={'container':name,'snapshot':str(version/'catalog.sqlite'),'proxyBackup':str(backup),'previousProxySha256':before,'image':image,'deployedAt':datetime.datetime.now(datetime.timezone.utc).isoformat()}
        (root/'deployment.json').write_text(json.dumps(report,indent=2));print(json.dumps(report))
    except Exception:
        config.write_bytes(previous)
        subprocess.run(['docker','exec','ual-site','nginx','-s','reload'],capture_output=True)
        subprocess.run(['docker','stop',name],capture_output=True)
        raise

if __name__=='__main__':main()
