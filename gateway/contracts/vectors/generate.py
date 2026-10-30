"""Generate public synthetic conformance inputs; expected outcomes are explicit cases, not a parser oracle."""
from pathlib import Path
import base64, copy, json

ROOT=Path(__file__).resolve().parent
CASES=[]
def raw(value):return json.dumps(value,ensure_ascii=False,separators=(',',':')).encode('utf-8')
def add(identifier,kind,value,accepted,normalized=None):
    data=value if isinstance(value,bytes) else raw(value)
    row={'id':identifier,'kind':kind,'inputUtf8Base64':base64.b64encode(data).decode(),'accepted':accepted}
    row.update({'normalized':normalized} if accepted else {'errorCode':'INVALID_REQUEST'})
    CASES.append(row)
def expected_policy(mode='direct-first',**changes):
    result={'mode':mode,'connectTimeoutMs':15000,'directWaitMs':{'fast-connect':1500,'direct-first':8000,'direct-only':15000}[mode],
        'allowRelay':mode!='direct-only','prewarm':False,'prewarmTimeoutMs':8000,'prewarmIdleTimeoutMs':60000,
        'relayBudgetBytes':None,'relayWarningBytes':None}
    result.update(changes);return result
CLIENT={'manifestVersion':1,'applicationId':'org.example.client','displayName':'Files client','exports':[],
    'imports':[{'alias':'files','protocol':'https','endpointMode':'brokered','logicalAuthority':'files.example.invalid','connectionPolicy':{'mode':'direct-first'}}]}
PUBLISHER={'manifestVersion':1,'applicationId':'org.example.publisher','displayName':'Files service',
    'exports':[{'serviceId':'files','label':'Private files','protocol':'https','localTarget':{'host':'127.0.0.1','port':18480},'logicalAuthority':'files.example.invalid','healthCheck':{'type':'tcp'}}],'imports':[]}
def client_expected(value):
    result=copy.deepcopy(value)
    for item in result['imports']:item['connectionPolicy']=expected_policy(**item['connectionPolicy'])
    return result

for mode in ['fast-connect','direct-first','direct-only']:
    p={'mode':mode}
    if mode=='direct-only':p['allowRelay']=False
    add('default-'+mode,'policy',p,True,expected_policy(mode))
    short={**p,'connectTimeoutMs':1000}
    add('short-default-'+mode,'policy',short,True,expected_policy(mode,connectTimeoutMs=1000,directWaitMs=1000))
add('explicit-no-relay','policy',{'mode':'direct-first','allowRelay':False},True,expected_policy(allowRelay=False))
add('zero-wait-fast','policy',{'mode':'fast-connect','directWaitMs':0},True,expected_policy('fast-connect',directWaitMs=0))
add('maximum-policy','policy',{'mode':'direct-only','connectTimeoutMs':60000,'directWaitMs':60000,'allowRelay':False,'prewarm':True,'prewarmTimeoutMs':60000,'prewarmIdleTimeoutMs':600000},True,
    expected_policy('direct-only',connectTimeoutMs=60000,directWaitMs=60000,prewarm=True,prewarmTimeoutMs=60000,prewarmIdleTimeoutMs=600000))
add('finite-budget','policy',{'mode':'direct-first','relayBudgetBytes':'1048576','relayWarningBytes':'524288','relayBudgetAction':'stop-relay'},True,
    expected_policy(relayBudgetBytes='1048576',relayWarningBytes='524288',relayBudgetAction='stop-relay'))
add('maximum-uint64','policy',{'mode':'direct-first','relayBudgetBytes':'18446744073709551615','relayBudgetAction':'stop-relay'},True,
    expected_policy(relayBudgetBytes='18446744073709551615',relayBudgetAction='stop-relay'))
add('zero-budget','policy',{'mode':'direct-first','relayBudgetBytes':'0','relayWarningBytes':'0','relayBudgetAction':'stop-relay'},True,
    expected_policy(relayBudgetBytes='0',relayWarningBytes='0',relayBudgetAction='stop-relay'))
