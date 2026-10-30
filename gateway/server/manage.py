"""Install one isolated, owner-verified FRP service. Python 3.6+ / Linux only.

Preflight never writes. No firewall, existing relay, SSH, Nginx or client changes.
Private files are deliberately retained by rollback for inspection and recovery.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shutil
import socket
import stat
import subprocess
import sys
import time

if os.name == 'posix':
    import grp
    import pwd
else:
    grp = pwd = None

VERSION = '0.1.0'
FRP_VERSION = '0.71.1'
OWNER = 'linkgate-frps-v1'
BASE = Path('/opt/linkgate')
CONFIG = Path('/etc/linkgate')
DATA = Path('/var/lib/linkgate')
UNIT = Path('/etc/systemd/system/linkgate-frps.service')
UNIT_NAME = UNIT.name
PORT = 17400


class Failure(Exception):
    pass


def require(ok, message):
    if not ok:
        raise Failure(message)


def digest(path):
    h = hashlib.sha256()
    with path.open('rb') as source:
        for block in iter(lambda: source.read(65536), b''):
            h.update(block)
    return h.hexdigest()


def ordinary(path, directory=False, root_owned=False, private=False):
    info = path.lstat()
    require(stat.S_ISDIR(info.st_mode) if directory else stat.S_ISREG(info.st_mode),
            'Expected ordinary directory/file, not a symlink: ' + str(path))
    if root_owned:
        require(info.st_uid == 0 and info.st_mode & 0o022 == 0,
                'Expected root ownership without group/world writes: ' + str(path))
    if private:
        require(info.st_mode & 0o007 == 0, 'Private source is accessible to other users.')
    return info


def command(args, timeout=30):
    result = subprocess.run(args, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            universal_newlines=True, timeout=timeout, check=False)
    # Never print arbitrary subprocess output: configuration/parser errors can
    # include credentials. The command name, not its arguments, is safe here.
    require(result.returncode == 0, 'Command failed: ' + args[0])
    return result.stdout.strip()


def unit_text():
    return '''[Unit]
Description=LinkGate isolated FRP coordinator and fallback relay
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=linkgate
Group=linkgate
WorkingDirectory=/var/lib/linkgate
ExecStart=/opt/linkgate/v0.1.0/frps -c /etc/linkgate/frps.toml
Restart=on-failure
RestartSec=5
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadOnlyPaths=/etc/linkgate /opt/linkgate
ReadWritePaths=/var/lib/linkgate
RestrictSUIDSGID=true
UMask=0077

[Install]
WantedBy=multi-user.target
'''


def validate_config(path):
    """Accept only this release's generated, flat TOML contract.

    Do not maintain a second general TOML parser on old Python hosts. The
    installer intentionally rejects valid-but-unreviewed FRP features.
    """
    data = path.read_text(encoding='utf-8')
    require(len(data.encode('utf-8')) <= 16384, 'Server configuration is too large.')
    values = {}
    for row in data.splitlines():
        row = row.strip()
        if not row or row.startswith('#'):
            continue
        require('=' in row and not row.startswith('['), 'Unsupported TOML construct.')
        name, value = (part.strip() for part in row.split('=', 1))
        require(name not in values, 'Duplicate server configuration key.')
        try:
            values[name] = json.loads(value)
        except ValueError:
            raise Failure('Unsupported server configuration value.')
    expected = {
        'bindAddr': '0.0.0.0', 'bindPort': PORT, 'proxyBindAddr': '127.0.0.1',
        'transport.tls.force': True,
        'transport.tls.certFile': (CONFIG / 'server.crt').as_posix(),
        'transport.tls.keyFile': (CONFIG / 'server.key').as_posix(),
        'auth.method': 'token', 'log.to': 'console', 'log.level': 'info',
        'log.disablePrintColor': True, 'detailedErrorsToClient': False,
    }
    require(set(values) == set(expected) | {'auth.token'}, 'Unknown or missing server configuration field.')
    require(all(type(values[k]) == type(v) and values[k] == v for k, v in expected.items()),
            'Server configuration does not match this isolated release.')
    require(isinstance(values['auth.token'], str) and re.fullmatch(r'[0-9a-f]{64}', values['auth.token']),
            'Expected a separately generated 32-byte FRP token.')
    return values


def inspect_package(package, config):
    for directory in (package, config):
        ordinary(directory, directory=True, root_owned=True)
    for name in ('frps', 'SHA256SUMS.txt'):
        ordinary(package / name, root_owned=True)
    sums = (package / 'SHA256SUMS.txt').read_text(encoding='ascii').splitlines()
    require(len(sums) == 1 and re.fullmatch(r'[0-9a-f]{64}  frps', sums[0]),
            'Expected a one-entry SHA256SUMS.txt for frps.')
    require(digest(package / 'frps') == sums[0][:64], 'frps SHA-256 does not match the reviewed package.')
    with (package / 'frps').open('rb') as source:
        header = source.read(20)
    require(header[:6] == b'\x7fELF\x02\x01' and header[18:20] == b'\x3e\x00',
            'Expected Linux x86_64 ELF frps.')
    for name in ('frps.toml', 'server.crt', 'server.key'):
        ordinary(config / name, root_owned=True, private=True)
    validate_config(config / 'frps.toml')
    cert = (config / 'server.crt').read_bytes()
    key = (config / 'server.key').read_bytes()
    require(b'-----BEGIN CERTIFICATE-----' in cert and b'PRIVATE KEY-----' in key,
            'Expected PEM server certificate and private key.')
    return {name: digest(path) for name, path in (
        ('frps', package / 'frps'), ('frps.toml', config / 'frps.toml'),
        ('server.crt', config / 'server.crt'), ('server.key', config / 'server.key'))}


def properties():
    output = command(['systemctl', 'show', UNIT_NAME, '--no-pager',
                      '-p', 'FragmentPath', '-p', 'DropInPaths', '-p', 'ExecStart',
                      '-p', 'MainPID', '-p', 'ActiveState', '-p', 'User',
                      '-p', 'Group', '-p', 'WorkingDirectory', '-p', 'NeedDaemonReload'])
    return dict(row.split('=', 1) for row in output.splitlines() if '=' in row)


def write_state(state):
    state['updatedUtc'] = time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())
    target = BASE / 'install-state.json'
    temporary = BASE / 'install-state.json.new'
    fd = os.open(str(temporary), os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, 'w') as stream:
        json.dump(state, stream, indent=2)
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(str(temporary), str(target))


def inspect_owned(state, partial=False):
    require(state.get('owner') == OWNER and state.get('version') == VERSION and
            state.get('frpVersion') == FRP_VERSION and state.get('port') == PORT,
            'Unrecognized installation state.')
    for root in (BASE, CONFIG, DATA):
        if partial and not root.exists():
            continue
        ordinary(root, directory=True, root_owned=(root != DATA))
    if not partial:
        account = pwd.getpwnam('linkgate')
        require(DATA.stat().st_uid == account.pw_uid, 'Service data belongs to a different user.')
    files = {
        'frps': BASE / ('v' + VERSION) / 'frps',
        'frps.toml': CONFIG / 'frps.toml', 'server.crt': CONFIG / 'server.crt',
        'server.key': CONFIG / 'server.key',
    }
    require(set(state.get('files', {})) == set(files), 'Installation file manifest differs.')
    for name, path in files.items():
        if partial and not path.exists():
            continue
        ordinary(path, root_owned=True)
        require(digest(path) == state['files'][name], 'Owned file changed; review before proceeding: ' + name)
    if not partial or UNIT.exists():
        ordinary(UNIT, root_owned=True)
        require(digest(UNIT) == hashlib.sha256(unit_text().encode('utf-8')).hexdigest(),
                'Service unit differs from the recorded release.')
    if not partial:
        p = properties()
        require(p.get('FragmentPath') == str(UNIT) and p.get('DropInPaths') == '' and
                p.get('User') == 'linkgate' and p.get('Group') == 'linkgate' and
                p.get('WorkingDirectory') == str(DATA) and p.get('NeedDaemonReload') == 'no',
                'Loaded service ownership or unit configuration differs.')
        loaded = re.findall(r'(?:^\{\s*|;\s*)argv\[\]=([^;]*)', p.get('ExecStart', ''))
        require([value.strip() for value in loaded] ==
                [str(BASE / ('v' + VERSION) / 'frps') + ' -c ' + str(CONFIG / 'frps.toml')],
                'Loaded service command differs from the exact owned unit.')
    return files


def read_state():
    ordinary(BASE / 'install-state.json', root_owned=True, private=True)
    return json.loads((BASE / 'install-state.json').read_text())


def listener_inodes(tables, port):
    """Linux may expose Go's IPv4 wildcard listener as a dual-stack tcp6 row."""
    found = set()
    for table in tables:
        for row in table.splitlines()[1:]:
            fields = row.split()
            if len(fields) <= 9 or fields[3] != '0A':
                continue
            host, _, raw_port = fields[1].rpartition(':')
            if host in ('0' * 8, '0' * 32) and raw_port.upper() == '{:04X}'.format(port):
                found.add('socket:[{}]'.format(fields[9]))
    return found


