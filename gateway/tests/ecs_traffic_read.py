#!/usr/bin/env python3
"""Read a bounded UTC range from a completed private ECS counter report."""
import argparse,json,os,stat,math
p=argparse.ArgumentParser(description=__doc__)
p.add_argument('--report',required=True)
p.add_argument('--from-ms',required=True,type=int)
p.add_argument('--to-ms',required=True,type=int)
a=p.parse_args()
base='/var/lib/linkgate/diagnostics-v02'
if not os.path.isabs(a.report) or os.path.commonpath([base,os.path.abspath(a.report)])!=base:p.error('Report outside owned diagnostics directory')
directory=os.lstat(os.path.dirname(a.report));assert stat.S_ISDIR(directory.st_mode) and directory.st_uid==0 and stat.S_IMODE(directory.st_mode)==0o700
fd=os.open(a.report,os.O_RDONLY|os.O_NOFOLLOW)
with os.fdopen(fd) as source:
 metadata=os.fstat(source.fileno());assert stat.S_ISREG(metadata.st_mode) and metadata.st_uid==0 and stat.S_IMODE(metadata.st_mode)==0o600 and metadata.st_size<=2*1024*1024
 report=json.load(source)
assert 0<a.from_ms<a.to_ms and a.to_ms-a.from_ms<=900000
assert report['schemaVersion']==1 and report['scope']=='interface-tcp-17400'
report['buckets']=[bucket for bucket in report['buckets'] if a.from_ms//1000<=bucket[0]<math.ceil(a.to_ms/1000)]
report['selectedFromUtcMs']=a.from_ms;report['selectedToUtcMs']=a.to_ms
print(json.dumps(report),flush=True)