add('warning-only','policy',{'mode':'direct-first','relayWarningBytes':'10'},True,expected_policy(relayWarningBytes='10'))
bad_policies=[{}, {'mode':'unknown'}, {'mode':'direct-only'}, {'mode':'direct-only','allowRelay':True},
 {'mode':'direct-only','allowRelay':False,'directWaitMs':5000}, {'mode':'direct-first','directWaitMs':16000},
 {'mode':'direct-first','connectTimeoutMs':1000,'directWaitMs':1500}, {'mode':'direct-first','relayBudgetBytes':'100'},
 {'mode':'direct-first','relayBudgetAction':'stop-relay'}, {'mode':'direct-first','relayBudgetBytes':None,'relayBudgetAction':'stop-relay'},
 {'mode':'direct-first','relayBudgetBytes':'100','relayWarningBytes':'101','relayBudgetAction':'stop-relay'},
 {'mode':'direct-first','extra':False}, {'mode':'direct-first','prewarm':'false'}]
for i,value in enumerate(bad_policies):add('invalid-policy-'+str(i),'policy',value,False)
for field,values in [('connectTimeoutMs',[0,999,60001,True,'15000',None]),('directWaitMs',[-1,60001,True,'8000',None]),('prewarmTimeoutMs',[0,999,60001,False]),('prewarmIdleTimeoutMs',[0,999,600001,False])]:
    for i,value in enumerate(values):add('invalid-'+field.lower()+'-'+str(i),'policy',{'mode':'direct-first',field:value},False)
for i,value in enumerate(['','-1','01','1.0','1e3','18446744073709551616',' 1','1 ','١',True,1]):
    add('invalid-uint64-'+str(i),'policy',{'mode':'direct-first','relayBudgetBytes':value,'relayBudgetAction':'stop-relay'},False)
for i,number in enumerate(['1.0','1e3','1E3','1e0','-0.0']):
    add('policy-lexical-'+str(i),'policy',b'{"mode":"direct-first","connectTimeoutMs":'+number.encode()+b'}',False)
add('client-defaults','manifest',CLIENT,True,client_expected(CLIENT))
add('publisher','manifest',PUBLISHER,True,PUBLISHER)
tcp={'manifestVersion':1,'applicationId':'org.example.tcp','displayName':'TCP','exports':[{'serviceId':'socket','label':'Socket','protocol':'tcp','localTarget':{'host':'127.0.0.1','port':1024}}],'imports':[]}
add('tcp-low-port','manifest',tcp,True,tcp)
high=copy.deepcopy(tcp);high['exports'][0]['localTarget']['port']=65535
add('tcp-high-port','manifest',high,True,high)
for i,protocol in enumerate(['tcp','http','https','ws','wss']):
    m=copy.deepcopy(CLIENT);m['imports'][0]['protocol']=protocol
    add('protocol-'+str(i),'manifest',m,True,client_expected(m))
unicode_label=copy.deepcopy(CLIENT);unicode_label['displayName']='屏幕🙂'*20
add('unicode-label-scalars','manifest',unicode_label,True,client_expected(unicode_label))
u64=copy.deepcopy(CLIENT);u64['displayName']='🙂'*64
add('unicode-label-exact-limit','manifest',u64,True,client_expected(u64))
replacement=copy.deepcopy(CLIENT);replacement['displayName']='valid�'
add('literal-replacement-scalar','manifest',replacement,True,client_expected(replacement))
upper=copy.deepcopy(CLIENT);upper['imports'][0]['logicalAuthority']='FILES.Example.Invalid:443';normalized=client_expected(upper);normalized['imports'][0]['logicalAuthority']='files.example.invalid:443'
add('authority-normalization','manifest',upper,True,normalized)
longhost=copy.deepcopy(CLIENT);longhost['imports'][0]['logicalAuthority']='a'*63+'.'+'b'*63+'.'+'c'*63+'.'+'d'*61
add('authority-host-exact-limit','manifest',longhost,True,client_expected(longhost))
http_health=copy.deepcopy(PUBLISHER);http_health['exports'][0]['healthCheck']={'type':'http','path':'/health?ready=1','expectedStatus':[200,401]}
add('declared-health','manifest',http_health,True,http_health)
many=copy.deepcopy(PUBLISHER);many['exports']=[{**copy.deepcopy(PUBLISHER['exports'][0]),'serviceId':'service-'+str(i)} for i in range(16)]
add('sixteen-exports','manifest',many,True,many)
many_imports=copy.deepcopy(CLIENT);many_imports['imports']=[{**copy.deepcopy(CLIENT['imports'][0]),'alias':'remote-'+str(i)} for i in range(16)]
add('sixteen-imports','manifest',many_imports,True,client_expected(many_imports))
for field in CLIENT:
    m=copy.deepcopy(CLIENT);del m[field];add('missing-'+field.lower(),'manifest',m,False)