def verify_active(state):
    inspect_owned(state)
    p = properties()
    require(p['ActiveState'] == 'active', 'Owned service is not active.')
    pid = int(p['MainPID'])
    require(pid > 1 and os.readlink('/proc/{}/exe'.format(pid)) == str(BASE / ('v' + VERSION) / 'frps'),
            'Running service executable differs.')
    tables = [path.read_text() for path in (Path('/proc/net/tcp'), Path('/proc/net/tcp6')) if path.exists()]
    inodes = listener_inodes(tables, PORT)
    owned_socket = False
    for descriptor in Path('/proc/{}/fd'.format(pid)).iterdir():
        try:
            owned_socket = owned_socket or os.readlink(str(descriptor)) in inodes
        except FileNotFoundError:
            pass
    require(owned_socket, 'TCP listener does not belong to the owned service process.')
    with socket.create_connection(('127.0.0.1', PORT), timeout=3):
        pass


def fresh_preflight(package, config):
    hashes = inspect_package(package, config)
    for path in (BASE, CONFIG, DATA, UNIT):
        require(not os.path.lexists(str(path)), 'Target already exists; inspect it instead of reinstalling: ' + str(path))
    require(not Path('/etc/systemd/system/' + UNIT_NAME + '.d').exists(), 'Existing service drop-in requires review.')
    require(command(['systemctl', 'list-unit-files', UNIT_NAME, '--no-legend', '--no-pager']) == '',
            'A service unit with this name already exists.')
    for table, label in ((pwd, 'user'), (grp, 'group')):
        try:
            (table.getpwnam if label == 'user' else table.getgrnam)('linkgate')
        except KeyError:
            continue
        raise Failure('Existing linkgate ' + label + ' requires ownership review.')
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
        listener.bind(('0.0.0.0', PORT))
    return hashes


