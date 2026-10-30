"""One bounded acceptance of the installable trial; no phone or ECS operations."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import uuid

PACKAGE = Path(sys.argv[1]).resolve()
VERSION = json.loads((PACKAGE / 'manifest.json').read_text(encoding='utf-8-sig'))['version']
assert VERSION in ('0.3.0-preview.1', '0.3.0-preview.2')
REPO = Path(__file__).resolve().parents[2]
RUN = REPO / 'artifacts' / 'trial-acceptance' / uuid.uuid4().hex
RUN.mkdir(parents=True)
INSTALL = RUN / 'installed'
LINUX = '/tmp/linkgate-firsttrial-' + RUN.name
DISTRO = 'Ubuntu-24.04'
OWNED = []
SUMMARY = {'version': VERSION, 'runId': RUN.name, 'passed': False,
           'physicalNetworkAcceptance': False, 'legacyInstallationChanged': False}

def write(path, value):
    Path(path).write_text(json.dumps(value), encoding='utf-8')

def read(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))

def run(args, timeout=30, data=None):
    result = subprocess.run(list(map(str, args)), input=data, capture_output=True,
                            encoding='utf-8', timeout=timeout,
                            creationflags=subprocess.CREATE_NO_WINDOW)
    if result.returncode:
        raise RuntimeError(str(args[0]) + ': ' + result.stderr.strip()[:800] + result.stdout.strip()[:800])
    return result.stdout.strip()

def owner(*args):
    output = run([INSTALL / 'owner/LinkGate.Owner.Console.exe', *args])
    return json.loads(output) if output else None

def uid():
    return str(uuid.uuid4())

def start(args, name, stdin=None):
    log = open(RUN / (name + '.log'), 'w', encoding='utf-8')
    child = subprocess.Popen(list(map(str, args)), stdin=stdin, stdout=log,
                             stderr=subprocess.STDOUT, text=True,
                             creationflags=subprocess.CREATE_NO_WINDOW)
    OWNED.append((child, log))
    return child

def wait_file(path, child, timeout=15):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        if Path(path).exists():
            return read(path)
        if child.poll() is not None:
            raise RuntimeError('Owned process exited before readiness: ' + str(child.returncode))
        time.sleep(.1)
    raise TimeoutError('Owned process readiness')

# This helper never reads the Linux private key. Only CSR/certificates leave Linux.
HELPER = r'''
import json,os,pathlib,shutil,socket,subprocess,sys,time
root=pathlib.Path(sys.argv[1]); command=sys.argv[2]
assert str(root).startswith('/tmp/linkgate-firsttrial-') and len(root.name)==52 and root.parent==pathlib.Path('/tmp')
assert not root.is_symlink()
binary=root/'linkgate-server';config=root/'server.json'
def save(path,text):
 with open(path,'x',encoding='utf-8') as f:f.write(text)
 os.chmod(path,0o600)
if command=='create':
 root.mkdir(mode=0o700);shutil.copyfile(sys.argv[3],binary);os.chmod(binary,0o700)
 for name in ('identity','authority','witness'):(root/name).mkdir(mode=0o700)
 subprocess.run([str(binary),'request',str(root/'identity')],check=True,stdout=subprocess.DEVNULL)
 sockets=[];ports=[]
 for i in range(2):
  s=socket.socket();s.bind(('0.0.0.0',0));sockets.append(s);ports.append(s.getsockname()[1])
 settings=dict(schema=1,identityDirectory=str(root/'identity'),authorityDirectory=str(root/'authority'),witnessDirectory=str(root/'witness'),controlAddress='0.0.0.0:'+str(ports[0]),backendAddress='0.0.0.0:'+str(ports[1]))
 save(config,json.dumps(settings))
 print(json.dumps(dict(csr=(root/'identity/device-request.pem').read_text(),ports=ports)))
elif command=='install':
 values=json.load(sys.stdin)
 for name in ('device-cert.pem','owner-root.pem'):save(root/'identity'/name,values[name])
 save(root/'owner.pem',values['admin'])
 subprocess.run([str(binary),'init',str(config),str(root/'owner.pem')],check=True)
elif command=='serve':
 save(root/'pid',str(os.getpid()));os.execv(str(binary),[str(binary),'serve',str(config)])
elif command=='stop':
 pidfile=root/'pid'
 if pidfile.exists():
  pid=int(pidfile.read_text());exe=pathlib.Path('/proc')/str(pid)/'exe'
  if exe.exists() and os.readlink(exe)==str(binary):os.kill(pid,15)
elif command=='remove':
 pidfile=root/'pid'
 if pidfile.exists():
  exe=pathlib.Path('/proc')/pidfile.read_text()/'exe'
  assert not exe.exists() or os.readlink(exe)!=str(binary)
 shutil.rmtree(root)
'''

def wsl(command, extra=(), data=None):
    return run(['wsl.exe', '-d', DISTRO, '--', 'python3', '-c', HELPER, LINUX, command, *extra], data=data)

def server_set(kind, intent):
    path = RUN / ('intent-' + uid() + '.json')
    write(path, intent)
    op = uid()
    write(RUN / 'last-server-operation.json', {'operationId': op})
    result = owner('server-' + kind + '-set', ADMIN, SETTINGS, SERVER_KEY, path, op)
    recovered = owner('server-operation-status', ADMIN, SETTINGS, SERVER_KEY, op)
    assert result == recovered and result['Kind'] == kind
    return result

def approve(app):
    snapshot = owner('app-show', STATE, app)
    owner('app-approve', STATE, app, snapshot['PendingRevision'], snapshot['DecisionVersion'], snapshot['manifestSha256'], uid())

server_created = False
installed = False
agent = None
server = None
try:
    print('Installing isolated Windows package', flush=True)
    setup = PACKAGE / ('LinkGate-' + VERSION + '-Windows-x64-Setup.exe')
    run([setup, '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-',
         '/DIR=' + str(INSTALL), '/GROUP=LinkGate Trial Acceptance ' + RUN.name,
         '/LOG=' + str(RUN / 'install.log')], timeout=90)
    installed = True
    assert (INSTALL / 'agent/LinkGate.Agent.Console.exe').is_file()
    ADMIN, IDENTITY, ROOT, STATE = (RUN / x for x in ('admin', 'identity', 'owner-root', 'state'))
    owner('owner-create', ROOT)
    device_ids = []
    for directory, name in ((ADMIN, 'admin'), (IDENTITY, 'pc')):
        owner('device-request', directory)
        cert = RUN / (name + '-cert.pem')
        owner('issue', ROOT, directory / 'device-request.pem', cert)
        device_ids.append(owner('device-install', directory, cert, ROOT / 'owner-root.pem')['deviceId'])
    PC = device_ids[1]
    print('Configuring private Linux server and explicit owner authority', flush=True)
    source = (PACKAGE / 'server/linkgate-server').as_posix()
    source = '/mnt/' + source[0].lower() + source[2:]
    server_created = True
    created = json.loads(wsl('create', [source]))
    csr = RUN / 'server-request.pem'; csr.write_text(created['csr'], encoding='utf-8')
    owner('issue', ROOT, csr, RUN / 'server-cert.pem')
    wsl('install', data=json.dumps({'device-cert.pem': (RUN / 'server-cert.pem').read_text(),
        'owner-root.pem': (ROOT / 'owner-root.pem').read_text(), 'admin': (RUN / 'admin-cert.pem').read_text()}))
    server = start(['wsl.exe', '-d', DISTRO, '--', 'python3', '-c', HELPER, LINUX, 'serve'], 'server')
    deadline = time.monotonic() + 10
    while '"ready":true' not in (RUN / 'server.log').read_text(encoding='utf-8'):
        if time.monotonic() > deadline or server.poll() is not None: raise RuntimeError('Server startup failed')
        time.sleep(.1)
    SERVER_KEY = owner('cert-key', RUN / 'server-cert.pem')['deviceId']
    SETTINGS = RUN / 'server.json'
    write(SETTINGS, dict(schema=1,host='127.0.0.1',controlPort=created['ports'][0],backendPort=created['ports'][1],
        serverCertificate=(RUN / 'server-cert.pem').read_text(),stunEndpoints=[]))
    server_set('device', dict(device=PC,expectedRevision='0',roles=dict(owner=False,publish=True,access=True),active=True))
    target = dict(publisherDevice=PC,publisherApp='org.example.trial.publisher',service='sample')
    server_set('service', dict(scope=target,expectedRevision='0',protocol='tcp',authority='',active=True))
    grant_scope = dict(visitorDevice=PC,visitorApp='org.example.trial.client',target=target)
    server_set('grant', dict(scope=grant_scope,expectedRevision='0',active=True))
    runtime = INSTALL / 'runtime/frpc-authority.exe'
    sha = hashlib.sha256(runtime.read_bytes()).hexdigest()
    assert sha == read(INSTALL / 'runtime.lock.json')['sha256'].lower()
    agent = start([INSTALL / 'agent/LinkGate.Agent.Console.exe','serve',STATE,IDENTITY,SETTINGS,SERVER_KEY,runtime,sha,'127.0.0.1','0'], 'agent', subprocess.PIPE)
    bootstrap = STATE / 'agent-bootstrap.json'
    ready = wait_file(bootstrap, agent)
    owner('trust', STATE, IDENTITY, RUN / 'pc-cert.pem', '0','127.0.0.1',ready['peerPort'],PC,uid())
    print('Approving independent sample declarations and exact grants', flush=True)
    sample = INSTALL / 'sample/LinkGate.Sample.exe'
    PUB, CLIENT = RUN / 'publisher', RUN / 'client'
    publisher = start([sample,'publish',bootstrap,PUB], 'publisher')
    pub = wait_file(PUB / 'publisher.json', publisher)
    approve(pub['applicationRef'])
    run([sample,'register',bootstrap,CLIENT,PC])
    approve(read(CLIENT / 'client.json')['applicationRef'])
    run([sample,'request',bootstrap,CLIENT,PC])
    requested = read(CLIENT / 'access.json')['grantRequestId']
    owner('access-propose', STATE, requested, RUN / 'access-proposal.json')
    owner('access-approve', STATE, RUN / 'access-proposal.json', uid())
    owner('grant-propose', STATE, PC, 'org.example.trial.client', pub['applicationRef'],'sample',RUN / 'publisher-grant.json')
    owner('grant-approve', STATE, RUN / 'publisher-grant.json', uid())
    # Allow the publisher's bounded discovery tick to register its real STCP publication.
    time.sleep(2)
    transfers = []
    for path in ('direct','relay'):
        print('Transferring bounded sample through ' + path, flush=True)
        run([sample,'fetch',bootstrap,CLIENT,PC,path], timeout=30)
        transfers.append(read(CLIENT / ('result-' + path + '.json')))
    server_set('grant', dict(scope=grant_scope,expectedRevision='1',active=False))
    rejected = subprocess.run([str(sample),'fetch',str(bootstrap),str(CLIENT),PC,'relay'],
        capture_output=True,text=True,timeout=25,creationflags=subprocess.CREATE_NO_WINDOW)
    assert rejected.returncode != 0
    print('Stopping owned processes and verifying uninstall preservation', flush=True)
    # Capture only children of this Agent; no global process-name termination.
    children = run(['powershell.exe','-NoProfile','-Command',
        f"@(Get-CimInstance Win32_Process -Filter 'ParentProcessId={agent.pid}' | Select-Object -ExpandProperty ProcessId) | ConvertTo-Json -Compress"])
    children = json.loads(children) if children else []
    if isinstance(children,int): children=[children]
    agent.stdin.write('stop\n'); agent.stdin.flush(); assert agent.wait(timeout=15)==0
    for pid in children:
        present = run(['powershell.exe','-NoProfile','-Command', f"[bool](Get-Process -Id {pid} -ErrorAction SilentlyContinue)"])
        assert present == 'False', 'Owned runtime survived Agent stop'
    publisher.terminate();publisher.wait(timeout=5)
    wsl('stop');assert server.wait(timeout=10)==0
    before = hashlib.sha256((IDENTITY / 'device-key.pem').read_bytes()).hexdigest()
    run([INSTALL / 'unins000.exe','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART'],timeout=60)
    deadline = time.monotonic()+15
    while (INSTALL / 'agent/LinkGate.Agent.Console.exe').exists() and time.monotonic()<deadline:time.sleep(.1)
    assert not (INSTALL / 'agent/LinkGate.Agent.Console.exe').exists()
    assert hashlib.sha256((IDENTITY / 'device-key.pem').read_bytes()).hexdigest()==before
    installed = False
    SUMMARY.update(passed=True,installerInstalled=True,uninstallVerified=True,identityPreserved=True,
        ownerRecovery=True,serverGrantRevocationRejectedNewConnection=True,ownedRuntimeChildrenStopped=len(children),transfers=transfers)
finally:
    for child, log in reversed(OWNED):
        if child.poll() is None:
            child.terminate()
            try:child.wait(timeout=5)
            except subprocess.TimeoutExpired:child.kill();child.wait(timeout=5)
        log.close()
    if server_created:
        wsl('stop');time.sleep(.2);wsl('remove')
    if installed and (INSTALL / 'unins000.exe').exists():
        run([INSTALL / 'unins000.exe','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART'],timeout=60)
    write(RUN / 'summary.json', SUMMARY)
    print(RUN / 'summary.json', flush=True)