for field,value in [('manifestVersion',2),('manifestVersion',True),('manifestVersion','1'),('applicationId','Org.Example.App'),('applicationId','bad id'),('applicationId',''),('applicationId','org.'+'a'*126),('displayName',''),('displayName','   '),('displayName','x'*65),('displayName','🙂'*65),('displayName','a\n'),('displayName','a\x7f'),('exports',None),('imports',{})]:
    m=copy.deepcopy(CLIENT);m[field]=value;add('invalid-manifest-field-'+str(len(CASES)),'manifest',m,False)
for i,authority in enumerate(['127.0.0.1','127.1','0177.0.0.1','0x7f000001','123','[::1]','host.','-host.invalid','host-.invalid','a..invalid','a'*64+'.invalid','a'*63+'.'+'b'*63+'.'+'c'*63+'.'+'d'*62,'https://host.invalid','user@host.invalid','host.invalid/path','host.invalid:0','host.invalid:65536','host.invalid:0443','host.invalid:+443','host.invalid:','host.invalid\n','host.invalid ','ホスト.invalid']):
    m=copy.deepcopy(CLIENT);m['imports'][0]['logicalAuthority']=authority;add('invalid-authority-'+str(i),'manifest',m,False)
for i,mutate in enumerate([
 lambda m:m.update(token='synthetic-secret-marker'),lambda m:m.update(exports=[],imports=[]),
 lambda m:m['imports'][0].update(protocol='udp'),lambda m:m['imports'][0].update(endpointMode='public'),
 lambda m:m['imports'][0].update(alias='BadAlias'),lambda m:m['imports'][0].update(alias='a'*33),
 lambda m:m['imports'][0].pop('logicalAuthority'),lambda m:m['imports'][0].update(secretKey='synthetic-secret-marker'),
 lambda m:m['imports'].append(copy.deepcopy(m['imports'][0])),lambda m:m['imports'][0]['connectionPolicy'].update(mode='unknown')]):
    m=copy.deepcopy(CLIENT);mutate(m);add('invalid-client-shape-'+str(i),'manifest',m,False)
for i,mutate in enumerate([
 lambda m:m['exports'][0]['localTarget'].update(host='0.0.0.0'),lambda m:m['exports'][0]['localTarget'].update(host='localhost'),
 lambda m:m['exports'][0]['localTarget'].update(port=22),lambda m:m['exports'][0]['localTarget'].update(port=65536),
 lambda m:m['exports'][0]['localTarget'].update(port='18480'),lambda m:m['exports'][0]['localTarget'].update(port=True),
 lambda m:m['exports'][0].update(label=''),lambda m:m['exports'][0].update(serviceId='files/name'),
 lambda m:m['exports'].append(copy.deepcopy(m['exports'][0])),lambda m:m['exports'][0].update(localTarget={'host':'127.0.0.1','port':18480,'command':'unused'}),
 lambda m:m['exports'][0].update(healthCheck={'type':'tcp','path':'/'}),
 lambda m:m['exports'][0].update(healthCheck={'type':'http','path':'//other.invalid','expectedStatus':[200]}),
 lambda m:m['exports'][0].update(healthCheck={'type':'http','path':'/health#fragment','expectedStatus':[200]}),
 lambda m:m['exports'][0].update(healthCheck={'type':'http','path':'/not safe','expectedStatus':[200]}),
 lambda m:m['exports'][0].update(healthCheck={'type':'http','path':'/health','expectedStatus':[200,200]}),
 lambda m:m['exports'][0].update(healthCheck={'type':'http','path':'/health','expectedStatus':[99]}),
 lambda m:m['exports'][0].update(healthCheck={'type':'http','path':'/health','expectedStatus':[600]}),
 lambda m:m['exports'][0].update(healthCheck={'type':'http','path':'/health','expectedStatus':[]}),
 lambda m:m['exports'][0].update(healthCheck={'type':'http','path':'/health','expectedStatus':[True]}),
 lambda m:m['exports'][0].update(healthCheck={'type':'http','path':'/health','expectedStatus':[200],'headers':{'Authorization':'synthetic-secret-marker'}})
 ]):
    m=copy.deepcopy(PUBLISHER);mutate(m);add('invalid-publisher-shape-'+str(i),'manifest',m,False)
