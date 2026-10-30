"""Prepare pinned FRP with reviewed metrics, Windows egress and STUN discovery."""
from pathlib import Path
import argparse,subprocess,shutil

COMMIT='d8cf1800dc1abd7fda1828cab105d7a6b5a50eb1'
root=Path(__file__).resolve().parent
p=argparse.ArgumentParser(description=__doc__);p.add_argument('--source',type=Path,required=True);a=p.parse_args()
source=a.source.resolve()
def git(*args):return subprocess.check_output(['git','-C',str(source),*args])
if git('rev-parse','HEAD').decode().strip()!=COMMIT:raise SystemExit('FRP source revision mismatch')
if git('status','--porcelain').strip():raise SystemExit('Source must be pristine; no files were changed')
patch=str(root/'instrumentation.patch')
subprocess.run(['git','-C',str(source),'apply','--check',patch],check=True)
subprocess.run(['git','-C',str(source),'apply',patch],check=True)
target=source/'pkg/linkgatemetrics';target.mkdir()
for name in ['metrics.go','metrics_test.go']:shutil.copyfile(root/name,target/name)
shutil.copytree(root/'egress',source/'pkg/linkgateegress')
for name in (root/'stun').glob('*.go'):shutil.copyfile(name,source/'pkg/nathole'/name.name)
print('Pinned metrics, Windows egress and multi-endpoint STUN source prepared')