def install(args):
    package, config = args.package.resolve(), args.config.resolve()
    hashes = fresh_preflight(package, config)
    print(json.dumps(dict(preflightPassed=True, readOnly=not args.apply, port=PORT,
                          protocols=['tcp'], version=VERSION, frpVersion=FRP_VERSION)))
    if not args.apply:
        return
    os.umask(0o077)
    BASE.mkdir(mode=0o755)
    os.chmod(str(BASE), 0o755)
    state = dict(owner=OWNER, version=VERSION, frpVersion=FRP_VERSION,
                 port=PORT, phase='prepared', files=hashes)
    write_state(state)
    try:
        command(['useradd', '--system', '--user-group', '--home-dir', str(DATA),
                 '--create-home', '--shell', '/sbin/nologin', 'linkgate'])
        account = pwd.getpwnam('linkgate')
        os.chmod(str(DATA), 0o700)
        CONFIG.mkdir(mode=0o750)
        os.chmod(str(CONFIG), 0o750)
        os.chown(str(CONFIG), 0, account.pw_gid)
        target = BASE / ('v' + VERSION)
        target.mkdir(mode=0o755)
        os.chmod(str(target), 0o755)
        for source, dest, mode in (
            (package / 'frps', target / 'frps', 0o755),
            (config / 'frps.toml', CONFIG / 'frps.toml', 0o640),
            (config / 'server.crt', CONFIG / 'server.crt', 0o640),
            (config / 'server.key', CONFIG / 'server.key', 0o640),
        ):
            with source.open('rb') as incoming, dest.open('xb') as output:
                shutil.copyfileobj(incoming, output)
            os.chown(str(dest), 0, account.pw_gid)
            os.chmod(str(dest), mode)
        require(FRP_VERSION == command(['runuser', '-u', 'linkgate', '--', str(target / 'frps'), '--version']),
                'frps runtime version differs from the locked release.')
        command(['runuser', '-u', 'linkgate', '--', str(target / 'frps'), 'verify', '-c', str(CONFIG / 'frps.toml')])
        with UNIT.open('x', encoding='utf-8', newline='\n') as output:
            output.write(unit_text())
        os.chmod(str(UNIT), 0o644)
        command(['systemctl', 'daemon-reload'])
        command(['systemctl', 'enable', '--now', UNIT_NAME])
        for _ in range(20):
            try:
                verify_active(state)
                break
            except (Failure, OSError):
                time.sleep(0.5)
        verify_active(state)
        state['phase'] = 'active'
        write_state(state)
        print(json.dumps(dict(installed=True, active=True, phase='active', credentialsPrinted=False,
                              firewallChanged=False, existingServicesChanged=False)))
    except Exception:
        state['phase'] = 'failed'
        write_state(state)
        # An exact owned unit may be disabled after a failed start. Do not alter
        # an unknown replacement and do not erase recovery data.
        if UNIT.exists() and digest(UNIT) == hashlib.sha256(unit_text().encode()).hexdigest():
            subprocess.run(['systemctl', 'disable', '--now', UNIT_NAME], stdout=subprocess.PIPE,
                           stderr=subprocess.PIPE, universal_newlines=True, timeout=30)
        raise