too_many=copy.deepcopy(many);too_many['exports'].append({**copy.deepcopy(PUBLISHER['exports'][0]),'serviceId':'extra'})
add('seventeen-exports','manifest',too_many,False)
too_many=copy.deepcopy(many_imports);too_many['imports'].append({**copy.deepcopy(CLIENT['imports'][0]),'alias':'extra'})
add('seventeen-imports','manifest',too_many,False)
syntax=[b'',b'null',b'[]',b'true',b'{}{}',b'{"mode":"direct-first",}',b'{"mode":"direct-first"}//comment',b"{'mode':'direct-first'}",
 b'{"mode":"direct-first","mode":"fast-connect"}',b'{"mode":"direct-first","m\\u006fde":"fast-connect"}',
 b'{"mode":"direct-first","prewarm":NaN}',b'{"mode":"direct-first","prewarm":Infinity}',
 b'\xef\xbb\xbf{"mode":"direct-first"}',b'{"mode":"\xff"}',b'{"mode":"\xc0\xaf"}',b'{"mode":"\xed\xa0\x80"}',
 b'{"mode":"\\ud800"}',b'{"mode":"\\udc00"}',b'{"mode":"\\ud800x"}',b'{"mode":"direct-first"}\xc2\xa0',
 b'{"mode":"direct-first","prewarm":TRUE}',b'{"mode":"direct-first","connectTimeoutMs":015000}',
 b'{"mode":"direct-first","connectTimeoutMs":+15000}',b'{"mode":"direct-first","connectTimeoutMs":1.}',
 b'{"mode":"direct-first","prewarm":"literal\ncontrol"}']
for i,value in enumerate(syntax):add('invalid-json-'+str(i),'policy',value,False)
valid_policy=raw({'mode':'direct-first'});valid_manifest=raw(CLIENT)
for kind,data,limit,normalized in [('policy',valid_policy,8192,expected_policy()),('manifest',valid_manifest,49152,client_expected(CLIENT))]:
    add(kind+'-exact-byte-limit',kind,data+b' '*(limit-len(data)),True,normalized)
    add(kind+'-over-byte-limit',kind,data+b' '*(limit-len(data)+1),False)
add('excessive-depth','policy',b'{"mode":"direct-first","extra":'+b'['*17+b'0'+b']'*17+b'}',False)
add('version-decimal-lexeme','manifest',valid_manifest.replace(b'"manifestVersion":1',b'"manifestVersion":1.0'),False)
add('version-exponent-lexeme','manifest',valid_manifest.replace(b'"manifestVersion":1',b'"manifestVersion":1e0'),False)
add('port-decimal-lexeme','manifest',raw(PUBLISHER).replace(b'"port":18480',b'"port":18480.0'),False)
add('status-exponent-lexeme','manifest',raw(http_health).replace(b'[200,401]',b'[2e2,401]'),False)
add('escaped-unicode-exact-limit','manifest',json.dumps(u64,ensure_ascii=True).encode(),True,client_expected(u64))
escaped_label=copy.deepcopy(CLIENT);escaped_label['displayName']='quoted "value" \\ path / value'
add('escaped-label-content','manifest',escaped_label,True,client_expected(escaped_label))
for i,space in enumerate(['\u0085','\u00a0','\u1680','\u2007','\u2028','\u2029','\u202f','\u205f','\u3000']):
    m=copy.deepcopy(CLIENT);m['displayName']=space;add('unicode-blank-label-'+str(i),'manifest',m,False)
    m=copy.deepcopy(PUBLISHER);m['exports'][0]['healthCheck']={'type':'http','path':'/'+space,'expectedStatus':[200]}
    add('unicode-health-whitespace-'+str(i),'manifest',m,False)
add('max-warning-no-budget','policy',{'mode':'direct-first','relayWarningBytes':'18446744073709551615'},True,expected_policy(relayWarningBytes='18446744073709551615'))
add('json-leading-whitespace','policy',b' \t\r\n'+valid_policy+b' \r\n',True,expected_policy())

# Connection params are a distinct public value; the approved policy is fixture
# metadata, not an input field a caller can assert as authority.
REQUEST={'applicationRef':'abcdef12-abcd-4abc-8abc-abcdef123456','importAlias':'files','deviceId':'0'*64,'publisherAppId':'org.example.publisher','remoteServiceId':'files','connectionPolicy':{'mode':'direct-first'}}
def request_expected(value):
    result=copy.deepcopy(value);result['applicationRef']=result['applicationRef'].lower();result['connectionPolicy']=expected_policy(**result['connectionPolicy']);return result
