"""Compare independent contract runners against each other and the reviewed corpus."""
import argparse,json
from pathlib import Path

p=argparse.ArgumentParser();p.add_argument('--vectors',type=Path,required=True);p.add_argument('--dotnet',type=Path,required=True);p.add_argument('--java',type=Path,required=True);p.add_argument('--output',type=Path,required=True);a=p.parse_args()
corpus=json.loads(a.vectors.read_text(encoding='utf-8'))
def expected(row):
    result={'id':row['id'],'accepted':row['accepted']}
    result.update({'normalized':row['normalized']} if row['accepted'] else {'errorCode':row['errorCode']})
    return result
def canonical(rows):return {row['id']:json.dumps(row,ensure_ascii=False,sort_keys=True,separators=(',',':')) for row in rows}
reference=canonical([expected(row) for row in corpus['cases']])
for path in [a.dotnet,a.java]:
    report=json.loads(path.read_text(encoding='utf-8-sig'))
    assert report['contractVersion']==1 and report['total']==report['passed']==len(reference),'Runner did not pass the complete corpus'
    assert len(report['cases'])==len(reference) and canonical(report['cases'])==reference,'Independent normalized results differ'
summary={'contractVersion':1,'corpusVersion':corpus['corpusVersion'],'cases':len(reference),'valid':sum(row['accepted'] for row in corpus['cases']),'invalid':sum(not row['accepted'] for row in corpus['cases']),'dotnetPassed':True,'javaPassed':True,'normalizedResultsEqual':True,'networkOrDeviceTestsPerformed':False}
a.output.parent.mkdir(parents=True,exist_ok=True);a.output.write_text(json.dumps(summary,indent=2)+'\n',encoding='utf-8')
print(json.dumps(summary))