def health(_args):
    state = read_state()
    require(state.get('phase') == 'active', 'Installation is not marked active; inspect recovery state.')
    verify_active(state)
    print(json.dumps(dict(ready=True, ownedServiceActive=True, version=VERSION,
                          frpVersion=FRP_VERSION, tcpPort=PORT, credentialsPrinted=False,
                          actualPeerDirectnessVerified=False)))


def recoverable_state(state):
    require(state.get('phase') in ('prepared', 'failed'),
            'Only an incomplete installation may use recover; use health for an active service.')


def recover(args):
    state = read_state()
    recoverable_state(state)
    # A missing/modified file or unit never gets reconstructed from a package.
    # Recovery is only a start/acceptance operation on the complete original
    # installation with the same private material and loaded systemd settings.
    inspect_owned(state)
    p = properties()
    require(p.get('ActiveState') in ('active', 'inactive', 'failed'),
            'Service is in a transitional state; inspect before recovery.')
    if p.get('ActiveState') == 'active':
        verify_active(state)
    else:
        require(int(p.get('MainPID', '0')) == 0, 'Incomplete service still has an unexpected running process.')
    print(json.dumps(dict(recoveryPreflight=True, readOnly=not args.apply,
                          phase=state['phase'], completeOwnedInstallation=True,
                          credentialsRetained=True)))
    if not args.apply:
        return
    command(['systemctl', 'enable', '--now', UNIT_NAME])
    for _ in range(20):
        try:
            verify_active(state)
            break
        except (Failure, OSError):
            time.sleep(0.5)
    verify_active(state)
    state['phase'] = 'active'
    write_state(state)
    print(json.dumps(dict(recovered=True, phase='active', credentialsRetained=True,
                          filesReinstalled=False, firewallChanged=False,
                          existingServicesChanged=False)))


def rollback(args):
    state = read_state()
    require(state.get('phase') in ('prepared', 'failed', 'active', 'stopped'), 'Unsupported recovery phase.')
    inspect_owned(state, partial=state.get('phase') in ('prepared', 'failed', 'stopped'))
    print(json.dumps(dict(rollbackPreflight=True, readOnly=not args.apply, credentialsRetained=True)))
    if not args.apply:
        return
    if UNIT.exists():
        p = properties()
        require(p.get('FragmentPath') == str(UNIT) and p.get('DropInPaths') == '',
                'Loaded unit differs; rollback refused.')
        command(['systemctl', 'disable', '--now', UNIT_NAME])
        stopped = properties()
        require(stopped.get('ActiveState') in ('inactive', 'failed') and int(stopped.get('MainPID', '0')) == 0,
                'Owned service is not stopped.')
        # Preserve the exact unit for recovery. No recursive delete is used.
        saved = BASE / 'linkgate-frps.service.stopped'
        require(not saved.exists(), 'Saved unit already exists; review recovery state.')
        os.rename(str(UNIT), str(saved))
        command(['systemctl', 'daemon-reload'])
    state['phase'] = 'stopped'
    write_state(state)
    print(json.dumps(dict(stopped=True, unitRemoved=True, binaryAndCredentialsRetained=True,
                          firewallChanged=False, existingServicesChanged=False)))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='mode')
    p = commands.add_parser('install')
    p.add_argument('--package', type=Path, required=True)
    p.add_argument('--config', type=Path, required=True)
    p.add_argument('--apply', action='store_true')
    commands.add_parser('health')
    p = commands.add_parser('recover')
    p.add_argument('--apply', action='store_true')
    p = commands.add_parser('rollback')
    p.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    require(args.mode is not None, 'Choose install, health, recover or rollback.')
    require(platform.system() == 'Linux' and platform.machine() == 'x86_64', 'Linux x86_64 is required.')
    require(os.geteuid() == 0, 'Root is required for complete ownership verification.')
    for name in ('systemctl', 'runuser', 'useradd'):
        require(shutil.which(name), 'Missing prerequisite: ' + name)
    {'install': install, 'health': health, 'recover': recover, 'rollback': rollback}[args.mode](args)


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        # Expected failure text is from controlled guards; arbitrary exceptions
        # can contain command arguments or configuration values.
        print('LinkGate: ' + (str(error) if isinstance(error, Failure) else type(error).__name__), file=sys.stderr)
        sys.exit(1)