def connection(identifier,value,accepted=True,within=True,approved=None,normalized=None):
    expected={'request':normalized if normalized is not None else request_expected(value),'withinApproved':within} if accepted else None
    add('connection-'+identifier,'connection-open',value,accepted,expected)
    CASES[-1]['approvedPolicyUtf8Base64']=base64.b64encode(raw(approved if approved is not None else {'mode':'direct-first'})).decode()
connection('default',REQUEST)
upper=copy.deepcopy(REQUEST);upper['applicationRef']=upper['applicationRef'].upper();connection('canonical-uuid',upper)
for field in REQUEST:
    changed=copy.deepcopy(REQUEST);del changed[field];connection('missing-'+field,changed,False)
for field in ['host','port','caller','sid','token','applicationId']:
    changed=copy.deepcopy(REQUEST);changed[field]='SIMULATED_PRIVATE_VALUE';connection('extra-'+field,changed,False)
for field,values in [('applicationRef',['not-a-uuid',None,7]),('importAlias',['Files','x/y','a'*33]),('deviceId',['127.0.0.1','F'*64,'0'*63]),('publisherAppId',['single','Org.example.app','org_example.app','a'*129]),('remoteServiceId',['Files','x/y','a'*33])]:
    for index,value in enumerate(values):
        changed=copy.deepcopy(REQUEST);changed[field]=value;connection('invalid-'+field+'-'+str(index),changed,False)
for index,policy in enumerate([None,[],{}, {'mode':'direct-first','token':'SIMULATED_PRIVATE_VALUE'}]):
    changed=copy.deepcopy(REQUEST);changed['connectionPolicy']=policy;connection('invalid-policy-'+str(index),changed,False)
encoded=raw(REQUEST)
connection('duplicate-key',encoded.replace(b'"deviceId":',b'"deviceId":"'+b'0'*64+b'","deviceId":'),False)
connection('escaped-duplicate-key',encoded.replace(b'"deviceId":',b'"deviceId":"'+b'0'*64+b'","device\\u0049d":'),False)
connection('escaped-policy-key',encoded.replace(b'"connectionPolicy"',b'"connection\\u0050olicy"'),normalized=request_expected(REQUEST))
connection('invalid-utf8',b'\xff'+encoded,False)
connection('bom',b'\xef\xbb\xbf'+encoded,False)
connection('maximum-params-bytes',encoded+b' '*(16384-len(encoded)),normalized=request_expected(REQUEST))
connection('over-params-bytes',encoded+b' '*(16385-len(encoded)),False)
for length in [8192,8193]:
    prefix=b'{"mode":"direct-first"';policy=prefix+b' '*(length-len(prefix)-1)+b'}'
    value=encoded.replace(raw(REQUEST['connectionPolicy']),policy)
    connection('policy-source-bytes-'+str(length),value,length==8192,normalized=request_expected(REQUEST))
def constrained(identifier,requested,within=True,approved=None):
    changed=copy.deepcopy(REQUEST);changed['connectionPolicy']=requested;connection(identifier,changed,within=within,approved=approved)
