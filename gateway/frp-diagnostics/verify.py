"""Verify the build checkout contains exactly the reviewed diagnostics changes."""
import argparse,hashlib,subprocess
from pathlib import Path

root=Path(__file__).resolve().parent
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--source',type=Path,required=True)
args=parser.parse_args()
source=args.source.resolve()
def git(*args):return subprocess.check_output(['git','-C',str(source),*args],stderr=subprocess.DEVNULL)
if git('rev-parse','HEAD').decode().strip()!='d8cf1800dc1abd7fda1828cab105d7a6b5a50eb1':raise SystemExit('Source revision mismatch')
patch=git('diff','HEAD').replace(b'\r\n',b'\n')
if patch!=(root/'instrumentation.patch').read_bytes().replace(b'\r\n',b'\n'):raise SystemExit('Instrumentation patch mismatch')
changed=set(git('diff','--name-only','HEAD').decode().splitlines())
if changed!={'client/connector.go','client/proxy/proxy.go','client/proxy/xtcp.go','client/visitor/stcp.go','client/visitor/xtcp.go','pkg/nathole/discovery.go','pkg/nathole/nathole.go','pkg/nathole/utils.go'}:raise SystemExit('Unexpected tracked source changes')
untracked=set(git('ls-files','--others','--exclude-standard').decode().splitlines())
expected={'pkg/linkgatemetrics/metrics.go','pkg/linkgatemetrics/metrics_test.go'}|{'pkg/linkgateegress/'+p.name for p in (root/'egress').glob('*.go')}|{'pkg/nathole/'+p.name for p in (root/'stun').glob('*.go')}
if untracked!=expected:raise SystemExit('Unexpected untracked source')
for name in ('metrics.go','metrics_test.go'):
    if (source/'pkg/linkgatemetrics'/name).read_text()!= (root/name).read_text():raise SystemExit('Metrics source mismatch')
for name in (root/'egress').glob('*.go'):
    if (source/'pkg/linkgateegress'/name.name).read_text()!=name.read_text():raise SystemExit('Egress source mismatch')
for name in (root/'stun').glob('*.go'):
    if (source/'pkg/nathole'/name.name).read_text()!=name.read_text():raise SystemExit('STUN source mismatch')
print('Exact diagnostics source verified')
