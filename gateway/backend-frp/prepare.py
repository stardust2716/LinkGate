"""Materialize a derived pinned backend copy without modifying shared upstream."""
import argparse
import io
import os
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import tarfile

COMMIT = 'd8cf1800dc1abd7fda1828cab105d7a6b5a50eb1'
MODULE = 'github.com/stardust2716/Redesk/gateway/control-plane'
HERE = Path(__file__).resolve().parent
REPOSITORY = HERE.parent.parent


def run(*args, cwd=None):
    return subprocess.run(args, cwd=cwd, check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE).stdout


def prepare():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--go', type=Path)
    parser.add_argument('--base-only', action='store_true', help='Create a pristine owned patch-development copy only.')
    args = parser.parse_args()
    source, original_target = args.source.resolve(), args.output.absolute()
    target = original_target.resolve()
    allowed = (REPOSITORY / 'artifacts' / 'linkgate-backend-frp').absolute()
    if target == allowed or allowed not in target.parents or target.exists():
        raise ValueError('A new owned artifact child directory is required.')
    for parent in [original_target, *original_target.parents]:
        if parent.is_symlink() or (parent.exists() and parent.is_junction()):
            raise ValueError('Linked artifact path rejected.')
    if run('git', '-C', str(source), 'rev-parse', f'{COMMIT}^{{commit}}').decode().strip() != COMMIT:
        raise ValueError('Pinned source object unavailable.')
    archive = run('git', '-C', str(source), 'archive', '--format=tar', COMMIT)
    target.mkdir(parents=True)
    with tarfile.open(fileobj=io.BytesIO(archive), mode='r:') as tree:
        for member in tree:
            relative = PurePosixPath(member.name)
            known_document_link = member.issym() and member.name == 'CLAUDE.md' and member.linkname == 'AGENTS.md'
            if relative.is_absolute() or '..' in relative.parts or ':' in member.name or '\\' in member.name or not (member.isfile() or member.isdir() or known_document_link):
                raise ValueError('Unsafe archive member.')
            destination = target.joinpath(*relative.parts)
            if member.isdir():
                destination.mkdir(parents=True, exist_ok=True)
            else:
                destination.parent.mkdir(parents=True, exist_ok=True)
                original = tree.getmember('AGENTS.md') if known_document_link else member
                with tree.extractfile(original) as content, destination.open('xb') as output:
                    # The pinned object stores CRLF Go files. Canonicalize only
                    # text line endings before creating the local patch base so
                    # reviewable hunks do not replace entire upstream files.
                    if destination.suffix == '.go':
                        output.write(content.read().replace(b'\r\n', b'\n'))
                    else:
                        shutil.copyfileobj(content, output)
    run('git', 'init', '-q', cwd=target)
    run('git', 'config', 'core.autocrlf', 'false', cwd=target)
    run('git', 'add', '-A', cwd=target)
    run('git', '-c', 'user.name=LinkGate build', '-c', 'user.email=build@example.invalid', 'commit', '-q', '-m', 'Pinned derived source', cwd=target)
    if args.base_only:
        print('Owned pinned base copy created; shared checkout unchanged.')
        return
    if args.go is None:
        raise ValueError('Explicit Go executable required.')
    patch = HERE / 'authority.patch'
    run('git', 'apply', '--check', str(patch), cwd=target)
    run('git', 'apply', str(patch), cwd=target)
    for path in sorted((HERE / 'overlays').rglob('*.go')):
        relative = path.relative_to(HERE / 'overlays')
        destination = target / relative
        if destination.exists():
            raise ValueError('Overlay would replace pinned source.')
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(path, destination)
    authority_relative = os.path.relpath(REPOSITORY / 'gateway' / 'control-plane', target).replace(os.sep, '/')
    run(str(args.go.resolve()), 'mod', 'edit', '-go=1.27.0', '-require=github.com/Microsoft/go-winio@v0.6.4', f'-require={MODULE}@v0.0.0', f'-replace={MODULE}={authority_relative}', cwd=target)
    print('Device-authorized backend source prepared; shared checkout unchanged.')


if __name__ == '__main__':
    prepare()