constrained('shorter-total',{'mode':'direct-first','connectTimeoutMs':1000})
CASES[-1]['normalized']['request']['connectionPolicy']['directWaitMs']=1000
constrained('longer-total',{'mode':'direct-first','connectTimeoutMs':20000},False)
constrained('earlier-relay',{'mode':'fast-connect'},False)
constrained('later-relay',{'mode':'direct-first','directWaitMs':9000})
constrained('mode-label-with-same-wait',{'mode':'fast-connect','directWaitMs':8000})
constrained('disable-relay',{'mode':'fast-connect','allowRelay':False,'directWaitMs':0})
constrained('direct-only',{'mode':'direct-only','allowRelay':False})
constrained('enable-forbidden-relay',{'mode':'direct-first'},False,{'mode':'direct-only','allowRelay':False})
constrained('enable-forbidden-prewarm',{'mode':'direct-first','prewarm':True},False)
constrained('shorter-prewarm',{'mode':'direct-first','prewarm':True,'prewarmTimeoutMs':5000,'prewarmIdleTimeoutMs':30000},approved={'mode':'direct-first','prewarm':True})
constrained('longer-prewarm',{'mode':'direct-first','prewarm':True,'prewarmTimeoutMs':9000},False,{'mode':'direct-first','prewarm':True})
constrained('longer-prewarm-idle',{'mode':'direct-first','prewarm':True,'prewarmIdleTimeoutMs':60001},False,{'mode':'direct-first','prewarm':True})
constrained('disable-prewarm',{'mode':'direct-first','prewarm':False,'prewarmTimeoutMs':60000,'prewarmIdleTimeoutMs':600000},approved={'mode':'direct-first','prewarm':True})
constrained('new-byte-cap',{'mode':'direct-first','relayBudgetBytes':'100','relayWarningBytes':'50','relayBudgetAction':'stop-relay'})
cap={'mode':'direct-first','relayBudgetBytes':'100','relayWarningBytes':'50','relayBudgetAction':'stop-relay'}
constrained('lower-byte-cap',{'mode':'direct-first','relayBudgetBytes':'50','relayWarningBytes':'25','relayBudgetAction':'stop-relay'},approved=cap)
constrained('larger-byte-cap',{**cap,'relayBudgetBytes':'101'},False,cap)
constrained('remove-byte-cap',{'mode':'direct-first','relayWarningBytes':'50'},False,cap)
constrained('larger-warning',{**cap,'relayWarningBytes':'51'},False,cap)
constrained('remove-warning',{'mode':'direct-first','relayBudgetBytes':'100','relayBudgetAction':'stop-relay'},False,cap)
constrained('exact-uint64',{'mode':'direct-first','relayBudgetBytes':'18446744073709551614','relayWarningBytes':'0','relayBudgetAction':'stop-relay'},approved={'mode':'direct-first','relayBudgetBytes':'18446744073709551615','relayBudgetAction':'stop-relay'})
constrained('zero-cap',{'mode':'direct-first','relayBudgetBytes':'0','relayBudgetAction':'stop-relay'},approved={'mode':'direct-first','relayBudgetBytes':'0','relayBudgetAction':'stop-relay'})
constrained('increase-zero-cap',{'mode':'direct-first','relayBudgetBytes':'1','relayBudgetAction':'stop-relay'},False,{'mode':'direct-first','relayBudgetBytes':'0','relayBudgetAction':'stop-relay'})
# Independent public status and retained-operation values, not a parser-generated oracle.
STAMP='2026-10-30T01:00:00.000Z'
READY='2026-10-30T01:00:00.010Z'
CLOSED='2026-10-30T01:00:01.000Z'
STATUS={'connectionId':'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa','serviceRef':{'ownerId':'a'*64,'deviceId':'b'*64,'publisherAppId':'org.example.publisher','serviceId':'files'},'bootId':'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb','networkGeneration':'1','flowState':'pending','selectedPath':'none','businessPath':'none','directTx':'0','directRx':'0','relayTx':'0','relayRx':'0','complete':False,'error':None,'acceptedUtc':STAMP,'readyUtc':None,'closedUtc':None,'setupMs':None}
def status_case(name,value,good=True):add('status-'+name,'connection-status',value,good,copy.deepcopy(value) if good else None)
def status_change(name,changes,good=False,base=None):
    v=copy.deepcopy(STATUS if base is None else base);v.update(changes);status_case(name,v,good);return v
status_case('pending',STATUS)
ATTACH=status_change('attaching',{'flowState':'attaching','selectedPath':'direct','readyUtc':READY,'setupMs':10},True)
OPEN=status_change('open-no-business',{'flowState':'open'},True,ATTACH)
PAYLOAD=status_change('open-direct',{'directTx':'33','directRx':'67108864','businessPath':'direct'},True,OPEN)
DRAIN=status_change('draining',{'flowState':'draining'},True,PAYLOAD)
DONE=status_change('complete',{'flowState':'closed','closedUtc':CLOSED,'complete':True},True,DRAIN)
RELAY=status_change('relay',{'selectedPath':'relay','businessPath':'relay','directTx':'0','directRx':'0','relayTx':'33','relayRx':'67108864'},True,DONE)
ERROR={'code':'NETWORK_CHANGED','stage':'connection','retryable':False,'message':'Connection ended.','details':{}}
status_change('failed-before-ready',{'flowState':'failed','closedUtc':CLOSED,'error':ERROR},True)
status_change('failed-after-ready',{'flowState':'failed','closedUtc':CLOSED,'error':ERROR},True,PAYLOAD)
status_change('closed-incomplete',{'flowState':'closed','closedUtc':CLOSED},True,PAYLOAD)
status_change('closed-no-ready',{'flowState':'closed','closedUtc':CLOSED,'error':{**ERROR,'code':'CONNECTION_CLOSED'}},True)
status_change('unknown-accepted-time',{'acceptedUtc':None},True)
status_change('maximum-counters',{'directTx':'18446744073709551615','directRx':'18446744073709551615','networkGeneration':'18446744073709551615'},True,PAYLOAD)
status_change('maximum-setup',{'setupMs':60000},True,ATTACH)
status_change('zero-setup',{'setupMs':0},True,ATTACH)
for field in STATUS:
    v=copy.deepcopy(STATUS);v.pop(field);status_case('missing-'+field,v,False)
