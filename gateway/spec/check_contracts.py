from pathlib import Path
import json,re,copy
from jsonschema import Draft202012Validator,FormatChecker,ValidationError
from referencing import Registry,Resource
root=Path(__file__).resolve().parent;repo=root.parents[1];schemas=[json.loads(p.read_text()) for p in root.glob('*.schema.json')]
registry=Registry().with_resources((s['$id'],Resource.from_contents(s)) for s in schemas)
validators={s['$id'].rsplit('/',1)[-1]:Draft202012Validator(s,registry=registry,format_checker=FormatChecker()) for s in schemas}
for s in schemas:Draft202012Validator.check_schema(s)
examples={p.name:json.loads(p.read_text()) for p in (root/'examples').glob('*.json')}
for name,value in examples.items():validators['app-manifest.schema.json' if name.endswith('.app.json') else 'rpc-envelope.schema.json'].validate(value)
passed=0
def reject(schema,value):
 global passed
 try:validators[schema].validate(value)
 except ValidationError:passed+=1;return
 raise AssertionError('Unexpectedly accepted negative case')
m=examples['file-client.app.json'];bad=copy.deepcopy(m);bad['token']='forbidden';reject('app-manifest.schema.json',bad)
bad=copy.deepcopy(m);bad['imports'][0].pop('logicalAuthority');reject('app-manifest.schema.json',bad)
bad=copy.deepcopy(m);bad['exports']=[];bad['imports']=[];reject('app-manifest.schema.json',bad)
bad=copy.deepcopy(examples['file-service.app.json']);bad['exports'][0]['localTarget']['host']='0.0.0.0';reject('app-manifest.schema.json',bad)
bad=copy.deepcopy(examples['file-service.app.json']);bad['exports'][0]['localTarget']['port']=65536;reject('app-manifest.schema.json',bad)
reject('connection-policy.schema.json',{'mode':'direct-only','allowRelay':True})
reject('connection-policy.schema.json',{'mode':'unknown'})
bad=copy.deepcopy(examples['connection-open.request.json']);bad.pop('operationId');reject('rpc-envelope.schema.json',bad)
bad=copy.deepcopy(examples['connection-open.request.json']);bad['requestId']='invalid';reject('rpc-envelope.schema.json',bad)
bad=copy.deepcopy(examples['connection-open.response.json']);bad['error']={};reject('rpc-envelope.schema.json',bad)
missing=[]
files=list((repo/'gateway/docs').glob('*.md'))+[root/'README.md',repo/'gateway/README.md',repo/'README.md']
for p in files:
 text=p.read_text(encoding='utf-8')
 for link in re.findall(r'\]\(([^)]+)\)',text):
  if '://' in link or link.startswith('#'):continue
  dest=link.split('#',1)[0]
  if dest and not (p.parent/dest).exists():missing.append((str(p),link))
assert not missing,missing
summary={'schemasValid':len(schemas),'examplesValid':len(examples),'negativeSchemaCasesRejected':passed,'localLinksVerified':True,'runtimeTestsExecuted':False}

print(json.dumps(summary))
