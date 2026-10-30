"""Download pinned official FRP assets; extract only named regular files."""
import argparse
import concurrent.futures
import hashlib
import io
import json
from pathlib import Path
import shutil
import subprocess
import tarfile
import time
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
LOCK = json.loads((ROOT / 'gateway/runtime.lock.json').read_text(encoding='utf-8'))
CACHE = ROOT / 'downloads/linkgate'


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def prepare(asset):
    CACHE.mkdir(parents=True, exist_ok=True)
    path = CACHE / asset['file']
    if not path.exists() or digest(path) != asset['sha256']:
        url = LOCK['repository'] + '/releases/download/' + LOCK['tag'] + '/' + asset['file']
        temporary = path.with_suffix(path.suffix + '.part')
        for attempt in range(1):
            try:
                if shutil.which('curl'):
                    subprocess.run(['curl', '--fail', '--silent', '--show-error', '--http1.1', '--location',
                                    '--connect-timeout', '15', '--max-time', '180',
                                    '--output', str(temporary), url], check=True, timeout=190)
                else:
                    with urllib.request.urlopen(url, timeout=60) as response, temporary.open('wb') as output:
                        shutil.copyfileobj(response, output)
                if digest(temporary) != asset['sha256']:
                    raise ValueError('Official runtime SHA-256 mismatch')
                temporary.replace(path)
                break
            except Exception:
                temporary.unlink(missing_ok=True)
                raise RuntimeError('Runtime download unavailable. Review network/security events; no security settings were changed.') from None
    directory = CACHE / ('frp_0.71.1_' + asset['platform'])
    directory.mkdir(exist_ok=True)
    names = ['LICENSE', 'frpc.exe', 'frps.exe'] if path.suffix == '.zip' else ['LICENSE', 'frpc', 'frps']
    if path.suffix == '.zip':
        with zipfile.ZipFile(path) as archive:
            for name in names:
                key = directory.name + '/' + name
                (directory / name).write_bytes(archive.read(key))
    else:
        with tarfile.open(path, 'r:gz') as archive:
            for name in names:
                member = archive.getmember(directory.name + '/' + name)
                if not member.isfile():
                    raise ValueError('Expected ordinary runtime file')
                (directory / name).write_bytes(archive.extractfile(member).read())
    if asset['platform'] == 'android_arm64':
        destination = ROOT / 'gateway/android/app/src/main/jniLibs/arm64-v8a'
        destination.mkdir(parents=True, exist_ok=True)
        shutil.copy2(directory / 'frpc', destination / 'libfrpc.so')
    if asset['platform'] == 'windows_amd64':
        destination = ROOT / 'gateway/windows/runtime'
        destination.mkdir(parents=True, exist_ok=True)
        shutil.copy2(directory / 'frpc.exe', destination / 'frpc.exe')
    return dict(platform=asset['platform'], verified=True,
                files={p.name:digest(p) for p in directory.iterdir() if p.is_file()})


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--platform', action='append', choices=[a['platform'] for a in LOCK['assets']])
    args = parser.parse_args()
    selected = [a for a in LOCK['assets'] if not args.platform or a['platform'] in args.platform]
    with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:
        futures = [pool.submit(prepare, asset) for asset in selected]
        for future in concurrent.futures.as_completed(futures):
            print(json.dumps(future.result()), flush=True)