for name,changes,base in [
 ('unknown-field',{'extra':'private-marker'},STATUS),('upper-connection',{'connectionId':STATUS['connectionId'].upper()},STATUS),('upper-boot',{'bootId':STATUS['bootId'].upper()},STATUS),('bad-uuid',{'connectionId':'bad'},STATUS),
 ('generation-zero',{'networkGeneration':'0'},STATUS),('generation-number',{'networkGeneration':1},STATUS),('generation-leading-zero',{'networkGeneration':'01'},STATUS),('generation-overflow',{'networkGeneration':'18446744073709551616'},STATUS),
 ('unknown-state',{'flowState':'ready'},STATUS),('unknown-path',{'selectedPath':'xtcp'},STATUS),('selected-without-ready',{'selectedPath':'direct'},STATUS),('ready-without-selected',{'readyUtc':READY,'setupMs':10},STATUS),
 ('ready-without-setup',{'setupMs':None},ATTACH),('setup-decimal',{'setupMs':1.5},ATTACH),('setup-negative',{'setupMs':-1},ATTACH),('setup-overflow',{'setupMs':60001},ATTACH),('setup-bool',{'setupMs':True},ATTACH),
 ('pending-with-ready',{'flowState':'pending'},ATTACH),('open-without-ready',{'flowState':'open'},STATUS),('draining-without-ready',{'flowState':'draining'},STATUS),('attaching-payload',{'flowState':'attaching'},PAYLOAD),
 ('wrong-counter-path',{'relayRx':'1'},PAYLOAD),('counter-negative',{'directRx':'-1'},PAYLOAD),('counter-number',{'directRx':1},PAYLOAD),('counter-leading-zero',{'directRx':'01'},PAYLOAD),('counter-exponent',{'directRx':'1e2'},PAYLOAD),('counter-overflow',{'directRx':'18446744073709551616'},PAYLOAD),
 ('missing-business',{'businessPath':'none'},PAYLOAD),('wrong-business',{'businessPath':'relay'},PAYLOAD),('no-byte-business',{'businessPath':'direct'},OPEN),('complete-active',{'complete':True},PAYLOAD),('complete-not-bool',{'complete':1},DONE),('complete-failed',{'flowState':'failed','error':ERROR},DONE),('complete-error',{'error':ERROR},DONE),
 ('terminal-without-time',{'closedUtc':None},DONE),('time-without-terminal',{'closedUtc':CLOSED},OPEN),('failed-without-error',{'flowState':'failed','closedUtc':CLOSED},STATUS),('active-error',{'error':ERROR},OPEN),
 ('backwards-ready',{'readyUtc':'2026-10-30T00:00:00.000Z'},ATTACH),('backwards-closed',{'closedUtc':'2026-10-30T01:00:00.005Z'},DONE),('backwards-accepted',{'acceptedUtc':'2026-10-31T01:00:00.000Z'},DONE),
]:status_change(name,changes,False,base)
for i,stamp in enumerate(['2026-02-30T01:00:00.000Z','2026-10-30T24:00:00.000Z','2026-10-30T23:59:60.000Z','0000-01-01T00:00:00.000Z','2026-10-30T01:00:00Z','2026-10-30T01:00:00.000+00:00','2026-10-30t01:00:00.000z','2026-10-30T01:00:00.0000Z']):status_change('bad-time-'+str(i),{'acceptedUtc':stamp})
for field,bad in [('ownerId','A'*64),('deviceId','x'*64),('publisherAppId','INVALID'),('serviceId','BAD'),('extra','private-marker')]:
    status_change('service-'+field,{'serviceRef':{**STATUS['serviceRef'],field:bad}})
