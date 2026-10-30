"""Check the source-control boundary without printing sensitive matches."""
import argparse
import json
from pathlib import Path
import re
import subprocess
import zipfile

ROOT = Path(__file__).resolve().parents[1]
EXCLUDED = {'private', 'local', 'archive', 'artifacts', 'dist', 'tools', 'upstream', 'downloads'}
EXECUTABLES = {'.exe', '.apk', '.dll', '.so', '.pfx', '.p12', '.jks', '.keystore', '.key', '.pem'}


def git(*args):
    return subprocess.check_output(['git', '-C', str(ROOT), *args])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    names = sorted(set(git('ls-files', '--cached', '--others', '--exclude-standard', '-z').decode().split('\0')) - {''})
    personal = []
    config = ROOT / 'private/deployment.local.json'
    if config.exists():
        values = json.loads(config.read_text(encoding='utf-8-sig'))
        personal = [str(values.get(k, '')) for k in ('rustdeskId', 'serverHost', 'instanceId', 'securityGroupId')]
        personal = [v for v in personal if len(v) >= 7]
    token_file = ROOT / 'private/adaptive-host-token.txt'
    tokens = [token_file.read_text().strip()] if token_file.exists() else []
    tokens = [t for t in tokens if len(t) >= 24]
    findings = []
    markdown = []
    source_zips = []

    def issue(path, rule):
        findings.append({'path': path, 'rule': rule})

    def scan(path, content):
        if any(value.encode() in content for value in tokens):
            issue(path, 'local_credential')
        if re.search(rb'-----BEGIN (?:OPENSSH |RSA |EC )?PRIVATE KEY-----', content):
            issue(path, 'private_key')
        if re.search(rb'\bLTAI[A-Za-z0-9]{16,}\b', content):
            issue(path, 'cloud_access_key')
        if any(value.encode() in content for value in personal):
            issue(path, 'personal_deployment_value')
        if re.search(rb'[A-Z]:\\Users\\(?!Public|Default|<|\$)[A-Za-z0-9_.-]+\\', content):
            issue(path, 'personal_absolute_path')

    for name in names:
        path = ROOT / name
        if not path.is_file():
            issue(name, 'missing_source_file')
            continue
        if path.is_symlink() or path.resolve().is_relative_to(ROOT) is False:
            issue(name, 'source_link')
            continue
        if Path(name).parts[0] in EXCLUDED or path.suffix.lower() in EXECUTABLES:
            issue(name, 'excluded_file_type_or_directory')
        data = path.read_bytes()
        if path.suffix == '.zip':
            if not name.startswith('android/generated-bridge/'):
                issue(name, 'unexpected_source_archive')
                continue
            with zipfile.ZipFile(path) as archive:
                members = [entry for entry in archive.infolist() if not entry.is_dir()]
                for entry in members:
                    if Path(entry.filename).suffix not in {'.rs', '.dart', '.h'}:
                        issue(name, 'non_source_bridge_member')
                    scan(name + ':' + entry.filename, archive.read(entry))
                source_zips.append({'path': name, 'members': len(members)})
            continue
        scan(name, data)
        if path.suffix == '.md':
            markdown.append((name, data.decode('utf-8-sig')))

    for name, content in markdown:
        for match in re.finditer(r'\]\(([^)]+)\)', content):
            target = match.group(1).split(' "', 1)[0].strip('<>')
            if re.match(r'^(https?://|mailto:|#)', target):
                continue
            target = target.split('#', 1)[0]
            if not target or '{{' in target:
                continue
            local = (ROOT / name).parent / target
            if not local.exists():
                issue(name, 'missing_document_link')

    result = {'passed': not findings, 'sourceFiles': len(names), 'sourceOnlyArchives': source_zips,
              'findings': findings, 'privateValuesNotPrinted': True}
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps(result, indent=2))
    raise SystemExit(0 if result['passed'] else 1)


if __name__ == '__main__':
    main()