for field,bad in [('code','private-marker'),('stage','socket'),('retryable',True),('message','private-marker'),('details',{'cause':'private-marker'}),('extra','private-marker')]:
    status_change('error-'+field,{'flowState':'failed','closedUtc':CLOSED,'error':{**ERROR,field:bad}})
status_case('duplicate-key',raw(STATUS).replace(b'"complete":false',b'"complete":false,"complete":false'),False)
status_case('setup-decimal-lexeme',raw(ATTACH).replace(b'"setupMs":10',b'"setupMs":10.0'),False)
add('status-exact-size','connection-status',raw(STATUS)+b' '*(16384-len(raw(STATUS))),True,STATUS)
add('status-over-size','connection-status',raw(STATUS)+b' '*(16385-len(raw(STATUS))),False)
OP='cccccccc-cccc-4ccc-8ccc-cccccccccccc'
def operation_case(name,value,good=True,kind=None,expected=OP):
    add('operation-'+name,'connection-operation',value,good,copy.deepcopy(value) if good else None);CASES[-1]['expectedOperation']=expected;CASES[-1]['expectedKind']=kind
PENDING={'operationId':OP,'status':'pending'}
SUCCESS={'operationId':OP,'status':'succeeded','result':{'connectionId':STATUS['connectionId']}}
STREAM={'operationId':OP,'status':'succeeded','result':{'streamRef':STATUS['connectionId']}}
FAILED={'operationId':OP,'status':'failed','error':ERROR}
operation_case('pending',PENDING)
operation_case('reconciling',{**PENDING,'status':'reconciling'})
operation_case('connection',SUCCESS,kind='connectionId')
operation_case('connection-any',SUCCESS)
operation_case('stream',STREAM,kind='streamRef')
operation_case('stream-any',STREAM)
operation_case('failed',FAILED,kind='connectionId')
operation_case('wrong-correlation',SUCCESS,False,expected=STATUS['connectionId'])
operation_case('wrong-kind',SUCCESS,False,kind='streamRef')
operation_case('invalid-expected-kind',SUCCESS,False,kind='private-marker')
operation_case('upper-operation',{**SUCCESS,'operationId':OP.upper()},False)
operation_case('upper-reference',{**SUCCESS,'result':{'connectionId':STATUS['connectionId'].upper()}},False)
operation_case('multiple-references',{**SUCCESS,'result':{**SUCCESS['result'],**STREAM['result']}},False)
operation_case('pipe-disclosure',{**SUCCESS,'result':{'dataPipe':'private-marker'}},False)
operation_case('empty-success',{**SUCCESS,'result':{}},False)
operation_case('pending-result',{**PENDING,'result':SUCCESS['result']},False)
operation_case('failed-result',{**FAILED,'result':SUCCESS['result']},False)
operation_case('success-error',{**SUCCESS,'error':ERROR},False)
operation_case('unknown-status',{**PENDING,'status':'ready'},False)
operation_case('null-error',{**FAILED,'error':None},False)
for field in PENDING:
    v=copy.deepcopy(PENDING);v.pop(field);operation_case('missing-'+field,v,False)
for field,bad in [('code','bad'),('stage','socket'),('retryable',True),('message','private-marker'),('details',{'token':'private-marker'})]:operation_case('bad-error-'+field,{**FAILED,'error':{**ERROR,field:bad}},False)
operation_case('duplicate-key',raw(PENDING).replace(b'"status":"pending"',b'"status":"pending","status":"pending"'),False)
add('operation-exact-size','connection-operation',raw(SUCCESS)+b' '*(16384-len(raw(SUCCESS))),True,SUCCESS);CASES[-1].update(expectedOperation=OP,expectedKind='connectionId')
operation_case('over-size',raw(SUCCESS)+b' '*(16385-len(raw(SUCCESS))),False)
assert len({c['id'] for c in CASES})==len(CASES)
payload={'corpusVersion':1,'cases':CASES}
(ROOT/'cases.json').write_text(json.dumps(payload,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps({'cases':len(CASES),'valid':sum(c['accepted'] for c in CASES),'invalid':sum(not c['accepted'] for c in CASES)}))
